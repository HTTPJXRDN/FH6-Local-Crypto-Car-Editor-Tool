using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace FH6LocalCryptoTool;

public sealed record WidebodyMergeCar(long Ordinal, string MediaName, int NewKitCount, int DonorKitCount)
{
    public override string ToString() =>
        $"{MediaName}  (car {Ordinal})  -  {NewKitCount} new / {DonorKitCount} donor kit(s)";
}

public sealed record WidebodyMergePreview(
    string BasePath, string DonorPath, string BaseHash, string DonorHash,
    IReadOnlyList<WidebodyMergeCar> Cars);

public static partial class Merge
{
    private static readonly (string Table, string BodyColumn)[] WidebodyPartTables =
    {
        ("List_UpgradeCarBodyChassisStiffness", "CarbodyId"),
        ("List_UpgradeCarBodyFrontBumper", "CarBodyID"),
        ("List_UpgradeCarBodyHood", "CarBodyID"),
        ("List_UpgradeCarBodyRearBumper", "CarBodyID"),
        ("List_UpgradeCarBodySideSkirt", "CarBodyID"),
        ("List_UpgradeCarBodyTireAspectRatioFront", "CarBodyId"),
        ("List_UpgradeCarBodyTireAspectRatioRear", "CarBodyId"),
        ("List_UpgradeCarBodyTireWidthFront", "CarBodyId"),
        ("List_UpgradeCarBodyTireWidthRear", "CarBodyId"),
        ("List_UpgradeCarBodyTrackSpacingFront", "CarBodyId"),
        ("List_UpgradeCarBodyTrackSpacingRear", "CarBodyId"),
        ("List_UpgradeCarBodyWeight", "CarBodyId"),
    };

    public static WidebodyMergePreview PreviewWidebodyCars(string baseSqlite, string donorSqlite)
    {
        string basePath = Path.GetFullPath(baseSqlite), donorPath = Path.GetFullPath(donorSqlite);
        if (string.Equals(basePath, donorPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Base and donor must be different database files.");
        if (!File.Exists(basePath) || !File.Exists(donorPath))
            throw new FileNotFoundException("The base or donor database was not found.");
        string baseHash = FileHash(basePath), donorHash = FileHash(donorPath);
        var cars = new List<WidebodyMergeCar>();
        using var con = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = basePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        con.Open();
        Exec(con, $"ATTACH DATABASE '{SqlPath(donorPath)}' AS ov;");
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT d.Ordinal, COALESCE(c.MediaName, ''),
                                   SUM(CASE WHEN NOT EXISTS
                                       (SELECT 1 FROM main.List_UpgradeCarBody x
                                        WHERE x.Ordinal=d.Ordinal AND x.CarBodyID=d.CarBodyID)
                                       THEN 1 ELSE 0 END), COUNT(*)
                            FROM ov.List_UpgradeCarBody d
                            JOIN ov.Data_Car c ON c.Id=d.Ordinal
                            JOIN main.Data_Car b ON b.Id=d.Ordinal
                            WHERE d.IsStock=0
                            GROUP BY d.Ordinal,c.MediaName ORDER BY c.MediaName";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            cars.Add(new WidebodyMergeCar(reader.GetInt64(0), reader.GetString(1),
                Convert.ToInt32(reader.GetInt64(2)), Convert.ToInt32(reader.GetInt64(3))));
        return new WidebodyMergePreview(basePath, donorPath, baseHash, donorHash, cars);
    }

