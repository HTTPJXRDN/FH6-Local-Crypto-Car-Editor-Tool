using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace FH6LocalCryptoTool;

public sealed record UpdateDbMergePreview(string UpdatedPath, string ModdedPath,
    string UpdatedHash, string ModdedHash, int NewRows, int ChangedRows,
    IReadOnlyList<string> KeylessTables, IReadOnlyList<string> Warnings,
    IReadOnlyDictionary<string, int> AddedByTable);

/// <summary>
/// The intentionally simple update workflow: start with the clean new DB and
/// overlay the old modded DB. Matching modded rows win; new-only update rows stay.
/// It does not use the embedded stock reference.
/// </summary>
public static class UpdateDbMerge
{
    private static readonly string[] KeepUpdatedTables = { "VersionInfo", "CarPartPositions" };
    private static string Q(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    private static string SqlPath(string path) => Path.GetFullPath(path).Replace("'", "''");

    public static UpdateDbMergePreview Preview(string updated, string modded)
    {
        updated = Path.GetFullPath(updated); modded = Path.GetFullPath(modded);
        if (updated.Equals(modded, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The updated and modded databases must be different files.");
        if (!File.Exists(updated) || !File.Exists(modded))
            throw new FileNotFoundException("An update-merge input was not found.");
        var preview = Merge.PreviewMods(updated, modded);
        var keyless = preview.Tables.Where(t => t.PrimaryKey.Length == 0 && t.Rows.Count > 0 &&
                                                !t.Name.Equals("CarPartPositions", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Name).ToList();
        return new UpdateDbMergePreview(updated, modded, Hash(updated), Hash(modded),
            preview.Tables.Sum(t => t.AddedCount), preview.Tables.Sum(t => t.ConflictCount),
            keyless, preview.Warnings.ToList(),
            preview.Tables.ToDictionary(t => t.Name, t => t.AddedCount, StringComparer.OrdinalIgnoreCase));
    }

    public static void Run(UpdateDbMergePreview preview, string outputPath, Action<string>? log = null)
    {
        string output = Path.GetFullPath(outputPath);
        if (File.Exists(output) || output.Equals(preview.UpdatedPath, StringComparison.OrdinalIgnoreCase) ||
            output.Equals(preview.ModdedPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a new output path. Neither input is overwritten.");
        if (Hash(preview.UpdatedPath) != preview.UpdatedHash || Hash(preview.ModdedPath) != preview.ModdedHash)
            throw new InvalidOperationException("An input DB changed after preview. Start the update merge again.");
        if (preview.Warnings.Count > 0)
            throw new InvalidDataException("Some tables have incompatible schemas; no partial update merge will be written: " +
                                           string.Join("; ", preview.Warnings.Take(3)));

        string partial = output + ".partial." + Guid.NewGuid().ToString("N");
        try
        {
            Merge.Run(preview.UpdatedPath, preview.ModdedPath, partial, null, log,
                MergeMode.OverlayWins, keepBaseTables: KeepUpdatedTables,
                preserveKeylessNewRows: true);
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = partial, Pooling = false }.ToString()))
            {
                db.Open();
                Exec(db, $"ATTACH DATABASE '{SqlPath(preview.ModdedPath)}' AS ov");
                OverlayCarPartPositions(db, log);
                using var check = db.CreateCommand(); check.CommandText = "PRAGMA quick_check";
                if (!string.Equals(Convert.ToString(check.ExecuteScalar()), "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The updated merged DB failed SQLite quick_check.");
                var resultFk = ForeignKeyCounts(db);
                using var updated = OpenRead(preview.UpdatedPath);
                using var modded = OpenRead(preview.ModdedPath);
                var originalFk = ForeignKeyCounts(updated);
                var moddedFk = ForeignKeyCounts(modded);
                // A donor may have a looser FK declaration than the clean update.
                // The clean schema can report its newly imported rows as warnings
                // even when the donor itself did not. Allow at most one extra
                // warning per newly added donor child row for each FK relation.
                var newWarnings = resultFk.Where(item =>
                {
                    string childTable = item.Key.Split(':')[0];
                    int inherited = originalFk.GetValueOrDefault(item.Key) + moddedFk.GetValueOrDefault(item.Key);
                    return item.Value > inherited + preview.AddedByTable.GetValueOrDefault(childTable);
                }).ToList();
                if (newWarnings.Count > 0)
                    throw new InvalidDataException("The merged DB has new foreign-key warnings (" +
                        string.Join(", ", newWarnings.Take(5).Select(x => x.Key)) + "); no output was saved.");
                Exec(db, "DETACH DATABASE ov");
            }
            if (Hash(preview.UpdatedPath) != preview.UpdatedHash || Hash(preview.ModdedPath) != preview.ModdedHash)
                throw new InvalidOperationException("An input DB changed during the update merge. No output was saved.");
            File.Move(partial, output);
        }
        finally
        {
            try { File.Delete(partial); } catch { }
            SqliteConnection.ClearAllPools();
        }
    }

    private static void OverlayCarPartPositions(SqliteConnection db, Action<string>? log)
    {
        if (!TableExists(db, "main", "CarPartPositions") || !TableExists(db, "ov", "CarPartPositions")) return;
        var baseCols = Columns(db, "main", "CarPartPositions");
        var donorCols = Columns(db, "ov", "CarPartPositions");
        if (!new HashSet<string>(baseCols, StringComparer.OrdinalIgnoreCase).SetEquals(donorCols) ||
            !baseCols.Contains("Ordinal", StringComparer.OrdinalIgnoreCase) ||
            !baseCols.Contains("ID", StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("CarPartPositions schema cannot be merged by car/body safely.");
        string cols = string.Join(",", baseCols.Select(Q));
        using var tx = db.BeginTransaction();
        Exec(db, "DELETE FROM main.CarPartPositions WHERE EXISTS " +
            "(SELECT 1 FROM ov.CarPartPositions d WHERE d.Ordinal=main.CarPartPositions.Ordinal AND d.ID=main.CarPartPositions.ID)", tx);
        int inserted = Exec(db, $"INSERT INTO main.CarPartPositions ({cols}) SELECT {cols} FROM ov.CarPartPositions", tx);
        tx.Commit();
        log?.Invoke($"CarPartPositions: {inserted:n0} modded positions copied by car/body; update-only cars kept.");
    }

    private static SqliteConnection OpenRead(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open(); return db;
    }
    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        string hash = Convert.ToHexString(SHA256.HashData(stream));
        if (File.Exists(path + "-wal"))
        {
            using var wal = File.OpenRead(path + "-wal");
            hash += ":" + Convert.ToHexString(SHA256.HashData(wal));
        }
        return hash;
    }
    private static bool TableExists(SqliteConnection db, string schema, string table)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {schema}.sqlite_master WHERE type='table' AND name=$name";
        cmd.Parameters.AddWithValue("$name", table);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }
    private static List<string> Columns(SqliteConnection db, string schema, string table)
    {
        var result = new List<string>();
        using var cmd = db.CreateCommand(); cmd.CommandText = $"PRAGMA {schema}.table_info({Q(table)})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(1));
        return result;
    }
    private static Dictionary<string, int> ForeignKeyCounts(SqliteConnection db)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA main.foreign_key_check";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string key = $"{reader.GetValue(0)}:{reader.GetValue(2)}:{reader.GetValue(3)}";
            result[key] = result.GetValueOrDefault(key) + 1;
        }
        return result;
    }
    private static int Exec(SqliteConnection db, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }
}
