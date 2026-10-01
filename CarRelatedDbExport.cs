using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace FH6LocalCryptoTool;

// A self-contained, single-car donor for merge/import, not a game-ready GameDB.
public static class CarRelatedDbExport
{
    public const int ApplicationId = 0x46483643; // FH6C: single-car export
    private static readonly HashSet<string> RequiredImportTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "Data_Car", "Data_CarBody", "List_UpgradeCarBody", "List_UpgradeRearWing",
        "List_UpgradeCarBodyChassisStiffness", "List_UpgradeCarBodyFrontBumper",
        "List_UpgradeCarBodyHood", "List_UpgradeCarBodyRearBumper",
        "List_UpgradeCarBodySideSkirt", "List_UpgradeCarBodyTireAspectRatioFront",
        "List_UpgradeCarBodyTireAspectRatioRear", "List_UpgradeCarBodyTireWidthFront",
        "List_UpgradeCarBodyTireWidthRear", "List_UpgradeCarBodyTrackSpacingFront",
        "List_UpgradeCarBodyTrackSpacingRear", "List_UpgradeCarBodyWeight",
        "UpgradePresetPackages"
    };
    public sealed record Car(long Id, string Name, int Year)
    {
        public override string ToString() => $"{Name} ({Year})  ·  ID {Id}";
    }
    public sealed record Result(string Path, int Tables, long Rows, IReadOnlyList<string> Warnings);

    private static string Q(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    private static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

    public static IReadOnlyList<Car> ListCars(string sourcePath)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT Id, COALESCE(MediaName, DisplayName, 'Car'), COALESCE(Year, 0) FROM Data_Car ORDER BY MediaName, Year, Id";
        using var reader = cmd.ExecuteReader();
        var cars = new List<Car>();
        while (reader.Read())
            cars.Add(new Car(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2)));
        return cars;
    }

    public static Car ReadSingleCarDonor(string path)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        db.Open();
        if (Scalar(db, "PRAGMA application_id") != ApplicationId)
            throw new InvalidDataException("Import car DB needs a file made by the updated Export Car Related DB. Re-export the car from its full GameDB first.");
        if (Scalar(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('Data_Car','List_UpgradeCarBody')") != 2)
            throw new InvalidDataException("The car export is missing required car/body tables. Re-export it from the full GameDB.");
        var cars = ListCars(path);
        if (cars.Count != 1)
            throw new InvalidDataException("Import car DB requires exactly one car in the donor file.");
        if (Scalar(db, "SELECT COUNT(*) FROM List_UpgradeCarBody WHERE Ordinal=" + cars[0].Id + " AND IsStock=1") != 1)
            throw new InvalidDataException("The donor is missing the selected car's stock body. Re-export it from the full GameDB.");
        return cars[0];
    }

    public static Result Export(string sourcePath, long carId, string outputPath)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        outputPath = Path.GetFullPath(outputPath);
        if (string.Equals(sourcePath, outputPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The car-related donor cannot overwrite its source database.");
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Source database was not found.", sourcePath);
        if (File.Exists(outputPath)) throw new IOException("The output file already exists. Choose a new name.");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        string tempPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Result result;
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = tempPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
            }.ToString()))
            {
                db.Open();
                Execute(db, "ATTACH DATABASE " + Literal(sourcePath) + " AS src");
                if (Scalar(db, "SELECT COUNT(*) FROM src.Data_Car WHERE Id=" + carId) != 1)
                    throw new InvalidOperationException("That car is no longer present in the staged database.");
                Execute(db, "PRAGMA foreign_keys=OFF");
                Execute(db, $"PRAGMA application_id={ApplicationId}");
                Execute(db, "PRAGMA user_version=1");

                var bodies = Ids(db, "SELECT CarBodyID FROM src.List_UpgradeCarBody WHERE Ordinal=" + carId);
                var engines = Ids(db, "SELECT EngineID FROM src.List_UpgradeEngine WHERE Ordinal=" + carId);
                var motors = Ids(db, "SELECT MotorID FROM src.List_UpgradeMotor WHERE Ordinal=" + carId);
                var drivetrains = Ids(db, "SELECT DrivetrainID FROM src.List_UpgradeDrivetrain WHERE Ordinal=" + carId);
                var compounds = Ids(db, "SELECT TireCompoundID FROM src.List_UpgradeTireCompound WHERE Ordinal=" + carId);
                var tireParts = Ids(db, "SELECT Id FROM src.List_UpgradeTireCompound WHERE Ordinal=" + carId);
                var aero = Ids(db, "SELECT AeroPhysicsID FROM src.List_UpgradeRearWing WHERE Ordinal=" + carId);
                aero.UnionWith(Ids(db, $"SELECT AeroPhysicsID FROM src.{Q("List_UpgradeCarBodyFrontBumper")} WHERE CarBodyID IN ({In(bodies)})"));
                var springs = Ids(db, "SELECT FrontSpringDamperPhysicsID FROM src.List_UpgradeSpringDamper WHERE Ordinal=" + carId);
                springs.UnionWith(Ids(db, "SELECT RearSpringDamperPhysicsID FROM src.List_UpgradeSpringDamper WHERE Ordinal=" + carId));
                var antiSway = Ids(db, "SELECT AntiSwayPhysicsID FROM src.List_UpgradeAntiSwayFront WHERE Ordinal=" + carId);
                antiSway.UnionWith(Ids(db, "SELECT AntiSwayPhysicsID FROM src.List_UpgradeAntiSwayRear WHERE Ordinal=" + carId));
                var wheelIds = Ids(db, "SELECT StockWheelID FROM src.Data_Car WHERE Id=" + carId);

                var tableSql = new List<(string Name, string Sql)>();
                using (var cmd = db.CreateCommand())
                {
                    cmd.CommandText = "SELECT name,sql FROM src.sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND sql IS NOT NULL ORDER BY name";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read()) tableSql.Add((reader.GetString(0), reader.GetString(1)));
                }
                var warnings = new List<string>();
                var parts = new HashSet<long>();
                var partStrings = new HashSet<long>();
                foreach (var (table, _) in tableSql)
                {
                    if (!table.StartsWith("List_Upgrade", StringComparison.OrdinalIgnoreCase)) continue;
                    var columns = Columns(db, "src", table);
                    string? scope = Scope(table, columns, carId, bodies, engines, motors, drivetrains,
                        compounds, tireParts, aero, springs, antiSway, wheelIds, parts, partStrings);
                    if (scope is null) continue;
                    if (columns.Contains("Id", StringComparer.OrdinalIgnoreCase))
                        parts.UnionWith(Ids(db, $"SELECT Id FROM src.{Q(table)} WHERE {scope}"));
                    if (columns.Contains("PartsStringId", StringComparer.OrdinalIgnoreCase))
                        partStrings.UnionWith(Ids(db, $"SELECT PartsStringId FROM src.{Q(table)} WHERE {scope}"));
                }
                int tableCount = 0;
                long rowCount = 0;
                Execute(db, "BEGIN");
                foreach (var (table, createSql) in tableSql)
                {
                    if (table.StartsWith("NewProfile_", StringComparison.OrdinalIgnoreCase)) continue;
                    var columns = Columns(db, "src", table);
                    string? scope = Scope(table, columns, carId, bodies, engines, motors, drivetrains,
                        compounds, tireParts, aero, springs, antiSway, wheelIds, parts, partStrings);
                    if (scope is null) continue;
                    string select = SelectScoped(table, columns, scope);
                    long count = Scalar(db, "SELECT COUNT(*) FROM (" + select + ")");
                    // Focused import queries these tables even if this particular
                    // car has no matching rows. Preserve their empty schemas.
                    if (count == 0 && !RequiredImportTables.Contains(table)) continue;
                    Execute(db, createSql);
                    Execute(db, $"INSERT INTO {Q(table)} ({string.Join(",", columns.Select(Q))}) {select}");
                    tableCount++;
                    rowCount += count;
                }
                Execute(db, "COMMIT");
                if (rowCount == 0)
                    throw new InvalidOperationException("No car-related rows were found in the source database.");
                using var check = db.CreateCommand();
                check.CommandText = "PRAGMA quick_check";
                if (!string.Equals(Convert.ToString(check.ExecuteScalar()), "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The exported donor database failed SQLite quick_check.");
                result = new Result(outputPath, tableCount, rowCount, warnings);
            }
            File.Move(tempPath, outputPath);
            return result;
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    private static string? Scope(string table, List<string> columns, long carId,
        HashSet<long> bodies, HashSet<long> engines, HashSet<long> motors, HashSet<long> drivetrains,
        HashSet<long> compounds, HashSet<long> tireParts, HashSet<long> aero,
        HashSet<long> springs, HashSet<long> antiSway, HashSet<long> wheels, HashSet<long> parts,
        HashSet<long> partStrings)
    {
        bool Has(string col) => columns.Contains(col, StringComparer.OrdinalIgnoreCase);
        if (table == "Data_Car") return $"Id={carId}";
        if (table is "Data_CarBody" or "Data_CarBody_DummyAxle") return Has("Id") ? $"Id IN ({In(bodies)})" : null;
        if (table == "CarTrackOffsets") return Has("Id") ? $"Id={carId}" : null;
        if (table == "CarPartNames") return Has("Id") && parts.Count > 0 ? $"Id IN ({In(parts)})" : null;
        if (table == "List_PartsStrings") return Has("Id") && partStrings.Count > 0 ? $"Id IN ({In(partStrings)})" : null;
        if (table == "Data_Engine") return Has("EngineID") ? $"EngineID IN ({In(engines)})" : null;
        if (table == "Data_Motor") return Has("MotorID") ? $"MotorID IN ({In(motors)})" : null;
        if (table == "Data_Drivetrain") return Has("DrivetrainID") ? $"DrivetrainID IN ({In(drivetrains)})" : null;
        if (table == "List_TireCompound") return Has("TireCompoundID") ? $"TireCompoundID IN ({In(compounds)})" : null;
        if (table == "List_Wheels") return Has("Id") ? $"Id IN ({In(wheels)})" : null;
        if (table == "List_UpgradeTireCompoundFictionModOverride")
            return Has("PartId") ? $"PartId IN ({In(tireParts)})" : null;
        if (table == "List_AeroPhysics") return Has("Ordinal")
            ? $"Ordinal={carId} OR AeroPhysicsID IN ({In(aero)})" : $"AeroPhysicsID IN ({In(aero)})";
        if (table == "List_SpringDamperPhysics") return Has("Ordinal")
            ? $"Ordinal={carId} OR SpringDamperPhysicsID IN ({In(springs)})" : $"SpringDamperPhysicsID IN ({In(springs)})";
        if (table == "List_AntiSwayPhysics") return Has("Ordinal")
            ? $"Ordinal={carId} OR AntiSwayPhysicsID IN ({In(antiSway)})" : $"AntiSwayPhysicsID IN ({In(antiSway)})";
        if (table.StartsWith("List_UpgradeEngine", StringComparison.OrdinalIgnoreCase) && Has("EngineID") && !Has("Ordinal"))
            return $"EngineID IN ({In(engines)})";
        if (table.StartsWith("List_UpgradeMotor", StringComparison.OrdinalIgnoreCase) && Has("MotorID") && !Has("Ordinal"))
            return $"MotorID IN ({In(motors)})";
        if (table.StartsWith("List_UpgradeDrivetrain", StringComparison.OrdinalIgnoreCase) && Has("DrivetrainID") && !Has("Ordinal"))
            return $"DrivetrainID IN ({In(drivetrains)})";
        if (table.StartsWith("List_UpgradeCarBody", StringComparison.OrdinalIgnoreCase) && Has("CarBodyID") && !Has("Ordinal"))
            return $"CarBodyID IN ({In(bodies)})";
        if (Has("Ordinal")) return $"Ordinal={carId}";
        if (Has("CarId")) return $"{Q(columns.First(c => c.Equals("CarId", StringComparison.OrdinalIgnoreCase)))}={carId}";
        return null;
    }

    private static string SelectScoped(string table, List<string> columns, string scope)
    {
        string projection = string.Join(",", columns.Select(Q));
        return $"SELECT {projection} FROM src.{Q(table)} WHERE {scope}";
    }
    private static string In(HashSet<long> ids) => ids.Count == 0 ? "NULL" : string.Join(",", ids.Order());
    private static HashSet<long> Ids(SqliteConnection db, string sql)
    {
        var values = new HashSet<long>();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (!reader.IsDBNull(0)) values.Add(Convert.ToInt64(reader.GetValue(0)));
        return values;
    }
    private static List<string> Columns(SqliteConnection db, string schema, string table)
    {
        var result = new List<string>();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"PRAGMA {schema}.table_info({Literal(table)})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(1));
        return result;
    }
    private static long Scalar(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
    private static void Execute(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
