using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace FH6LocalCryptoTool;

/// <summary>
/// Merge one decrypted gamedb into another. Two modes:
///
///   * AddOnly (default) — the BASE wins. Every row already in the base is left
///     exactly as it is; only rows the overlay has that the base does NOT are
///     inserted. Nothing you changed is ever overwritten or deleted. This is the
///     right mode for pulling the new cars/parts out of a game update onto your
///     modded DB without reverting any of your work.
///
///   * OverlayWins — the OVERLAY wins. Wherever it has a row (matched by primary
///     key), that row's values overwrite the base's, and rows the overlay adds are
///     inserted. Use this when the overlay is a small DB holding only your changed
///     rows and you want it applied onto a fresh stock DB.
///
/// A short list of "force overlay" tables (default: VersionInfo) is always taken
/// wholesale from the overlay in AddOnly mode — the DB version stamp must match the
/// game build even though every other row stays yours.
///
/// Sources, chosen by the overlay's extension:
///   * .sql     -> copy base to out, run the SQL script (mode is ignored).
///   * .sqlite  -> copy base to out, ATTACH the overlay, and per shared table apply
///                 the chosen mode.
///
/// Row identity per table:
///   * real PRIMARY KEY  -> AddOnly: INSERT ... ON CONFLICT(pk) DO NOTHING.
///                          OverlayWins: INSERT ... ON CONFLICT(pk) DO UPDATE.
///   * no PK (rowid tbl)  -> AddOnly: insert only overlay rows whose full content
///                          isn't already in the base (EXCEPT), so a genuinely new
///                          row is added and none of yours are duplicated or lost —
///                          rowids are NOT trusted to line up between two DBs.
///                          OverlayWins: INSERT OR REPLACE by explicit rowid.
///   * WITHOUT ROWID has a PK, so it takes the PRIMARY KEY path.
/// </summary>
public enum MergeMode { AddOnly, OverlayWins }

public static class Merge
{
    // Tables whose rows are always taken wholesale from the overlay in AddOnly mode.
    public static readonly string[] DefaultForceOverlayTables = { "VersionInfo" };

