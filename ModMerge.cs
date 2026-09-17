using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace FH6LocalCryptoTool;

public sealed class ModMergeRow
{
    internal ModMergeRow(string table, object?[] keyValues, long? overlayRowId,
                         bool conflict, string label, string[] changedColumns)
    {
        Table = table;
        KeyValues = keyValues;
        OverlayRowId = overlayRowId;
        IsConflict = conflict;
        Label = label;
        ChangedColumns = changedColumns;
    }
    public string Table { get; }
    public object?[] KeyValues { get; }
    public long? OverlayRowId { get; }
    public bool IsConflict { get; }
    public string Label { get; }
    public string[] ChangedColumns { get; }
}

public sealed class ModMergeTable
{
    internal ModMergeTable(string name, bool isNew, string[] columns, string[] primaryKey, bool hasRowid,
                           List<ModMergeRow> rows, int donorRowCount, int baseOnlyCount)
    {
        Name = name;
        IsNew = isNew;
        Columns = columns;
        PrimaryKey = primaryKey;
        HasRowid = hasRowid;
        Rows = rows;
        DonorRowCount = donorRowCount;
        BaseOnlyCount = baseOnlyCount;
    }
    public string Name { get; }
    public bool IsNew { get; }
    public string[] Columns { get; }
    public string[] PrimaryKey { get; }
    public bool HasRowid { get; }
    public IReadOnlyList<ModMergeRow> Rows { get; }
    public int DonorRowCount { get; }
    public int BaseOnlyCount { get; }
    public int AddedCount => Rows.Count(r => !r.IsConflict);
    public int ConflictCount => Rows.Count(r => r.IsConflict);
}

public sealed class ModMergePreview
{
    internal ModMergePreview(string basePath, string overlayPath, string baseHash, string overlayHash,
                             List<ModMergeTable> tables, List<string> warnings)
    {
        BasePath = basePath;
        OverlayPath = overlayPath;
        BaseHash = baseHash;
        OverlayHash = overlayHash;
        Tables = tables;
        Warnings = warnings;
    }
    public string BasePath { get; }
    public string OverlayPath { get; }
    internal string BaseHash { get; }
    internal string OverlayHash { get; }
    public IReadOnlyList<ModMergeTable> Tables { get; }
    public IReadOnlyList<string> Warnings { get; }
}