    public static int RunWidebodyCar(WidebodyMergePreview preview, long carId, string outputPath,
                                      bool overrideConflicts = false,
                                      Action<string>? log = null)
    {
        if (!preview.Cars.Any(car => car.Ordinal == carId))
            throw new InvalidOperationException("The selected car has no donor bodykits in this preview.");
        if (!overrideConflicts && preview.Cars.Single(car => car.Ordinal == carId).NewKitCount == 0)
            throw new InvalidOperationException("This car has no new kit IDs. Check override to import changed rows for existing kits.");
        string output = Path.GetFullPath(outputPath);
        if (File.Exists(output) ||
            string.Equals(output, preview.BasePath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(output, preview.DonorPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a new output path; source databases are never overwritten.");
        if (FileHash(preview.BasePath) != preview.BaseHash || FileHash(preview.DonorPath) != preview.DonorHash)
            throw new InvalidOperationException("A source database changed after car selection. Start the import again.");

        string partial = output + ".partial." + Guid.NewGuid().ToString("N");
        int written = 0;
        try
        {
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
                Exec(con, $"ATTACH DATABASE '{SqlPath(preview.DonorPath)}' AS ov;");
                var previousForeignKeys = ForeignKeyIssues(con);
                var columnsCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                using (var tx = con.BeginTransaction())
                {
                    var kits = new List<(long PartId, long BodyId)>();
                    using (var cmd = con.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = @"SELECT d.Id,d.CarBodyID FROM ov.List_UpgradeCarBody d
                                            WHERE d.Ordinal=$car AND d.IsStock=0 AND
                                              ($override=1 OR NOT EXISTS
                                                (SELECT 1 FROM main.List_UpgradeCarBody b
                                                 WHERE b.Ordinal=d.Ordinal AND b.CarBodyID=d.CarBodyID))
                                            ORDER BY d.CarBodyID";
                        cmd.Parameters.AddWithValue("$car", carId);
                        cmd.Parameters.AddWithValue("$override", overrideConflicts ? 1 : 0);
                        using var reader = cmd.ExecuteReader();
                        while (reader.Read()) kits.Add((reader.GetInt64(0), reader.GetInt64(1)));
                    }
                    if (kits.Count == 0)
                        throw new InvalidOperationException("No new kits remain for this car.");

                    foreach (var (partId, bodyId) in kits)
                    {
                        long index = bodyId - carId * 1000;
                        long blockStart = carId * 1000 + index * 100;
                        if (index is < 1 or > 9 || partId < blockStart || partId >= blockStart + 100)
                            throw new InvalidDataException($"Kit {partId} has an unexpected CarBodyID {bodyId}; import manually.");
                        if (ReadIds(con, tx,
                            "SELECT Ordinal FROM main.List_UpgradeCarBody WHERE CarBodyID=$v AND Ordinal<>$car",
                            bodyId, carId).Count > 0)
                            throw new InvalidDataException($"CarBodyID {bodyId} is already used by another car; override cannot replace it.");
                        written += CopyIdRow(con, tx, columnsCache, "Data_CarBody", "Id", bodyId, required: true, overrideConflicts);
                        written += CopyIdRow(con, tx, columnsCache, "Data_CarBody_DummyAxle", "Id", bodyId, overrideConflicts: overrideConflicts);
                        written += CopyIdRow(con, tx, columnsCache, "List_UpgradeCarBody", "Id", partId, required: true, overrideConflicts);

                        foreach (var (table, bodyColumn) in WidebodyPartTables)
                        {
                            var ids = ReadIds(con, tx, $"SELECT Id FROM ov.{Q(table)} WHERE {Q(bodyColumn)}=$v ORDER BY Id", bodyId);
                            if (ids.Count == 0)
                                throw new InvalidDataException($"Donor kit {partId} is missing required {table} rows.");
                            foreach (long id in ids) written += CopyIdRow(con, tx, columnsCache, table, "Id", id, required: true, overrideConflicts);
                        }

                        var presets = ReadIds(con, tx,
                            "SELECT Id FROM ov.UpgradePresetPackages WHERE Ordinal=$car AND CarBody=$kit ORDER BY Id",
                            carId, partId);
                        foreach (long id in presets)
                            written += CopyIdRow(con, tx, columnsCache, "UpgradePresetPackages", "Id", id, required: true, overrideConflicts);
                        written += CopyCarPartPositions(con, tx, carId, bodyId, overrideConflicts);
                        log?.Invoke($"CarBodyID {bodyId}: imported kit, body data, 12 part categories, " +
                                    $"{presets.Count} preset(s).");
                    }

                    // Rear wings are car-wide in FH6, so bring over every new
                    // donor wing for this selected car, not only kit ID blocks.
                    var wings = ReadIds(con, tx,
                        "SELECT w.Id FROM ov.List_UpgradeRearWing w WHERE w.Ordinal=$car AND " +
                        "($override=1 OR NOT EXISTS (SELECT 1 FROM main.List_UpgradeRearWing b WHERE b.Id=w.Id)) " +
                        "ORDER BY w.Id", carId, overrideConflicts ? 1 : 0);
                    foreach (long id in wings)
                        written += CopyIdRow(con, tx, columnsCache, "List_UpgradeRearWing", "Id", id, required: true, overrideConflicts);
                    log?.Invoke($"Car {carId}: processed {wings.Count} car-wide rear-wing option(s).");

                    // A modded front bumper or wing may refer to custom aero tuning.
                    var aeroIds = new HashSet<long>();
                    foreach (var (_, bodyId) in kits)
                    {
                        foreach (long id in ReadIds(con, tx,
                            "SELECT AeroPhysicsID FROM ov.List_UpgradeCarBodyFrontBumper WHERE CarBodyID=$v", bodyId))
                            aeroIds.Add(id);
                    }
                    foreach (long id in ReadIds(con, tx,
                        "SELECT AeroPhysicsID FROM ov.List_UpgradeRearWing WHERE Ordinal=$car AND Id IN " +
                        "(SELECT Id FROM main.List_UpgradeRearWing WHERE Ordinal=$car)", carId))
                        aeroIds.Add(id);
                    foreach (long id in aeroIds.Where(id => id > 0))
                        written += CopyIdRow(con, tx, columnsCache, "List_AeroPhysics", "AeroPhysicsID", id,
                                             overrideConflicts: overrideConflicts);

                    tx.Commit();
                }
                using (var check = con.CreateCommand())
                {
                    check.CommandText = "PRAGMA quick_check;";
                    if (!string.Equals(Convert.ToString(check.ExecuteScalar()), "ok", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The imported database failed SQLite quick_check.");
                }
                var addedForeignKeys = ForeignKeyIssues(con).Except(previousForeignKeys).ToList();
                if (addedForeignKeys.Count > 0)
                    throw new InvalidDataException($"Import introduced {addedForeignKeys.Count} foreign-key warning(s); no output was saved. Use manual merge for this mod.");
                Exec(con, "DETACH DATABASE ov;");
            }
            File.Move(partial, output);
            log?.Invoke($"Widebody car import complete: {written} row(s) written; base and donor were not changed.");
            return written;
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
            SqliteConnection.ClearAllPools();
        }
    }

    private static List<long> ReadIds(SqliteConnection con, SqliteTransaction tx, string sql, params long[] values)
    {
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        string[] names = { "$v", "$lo", "$hi" };
        if (sql.Contains("$car", StringComparison.Ordinal))
        {
            cmd.Parameters.AddWithValue("$car", sql.Contains("$v", StringComparison.Ordinal) ? values[1] : values[0]);
            if (sql.Contains("$v", StringComparison.Ordinal)) cmd.Parameters.AddWithValue("$v", values[0]);
            if (sql.Contains("$kit", StringComparison.Ordinal)) cmd.Parameters.AddWithValue("$kit", values[1]);
            if (sql.Contains("$override", StringComparison.Ordinal)) cmd.Parameters.AddWithValue("$override", values[1]);
            if (sql.Contains("$lo", StringComparison.Ordinal))
            {
                cmd.Parameters.AddWithValue("$lo", values[1]);
                cmd.Parameters.AddWithValue("$hi", values[2]);
            }
        }
        else
            cmd.Parameters.AddWithValue(names[0], values[0]);
        var ids = new List<long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (!reader.IsDBNull(0)) ids.Add(reader.GetInt64(0));
        return ids;
    }

    private static int CopyIdRow(SqliteConnection con, SqliteTransaction tx,
                                 Dictionary<string, string?> columnsCache, string table,
                                 string idColumn, long id, bool required = false,
                                 bool overrideConflicts = false)
    {
        if (!columnsCache.TryGetValue(table, out string? columns))
        {
            if (!TableNames(con, "ov").Contains(table) || !TableNames(con, "main").Contains(table))
                columnsCache[table] = columns = null;
            else
            {
                var baseColumns = ColumnNames(con, "main", table);
                var donorColumns = ColumnNames(con, "ov", table);
                if (!new HashSet<string>(baseColumns, StringComparer.OrdinalIgnoreCase).SetEquals(donorColumns))
                    throw new InvalidDataException($"{table} has a different schema in base and donor.");
                columnsCache[table] = columns = string.Join(",", baseColumns.Select(Q));
            }
        }
        if (columns is null)
        {
            if (required) throw new InvalidDataException($"Required table {table} is missing in base or donor.");
            return 0;
        }
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.CommandText = $"SELECT COUNT(*) FROM ov.{Q(table)} WHERE {Q(idColumn)}=$id";
        long donorCount = Convert.ToInt64(cmd.ExecuteScalar());
        if (donorCount != 1)
        {
            if (required) throw new InvalidDataException($"Expected one donor {table} row for {id}, found {donorCount}.");
            return 0;
        }
        cmd.CommandText = $"SELECT COUNT(*) FROM main.{Q(table)} WHERE {Q(idColumn)}=$id";
        if (Convert.ToInt64(cmd.ExecuteScalar()) != 0)
        {
            string? ownerColumn = table switch
            {
                "List_UpgradeCarBody" or "List_UpgradeRearWing" or "UpgradePresetPackages" => "Ordinal",
                _ => WidebodyPartTables.FirstOrDefault(entry =>
                    entry.Table.Equals(table, StringComparison.OrdinalIgnoreCase)).BodyColumn
            };
            if (ownerColumn is not null)
            {
                cmd.CommandText = $"SELECT {Q(ownerColumn)} FROM main.{Q(table)} WHERE {Q(idColumn)}=$id";
                object? baseOwner = cmd.ExecuteScalar();
                cmd.CommandText = $"SELECT {Q(ownerColumn)} FROM ov.{Q(table)} WHERE {Q(idColumn)}=$id";
                object? donorOwner = cmd.ExecuteScalar();
                if (Convert.ToInt64(baseOwner) != Convert.ToInt64(donorOwner))
                    throw new InvalidDataException($"{table} ID {id} belongs to a different car/body in the base; override refused.");
            }
            cmd.CommandText = $"SELECT COUNT(*) FROM (SELECT {columns} FROM ov.{Q(table)} WHERE {Q(idColumn)}=$id " +
                              $"EXCEPT SELECT {columns} FROM main.{Q(table)} WHERE {Q(idColumn)}=$id)";
            if (Convert.ToInt64(cmd.ExecuteScalar()) != 0)
            {
                if (overrideConflicts)
                {
                    var updateColumns = ColumnNames(con, "main", table)
                        .Where(name => !name.Equals(idColumn, StringComparison.OrdinalIgnoreCase));
                    cmd.CommandText = $"UPDATE main.{Q(table)} SET " +
                        string.Join(",", updateColumns.Select(name =>
                            $"{Q(name)}=(SELECT {Q(name)} FROM ov.{Q(table)} WHERE {Q(idColumn)}=$id)")) +
                        $" WHERE {Q(idColumn)}=$id";
                    return cmd.ExecuteNonQuery();
                }
                if (required)
                    throw new InvalidDataException($"{table} ID {id} already exists with different values; nothing was overwritten.");
                return 0; // optional shared reference (e.g. stock aero tuning): base wins
            }
            return 0;
        }
        cmd.CommandText = $"INSERT INTO main.{Q(table)} ({columns}) SELECT {columns} FROM ov.{Q(table)} WHERE {Q(idColumn)}=$id";
        return cmd.ExecuteNonQuery();
    }

    private static int CopyCarPartPositions(SqliteConnection con, SqliteTransaction tx,
                                            long carId, long bodyId, bool overrideConflicts)
    {
        if (!TableNames(con, "ov").Contains("CarPartPositions") ||
            !TableNames(con, "main").Contains("CarPartPositions")) return 0;
        var columns = ColumnNames(con, "ov", "CarPartPositions");
        if (!new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase)
                .SetEquals(ColumnNames(con, "main", "CarPartPositions")))
            throw new InvalidDataException("CarPartPositions has a different schema in base and donor.");
        string names = string.Join(",", columns.Select(Q));
        string equality = string.Join(" AND ", columns.Select(name => $"b.{Q(name)} IS o.{Q(name)}"));
        if (overrideConflicts)
        {
            using var remove = con.CreateCommand();
            remove.Transaction = tx;
            remove.CommandText = "DELETE FROM main.CarPartPositions WHERE Ordinal=$car AND ID=$body " +
                                 "AND EXISTS (SELECT 1 FROM ov.CarPartPositions WHERE Ordinal=$car AND ID=$body)";
            remove.Parameters.AddWithValue("$car", carId);
            remove.Parameters.AddWithValue("$body", bodyId);
            remove.ExecuteNonQuery();
        }
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"INSERT INTO main.CarPartPositions ({names}) SELECT {string.Join(",", columns.Select(name => "o." + Q(name)))} " +
                          "FROM ov.CarPartPositions o WHERE o.Ordinal=$car AND o.ID=$body " +
                          $"AND NOT EXISTS (SELECT 1 FROM main.CarPartPositions b WHERE {equality})";
        cmd.Parameters.AddWithValue("$car", carId);
        cmd.Parameters.AddWithValue("$body", bodyId);
        return cmd.ExecuteNonQuery();
    }

    private static HashSet<string> ForeignKeyIssues(SqliteConnection con)
    {
        var issues = new HashSet<string>(StringComparer.Ordinal);
        using var cmd = con.CreateCommand();
        cmd.CommandText = "PRAGMA main.foreign_key_check;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            issues.Add($"{reader.GetString(0)}:{reader.GetValue(1)}:{reader.GetString(2)}:{reader.GetValue(3)}");
        return issues;
    }
}
