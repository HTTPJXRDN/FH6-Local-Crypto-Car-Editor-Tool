using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace FH6LocalCryptoTool.GarageViewer;

public sealed record GarageCarDefinition(long Id, string MediaName, long Year, bool RemovalLocked)
{
    public string Manufacturer { get; init; } = "Unknown manufacturer";
    readonly string _friendlyName = FH6CarEditor.Naming.Friendly(MediaName);
    public string Name => _friendlyName;
    public string DisplayName => $"{Year} {Name}";
    public override string ToString() => DisplayName;
}

/// <summary>Read-only snapshot of a GameDB; names use the Car Editor's exact formatter.</summary>
public sealed class GarageCarCatalog : IDisposable
{
    readonly string _path;
    readonly SqliteConnection _db;
    readonly Dictionary<string, HashSet<string>> _columns = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<GarageCarDefinition> Cars { get; }
    public string Label { get; }

    GarageCarCatalog(string path, string label)
    {
        _path = path; Label = label; _db = Open(path, SqliteOpenMode.ReadOnly);
        try {
            foreach (var table in Read("SELECT name FROM sqlite_master WHERE type='table' AND (name IN ('Data_Car','List_CarMake') OR name LIKE 'List_Upgrade%')")) {
                string name = (string)table["name"];
                _columns[name] = Read("PRAGMA table_info(" + Q(name) + ")").Select(c => (string)c["name"]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            if (!Has("Data_Car", "Id") || !Has("Data_Car", "MediaName") || !Has("Data_Car", "Year") ||
                !Has("List_UpgradeCarBody", "IsStock")) throw new InvalidDataException("Choose a complete FH6 GameDB catalog, not a car-only export or profile save.");
            if (!Has("Data_Car", "NotAvailableInAutoshow"))
                throw new InvalidDataException("This catalog does not have the supported FH6 schema. Motorsport stock parts must not be added to an FH6 save.");
            if (Read("PRAGMA application_id").Any(r => L(r.Values.First()) == 0x46483643))
                throw new InvalidDataException("Choose a complete FH6 GameDB, not a sparse car export.");
            string locked = Has("Data_Car", "DoNotAllowRemovalFromGarage") ? "COALESCE(DoNotAllowRemovalFromGarage,0)" : "0";
            string drive = Has("Data_Car", "IsDrivable") ? "WHERE IsDrivable=1" : "";
            var makes = new Dictionary<long, string>();
            if (Has("Data_Car", "MakeID") && Has("List_CarMake", "ID")) {
                foreach (var make in Read("SELECT * FROM List_CarMake")) {
                    string display = Convert.ToString(make.GetValueOrDefault("DisplayName")) ?? "";
                    string icon = Convert.ToString(make.GetValueOrDefault("IconPathBase")) ?? "";
                    string code = Convert.ToString(make.GetValueOrDefault("ManufacturerCode")) ?? "";
                    string name = icon.Length > 0 ? icon : code;
                    name = System.Text.RegularExpressions.Regex.Replace(name, "([a-z0-9])([A-Z])", "$1 $2");
                    name = System.Text.RegularExpressions.Regex.Replace(name, "([A-Z])([A-Z][a-z])", "$1 $2");
                    string friendly = FH6CarEditor.Naming.Friendly(code);
                    if (friendly.Length > 0 && friendly != code) name = friendly;
                    if (display.Length > 0 && !display.StartsWith("_&")) name = display;
                    makes[L(make["ID"])] = name.Length > 0 ? name : "Unknown manufacturer";
                }
            }
            string makeId = Has("Data_Car", "MakeID") ? "MakeID" : "0";
            Cars = Read($"SELECT Id,MediaName,Year,{locked} locked,{makeId} makeId FROM Data_Car {drive}")
                .Select(c => new GarageCarDefinition(L(c["Id"]), Convert.ToString(c["MediaName"])!, L(c["Year"]), L(c["locked"]) != 0) {
                    Manufacturer = makes.GetValueOrDefault(L(c["makeId"]), "Unknown manufacturer")
                })
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Year).ToArray();
        } catch { _db.Dispose(); Cleanup(path); throw; }
    }

    public static GarageCarCatalog Embedded()
    {
        string path = NewPath();
        try {
            // Version stamps are opaque IDs, not sortable release numbers. Explicitly
            // designate the newest verified clean FH6 catalog, retaining older baselines.
            using var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("FH6LocalCryptoTool.gamedbRC.stock.661353983.sqlite.gz")
                ?? throw new InvalidDataException("The embedded FH6 car catalog is missing.");
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using (var output = new FileStream(path, FileMode.CreateNew)) gzip.CopyTo(output);
            return new(path, "Embedded FH6 catalog (682 models; build 661353983)");
        } catch { Cleanup(path); throw; }
    }

    public static GarageCarCatalog Load(string source)
    {
        string path = NewPath();
        try {
            using var materialized = GameDbSqliteBridge.Materialize(source);
            using (var input = Open(materialized.SqlitePath, SqliteOpenMode.ReadOnly))
            using (var output = Open(path, SqliteOpenMode.ReadWriteCreate)) input.BackupDatabase(output);
            materialized.VerifySourceUnchanged();
            return new(path, Path.GetFileName(source));
        } catch { Cleanup(path); throw; }
    }

    internal static string Q(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    internal static long L(object value) => value is DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    internal static SqliteConnection Open(string path, SqliteOpenMode mode) {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());
        try { db.Open(); return db; } catch { db.Dispose(); throw; }
    }
    internal static void Cleanup(string path) {
        foreach (string file in new[] { path, path + "-wal", path + "-shm", path + "-journal" }) try { File.Delete(file); } catch { }
    }
    static string NewPath() => Path.Combine(FH6LocalCryptoTool.TempWorkspace.Root, $"forza_garage_catalog_{Environment.ProcessId}_{Guid.NewGuid():N}.sqlite");
    bool Has(string table, string field) => _columns.TryGetValue(table, out var columns) && columns.Contains(field);
    List<Dictionary<string, object>> Read(string sql, params object[] values) {
        using var cmd = _db.CreateCommand(); cmd.CommandText = sql;
        for (int i = 0; i < values.Length; i++) cmd.Parameters.AddWithValue("$p" + i, values[i]);
        using var r = cmd.ExecuteReader(); var rows = new List<Dictionary<string, object>>();
        while (r.Read()) rows.Add(Enumerable.Range(0, r.FieldCount).ToDictionary(r.GetName, r.GetValue, StringComparer.OrdinalIgnoreCase));
        return rows;
    }
    Dictionary<string, object>? Stock(string table, string key, long value) {
        if (!Has(table, key) || !Has(table, "IsStock")) return null;
        var rows = Read($"SELECT * FROM {Q(table)} WHERE {Q(key)}=$p0 AND IsStock=1 ORDER BY Id", value);
        if (rows.Count > 1) throw new InvalidDataException($"Ambiguous stock parts in {table} for {value}; no car was added.");
        return rows.SingleOrDefault();
    }

    internal static readonly (string Field, string Table)[] CarParts = {
        ("Engine","List_UpgradeEngine"),("Motor","List_UpgradeMotor"),("Drivetrain","List_UpgradeDrivetrain"),
        ("CarBody","List_UpgradeCarBody"),("Brakes","List_UpgradeBrakes"),("SpringDamper","List_UpgradeSpringDamper"),
        ("AntiSwayFront","List_UpgradeAntiSwayFront"),("AntiSwayRear","List_UpgradeAntiSwayRear"),
        ("TireCompound","List_UpgradeTireCompound"),("RearWing","List_UpgradeRearWing"),
        ("RimSizeFront","List_UpgradeRimSizeFront"),("RimSizeRear","List_UpgradeRimSizeRear")
    };
    internal static readonly (string Field, string Suffix)[] EngineParts = {
        ("Camshaft","Camshaft"),("Valves","Valves"),("Displacement","Displacement"),("PistonsCompression","PistonsCompression"),
        ("FuelSystem","FuelSystem"),("Ignition","Ignition"),("Exhaust","Exhaust"),("Intake","Intake"),
        ("Flywheel","Flywheel"),("Manifold","Manifold"),("RestrictorPlate","RestrictorPlate"),("OilCooling","OilCooling"),
        ("SingleTurbo","TurboSingle"),("TwinTurbo","TurboTwin"),("QuadTurbo","TurboQuad"),
        ("SuperchargerCSC","SuperchargerCSC"),("SuperchargerDSC","SuperchargerDSC"),("Intercooler","Intercooler")
    };
    internal static readonly (string Field, string Suffix)[] BodyParts = {
        ("FrontBumper","FrontBumper"),("RearBumper","RearBumper"),("Hood","Hood"),("SideSkirts","SideSkirt"),
        ("TireWidthFront","TireWidthFront"),("TireWidthRear","TireWidthRear"),("WeightReduction","Weight"),
        ("ChassisStiffness","ChassisStiffness"),("TrackSpacingFront","TrackSpacingFront"),("TrackSpacingRear","TrackSpacingRear"),
        ("FrontAspectRatio","TireAspectRatioFront"),("RearAspectRatio","TireAspectRatioRear")
    };
    internal static readonly string[] DriveParts = { "Clutch", "Transmission", "Driveline", "Differential" };

    public Dictionary<string, object> StockConfiguration(long carId)
    {
        if (!Cars.Any(c => c.Id == carId)) throw new InvalidOperationException("The selected car is not in this catalog's drivable car list.");
        var car = Read("SELECT * FROM Data_Car WHERE Id=$p0", carId).Single();
        var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["CarId"] = carId };
        foreach (string field in new[] { "PerformanceIndex","ClassID","SpeedRating","OffroadRating","AccelerationRating","LaunchRating","BrakingRating","HandlingRating","CurbWeight","WeightDistribution","AspirationTypeId","SimPeakPower","SimPeakAngVel","SimPeakTorque","SimPeakTorqueAngVel","SimRedlineAngVel","Traction_Road","Traction_OffRoad","Traction_Snow","Thumbnail" })
            values[field] = car.GetValueOrDefault(field, DBNull.Value);
        values["TopSpeed"] = car.GetValueOrDefault("SimTopSpeed", DBNull.Value);
        values["TireBrand"] = DBNull.Value; // stock garage entries inherit the model's brand
        values["WheelStyle"] = values["WheelStyleRear"] = -1L;
        values["PeakIntakePSI"] = car.GetValueOrDefault("PeakIntakePSI", 0d); // observed untuned stock cache
        var linked = new Dictionary<string, Dictionary<string, object>>();
        foreach (var (field, table) in CarParts) {
            var part = Stock(table, "Ordinal", carId);
            values[field] = part?.GetValueOrDefault("Id") ?? -1L;
            if (part != null) linked[field] = part;
        }
        if (!linked.ContainsKey("CarBody") || !linked.ContainsKey("Drivetrain") ||
            !linked.ContainsKey("Engine") && !linked.ContainsKey("Motor"))
            throw new InvalidDataException("This car has no complete stock body/drivetrain/powertrain configuration in the catalog. Load the matching full GameDB or duplicate an existing garage entry instead.");
        long body = L(linked["CarBody"]["CarBodyID"]);
        long drive = L(linked["Drivetrain"]["DrivetrainID"]);
        long engine = linked.TryGetValue("Engine", out var eng) ? L(eng["EngineID"]) : -1;
        long motor = linked.TryGetValue("Motor", out var mot) ? L(mot["MotorID"]) : -1;
        foreach (var (field, suffix) in EngineParts) values[field] = engine < 0 ? -1L : Stock("List_UpgradeEngine" + suffix, "EngineID", engine)?.GetValueOrDefault("Id") ?? -1L;
        foreach (var (field, suffix) in BodyParts) values[field] = Stock("List_UpgradeCarBody" + suffix, "CarBodyID", body)?.GetValueOrDefault("Id") ?? -1L;
        foreach (string field in DriveParts) values[field] = Stock("List_UpgradeDrivetrain" + field, "DrivetrainID", drive)?.GetValueOrDefault("Id") ?? -1L;
        values["MotorParts"] = motor < 0 ? -1L : Stock("List_UpgradeMotorParts", "MotorID", motor)?.GetValueOrDefault("Id") ?? -1L;
        return values;
    }
    public void Dispose() { _db.Dispose(); Cleanup(_path); }
}