    public static void Run(string baseSqlite, string overlay, string outSqlite,
                           string[]? tables, Action<string>? log = null,
                           MergeMode mode = MergeMode.AddOnly,
                           IEnumerable<string>? forceOverlayTables = null)
    {
        File.Copy(baseSqlite, outSqlite, overwrite: true);

        var force = new HashSet<string>(forceOverlayTables ?? DefaultForceOverlayTables,
                                        StringComparer.OrdinalIgnoreCase);

        // Pooling=False + ClearAllPools so the OS handle on outSqlite is released the
        // moment we finish — otherwise the next step (re-encrypting the merged .sqlite)
        // fails with "being used by another process".
        try
        {
            using (var con = new SqliteConnection($"Data Source={outSqlite};Pooling=False"))
            {
                con.Open();
                Exec(con, "PRAGMA foreign_keys=OFF;");   // we insert in dependency-agnostic order
                Apply(con, overlay, tables, log, mode, force);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private static void Apply(SqliteConnection con, string overlay, string[]? tables,
                              Action<string>? log, MergeMode mode, HashSet<string> force)
    {
        if (overlay.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
        {
            string sql = File.ReadAllText(overlay);
            using var tx = con.BeginTransaction();
            using (var cmd = con.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            log?.Invoke($"applied SQL patch: {Path.GetFileName(overlay)}");
            return;
        }

        log?.Invoke(mode == MergeMode.AddOnly
            ? "mode: ADD-ONLY — your rows are kept; only new rows from the overlay are added."
            : "mode: OVERLAY-WINS — the overlay's rows overwrite matching base rows.");

        // Attach the overlay DB (escaped literal path — parameterised ATTACH is flaky).
        string ovEsc = Path.GetFullPath(overlay).Replace("'", "''");
        Exec(con, $"ATTACH DATABASE '{ovEsc}' AS ov;");

        List<string> targets = tables is { Length: > 0 } ? tables.ToList() : SharedTables(con);

        // Whole new tables introduced by the update (present only in the overlay). Unless
        // the caller restricted to an explicit table list, create each one in the base and
        // copy its rows wholesale, so a game update's brand-new tables actually come across
        // instead of being dropped.
        List<string> overlayOnly = new();
        if (tables is not { Length: > 0 })
        {
            var mainTabs = TableNames(con, "main");
            foreach (var ot in TableNames(con, "ov"))
                if (!mainTabs.Contains(ot)) overlayOnly.Add(ot);
        }

        int totalRows = 0, tableCount = 0;
        using (var tx = con.BeginTransaction())
        {
            foreach (var t in overlayOnly)
            {
                string? createSql = ObjectSql(con, "ov", "table", t);
                if (createSql is null) { log?.Invoke($"{t}: overlay-only table — no schema found, skipped."); continue; }
                Exec(con, createSql, tx);                                   // same bare name → created in main
                var ncols = ColumnNames(con, "ov", t);
                string ncolList = string.Join(", ", ncols.Select(Q));
                int nrows = Exec(con, $"INSERT INTO main.{Q(t)} ({ncolList}) SELECT {ncolList} FROM ov.{Q(t)};", tx);
                foreach (var idxSql in ObjectSqls(con, "ov", "index", t))
                    try { Exec(con, idxSql, tx); } catch { /* skip a duplicate/again index */ }
                log?.Invoke($"{t}: NEW table created from update — {nrows} row(s) copied.");
                totalRows += nrows; tableCount++;
            }

            foreach (var t in targets)
            {
                var cols = SharedColumns(con, t);
                if (cols.Count == 0)
                {
                    log?.Invoke($"{t}: skipped (no shared columns / table missing)");
                    continue;
                }

                string colList = string.Join(", ", cols.Select(Q));
                int rows;

                // Force tables (e.g. VersionInfo): always take the overlay's rows wholesale,
                // even in add-only mode, so the DB version matches the game build.
                if (mode == MergeMode.AddOnly && force.Contains(t))
                {
                    Exec(con, $"DELETE FROM main.{Q(t)};", tx);
                    rows = Exec(con, $"INSERT INTO main.{Q(t)} ({colList}) SELECT {colList} FROM ov.{Q(t)};", tx);
                    log?.Invoke($"{t}: replaced with {rows} overlay row(s)  [forced from update]");
                    totalRows += rows; tableCount++;
                    continue;
                }

                var pk = PrimaryKeyColumns(con, "main", t);
                bool pkUsable = pk.Count > 0 &&
                                pk.All(k => cols.Any(c => string.Equals(c, k, StringComparison.OrdinalIgnoreCase)));

                if (pkUsable)
                {
                    string conflict = string.Join(", ", pk.Select(Q));

                    if (mode == MergeMode.AddOnly)
                    {
                        // Insert rows whose key isn't already present; keep every existing row.
                        string sql = $"INSERT INTO main.{Q(t)} ({colList}) SELECT {colList} FROM ov.{Q(t)} WHERE true " +
                                     $"ON CONFLICT({conflict}) DO NOTHING;";
                        rows = Exec(con, sql, tx);
                        log?.Invoke($"{t}: added {rows} new row(s) on ({conflict})  [kept yours]");
                    }
                    else
                    {
                        var setCols = cols.Where(c => !pk.Any(k => string.Equals(k, c, StringComparison.OrdinalIgnoreCase))).ToList();
                        string sql = setCols.Count == 0
                            ? $"INSERT OR IGNORE INTO main.{Q(t)} ({colList}) SELECT {colList} FROM ov.{Q(t)};"
                            : $"INSERT INTO main.{Q(t)} ({colList}) SELECT {colList} FROM ov.{Q(t)} WHERE true " +
                              $"ON CONFLICT({conflict}) DO UPDATE SET " +
                              string.Join(", ", setCols.Select(c => $"{Q(c)}=excluded.{Q(c)}")) + ";";
                        rows = Exec(con, sql, tx);
                        log?.Invoke($"{t}: {rows} row(s) upserted on ({conflict})  [overlay wins]");
                    }
                }
                else if (mode == MergeMode.AddOnly)
                {
                    // No usable primary key: rowids don't line up between two independently
                    // built DBs, so identity is the full row. Add overlay rows that aren't
                    // already present by content; never touch or duplicate yours.
                    string sql = $"INSERT INTO main.{Q(t)} ({colList}) " +
                                 $"SELECT {colList} FROM ov.{Q(t)} " +
                                 $"EXCEPT SELECT {colList} FROM main.{Q(t)};";
                    rows = Exec(con, sql, tx);
                    log?.Invoke($"{t}: added {rows} new row(s) by content  [no PK, kept yours]");
                }
                else if (!IsWithoutRowid(con, "main", t))
                {
                    string sql = $"INSERT OR REPLACE INTO main.{Q(t)} (rowid, {colList}) " +
                                 $"SELECT rowid, {colList} FROM ov.{Q(t)};";
                    rows = Exec(con, sql, tx);
                    log?.Invoke($"{t}: {rows} row(s) replaced by rowid  [overlay wins, no PK]");
                }
                else
                {
                    log?.Invoke($"{t}: skipped (WITHOUT ROWID and no shared primary key — can't match rows)");
                    continue;
                }

                totalRows += rows;
                tableCount++;
            }
            tx.Commit();
        }

        Exec(con, "DETACH DATABASE ov;");
        log?.Invoke($"done: {totalRows} row(s) across {tableCount} table(s).");
    }

    // ---------- schema helpers ----------
    private static string Q(string ident) => "\"" + ident.Replace("\"", "\"\"") + "\"";

    private static int Exec(SqliteConnection con, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = con.CreateCommand();
        if (tx != null) cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }

    private static List<string> SharedTables(SqliteConnection con)
    {
        var main = TableNames(con, "main");
        var ov = TableNames(con, "ov");
        main.IntersectWith(ov);
        return main.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static HashSet<string> TableNames(SqliteConnection con, string schema)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = con.CreateCommand();
        cmd.CommandText =
            $"SELECT name FROM {schema}.sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }

    // The CREATE sql for one object (table/index/trigger) by name, or null.
    private static string? ObjectSql(SqliteConnection con, string schema, string type, string name)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"SELECT sql FROM {schema}.sqlite_master WHERE type=$t AND name=$n;";
        cmd.Parameters.AddWithValue("$t", type);
        cmd.Parameters.AddWithValue("$n", name);
        return cmd.ExecuteScalar() as string;
    }

    // The CREATE sql for every object of a type attached to a table (e.g. its indexes).
    private static List<string> ObjectSqls(SqliteConnection con, string schema, string type, string tbl)
    {
        var list = new List<string>();
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"SELECT sql FROM {schema}.sqlite_master WHERE type=$t AND tbl_name=$n AND sql IS NOT NULL;";
        cmd.Parameters.AddWithValue("$t", type);
        cmd.Parameters.AddWithValue("$n", tbl);
        using var r = cmd.ExecuteReader();
        while (r.Read()) if (!r.IsDBNull(0)) list.Add(r.GetString(0));
        return list;
    }

    // Columns present in BOTH main.T and ov.T, in main's order.
    private static List<string> SharedColumns(SqliteConnection con, string table)
    {
        var mainCols = ColumnNames(con, "main", table);
        var ovCols = new HashSet<string>(ColumnNames(con, "ov", table), StringComparer.OrdinalIgnoreCase);
        return mainCols.Where(c => ovCols.Contains(c)).ToList();
    }

    private static List<string> ColumnNames(SqliteConnection con, string schema, string table)
    {
        var cols = new List<string>();
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"PRAGMA {schema}.table_info({Q(table)});";
        using var r = cmd.ExecuteReader();
        while (r.Read()) cols.Add(r.GetString(1)); // 1 = name
        return cols;
    }

    // Primary-key columns in key order (pk flag > 0), empty if the table has none.
    private static List<string> PrimaryKeyColumns(SqliteConnection con, string schema, string table)
    {
        var pk = new List<(int seq, string name)>();
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"PRAGMA {schema}.table_info({Q(table)});";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            int flag = r.GetInt32(5);          // 5 = pk (0 = not part of PK, else 1-based position)
            if (flag > 0) pk.Add((flag, r.GetString(1)));
        }
        return pk.OrderBy(p => p.seq).Select(p => p.name).ToList();
    }

    private static bool IsWithoutRowid(SqliteConnection con, string schema, string table)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"SELECT sql FROM {schema}.sqlite_master WHERE type='table' AND name=$n;";
        cmd.Parameters.AddWithValue("$n", table);
        var sql = cmd.ExecuteScalar() as string ?? "";
        return sql.ToUpperInvariant().Replace(" ", "").Contains("WITHOUTROWID");
    }
}