public static partial class Merge
{
    private static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        string hash = Convert.ToHexString(SHA256.HashData(stream));
        string walPath = path + "-wal";
        if (!File.Exists(walPath)) return hash;
        using var wal = File.OpenRead(walPath);
        return hash + ":" + Convert.ToHexString(SHA256.HashData(wal));
    }

    private static string SqlPath(string path) => Path.GetFullPath(path).Replace("'", "''");

    public static ModMergePreview PreviewMods(string baseSqlite, string overlaySqlite)
    {
        string basePath = Path.GetFullPath(baseSqlite), overlayPath = Path.GetFullPath(overlaySqlite);
        if (string.Equals(basePath, overlayPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Base and donor must be different database files.");
        if (!File.Exists(basePath) || !File.Exists(overlayPath))
            throw new FileNotFoundException("The base or donor database was not found.");

        string baseHash = FileHash(basePath), overlayHash = FileHash(overlayPath);
        var tables = new List<ModMergeTable>();
        var warnings = new List<string>();
        using var con = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = basePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        con.Open();
        Exec(con, $"ATTACH DATABASE '{SqlPath(overlayPath)}' AS ov;");
        var baseTables = TableNames(con, "main");
        foreach (string table in TableNames(con, "ov").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            bool isNew = !baseTables.Contains(table);
            var overlayColumns = ColumnNames(con, "ov", table);
            var baseColumns = isNew ? overlayColumns : ColumnNames(con, "main", table);
            if (!isNew && !new HashSet<string>(baseColumns, StringComparer.OrdinalIgnoreCase)
                    .SetEquals(overlayColumns))
            { warnings.Add($"{table}: different column sets; use the game-update merge first."); continue; }
            var baseKey = isNew ? PrimaryKeyColumns(con, "ov", table) : PrimaryKeyColumns(con, "main", table);
            var overlayKey = PrimaryKeyColumns(con, "ov", table);
            if (!baseKey.SequenceEqual(overlayKey, StringComparer.OrdinalIgnoreCase))
            { warnings.Add($"{table}: different primary keys; skipped for safety."); continue; }
            bool hasRowid = !IsWithoutRowid(con, "ov", table);
            if (!isNew && hasRowid != !IsWithoutRowid(con, "main", table))
            { warnings.Add($"{table}: different rowid storage; skipped for safety."); continue; }
            try
            {
                var rows = PreviewTableRows(con, table, baseColumns, overlayKey, isNew, hasRowid);
                int donorRowCount = CountRows(con, "ov", table);
                int baseOnlyCount = isNew ? 0 : CountBaseOnlyRows(con, table, baseColumns, overlayKey);
                if (rows.Count > 0 || baseOnlyCount > 0)
                    tables.Add(new ModMergeTable(table, isNew, baseColumns.ToArray(), overlayKey.ToArray(),
                        hasRowid, rows, donorRowCount, baseOnlyCount));
            }
            catch (SqliteException ex)
            {
                warnings.Add($"{table}: could not compare rows ({ex.Message}); skipped.");
            }
        }
        Exec(con, "DETACH DATABASE ov;");
        return new ModMergePreview(basePath, overlayPath, baseHash, overlayHash, tables, warnings);
    }

    private static int CountRows(SqliteConnection con, string schema, string table)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {schema}.{Q(table)};";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static int CountBaseOnlyRows(SqliteConnection con, string table,
        List<string> columns, List<string> key)
    {
        string equality = key.Count > 0
            ? string.Join(" AND ", key.Select(c => $"o.{Q(c)} IS b.{Q(c)}"))
            : string.Join(" AND ", columns.Select(c => $"o.{Q(c)} IS b.{Q(c)}"));
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM main.{Q(table)} b " +
                          $"WHERE NOT EXISTS (SELECT 1 FROM ov.{Q(table)} o WHERE {equality});";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static List<ModMergeRow> PreviewTableRows(SqliteConnection con, string table,
        List<string> columns, List<string> key, bool isNew, bool hasRowid)
    {
        var rows = new List<ModMergeRow>();
        string quotedTable = Q(table);
        var display = new[] { "MediaName", "Ordinal", "EngineID", "MotorID", "CarBodyID", "Name", "Level" }
            .Where(x => columns.Contains(x, StringComparer.OrdinalIgnoreCase) &&
                        !key.Contains(x, StringComparer.OrdinalIgnoreCase)).Take(2).ToList();
        if (key.Count == 0)
        {
            if (!hasRowid) return rows;
            string equality = string.Join(" AND ", columns.Select(c => $"b.{Q(c)} IS o.{Q(c)}"));
            string select = string.Join(",", display.Select(c => $"o.{Q(c)}"));
            string sql = $"SELECT o.rowid{(select.Length > 0 ? "," + select : "")} FROM ov.{quotedTable} o" +
                         (isNew ? "" : $" WHERE NOT EXISTS (SELECT 1 FROM main.{quotedTable} b WHERE {equality})");
            using var cmd = con.CreateCommand(); cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                long rowid = reader.GetInt64(0);
                string label = $"donor rowid {rowid}" + DisplaySuffix(reader, 1, display);
                rows.Add(new ModMergeRow(table, Array.Empty<object>(), rowid, false, label,
                    key.Count == 0 && !isNew ? new[] { "No primary key: add by content only" } : Array.Empty<string>()));
            }
            return rows;
        }

        var nonKey = columns.Where(c => !key.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
        string keyJoin = string.Join(" AND ", key.Select(c => $"o.{Q(c)} IS b.{Q(c)}"));
        string basePresent = hasRowid && !isNew ? "b.rowid IS NOT NULL" : $"b.{Q(key[0])} IS NOT NULL";
        string differences = nonKey.Count == 0 ? "0" :
            string.Join(" OR ", nonKey.Select(c => $"NOT (o.{Q(c)} IS b.{Q(c)})"));
        string selectKey = string.Join(",", key.Select(c => $"o.{Q(c)}"));
        string selectDisplay = string.Join(",", display.Select(c => $"o.{Q(c)}"));
        string flags = isNew ? "" : ",CASE WHEN " + basePresent + " THEN 1 ELSE 0 END" +
            (nonKey.Count == 0 ? "" : "," + string.Join(",", nonKey.Select(c =>
                $"CASE WHEN o.{Q(c)} IS b.{Q(c)} THEN 0 ELSE 1 END")));
        string sqlRows = $"SELECT {selectKey}{(selectDisplay.Length > 0 ? "," + selectDisplay : "")}{flags} " +
                         $"FROM ov.{quotedTable} o" +
                         (isNew ? "" : $" LEFT JOIN main.{quotedTable} b ON {keyJoin} WHERE NOT ({basePresent}) OR ({differences})");
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = sqlRows;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                object?[] values = Enumerable.Range(0, key.Count)
                    .Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray();
                int flagIndex = key.Count + display.Count;
                bool conflict = !isNew && reader.GetInt64(flagIndex) == 1;
                var changed = conflict ? nonKey.Where((_, i) => reader.GetInt64(flagIndex + 1 + i) == 1).ToArray()
                                       : Array.Empty<string>();
                string label = string.Join(", ", key.Select((c, i) => $"{c}={ShortValue(values[i])}")) +
                               DisplaySuffix(reader, key.Count, display);
                rows.Add(new ModMergeRow(table, values, null, conflict, label, changed));
            }
        }
        return rows;
    }

    private static string DisplaySuffix(SqliteDataReader reader, int offset, List<string> names) => names.Count == 0 ? "" :
        "  ·  " + string.Join(", ", names.Select((name, i) => $"{name}={ShortValue(reader.IsDBNull(offset + i) ? null : reader.GetValue(offset + i))}"));

    private static string ShortValue(object? value) => value switch
    {
        null => "NULL",
        byte[] blob => $"<blob {blob.Length} bytes>",
        _ => Convert.ToString(value)?.Length > 48 ? Convert.ToString(value)![..48] + "…" : Convert.ToString(value) ?? ""
    };

    public static int RunSelected(ModMergePreview preview, IEnumerable<ModMergeRow> selectedRows,
                                  string outSqlite, Action<string>? log = null)
        => RunSelected(preview, selectedRows, Array.Empty<string>(), outSqlite, log);

    public static int RunSelected(ModMergePreview preview, IEnumerable<ModMergeRow> selectedRows,
                                  IEnumerable<string> replaceTables, string outSqlite,
                                  Action<string>? log = null)
    {
        var selected = selectedRows.Distinct().ToList();
        var replacementNames = new HashSet<string>(replaceTables, StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0 && replacementNames.Count == 0)
            throw new InvalidOperationException("Select at least one donor row or table replacement.");
        var known = preview.Tables.SelectMany(t => t.Rows).ToHashSet();
        if (selected.Any(r => !known.Contains(r)))
            throw new InvalidOperationException("A selected row does not belong to this preview.");
        var knownTables = preview.Tables.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (replacementNames.Any(name => !knownTables.Contains(name)))
            throw new InvalidOperationException("A replacement table does not belong to this preview.");
        string output = Path.GetFullPath(outSqlite);
        if (File.Exists(output) || string.Equals(output, preview.BasePath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(output, preview.OverlayPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a new output path; source databases are never overwritten.");
        if (FileHash(preview.BasePath) != preview.BaseHash || FileHash(preview.OverlayPath) != preview.OverlayHash)
            throw new InvalidOperationException("A source database changed after the preview. Reopen the merge picker.");

        string partial = output + ".partial." + Guid.NewGuid().ToString("N");
        int written = 0;
        int removed = 0;
        try
        {
            // SQLite backup captures a consistent snapshot, including committed WAL pages.
            using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = preview.BasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            using (var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = partial, Pooling = false }.ToString()))
            {
                source.Open(); snapshot.Open(); source.BackupDatabase(snapshot);
            }
            using (var con = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = partial, Pooling = false }.ToString()))
            {
                con.Open();
                Exec(con, "PRAGMA foreign_keys=OFF;");
                using (var foreignKeys = con.CreateCommand())
                {
                    foreignKeys.CommandText = "PRAGMA foreign_keys;";
                    if (Convert.ToInt32(foreignKeys.ExecuteScalar()) != 0)
                        throw new InvalidOperationException("Could not disable SQLite foreign-key enforcement for table replacement.");
                }
                Exec(con, $"ATTACH DATABASE '{SqlPath(preview.OverlayPath)}' AS ov;");
                using (var tx = con.BeginTransaction())
                {
                    foreach (var table in preview.Tables)
                    {
                        if (replacementNames.Contains(table.Name))
                        {
                            var rebuilt = RebuildTableFromDonor(con, tx, table);
                            written += rebuilt.Written;
                            removed += rebuilt.Removed;
                            log?.Invoke($"{table.Name}: dropped and rebuilt from donor schema, " +
                                        $"{rebuilt.Written:n0} donor row(s) copied");
                            continue;
                        }

                        var chosen = selected.Where(r => r.Table == table.Name).ToList();
                        if (chosen.Count == 0) continue;
                        if (table.IsNew)
                        {
                            string createSql = ObjectSql(con, "ov", "table", table.Name)
                                ?? throw new InvalidDataException($"Missing donor schema for {table.Name}.");
                            Exec(con, createSql, tx);
                        }
                        foreach (var row in chosen) written += ApplySelectedRow(con, tx, table, row);
                        if (table.IsNew)
                        {
                            foreach (string indexSql in ObjectSqls(con, "ov", "index", table.Name))
                                Exec(con, indexSql, tx);
                            foreach (string triggerSql in ObjectSqls(con, "ov", "trigger", table.Name))
                                Exec(con, triggerSql, tx);
                        }
                        log?.Invoke($"{table.Name}: {chosen.Count} selected, {written} total row(s) written");
                    }
                    tx.Commit();
                    using var check = con.CreateCommand(); check.CommandText = "PRAGMA quick_check;";
                    if (!string.Equals(Convert.ToString(check.ExecuteScalar()), "ok", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The merged SQLite database did not pass quick_check.");
                    log?.Invoke($"selected mod merge complete: {written:n0} row(s) written" +
                                (removed > 0 ? $", {removed:n0} old row(s) removed by rebuilt replacements" : ""));
                }
                Exec(con, "DETACH DATABASE ov;");
            }
            File.Move(partial, output);
            return written;
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
            SqliteConnection.ClearAllPools();
        }
    }

    private static (int Written, int Removed) RebuildTableFromDonor(
        SqliteConnection con, SqliteTransaction tx, ModMergeTable table)
    {
        string createSql = ObjectSql(con, "ov", "table", table.Name)
            ?? throw new InvalidDataException($"Missing donor schema for {table.Name}.");
        int removed = 0;
        if (!table.IsNew)
        {
            using (var count = con.CreateCommand())
            {
                count.Transaction = tx;
                count.CommandText = $"SELECT COUNT(*) FROM main.{Q(table.Name)};";
                removed = Convert.ToInt32(count.ExecuteScalar());
            }
            Exec(con, $"DROP TABLE main.{Q(table.Name)};", tx);
        }

        // Recreate from the donor's CREATE TABLE statement so column constraints,
        // primary keys, WITHOUT ROWID/STRICT flags, and defaults all match it.
        Exec(con, createSql, tx);
        string columns = string.Join(",", table.Columns.Select(Q));
        bool copyRowId = table.HasRowid && !table.Columns.Any(c =>
            c.Equals("rowid", StringComparison.OrdinalIgnoreCase) ||
            c.Equals("oid", StringComparison.OrdinalIgnoreCase) ||
            c.Equals("_rowid_", StringComparison.OrdinalIgnoreCase));
        string copiedColumns = copyRowId ? $"rowid,{columns}" : columns;
        int written;
        using (var insert = con.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = $"INSERT INTO main.{Q(table.Name)} ({copiedColumns}) " +
                                 $"SELECT {copiedColumns} FROM ov.{Q(table.Name)};";
            written = insert.ExecuteNonQuery();
        }

        // Build these after the data copy. This preserves the donor schema without
        // firing donor triggers while the existing rows are being restored.
        foreach (string indexSql in ObjectSqls(con, "ov", "index", table.Name))
            Exec(con, indexSql, tx);
        foreach (string triggerSql in ObjectSqls(con, "ov", "trigger", table.Name))
            Exec(con, triggerSql, tx);

        using (var verify = con.CreateCommand())
        {
            verify.Transaction = tx;
            verify.CommandText = $"SELECT " +
                $"(SELECT COUNT(*) FROM main.{Q(table.Name)})," +
                $"(SELECT COUNT(*) FROM ov.{Q(table.Name)})," +
                $"EXISTS(SELECT 1 FROM (SELECT {copiedColumns} FROM main.{Q(table.Name)} " +
                    $"EXCEPT SELECT {copiedColumns} FROM ov.{Q(table.Name)}) LIMIT 1)," +
                $"EXISTS(SELECT 1 FROM (SELECT {copiedColumns} FROM ov.{Q(table.Name)} " +
                    $"EXCEPT SELECT {copiedColumns} FROM main.{Q(table.Name)}) LIMIT 1);";
            using var reader = verify.ExecuteReader();
            if (!reader.Read() || reader.GetInt64(0) != reader.GetInt64(1) ||
                reader.GetInt64(2) != 0 || reader.GetInt64(3) != 0)
                throw new InvalidDataException($"Exact replacement verification failed for {table.Name}.");
        }
        return (written, removed);
    }

    private static int ApplySelectedRow(SqliteConnection con, SqliteTransaction tx, ModMergeTable table, ModMergeRow row)
    {
        string cols = string.Join(",", table.Columns.Select(Q));
        string source = $"ov.{Q(table.Name)}";
        string target = $"main.{Q(table.Name)}";
        string where = table.PrimaryKey.Length > 0
            ? string.Join(" AND ", table.PrimaryKey.Select((c, i) => $"{Q(c)} IS $k{i}"))
            : "rowid=$rowid";
        string sql;
        if (table.PrimaryKey.Length > 0)
        {
            string conflict = string.Join(",", table.PrimaryKey.Select(Q));
            var updates = table.Columns.Where(c => !table.PrimaryKey.Contains(c, StringComparer.OrdinalIgnoreCase));
            string onConflict = updates.Any()
                ? $"DO UPDATE SET {string.Join(",", updates.Select(c => $"{Q(c)}=excluded.{Q(c)}"))}"
                : "DO NOTHING";
            sql = $"INSERT INTO {target} ({cols}) SELECT {cols} FROM {source} WHERE {where} " +
                  $"ON CONFLICT({conflict}) {onConflict};";
        }
        else
        {
            string same = string.Join(" AND ", table.Columns.Select(c => $"b.{Q(c)} IS o.{Q(c)}"));
            sql = table.IsNew
                ? $"INSERT INTO {target} ({cols}) SELECT {cols} FROM {source} WHERE {where};"
                : $"INSERT INTO {target} ({cols}) SELECT {cols} FROM {source} o WHERE o.{where} " +
                  $"AND NOT EXISTS (SELECT 1 FROM {target} b WHERE {same});";
        }
        using var cmd = con.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        if (table.PrimaryKey.Length > 0)
            for (int i = 0; i < row.KeyValues.Length; i++)
                cmd.Parameters.AddWithValue($"$k{i}", row.KeyValues[i] ?? DBNull.Value);
        else cmd.Parameters.AddWithValue("$rowid", row.OverlayRowId!.Value);
        int affected = cmd.ExecuteNonQuery();
        if (affected == 0 && table.PrimaryKey.Length > 0)
            throw new InvalidDataException($"Donor row disappeared from {table.Name}; restart the preview.");
        return affected;
    }
}
