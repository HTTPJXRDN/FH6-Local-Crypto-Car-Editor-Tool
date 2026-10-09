using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FH6LocalCryptoTool;

internal sealed record DeltaTable(string Name, string[] Columns, string[] Keys, bool New,
    int Added, int Changed, int Deleted);

/// <summary>Three-way merge: old clean -> old modded changes applied to the clean update.</summary>
internal static class GameDbDelta
{
    private static string Q(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    internal static SqliteConnection Open(string updated, string modded, string baseline)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = updated, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        try
        {
            // Inputs are only queried; all mutations explicitly target main in the private output.
            Attach(db, "ov", modded); Attach(db, "old", baseline);
            return db;
        }
        catch { db.Dispose(); throw; }
    }
    internal static void Attach(SqliteConnection db, string alias, string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("A merge input or clean baseline is missing.", path);
        Exec(db, $"ATTACH DATABASE '{Path.GetFullPath(path).Replace("'", "''")}' AS {alias}");
    }
    internal static List<DeltaTable> Inspect(SqliteConnection db, List<string> warnings)
    {
        var result = new List<DeltaTable>();
        var oldTables = Tables(db, "old"); var donorTables = Tables(db, "ov"); var newTables = Tables(db, "main");
        // A sparse car export is not evidence of deletion of the rest of the game.
        foreach (string missing in oldTables.Except(donorTables, StringComparer.OrdinalIgnoreCase))
            warnings.Add($"{missing}: missing from the modded DB; update merge requires a complete GameDB, not a car export.");
        foreach (string table in donorTables.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
        {
            if (table.Equals("VersionInfo", StringComparison.OrdinalIgnoreCase)) continue;
            bool addedTable = !oldTables.Contains(table, StringComparer.OrdinalIgnoreCase);
            var donor = Info(db, "ov", table); var updated = Info(db, "main", table);
            var old = Info(db, "old", table);
            if ((!addedTable && !Compatible(old, donor)) ||
                (newTables.Contains(table, StringComparer.OrdinalIgnoreCase) && !Compatible(updated, donor)))
            { warnings.Add($"{table}: column sets or primary keys differ; review schema changes before merging."); continue; }
            if (!addedTable && !newTables.Contains(table, StringComparer.OrdinalIgnoreCase))
            { warnings.Add($"{table}: removed by the update; refusing to recreate an old game table automatically."); continue; }
            int added, changed = 0, deleted = 0;
            if (addedTable) added = Count(db, $"SELECT COUNT(*) FROM ov.{Q(table)}");
            else if (donor.Keys.Length > 0)
            {
                string match = Match(donor.Keys, "b", "d");
                string different = Different(donor.Columns.Except(donor.Keys, StringComparer.OrdinalIgnoreCase), "b", "d");
                added = Count(db, $"SELECT COUNT(*) FROM ov.{Q(table)} d WHERE NOT EXISTS (SELECT 1 FROM old.{Q(table)} b WHERE {match})");
                changed = Count(db, $"SELECT COUNT(*) FROM ov.{Q(table)} d JOIN old.{Q(table)} b ON {match} WHERE {different}");
                deleted = Count(db, $"SELECT COUNT(*) FROM old.{Q(table)} b WHERE NOT EXISTS (SELECT 1 FROM ov.{Q(table)} d WHERE {match})");
            }
            else
            {
                var rows = ContentDelta(db, table, donor.Columns);
                added = rows.Sum(r => Math.Max(0, r.Difference));
                deleted = rows.Sum(r => Math.Max(0, -r.Difference));
            }
            result.Add(new(table, donor.Columns, donor.Keys, addedTable, added, changed, deleted));
        }
        return result;
    }
    private static bool Compatible((string[] Columns, string[] Keys) a, (string[] Columns, string[] Keys) b) =>
        a.Columns.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b.Columns) &&
        a.Keys.SequenceEqual(b.Keys, StringComparer.OrdinalIgnoreCase);
    private static string Match(IEnumerable<string> keys, string a, string b) =>
        string.Join(" AND ", keys.Select(k => $"{a}.{Q(k)} IS {b}.{Q(k)}"));
    private static string Different(IEnumerable<string> cols, string a, string b) =>
        string.Join(" OR ", cols.Select(k => $"{a}.{Q(k)} IS NOT {b}.{Q(k)}").DefaultIfEmpty("0"));

    internal static void Apply(SqliteConnection db, IReadOnlyList<DeltaTable> tables, Action<string>? log)
    {
        using var tx = db.BeginTransaction();
        foreach (var t in tables.Where(t => t.Added + t.Changed + t.Deleted > 0 || t.New))
        {
            string target = $"main.{Q(t.Name)}", donor = $"ov.{Q(t.Name)}", old = $"old.{Q(t.Name)}";
            string cols = string.Join(",", t.Columns.Select(Q));
            if (t.New && !Tables(db, "main").Contains(t.Name, StringComparer.OrdinalIgnoreCase))
            {
                using var cmd = db.CreateCommand(); cmd.Transaction = tx;
                cmd.CommandText = "SELECT sql FROM ov.sqlite_master WHERE type='table' AND name=$name";
                cmd.Parameters.AddWithValue("$name", t.Name);
                Exec(db, Convert.ToString(cmd.ExecuteScalar())!, tx);
                Exec(db, $"INSERT INTO {target} ({cols}) SELECT {cols} FROM {donor}", tx);
                foreach (string kind in new[] { "index", "trigger" })
                {
                    using var objects = db.CreateCommand(); objects.Transaction = tx;
                    objects.CommandText = "SELECT sql FROM ov.sqlite_master WHERE type=$type AND tbl_name=$name AND sql IS NOT NULL";
                    objects.Parameters.AddWithValue("$type", kind); objects.Parameters.AddWithValue("$name", t.Name);
                    var definitions = new List<string>();
                    using (var reader = objects.ExecuteReader()) while (reader.Read()) definitions.Add(reader.GetString(0));
                    foreach (string definition in definitions) Exec(db, definition, tx);
                }
            }
            else if (t.Keys.Length > 0)
            {
                string keyBd = Match(t.Keys, "b", "d"), keyMb = Match(t.Keys, "m", "b"), keyMd = Match(t.Keys, "m", "d");
                var nonkeys = t.Columns.Except(t.Keys, StringComparer.OrdinalIgnoreCase).ToArray();
                if (!t.New)
                {
                    Exec(db, $"DELETE FROM {target} AS m WHERE EXISTS (SELECT 1 FROM {old} b WHERE {keyMb} AND NOT EXISTS (SELECT 1 FROM {donor} d WHERE {keyBd}))", tx);
                    // Only user-edited columns win. Official edits to untouched columns survive.
                    string changed = Different(nonkeys, "b", "d");
                    if (t.Changed > 0 && nonkeys.Length > 0)
                    {
                        string set = string.Join(",", nonkeys.Select(c => $"{Q(c)}=(SELECT CASE WHEN b.{Q(c)} IS NOT d.{Q(c)} THEN d.{Q(c)} ELSE m.{Q(c)} END FROM {donor} d JOIN {old} b ON {keyBd} WHERE {keyMd})"));
                        Exec(db, $"UPDATE {target} AS m SET {set} WHERE EXISTS (SELECT 1 FROM {donor} d JOIN {old} b ON {keyBd} WHERE {keyMd} AND ({changed}))", tx);
                        // Preserve a user-edited row even if the official update removed that key.
                        Exec(db, $"INSERT INTO {target} ({cols}) SELECT {string.Join(",", t.Columns.Select(c => "d." + Q(c)))} FROM {donor} d JOIN {old} b ON {keyBd} WHERE ({changed}) AND NOT EXISTS (SELECT 1 FROM {target} m WHERE {keyMd})", tx);
                    }
                }
                // User-added keys may also exist in the update: donor wins that deliberate conflict.
                string conflict = nonkeys.Length == 0 ? "DO NOTHING" : "DO UPDATE SET " + string.Join(",", nonkeys.Select(c => $"{Q(c)}=excluded.{Q(c)}"));
                Exec(db, $"INSERT INTO {target} ({cols}) SELECT {string.Join(",", t.Columns.Select(c => "d." + Q(c)))} FROM {donor} d WHERE " +
                    (t.New ? "true" : $"NOT EXISTS (SELECT 1 FROM {old} b WHERE {keyBd})") +
                    $" ON CONFLICT({string.Join(",", t.Keys.Select(Q))}) {conflict}", tx);
            }
            else
            {
                // Keyless tables use exact content and multiplicity, never unrelated rowids.
                var rows = t.New ? ContentRows(db, "ov", t.Name, t.Columns).Select(r => new ContentChange(r.Values, r.Count)).ToList()
                                 : ContentDelta(db, t.Name, t.Columns);
                foreach (var row in rows)
                {
                    using var cmd = db.CreateCommand(); cmd.Transaction = tx;
                    string where = string.Join(" AND ", t.Columns.Select((c, i) => $"{Q(c)} IS $p{i}"));
                    for (int i = 0; i < row.Values.Length; i++) cmd.Parameters.AddWithValue("$p" + i, row.Values[i] ?? DBNull.Value);
                    if (row.Difference < 0)
                    {
                        cmd.CommandText = $"DELETE FROM {target} WHERE rowid IN (SELECT rowid FROM {target} WHERE {where} LIMIT $count)";
                        cmd.Parameters.AddWithValue("$count", -row.Difference); cmd.ExecuteNonQuery();
                    }
                    else
                    {
                        cmd.CommandText = $"INSERT INTO {target} ({cols}) VALUES ({string.Join(",", t.Columns.Select((_, i) => "$p" + i))})";
                        for (int i = 0; i < row.Difference; i++) cmd.ExecuteNonQuery();
                    }
                }
            }
            log?.Invoke($"{t.Name}: {t.Added:n0} additions, {t.Changed:n0} edited rows, {t.Deleted:n0} baseline deletions applied");
        }
        tx.Commit();
    }

    private sealed record ContentChange(object?[] Values, int Difference);
    private sealed record ContentRow(object?[] Values, int Count);
    private static List<ContentRow> ContentRows(SqliteConnection db, string schema, string table, string[] cols)
    {
        using var cmd = db.CreateCommand();
        string list = string.Join(",", cols.Select(Q));
        cmd.CommandText = $"SELECT {list},COUNT(*) FROM {schema}.{Q(table)} GROUP BY {list}";
        var rows = new List<ContentRow>(); using var reader = cmd.ExecuteReader();
        while (reader.Read()) rows.Add(new(Enumerable.Range(0, cols.Length).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray(), reader.GetInt32(cols.Length)));
        return rows;
    }
    private static List<ContentChange> ContentDelta(SqliteConnection db, string table, string[] cols)
    {
        // Type-tagged JSON keeps NULL, blobs, numbers and text distinct; SQLite groups duplicates.
        string Identity(object?[] values) => JsonSerializer.Serialize(values.Select(v =>
            v is null ? new[] { "null", "" } : new[] { v.GetType().FullName!, v is byte[] blob ? Convert.ToBase64String(blob) : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)! }));
        var rows = new Dictionary<string, ContentChange>(StringComparer.Ordinal);
        foreach (var row in ContentRows(db, "old", table, cols)) rows[Identity(row.Values)] = new(row.Values, -row.Count);
        foreach (var row in ContentRows(db, "ov", table, cols))
        {
            string key = Identity(row.Values); rows[key] = new(row.Values, rows.GetValueOrDefault(key)?.Difference + row.Count ?? row.Count);
        }
        return rows.Values.Where(r => r.Difference != 0).ToList();
    }
    private static (string[] Columns, string[] Keys) Info(SqliteConnection db, string schema, string table)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = $"PRAGMA {schema}.table_info({Q(table)})";
        using var reader = cmd.ExecuteReader(); var cols = new List<string>(); var keys = new SortedDictionary<int, string>();
        while (reader.Read()) { cols.Add(reader.GetString(1)); int order = reader.GetInt32(5); if (order > 0) keys[order] = reader.GetString(1); }
        return (cols.ToArray(), keys.Values.ToArray());
    }
    private static List<string> Tables(SqliteConnection db, string schema)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = $"SELECT name FROM {schema}.sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        using var reader = cmd.ExecuteReader(); var result = new List<string>(); while (reader.Read()) result.Add(reader.GetString(0)); return result;
    }
    private static int Count(SqliteConnection db, string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt32(cmd.ExecuteScalar()); }
    internal static int Exec(SqliteConnection db, string sql, SqliteTransaction? tx = null)
    { using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; return cmd.ExecuteNonQuery(); }
}
