#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace FH6CarEditor;

record CarItem(long Id, string Media, long Year, string Type)
{ public override string ToString() => $"{Naming.Friendly(Media)}   ({Year})   {Type}"; }

record EngItem(long Id, string Media, string Name, double Pw, bool Rotary, bool Diesel, string EngType)
{ public override string ToString() => $"{Name}   —   {Naming.Friendly(Media)}{(Rotary ? "   ROTOR" : Diesel ? "   DIESEL" : "")}"; }

record MotorItem(long Id, string Media, string Name, double Pw, int Upg, double MaxScale)
{ public override string ToString() => $"{Naming.Friendly(Media)}{(string.IsNullOrWhiteSpace(Name) ? "" : "  (" + Name + ")")}   ~{Pw:0}   {(Upg > 0 ? $"▲{Upg} ×{MaxScale:0.##}" : "no upgrades")}"; }

record PowerPartTarget(long Id, string Name, bool IsStock)
{ public override string ToString() => $"{(IsStock ? "STOCK" : "SWAP")}  ·  {Name}  (ID {Id})"; }

record TurboScaleTarget(string Table, long Id, long Level, double Scale0, double Scale1);

record DbFieldSnapshot(string Table, string KeyColumn, object KeyValue, Dictionary<string, object> Values);
record DbCreatedRow(string Table, string KeyColumn, object KeyValue);
sealed class HandlingSnapshot
{
    public List<DbFieldSnapshot> Fields { get; } = new();
    public List<DbCreatedRow> Created { get; } = new();
}

// Display-only friendly names built from a car's MediaName code (e.g. "POR_MissionR_22" -> "Porsche Mission R").
// Never written to the DB — purely for how the tool lists cars/engines/motors.
static class Naming
{
    static readonly Dictionary<string, string> Makes = new()
    {
        {"343","Halo"},{"AC","AC"},{"ACU","Acura"},{"AH","Austin-Healey"},{"ALF","Alfa Romeo"},{"APO","Apollo"},
        {"ARI","Ariel"},{"AST","Aston Martin"},{"AUD","Audi"},{"AZM","Autozam"},{"BAC","BAC"},{"BEN","Bentley"},
        {"BMW","BMW"},{"BUI","Buick"},{"CAD","Cadillac"},{"CAN","Can-Am"},{"CHE","Chevrolet"},{"CHR","Chrysler"},
        {"DAT","Datsun"},{"DEL","DeLorean"},{"DET","De Tomaso"},{"DOD","Dodge"},{"EXO","Exomotive"},{"FER","Ferrari"},
        {"FIA","Fiat"},{"FOR","Ford"},{"FUN","Funco"},{"GMA","Gordon Murray"},{"GMC","GMC"},{"HEN","Hennessey"},
        {"HOL","Holden"},{"HON","Honda"},{"HYU","Hyundai"},{"JAG","Jaguar"},{"JEE","Jeep"},{"JIM","Jimco"},
        {"KOE","Koenigsegg"},{"KTM","KTM"},{"LAM","Lamborghini"},{"LAN","Lancia"},{"LEX","Lexus"},{"LIN","Lincoln"},
        {"LOT","Lotus"},{"LR","Land Rover"},{"LUC","Lucid"},{"MAS","Maserati"},{"MAZ","Mazda"},{"MCL","McLaren"},
        {"MER","Mercedes-Benz"},{"MEY","Meyers Manx"},{"MG","MG"},{"MIN","Mini"},{"MIT","Mitsubishi"},{"NIS","Nissan"},
        {"NOB","Noble"},{"NUL","Test Car"},{"OPE","Opel"},{"PAG","Pagani"},{"PEE","Peel"},{"PEN","Penhall"},
        {"PEU","Peugeot"},{"PG","Playground"},{"PLY","Plymouth"},{"POL","Polaris"},{"PON","Pontiac"},{"POR","Porsche"},
        {"RAD","Radical"},{"RAM","RAM"},{"REL","Reliant"},{"REN","Renault"},{"RIM","Rimac"},{"RIV","Rivian"},
        {"RJ","RJ Anderson"},{"SAL","Saleen"},{"SHE","Shelby"},{"SIE","Sierra"},{"SUB","Subaru"},{"TOY","Toyota"},
        {"TVR","TVR"},{"ULT","Ultima"},{"VIP","SRT"},{"VOL","Volvo"},{"VW","Volkswagen"},{"WUL","Wuling"},{"ZEN","Zenvo"},
    };
    static string Spacify(string s)
    {
        s = System.Text.RegularExpressions.Regex.Replace(s, "(?<=[a-z0-9])(?=[A-Z])", " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, "(?<=[A-Z])(?=[A-Z][a-z])", " ");
        return s;
    }
    public static string Friendly(string media)
    {
        if (string.IsNullOrWhiteSpace(media)) return media ?? "";
        var parts = media.Split('_');
        string make = parts[0];
        var rest = parts.Skip(1).ToList();
        if (rest.Count > 0 && System.Text.RegularExpressions.Regex.IsMatch(rest[^1], @"^\d{1,2}$")) rest.RemoveAt(rest.Count - 1);
        string model = string.Join(" ", rest.Select(Spacify)).Trim();
        string makeName = Makes.TryGetValue(make, out var v) ? v : make;
        return (makeName + " " + model).Trim();
    }

    // CylinderID -> cylinder count (from List_Cylinders)
    static readonly Dictionary<long, int> Cyls = new()
    { {1,4},{2,5},{3,6},{4,8},{5,10},{6,12},{7,16},{8,2},{9,3},{10,0},{11,3},{12,4},{13,2},{14,1} };
    // ConfigID (List_EngineConfig): 1=V, 2=W, 3=Inline, 4=Rotary, 5=Flat
    public static string EngineType(long configId, long cylId, bool rotary)
    {
        if (rotary || configId == 4) return "Rotary";
        int n = Cyls.TryGetValue(cylId, out var c) ? c : 0;
        return configId switch
        {
            1 => "V" + n,
            2 => "W" + n,
            3 => "I" + n,
            5 => "Flat " + n,
            _ => n > 0 ? n + "-cyl" : "Other",
        };
    }
    // sort key so the type dropdown reads I…, V…, Flat…, W…, Rotary — each by cylinder count
    public static (int, int) TypeSortKey(string t)
    {
        int rank = t.StartsWith("I") ? 0 : t.StartsWith("V") ? 1 : t.StartsWith("Flat") ? 2 : t.StartsWith("W") ? 3 : t == "Rotary" ? 5 : 4;
        var m = System.Text.RegularExpressions.Regex.Match(t, @"\d+");
        return (rank, m.Success ? int.Parse(m.Value) : 0);
    }
}

public partial class CarEditorView : UserControl
{
    // ---- data ----
    SqliteConnection _conn;
    string _origPath, _workPath;
    readonly HashSet<string> _motorMedia = new();   // fallback for cars absent from the embedded stock reference
    HashSet<long> _referenceCars = new();           // car Ids whose original powertrain is known from stock
    HashSet<long> _originalEvCars = new();          // stock cars that originally had an electric motor
    HashSet<long> _electricSpecCars = new();         // Data_Car's current EV configuration
    HashSet<long> _bodykitPresetCars = new();         // preset package points at a non-stock car-body row
    readonly HashSet<long> _modifiedCars = new();     // cars that differ from the embedded clean stock reference
    Dictionary<long, long> _loadedBaseCosts = new(); // exact price snapshot for the Revert prices button
    readonly Dictionary<long, HandlingSnapshot> _handlingSnapshots = new();
    List<CarItem> _cars = new();
    List<EngItem> _engines = new();
    List<MotorItem> _motors = new();
    CarItem _car;
    long? _powerTargetCarId;
    bool _updatingPowerTargets;
    string _stockReferencePath;
    bool _stockReferenceAttached;
    bool _batchRunning;
    bool _updatingCarSelection;

    const string StockReferenceResource = "FH6LocalCryptoTool.gamedbRC.stock.sqlite.gz";
    static readonly string[] CarOrdinalTables =
    {
        "List_UpgradeAntiSwayFront", "List_UpgradeAntiSwayRear", "List_UpgradeBrakes",
        "List_UpgradeCarBody", "List_UpgradeDrivetrain", "List_UpgradeEngine", "List_UpgradeMotor",
        "List_UpgradeRearWing", "List_UpgradeRimSizeFront", "List_UpgradeRimSizeRear",
        "List_UpgradeSpringDamper", "List_UpgradeTireCompound", "UpgradePresetPackages"
    };
    static readonly (string Table, string Column)[] BodyTables =
    {
        ("List_UpgradeCarBodyChassisStiffness", "CarbodyId"),
        ("List_UpgradeCarBodyFrontBumper", "CarBodyID"), ("List_UpgradeCarBodyHood", "CarBodyID"),
        ("List_UpgradeCarBodyRearBumper", "CarBodyID"), ("List_UpgradeCarBodySideSkirt", "CarBodyID"),
        ("List_UpgradeCarBodyTireAspectRatioFront", "CarBodyId"), ("List_UpgradeCarBodyTireAspectRatioRear", "CarBodyId"),
        ("List_UpgradeCarBodyTireWidthFront", "CarBodyId"), ("List_UpgradeCarBodyTireWidthRear", "CarBodyId"),
        ("List_UpgradeCarBodyTrackSpacingFront", "CarBodyId"), ("List_UpgradeCarBodyTrackSpacingRear", "CarBodyId"),
        ("List_UpgradeCarBodyWeight", "CarBodyId")
    };
    static readonly string[] EnginePowerTables =
    {
        "List_UpgradeEngineCamshaft", "List_UpgradeEngineTurboSingle",
        "List_UpgradeEngineTurboTwin", "List_UpgradeEngineTurboQuad"
    };

    // Real handling modifiers plus the matching garage-rating values. Geometry,
    // curb weight, drivetrain and tire dimensions stay specific to the target car.
    static readonly string[] HandlingColumns =
    {
        "FixListingRearFricScale", "FixListingNormSlip0", "FixListingNormSlip1",
        "FixListingSteerAngle0", "FixListingSteerAngle1",
        "BodyAeroVerticalDrag", "BodyAeroLateralDragFront", "BodyAeroLateralDragRear",
        "BodyAeroForwardDownforceFront", "BodyAeroForwardDownforceRear",
        "BodyAeroAngleZeroDownforce", "BodyAeroWIForceScale",
        "TireAeroHackShouldApply", "TireAeroHackMinSpringLoad", "TireAeroHackMaxSpringLoad",
        "SimBrakeDistance100MPH", "SimLatGees60MPH", "SimLatGees120MPH", "SimBrakeDistance60MPH",
        "HandlingRating", "BrakingRating", "GameDownforceScale", "GameDownforceScaleOffroad",
        "LatFrontScalarMax", "LatFrontScalarClamp", "LatRearScalarMax", "LatRearScalarClamp",
        "LongAccelFrontScalarMax", "LongAccelFrontScalarClamp", "LongAccelRearScalarMax", "LongAccelRearScalarClamp",
        "LongBrakeFrontScalarMax", "LongBrakeFrontScalarClamp", "LongBrakeRearScalarMax", "LongBrakeRearScalarClamp",
        "FrontDownforceClampKG", "RearDownforceClampKG", "Traction_Road", "Traction_OffRoad", "Traction_Snow"
    };
    static readonly string[] SpringHandlingColumns =
    {
        "UseProgressiveSpring", "DefSpringRate", "MinSpringRate", "MaxSpringRate",
        "DefDampenBumpRate", "MinDampenBumpRate", "MaxDampenBumpRate",
        "MinDampenBumpRateHighVelocity", "MaxDampenBumpRateHighVelocity", "DampenBumpHighVelocityThreshold",
        "MinDampenBumpRateSuperHighVelocity", "MaxDampenBumpRateSuperHighVelocity", "DampenBumpSuperHighVelocityThreshold",
        "DefDampenReboundRate", "MinDampenReboundRate", "MaxDampenReboundRate",
        "MinDampenReboundRateHighVelocity", "MaxDampenReboundRateHighVelocity", "DampenReboundHighVelocityThreshold",
        "MinDampenReboundRateSuperHighVelocity", "MaxDampenReboundRateSuperHighVelocity", "DampenReboundSuperHighVelocityThreshold",
        "StaticToe", "StaticCamber", "Caster", "BumpStopSpringK", "BumpStopSpringKMax",
        "BumpStopBumpD", "BumpStopBumpDMax", "BumpStopReboundD", "BumpStopReboundDMax",
        "BumpStopPowerK", "BumpStopPowerBump", "BumpStopPowerReb", "BumpStopGForceClamp",
        "RollCentreHeight", "AntiGeometryPercent", "UseBlowOffDamper"
    };
    static readonly HashSet<string> MassScaledSpringColumns = new()
    {
        "DefSpringRate", "MinSpringRate", "MaxSpringRate",
        "DefDampenBumpRate", "MinDampenBumpRate", "MaxDampenBumpRate",
        "MinDampenBumpRateHighVelocity", "MaxDampenBumpRateHighVelocity",
        "MinDampenBumpRateSuperHighVelocity", "MaxDampenBumpRateSuperHighVelocity",
        "DefDampenReboundRate", "MinDampenReboundRate", "MaxDampenReboundRate",
        "MinDampenReboundRateHighVelocity", "MaxDampenReboundRateHighVelocity",
        "MinDampenReboundRateSuperHighVelocity", "MaxDampenReboundRateSuperHighVelocity",
        "BumpStopSpringK", "BumpStopSpringKMax", "BumpStopBumpD", "BumpStopBumpDMax",
        "BumpStopReboundD", "BumpStopReboundDMax"
    };
    static readonly string[] AntiSwayHandlingColumns =
        { "DefSwaybarStiffness", "MinSwaybarStiffness", "MaxSwaybarStiffness", "SwaybarDamping", "BeamStiffness" };
    static readonly string[] WeightHandlingColumns = { "CMHeight", "CMBackFront" };
    static readonly string[] ChassisHandlingColumns =
        { "CMHeightDiff", "FrontLatFrictionScale", "RearLatFrictionScale", "FrontLongFrictionScale", "RearLongFrictionScale" };
    static readonly string[] TireHandlingColumns = { "TireCompoundID", "FrontTirePressure", "RearTirePressure" };
    static readonly string[] TireOverrideColumns =
        { "FrontLatFrictionMult", "FrontLongFrictionMult", "RearLatFrictionMult", "RearLongFrictionMult" };

    const double EnhancedTireLateralGrip = 1.18;
    const double EnhancedTireLongitudinalGrip = 1.12;
    const double EnhancedSpringRate = 1.15;
    const double EnhancedDamperRate = 1.10;
    const double EnhancedAntiRollRate = 1.20;
    const double EnhancedAeroForce = 1.12;
    const double EnhancedDownforceScale = 1.15;

    // ---- log brushes ----
    static readonly Brush BrPink  = Freeze(Color.FromRgb(232, 23, 93));
    static readonly Brush BrPink2 = Freeze(Color.FromRgb(204, 82, 122));
    static readonly Brush BrDim   = Freeze(Color.FromRgb(168, 167, 167));
    static readonly Brush BrRed   = Freeze(Color.FromRgb(240, 96, 96));
    static readonly Brush BrAmber = Freeze(Color.FromRgb(232, 176, 72));
    static Brush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public CarEditorView()
    {
        InitializeComponent();
        LogBox.Document = new FlowDocument { PagePadding = new Thickness(0) };
        PrepareEmbeddedStockReference();
        UpdateFilterLabel();
        Log("Ready. Load a database to begin.", "info");
    }

    // ============================================================ log
    void Log(string m, string cls = "info")
    {
        var (pre, brush) = cls switch
        {
            "ok"   => ("✓ ", BrPink),
            "err"  => ("✗ ", BrRed),
            "warn" => ("! ", BrAmber),
            _      => ("· ", BrDim),
        };
        var p = new Paragraph { Margin = new Thickness(0) };
        p.Inlines.Add(new Run(DateTime.Now.ToString("HH:mm:ss") + "  ") { Foreground = BrPink2 });
        p.Inlines.Add(new Run(pre + m) { Foreground = brush });
        LogBox.Document.Blocks.Add(p);
        LogBox.ScrollToEnd();
    }

    // ============================================================ UI events
    void CarSearch_Changed(object s, TextChangedEventArgs e)
    {
        CarSearchPh.Visibility = string.IsNullOrEmpty(CarSearch.Text) ? Visibility.Visible : Visibility.Collapsed;
        RenderCars();
    }
    void EngSearch_Changed(object s, TextChangedEventArgs e)
    {
        EngSearchPh.Visibility = string.IsNullOrEmpty(EngSearch.Text) ? Visibility.Visible : Visibility.Collapsed;
        RenderEngines();
    }
    void CarList_Changed(object s, SelectionChangedEventArgs e)
    {
        if (!_updatingCarSelection && !_batchRunning) PickCar();
    }
    void Filt_Changed(object s, RoutedEventArgs e)
    {
        UpdateFilterLabel();
        if (FiltModified?.IsChecked == true && _stockReferenceAttached) RefreshModifiedCarsFromReference();
        RenderCars();
    }
    void UpdateFilterLabel()
    {
        if (FilterToggle == null) return;
        var on = new List<string>();
        if (FiltICE?.IsChecked == true) on.Add("ICE");
        if (FiltEV?.IsChecked == true) on.Add("EV");
        if (FiltConv?.IsChecked == true) on.Add("EV→ICE");
        if (FiltIceEv?.IsChecked == true) on.Add("ICE→EV");
        string types = on.Count == 4 ? "All types" : on.Count == 0 ? "None" : string.Join(", ", on);
        FilterToggle.Content = types +
            (FiltBodykit?.IsChecked == true ? " · Bodykits" : "") +
            (FiltModified?.IsChecked == true ? " · Modified" : "");
    }
    void MakeFilter_Changed(object s, SelectionChangedEventArgs e) => RenderEngines();
    void Filter_Changed(object s, RoutedEventArgs e) => RenderEngines();

    void LoadDb_Click(object s, RoutedEventArgs e) => LoadDb();
    void Reload_Click(object s, RoutedEventArgs e) => ReloadOriginal();
    void Export_Click(object s, RoutedEventArgs e) => ExportDb();
    void SetStock_Click(object s, RoutedEventArgs e)
    {
        if (MotorMode)
        {
            if (SelMot() is MotorItem m) RunForSelectedCars("set stock motor", () => ConvertToElectric(m.Id));
            else Log("pick a motor first", "warn");
        }
        else RunForSelectedCars("set stock engine", SetStockEngine);
    }
    void AddSwap_Click(object s, RoutedEventArgs e)
    {
        if (MotorMode)
        {
            if (SelMot() is MotorItem m) RunForSelectedCars("add motor option", () => AddMotorOption(m.Id));
            else Log("pick a motor first", "warn");
        }
        else if (SelEng() is EngItem en)
            RunForSelectedCars("add engine swap", () => { AddEngineSwap(en.Id, false); RefreshCar(); });
    }
    void Mode_Changed(object s, SelectionChangedEventArgs e)
    {
        if (EngList == null) return;   // fires once during init before controls exist
        bool mot = MotorMode;
        EngCardTitle.Text = mot ? "MOTOR" : "ENGINE";
        BtnSetStock.Content = mot ? "⚡  Set as STOCK motor" : "★  Set as STOCK engine";
        BtnAddSwap.Content = mot ? "＋  Add as motor option" : "＋  Add as swap";
        MakeRow.Visibility = EngBulkRow.Visibility = mot ? Visibility.Collapsed : Visibility.Visible;
        MotorBulkRow.Visibility = mot ? Visibility.Visible : Visibility.Collapsed;
        EngSearchPh.Text = mot ? "search motors: Nevera, Taycan, Evija…" : "search engines: V10, Porsche, 2JZ, Diesel…";
        EngHint.Text = mot
            ? "Set as STOCK motor makes the car an EV with the selected motor. Add as motor option adds it as a selectable swap. Drivetrain swaps still apply."
            : "“Set as STOCK” on an EV auto-converts it to combustion: copies the engine spec, drops the electric motor, fits the donor’s gearbox.";
        EngList.SelectedIndex = -1;
        RenderEngines();
    }
    void AddMake_Click(object s, RoutedEventArgs e) => RunForSelectedCars("add engines by make", AddAllMake);
    void AddType_Click(object s, RoutedEventArgs e) => RunForSelectedCars("add engines by type", AddAllType);
    void AddAll_Click(object s, RoutedEventArgs e) => RunForSelectedCars("add every engine", AddAllEngines);
    void AddAllMotors_Click(object s, RoutedEventArgs e) => RunForSelectedCars("add every motor", AddAllMotors);
    void Apply_Click(object s, RoutedEventArgs e) => RunForSelectedCars("apply checked options", ApplyOptions);
    void AddFe_Click(object s, RoutedEventArgs e) => SetFeCarsAutoshowAvailability(true);
    void RemoveFe_Click(object s, RoutedEventArgs e) => SetFeCarsAutoshowAvailability(false);
    void AllPriceOne_Click(object s, RoutedEventArgs e) => SetAllCarPricesToOne();
    void RevertPrices_Click(object s, RoutedEventArgs e) => RevertAllCarPrices();
    void BestHandling_Click(object s, RoutedEventArgs e) => RunForSelectedCars("apply enhanced handling", ApplyBestHandling);
    void RevertHandling_Click(object s, RoutedEventArgs e) => RunForSelectedCars("revert enhanced handling", RevertHandling);
    void ApplyPower_Click(object s, RoutedEventArgs e) => ApplyPowerBuilder();
    void RestoreCar_Click(object s, RoutedEventArgs e) => RestoreSelectedCar();

    List<CarItem> SelectedCars()
    {
        var selected = CarList?.SelectedItems.Cast<CarItem>().ToList() ?? new List<CarItem>();
        if (selected.Count == 0 && _car != null) selected.Add(_car);
        return selected;
    }

    void RunForSelectedCars(string actionName, Action action, bool confirm = true)
    {
        var targets = SelectedCars();
        if (targets.Count == 0) { Log("pick at least one car first", "warn"); return; }
        if (targets.Count == 1) { action(); return; }
        if (confirm && MessageBox.Show($"{actionName} for all {targets.Count} selected cars?\n\n" +
                                       "The current controls are used as the batch template. This includes rim size, tire width, tire profile, track width, " +
                                       "Drift/Rally suspension, drivetrain, tires, engines and selected power settings.\n\n" +
                                       "Each preset click queues another rim, tire-width or profile step from every car's stock value. " +
                                       "Each track click queues another 0.02 m extension from the widest existing value. Existing rows are preserved, " +
                                       "new choices are stored lowest-to-highest, and unchanged values are not added twice. " +
                                       "A car's stock value remains available in game but is not duplicated as an upgrade row. " +
                                       "Bodykit creation and bodykit targets are excluded.",
                                       "Confirm batch edit", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        long[] targetIds = targets.Select(c => c.Id).ToArray();
        long primaryId = _car?.Id ?? targetIds[0];
        _batchRunning = true;
        try
        {
            foreach (var target in targets)
            {
                _car = _cars.FirstOrDefault(c => c.Id == target.Id) ?? target;
                action();
            }
        }
        finally
        {
            _batchRunning = false;
            _car = _cars.FirstOrDefault(c => c.Id == primaryId) ??
                   _cars.FirstOrDefault(c => targetIds.Contains(c.Id));
            RenderCars(targetIds, primaryId);
            PickCar();
        }
        Log($"batch complete: {actionName} processed for {targets.Count} cars", "ok");
    }

    // ============================================================ embedded stock reference
    void PrepareEmbeddedStockReference()
    {
        try
        {
            string directory = Path.Combine(Path.GetTempPath(), "FH6LocalCryptoTool");
            Directory.CreateDirectory(directory);
            _stockReferencePath = Path.Combine(directory, $"gamedbRC.stock.v1.1.2.{Environment.ProcessId}.sqlite");

            using Stream packed = Assembly.GetExecutingAssembly().GetManifestResourceStream(StockReferenceResource)
                ?? throw new InvalidOperationException("the embedded stock database resource is missing");
            using (var gzip = new GZipStream(packed, CompressionMode.Decompress))
            using (var output = new FileStream(_stockReferencePath, FileMode.Create, FileAccess.Write, FileShare.Read))
                gzip.CopyTo(output);

            StockReferenceStatus.Text = "Embedded stock reference ready";
        }
        catch (Exception ex)
        {
            _stockReferencePath = null;
            StockReferenceStatus.Text = "Embedded stock reference unavailable";
            Log("stock reference could not be prepared: " + ex.Message, "warn");
        }
    }

    void AttachStockReference()
    {
        _stockReferenceAttached = false;
        RestoreCarBtn.IsEnabled = false;
        if (string.IsNullOrWhiteSpace(_stockReferencePath) || !File.Exists(_stockReferencePath))
            PrepareEmbeddedStockReference();
        if (string.IsNullOrWhiteSpace(_stockReferencePath) || !File.Exists(_stockReferencePath))
        {
            StockReferenceStatus.Text = "Embedded stock reference unavailable (see activity log)";
            return;
        }

        try
        {
            Exec("ATTACH DATABASE ? AS stockref", _stockReferencePath);
            string integrity = Convert.ToString(Scalar("PRAGMA stockref.integrity_check"));
            long cars = ScalarL("SELECT COUNT(*) FROM stockref.Data_Car") ?? 0;
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase) || cars == 0)
                throw new InvalidDataException("the embedded database did not pass its integrity check");
            _stockReferenceAttached = true;
            StockReferenceStatus.Text = $"Embedded stock reference · {cars} cars";
        }
        catch (Exception ex)
        {
            StockReferenceStatus.Text = "Embedded stock reference unavailable (see activity log)";
            Log("stock reference could not be attached: " + ex.Message, "warn");
        }
    }

    bool TableExists(string database, string table) =>
        (ScalarL($"SELECT COUNT(*) FROM {database}.sqlite_master WHERE type='table' AND name=?", table) ?? 0) > 0;

    List<Dictionary<string, object>> ReferenceDifferences(string table, params string[] ignoredColumns)
    {
        if (!TableExists("main", table) || !TableExists("stockref", table)) return new();
        var referenceColumns = Query($"PRAGMA stockref.table_info(\"{table}\")")
            .Select(r => (string)r["name"]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ignored = ignoredColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var columns = Query($"PRAGMA main.table_info(\"{table}\")")
            .Select(r => (string)r["name"]).Where(c => referenceColumns.Contains(c) && !ignored.Contains(c)).ToArray();
        if (columns.Length == 0) return new();
        string selected = QuotedColumns(columns);
        return Query($"SELECT * FROM (SELECT {selected} FROM main.\"{table}\" EXCEPT SELECT {selected} FROM stockref.\"{table}\") " +
                     $"UNION ALL SELECT * FROM (SELECT {selected} FROM stockref.\"{table}\" EXCEPT SELECT {selected} FROM main.\"{table}\")");
    }

    void RefreshModifiedCarsFromReference()
    {
        if (_conn == null || !_stockReferenceAttached) return;
        try
        {
            var changed = new HashSet<long>();
            void AddColumn(IEnumerable<Dictionary<string, object>> rows, string column)
            {
                foreach (var row in rows)
                    if (row.TryGetValue(column, out object value) && value != null)
                        changed.Add(Convert.ToInt64(value));
            }

            // Price and Autoshow visibility are global/convenience settings, not
            // car-editor modifications for the "Modified cars only" filter.
            AddColumn(ReferenceDifferences("Data_Car", "BaseCost", "NotAvailableInAutoshow"), "Id");
            foreach (string table in CarOrdinalTables)
                AddColumn(ReferenceDifferences(table), "Ordinal");
            AddColumn(ReferenceDifferences("List_SpringDamperPhysics"), "Ordinal");
            AddColumn(ReferenceDifferences("List_AntiSwayPhysics"), "Ordinal");

            var bodyOwners = new Dictionary<long, long>();
            foreach (var row in Query(@"SELECT CarBodyID body,Ordinal car FROM main.List_UpgradeCarBody
                                        UNION SELECT CarBodyID body,Ordinal car FROM stockref.List_UpgradeCarBody"))
                bodyOwners[Convert.ToInt64(row["body"])] = Convert.ToInt64(row["car"]);
            foreach (var (table, column) in BodyTables)
                foreach (var row in ReferenceDifferences(table))
                    if (row.TryGetValue(column, out object value) && value != null &&
                        bodyOwners.TryGetValue(Convert.ToInt64(value), out long owner))
                        changed.Add(owner);

            var engineUsers = new Dictionary<long, HashSet<long>>();
            foreach (var row in Query(@"SELECT EngineID part,Ordinal car FROM main.List_UpgradeEngine
                                       UNION SELECT EngineID part,Ordinal car FROM stockref.List_UpgradeEngine"))
            {
                long part = Convert.ToInt64(row["part"]), car = Convert.ToInt64(row["car"]);
                if (!engineUsers.TryGetValue(part, out var users)) engineUsers[part] = users = new();
                users.Add(car);
            }
            foreach (string table in EnginePowerTables)
                foreach (var row in ReferenceDifferences(table))
                    if (row.TryGetValue("EngineID", out object value) && value != null &&
                        engineUsers.TryGetValue(Convert.ToInt64(value), out var users))
                        changed.UnionWith(users);
            foreach (var row in ReferenceDifferences("Data_Engine"))
                if (row.TryGetValue("EngineID", out object value) && value != null &&
                    engineUsers.TryGetValue(Convert.ToInt64(value), out var users))
                    changed.UnionWith(users);

            var motorUsers = new Dictionary<long, HashSet<long>>();
            foreach (var row in Query(@"SELECT MotorID part,Ordinal car FROM main.List_UpgradeMotor
                                       UNION SELECT MotorID part,Ordinal car FROM stockref.List_UpgradeMotor"))
            {
                long part = Convert.ToInt64(row["part"]), car = Convert.ToInt64(row["car"]);
                if (!motorUsers.TryGetValue(part, out var users)) motorUsers[part] = users = new();
                users.Add(car);
            }
            foreach (var row in ReferenceDifferences("List_UpgradeMotorParts"))
                if (row.TryGetValue("MotorID", out object value) && value != null &&
                    motorUsers.TryGetValue(Convert.ToInt64(value), out var users))
                    changed.UnionWith(users);
            foreach (var row in ReferenceDifferences("Data_Motor"))
                if (row.TryGetValue("MotorID", out object value) && value != null &&
                    motorUsers.TryGetValue(Convert.ToInt64(value), out var users))
                    changed.UnionWith(users);

            var tireOwners = new Dictionary<long, long>();
            foreach (var row in Query(@"SELECT Id part,Ordinal car FROM main.List_UpgradeTireCompound
                                       UNION SELECT Id part,Ordinal car FROM stockref.List_UpgradeTireCompound"))
                tireOwners[Convert.ToInt64(row["part"])] = Convert.ToInt64(row["car"]);
            foreach (var row in ReferenceDifferences("List_UpgradeTireCompoundFictionModOverride"))
                if (row.TryGetValue("PartId", out object value) && value != null &&
                    tireOwners.TryGetValue(Convert.ToInt64(value), out long owner))
                    changed.Add(owner);

            var loadedCars = _cars.Select(c => c.Id).ToHashSet();
            _modifiedCars.Clear();
            _modifiedCars.UnionWith(changed.Where(loadedCars.Contains));
            StockReferenceStatus.Text = $"Embedded stock reference · {_modifiedCars.Count} modified car{(_modifiedCars.Count == 1 ? "" : "s")}";
        }
        catch (Exception ex)
        {
            StockReferenceStatus.Text = "Embedded stock comparison failed (see activity log)";
            FiltModified.IsChecked = false;
            FiltModified.IsEnabled = false;
            Log("stock comparison failed: " + ex.Message, "err");
        }
    }

    HashSet<long> IdSet(string sql, params object[] values) =>
        Query(sql, values).Where(r => r["v"] != null).Select(r => Convert.ToInt64(r["v"])).ToHashSet();

    static object[] SqlValues(IEnumerable<long> ids) => ids.Cast<object>().ToArray();
    static string SqlSlots(int count) => string.Join(",", Enumerable.Repeat("?", count));

    void DeleteRowsByIds(string table, string column, IEnumerable<long> values)
    {
        long[] ids = values.Distinct().ToArray();
        if (ids.Length > 0)
            Exec($"DELETE FROM main.\"{table}\" WHERE \"{column}\" IN ({SqlSlots(ids.Length)})", SqlValues(ids));
    }

    void InsertReferenceRowsByIds(string table, string column, IEnumerable<long> values)
    {
        long[] ids = values.Distinct().ToArray();
        if (ids.Length > 0)
            Exec($"INSERT INTO main.\"{table}\" SELECT * FROM stockref.\"{table}\" " +
                 $"WHERE \"{column}\" IN ({SqlSlots(ids.Length)})", SqlValues(ids));
    }

    void ReplaceReferenceRowsByIds(string table, string column, IEnumerable<long> values)
    {
        if (!TableExists("main", table) || !TableExists("stockref", table)) return;
        long[] ids = values.Distinct().ToArray();
        DeleteRowsByIds(table, column, ids);
        InsertReferenceRowsByIds(table, column, ids);
    }

    bool ReferenceHasCar(long carId) => _stockReferenceAttached &&
        (ScalarL("SELECT COUNT(*) FROM stockref.Data_Car WHERE Id=?", carId) ?? 0) > 0;

    void RestoreSelectedCar()
    {
        if (_conn == null || _car == null) { Log("pick a car first", "warn"); return; }
        long carId = _car.Id;
        if (!_stockReferenceAttached)
        {
            Log("embedded stock reference is unavailable; reload the database to retry (see activity log)", "warn");
            return;
        }
        if (!ReferenceHasCar(carId))
        {
            Log("this car is not present in the embedded stock database", "warn");
            return;
        }

        var answer = MessageBox.Show(
            $"Restore {Naming.Friendly(_car.Media)} from the embedded clean stock database?\n\n" +
            "This replaces the car's editable rows. Engine and motor upgrade data is shared by ID, " +
            "so restoring those rows can also affect other cars using the same engine or motor.",
            "Restore selected car", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        RestoreCarFromReference(carId, _car.Media);
    }

    void RestoreCarFromReference(long carId, string media)
    {
        try
        {
            var bodyIds = IdSet("SELECT CarBodyID v FROM main.List_UpgradeCarBody WHERE Ordinal=?", carId);
            bodyIds.UnionWith(IdSet("SELECT CarBodyID v FROM stockref.List_UpgradeCarBody WHERE Ordinal=?", carId));
            bodyIds.Remove(0);

            var springIds = IdSet(@"SELECT FrontSpringDamperPhysicsID v FROM main.List_UpgradeSpringDamper WHERE Ordinal=?
                                    UNION SELECT RearSpringDamperPhysicsID v FROM main.List_UpgradeSpringDamper WHERE Ordinal=?
                                    UNION SELECT FrontSpringDamperPhysicsID v FROM stockref.List_UpgradeSpringDamper WHERE Ordinal=?
                                    UNION SELECT RearSpringDamperPhysicsID v FROM stockref.List_UpgradeSpringDamper WHERE Ordinal=?",
                                  carId, carId, carId, carId);
            springIds.Remove(0);
            var antiSwayIds = IdSet(@"SELECT AntiSwayPhysicsID v FROM main.List_UpgradeAntiSwayFront WHERE Ordinal=?
                                     UNION SELECT AntiSwayPhysicsID v FROM main.List_UpgradeAntiSwayRear WHERE Ordinal=?
                                     UNION SELECT AntiSwayPhysicsID v FROM stockref.List_UpgradeAntiSwayFront WHERE Ordinal=?
                                     UNION SELECT AntiSwayPhysicsID v FROM stockref.List_UpgradeAntiSwayRear WHERE Ordinal=?",
                                   carId, carId, carId, carId);
            antiSwayIds.Remove(0);
            var tireIds = IdSet("SELECT Id v FROM main.List_UpgradeTireCompound WHERE Ordinal=?", carId);
            tireIds.UnionWith(IdSet("SELECT Id v FROM stockref.List_UpgradeTireCompound WHERE Ordinal=?", carId));
            tireIds.Remove(0);
            // Include swap-option links as well as the stock link. A shared engine or motor
            // change marks every linked car modified, so Restore must also work when the
            // selected car sees the changed part as an option rather than as its stock unit.
            var engineIds = IdSet("SELECT EngineID v FROM main.List_UpgradeEngine WHERE Ordinal=?", carId);
            engineIds.UnionWith(IdSet("SELECT EngineID v FROM stockref.List_UpgradeEngine WHERE Ordinal=?", carId));
            engineIds.RemoveWhere(id => (ScalarL("SELECT COUNT(*) FROM stockref.Data_Engine WHERE EngineID=?", id) ?? 0) == 0);
            var motorIds = IdSet("SELECT MotorID v FROM main.List_UpgradeMotor WHERE Ordinal=?", carId);
            motorIds.UnionWith(IdSet("SELECT MotorID v FROM stockref.List_UpgradeMotor WHERE Ordinal=?", carId));
            motorIds.RemoveWhere(id => (ScalarL("SELECT COUNT(*) FROM stockref.Data_Motor WHERE MotorID=?", id) ?? 0) == 0);

            Exec("SAVEPOINT restore_car");

            foreach (var (table, column) in BodyTables)
                DeleteRowsByIds(table, column, bodyIds);
            DeleteRowsByIds("List_UpgradeTireCompoundFictionModOverride", "PartId", tireIds);
            DeleteRowsByIds("List_SpringDamperPhysics", "SpringDamperPhysicsID", springIds);
            DeleteRowsByIds("List_AntiSwayPhysics", "AntiSwayPhysicsID", antiSwayIds);
            foreach (string table in CarOrdinalTables)
                Exec($"DELETE FROM main.\"{table}\" WHERE Ordinal=?", carId);
            Exec("DELETE FROM main.Data_Car WHERE Id=?", carId);

            Exec("INSERT INTO main.Data_Car SELECT * FROM stockref.Data_Car WHERE Id=?", carId);
            foreach (string table in CarOrdinalTables)
                Exec($"INSERT INTO main.\"{table}\" SELECT * FROM stockref.\"{table}\" WHERE Ordinal=?", carId);
            InsertReferenceRowsByIds("List_SpringDamperPhysics", "SpringDamperPhysicsID", springIds);
            InsertReferenceRowsByIds("List_AntiSwayPhysics", "AntiSwayPhysicsID", antiSwayIds);
            foreach (var (table, column) in BodyTables)
                InsertReferenceRowsByIds(table, column, bodyIds);
            InsertReferenceRowsByIds("List_UpgradeTireCompoundFictionModOverride", "PartId", tireIds);
            foreach (string table in EnginePowerTables)
                ReplaceReferenceRowsByIds(table, "EngineID", engineIds);
            ReplaceReferenceRowsByIds("List_UpgradeMotorParts", "MotorID", motorIds);

            Exec("RELEASE restore_car");

            _handlingSnapshots.Remove(carId);
            var loadedBaseCosts = _loadedBaseCosts;
            IndexDb();
            _loadedBaseCosts = loadedBaseCosts;
            RefreshModifiedCarsFromReference();
            _car = null;
            CarInfo.Text = "";
            EngList.ItemsSource = null;
            ClearPowerBuilder();
            AddBodyKitBtn.IsEnabled = RestoreCarBtn.IsEnabled = ApplyPowerBtn.IsEnabled = false;
            RenderCars();
            Log($"restored {media} from the embedded stock database", "ok");
        }
        catch (Exception ex)
        {
            try { Exec("ROLLBACK TO restore_car"); Exec("RELEASE restore_car"); } catch { }
            Log("car restore failed: " + ex.Message, "err");
        }
    }

    // ============================================================ DB open / export
    void LoadDb()
    {
        var d = new OpenFileDialog { Filter = "SQLite DB (*.sqlite;*.slt;*.db)|*.sqlite;*.slt;*.db|All files|*.*" };
        if (d.ShowDialog() != true) return;
        try
        {
            _conn?.Close(); _conn?.Dispose();
            _origPath = d.FileName;
            _workPath = Path.Combine(Path.GetTempPath(), "fh6_local_crypto_working.sqlite");
            File.Copy(_origPath, _workPath, true);
            Open();
            OutName.Text = Path.GetFileNameWithoutExtension(_origPath) + "_modified.sqlite";
            Log($"loaded {Path.GetFileName(_origPath)}", "ok");
        }
        catch (Exception ex) { Log("load failed: " + ex.Message, "err"); }
    }
    void Open()
    {
        // This editor attaches the embedded reference as `stockref`. A pooled SQLite
        // handle keeps ATTACH state across Close/Dispose, so a later Open could
        // inherit that handle and fail with "database stockref is already in use".
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _workPath,
            Pooling = false
        }.ToString());
        _conn.Open();
        Exec("PRAGMA foreign_keys=OFF");
        _car = null;
        _modifiedCars.Clear();
        ClearPowerBuilder();
        AddBodyKitBtn.IsEnabled = false;
        RestoreCarBtn.IsEnabled = false;
        ApplyPowerBtn.IsEnabled = false;
        CarInfo.Text = "";
        EngList.ItemsSource = null;
        _handlingSnapshots.Clear();
        AttachStockReference();
        IndexDb();
        FiltModified.IsEnabled = _stockReferenceAttached;
        if (_stockReferenceAttached) RefreshModifiedCarsFromReference();
        else FiltModified.IsChecked = false;
        ReloadBtn.IsEnabled = ExportBtn.IsEnabled = AddFeBtn.IsEnabled = RemoveFeBtn.IsEnabled =
            AllPriceOneBtn.IsEnabled = RevertPricesBtn.IsEnabled =
            BestHandlingBtn.IsEnabled = RevertHandlingBtn.IsEnabled = true;
        RenderCars();
        Status.Text = $"{Path.GetFileName(_origPath)}   ·   {_cars.Count} cars   ·   {_engines.Count} engines";
        Log($"cars: {_cars.Count} · engines: {_engines.Count}", "info");
        int conv = _cars.Count(c => c.Type is "EV→ICE" or "ICE→EV");
        if (conv > 0) Log($"detected {conv} converted car{(conv == 1 ? "" : "s")} (EV→ICE / ICE→EV)", "info");
    }
    void ReloadOriginal()
    {
        if (_origPath == null) return;
        _conn.Close(); _conn.Dispose();
        File.Copy(_origPath, _workPath, true);
        _car = null; CarInfo.Text = ""; EngList.ItemsSource = null;
        Open();
        Log("reverted to original", "warn");
    }
    void ExportDb()
    {
        if (_conn == null) return;
        try
        {
            Exec("PRAGMA wal_checkpoint(TRUNCATE)");
            var d = new SaveFileDialog { FileName = OutName.Text, Filter = "SQLite DB (*.sqlite)|*.sqlite|All files|*.*" };
            if (d.ShowDialog() != true) return;
            File.Copy(_workPath, d.FileName, true);
            Log("⬇ exported " + Path.GetFileName(d.FileName), "ok");
        }
        catch (Exception ex) { Log("export failed: " + ex.Message, "err"); }
    }

    // ============================================================ SQL helpers
    SqliteCommand Cmd(string sql, object[] p)
    {
        var cmd = _conn.CreateCommand();
        int i = 0;
        cmd.CommandText = Regex.Replace(sql, @"\?", _ => "$p" + (i++));
        for (int j = 0; j < (p?.Length ?? 0); j++) cmd.Parameters.AddWithValue("$p" + j, p[j] ?? DBNull.Value);
        return cmd;
    }
    int Exec(string sql, params object[] p) { using var c = Cmd(sql, p); return c.ExecuteNonQuery(); }
    object Scalar(string sql, params object[] p) { using var c = Cmd(sql, p); var r = c.ExecuteScalar(); return r == DBNull.Value ? null : r; }
    long? ScalarL(string sql, params object[] p) { var v = Scalar(sql, p); return v == null ? null : Convert.ToInt64(v); }
    List<Dictionary<string, object>> Query(string sql, params object[] p)
    {
        var outl = new List<Dictionary<string, object>>();
        using var c = Cmd(sql, p); using var r = c.ExecuteReader();
        while (r.Read())
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i < r.FieldCount; i++) d[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
            outl.Add(d);
        }
        return outl;
    }

    // ============================================================ index / lists
    void IndexDb()
    {
        _motorMedia.Clear();
        foreach (var r in Query("SELECT MediaName FROM Data_Motor")) _motorMedia.Add((string)r["MediaName"]);
        _referenceCars = _stockReferenceAttached
            ? Query("SELECT Id FROM stockref.Data_Car").Select(r => Convert.ToInt64(r["Id"])).ToHashSet()
            : new();
        _originalEvCars = _stockReferenceAttached
            ? Query("SELECT DISTINCT Ordinal FROM stockref.List_UpgradeMotor WHERE IsStock=1")
                .Select(r => Convert.ToInt64(r["Ordinal"])).ToHashSet()
            : new();
        _electricSpecCars = Query("SELECT Id FROM Data_Car WHERE EngineConfigID=6 AND Displacement=0")
            .Select(r => Convert.ToInt64(r["Id"])).ToHashSet();
        _bodykitPresetCars = Query(@"SELECT DISTINCT p.Ordinal
                                    FROM UpgradePresetPackages p
                                    JOIN List_UpgradeCarBody b ON b.Id=p.CarBody
                                    WHERE b.IsStock=0")
            .Select(r => Convert.ToInt64(r["Ordinal"])).ToHashSet();
        _loadedBaseCosts = Query("SELECT Id,BaseCost FROM Data_Car")
            .ToDictionary(r => Convert.ToInt64(r["Id"]), r => Convert.ToInt64(r["BaseCost"] ?? 0L));
        _cars = Query("SELECT Id,MediaName,Year FROM Data_Car ORDER BY MediaName")
            .Select(r =>
            {
                long id = Convert.ToInt64(r["Id"]);
                string media = (string)r["MediaName"];
                string type = TypeFor(id, media);
                return new CarItem(id, media, Convert.ToInt64(r["Year"]), type);
            }).ToList();
        _engines = Query("SELECT EngineID,MediaName,EngineName,EngineGraphingMaxPower pw,Rotary,Diesel,ConfigID,CylinderID FROM Data_Engine ORDER BY MediaName")
            .Select(r =>
            {
                bool rot = Convert.ToInt64(r["Rotary"] ?? 0L) == 1;
                return new EngItem(Convert.ToInt64(r["EngineID"]), (string)(r["MediaName"] ?? ""), (string)(r["EngineName"] ?? "(unnamed)"),
                    r["pw"] == null ? 0 : Convert.ToDouble(r["pw"]), rot, Convert.ToInt64(r["Diesel"] ?? 0L) == 1,
                    Naming.EngineType(Convert.ToInt64(r["ConfigID"] ?? 0L), Convert.ToInt64(r["CylinderID"] ?? 0L), rot));
            }).ToList();
        LoadMotors();  // peak achievable output first
        var makes = _engines.Select(en => en.Media.Split('_')[0]).Where(m => m.Length > 0).Distinct().OrderBy(m => m).ToList();
        MakeFilter.Items.Clear(); MakeFilter.Items.Add("All makes"); foreach (var m in makes) MakeFilter.Items.Add(m); MakeFilter.SelectedIndex = 0;
        var types = _engines.Select(e => e.EngType).Distinct().OrderBy(Naming.TypeSortKey).ToList();
        TypeFilter.Items.Clear(); TypeFilter.Items.Add("All types"); foreach (var t in types) TypeFilter.Items.Add(t); TypeFilter.SelectedIndex = 0;
    }
    // Data_Car's electric configuration determines the current powertrain. Upgrade
    // menus can be stale or contradictory in an imported DB; they are not the type.
    string TypeFor(long id, string media)
    {
        bool originEV = _referenceCars.Contains(id) ? _originalEvCars.Contains(id) : _motorMedia.Contains(media);
        bool currentEV = _electricSpecCars.Contains(id);
        return currentEV ? (originEV ? "EV" : "ICE→EV") : (originEV ? "EV→ICE" : "ICE");
    }

    void RenderCars(IEnumerable<long> restoreSelection = null, long? primaryId = null)
    {
        if (_cars == null || CarList == null || _batchRunning) return;
        var selectedIds = (restoreSelection ?? CarList.SelectedItems.Cast<CarItem>().Select(c => c.Id)).ToHashSet();
        var term = (CarSearch.Text ?? "").ToLower();
        bool ev = FiltEV?.IsChecked == true, ice = FiltICE?.IsChecked == true,
             conv = FiltConv?.IsChecked == true, iceev = FiltIceEv?.IsChecked == true,
             bodykit = FiltBodykit?.IsChecked == true, modified = FiltModified?.IsChecked == true;
        var visibleCars = _cars
            .Where(c => (!bodykit || _bodykitPresetCars.Contains(c.Id)) &&
                        (!modified || _modifiedCars.Contains(c.Id)) &&
                        (c.Media + " " + Naming.Friendly(c.Media)).ToLower().Contains(term) && (
                c.Type == "ICE"    ? ice   :
                c.Type == "EV→ICE" ? conv  :
                c.Type == "ICE→EV" ? iceev :
                                     ev))     // "EV"
            .ToList();
        _updatingCarSelection = true;
        try
        {
            CarList.ItemsSource = visibleCars;
            foreach (var car in visibleCars.Where(c => selectedIds.Contains(c.Id)))
                CarList.SelectedItems.Add(car);
            if (primaryId.HasValue)
            {
                var primary = visibleCars.FirstOrDefault(c => c.Id == primaryId.Value);
                if (primary != null) CarList.SelectedItem = primary;
            }
        }
        finally { _updatingCarSelection = false; }
    }
    void PickCar()
    {
        var selected = CarList.SelectedItems.Cast<CarItem>().ToList();
        if (selected.Count == 0) return;
        var c = CarList.SelectedItem as CarItem ?? selected[^1];
        _car = c;
        bool batch = selected.Count > 1;
        AddBodyKitBtn.IsEnabled = !batch;
        BodyTargets.IsEnabled = !batch;
        ShowSingleCarFitmentAddButtons(!batch);
        bool canRestore = !batch && ReferenceHasCar(c.Id);
        RestoreCarBtn.IsEnabled = canRestore;
        RestoreCarBtn.ToolTip = batch
            ? "Restore is disabled in batch mode because restoring a whole car also replaces its bodykit rows."
            : canRestore
            ? $"Replace {(batch ? "these cars'" : "this car's")} editable rows with embedded stock rows. Shared engine and motor upgrades may affect other cars using the same IDs."
            : _stockReferenceAttached
                ? "This car is not in the embedded stock database, so there are no stock rows to restore."
                : "The embedded stock reference is unavailable. Check the activity log, then reload the database to retry.";
        ShowCarInfo(); RenderEngines(); PopulateFitmentFields(); PopulatePowerBuilder();
        ApplyOptionsBtn.Content = batch ? $"⚙  APPLY changes to {selected.Count} cars" : "⚙  APPLY changes to this car";
        if (batch)
        {
            PrepareBatchFitmentTemplate();
            PopulateBatchPowerBuilder(selected);
            bool[] autoshow = selected.Select(x => (ScalarL("SELECT NotAvailableInAutoshow FROM Data_Car WHERE Id=?", x.Id) ?? 1) == 0).ToArray();
            OptAutoshow.IsThreeState = true;
            OptAutoshow.IsChecked = autoshow.All(x => x) ? true : autoshow.All(x => !x) ? false : null;
            CarInfo.Text = $"BATCH MODE    {selected.Count} cars selected\n" +
                           $"Primary       {Naming.Friendly(c.Media)}\n\n" +
                           "The controls use the primary car as a template.\n" +
                           "Click fitment buttons repeatedly to queue levels:\n" +
                           "rims ±1 in · widths ±10 mm · profiles ±2 per click.\n" +
                           "Track adds +0.02 m per click from each widest row.\n" +
                           "Right-click a button to remove its last queued level.\n" +
                           "Queued fitment is written lowest to highest.\n" +
                           "Existing choices are skipped; unchanged Apply adds nothing.\n" +
                           "Batch: rims, tire width/profile, track width,\n" +
                           "Drift/Rally suspension, drivetrain and tires.\n" +
                           "Fitment applies to each car's stock body.\n" +
                           "Bodykit creation/targets are disabled.";
            Log($"batch selection: {selected.Count} cars (primary: {c.Media})", "info");
        }
        else
        {
            OptAutoshow.IsThreeState = false;
            ApplyPowerBtn.ToolTip = null;
            Log("selected " + c.Media, "info");
        }
    }
    long BodyId(long carId) => ScalarL("SELECT CarBodyID FROM List_UpgradeCarBody WHERE Ordinal=? AND IsStock=1", carId) ?? carId * 1000;

    void RefreshCar()
    {
        if (_batchRunning) return;
        ShowCarInfo(); RenderEngines(); PopulatePowerBuilder();
    }
    void ShowCarInfo()
    {
        if (_car == null || _batchRunning) return;
        var c = _car;
        var engN = ScalarL("SELECT COUNT(*) FROM List_UpgradeEngine WHERE Ordinal=?", c.Id) ?? 0;
        var stock = Query("SELECT de.EngineName n FROM List_UpgradeEngine le LEFT JOIN Data_Engine de ON de.EngineID=le.EngineID WHERE le.Ordinal=? AND le.IsStock=1", c.Id);
        var dc = Query("SELECT DriveTypeID,NumGears,Displacement,TireBrandID,NotAvailableInAutoshow FROM Data_Car WHERE Id=?", c.Id).FirstOrDefault();
        string dt = dc == null ? "?" : (Convert.ToInt64(dc["DriveTypeID"]) switch { 1 => "FWD", 2 => "RWD", 3 => "AWD", _ => "?" });
        string se = stock.Count > 0 && stock[0]["n"] != null ? (string)stock[0]["n"] : "— (electric)";
        bool inShow = dc != null && Convert.ToInt64(dc["NotAvailableInAutoshow"] ?? 0L) == 0;
        CarInfo.Text =
            $"Type          {c.Type}{(c.Type is "EV→ICE" or "ICE→EV" ? "  (converted)" : "")}\n" +
            $"Stock engine  {se}\n" +
            $"Engine opts   {engN}\n" +
            $"Drive/gears   {dt} · {dc?["NumGears"]}\n" +
            $"Displacement  {dc?["Displacement"]} cc\n" +
            $"Tire brand    {dc?["TireBrandID"]}\n" +
            $"Autoshow      {(inShow ? "yes" : "no")}";
        // reflect current autoshow state without firing the Click handler (Click ≠ programmatic IsChecked)
        OptAutoshow.IsEnabled = true;
        OptAutoshow.IsChecked = inShow;
        // Manual transmission only makes sense on a car that is currently electric: it must have a stock
        // motor AND no stock engine. That disables it for native ICE, EV→ICE conversions, and any car that
        // had an engine set as stock this session — while staying on for native EVs and ICE→EV swaps.
        bool hasMotor = (ScalarL("SELECT COUNT(*) FROM List_UpgradeMotor WHERE Ordinal=? AND IsStock=1", c.Id) ?? 0) > 0;
        bool hasEngine = (ScalarL("SELECT COUNT(*) FROM List_UpgradeEngine WHERE Ordinal=? AND IsStock=1", c.Id) ?? 0) > 0;
        bool isEv = _electricSpecCars.Contains(c.Id);
        bool canUseManual = isEv && hasMotor && !hasEngine;
        OptManual.IsEnabled = canUseManual;
        if (!canUseManual) OptManual.IsChecked = false;
    }

    void MarkModified(params long[] carIds)
    {
        foreach (long carId in carIds) _modifiedCars.Add(carId);
    }

    void RefreshModifiedAfterExcludedEdit()
    {
        if (_stockReferenceAttached) RefreshModifiedCarsFromReference();
        if (FiltModified?.IsChecked == true) RenderCars();
    }

    long[] CarsUsingEngine(long engineId) =>
        Query(@"SELECT DISTINCT e.Ordinal FROM List_UpgradeEngine e
                JOIN Data_Car c ON c.Id=e.Ordinal WHERE e.EngineID=?", engineId)
            .Select(r => Convert.ToInt64(r["Ordinal"])).ToArray();

    long[] CarsUsingMotor(long motorId) =>
        Query(@"SELECT DISTINCT m.Ordinal FROM List_UpgradeMotor m
                JOIN Data_Car c ON c.Id=m.Ordinal WHERE m.MotorID=?", motorId)
            .Select(r => Convert.ToInt64(r["Ordinal"])).ToArray();

    static string PowerText(object value, string format) => value == null
        ? "" : Convert.ToDouble(value).ToString(format, CultureInfo.InvariantCulture);

    void SetPowerField(CheckBox option, TextBox box, bool enabled, string text)
    {
        option.IsChecked = false;
        option.IsEnabled = enabled;
        box.IsEnabled = enabled;
        box.Text = enabled ? text : "";
    }

    void ClearPowerBuilder()
    {
        _powerTargetCarId = null;
        _updatingPowerTargets = true;
        try
        {
            PowerEngineTarget.ItemsSource = null;
            PowerMotorTarget.ItemsSource = null;
            PowerEngineTarget.IsEnabled = PowerMotorTarget.IsEnabled = false;
        }
        finally { _updatingPowerTargets = false; }
        SetPowerField(OptPowerBoost, PowerBoostScale, false, "");
        SetPowerField(OptPowerRedline, PowerRedline, false, "");
        SetPowerField(OptPowerEngineMass, PowerEngineMass, false, "");
        SetPowerField(OptPowerWeight, PowerWeightDistribution, false, "");
        SetPowerField(OptPowerEvTorque, PowerEvTorque, false, "");
        ApplyPowerBtn.IsEnabled = false;
    }

    void PowerTarget_Changed(object s, SelectionChangedEventArgs e)
    {
        if (!_updatingPowerTargets && _conn != null && _car != null &&
            PowerEngineTarget != null && PowerMotorTarget != null)
            PopulatePowerFields(s == PowerEngineTarget, s == PowerMotorTarget, false);
    }

    long? SelectedPowerEngineId() => (PowerEngineTarget.SelectedItem as PowerPartTarget)?.Id;
    long? SelectedPowerMotorId() => (PowerMotorTarget.SelectedItem as PowerPartTarget)?.Id;

    void PopulatePowerBuilder()
    {
        if (_conn == null || _car == null || _batchRunning) return;
        long carId = _car.Id;
        bool sameCar = _powerTargetCarId == carId;
        long? previousEngine = sameCar ? SelectedPowerEngineId() : null;
        long? previousMotor = sameCar ? SelectedPowerMotorId() : null;

        var engineTargets = Query(@"SELECT e.EngineID id, MAX(e.IsStock) stock, MIN(e.Level) firstLevel, de.EngineName name
                                    FROM List_UpgradeEngine e JOIN Data_Engine de ON de.EngineID=e.EngineID
                                    WHERE e.Ordinal=? GROUP BY e.EngineID ORDER BY stock DESC,firstLevel,e.EngineID", carId)
            .Select(r => new PowerPartTarget(Convert.ToInt64(r["id"]),
                string.IsNullOrWhiteSpace(r["name"] as string) ? $"Engine {r["id"]}" : (string)r["name"],
                Convert.ToInt64(r["stock"] ?? 0L) != 0)).ToList();
        var motorTargets = Query(@"SELECT m.MotorID id, MAX(m.IsStock) stock, MIN(m.Level) firstLevel, dm.MediaName media, dm.MotorName name
                                   FROM List_UpgradeMotor m JOIN Data_Motor dm ON dm.MotorID=m.MotorID
                                   WHERE m.Ordinal=? GROUP BY m.MotorID ORDER BY stock DESC,firstLevel,m.MotorID", carId)
            .Select(r => new PowerPartTarget(Convert.ToInt64(r["id"]),
                string.IsNullOrWhiteSpace(r["name"] as string) ? Naming.Friendly(r["media"] as string) : (string)r["name"],
                Convert.ToInt64(r["stock"] ?? 0L) != 0)).ToList();

        _updatingPowerTargets = true;
        try
        {
            PowerEngineTarget.ItemsSource = engineTargets;
            PowerEngineTarget.IsEnabled = engineTargets.Count > 0;
            PowerEngineTarget.SelectedItem = engineTargets.FirstOrDefault(t => t.Id == previousEngine)
                ?? engineTargets.FirstOrDefault(t => t.IsStock) ?? engineTargets.FirstOrDefault();
            PowerMotorTarget.ItemsSource = motorTargets;
            PowerMotorTarget.IsEnabled = motorTargets.Count > 0;
            PowerMotorTarget.SelectedItem = motorTargets.FirstOrDefault(t => t.Id == previousMotor)
                ?? motorTargets.FirstOrDefault(t => t.IsStock) ?? motorTargets.FirstOrDefault();
            _powerTargetCarId = carId;
        }
        finally { _updatingPowerTargets = false; }
        PopulatePowerFields();
    }

    void PopulateBatchPowerBuilder(IReadOnlyCollection<CarItem> cars)
    {
        long[] engineIds = cars.Select(c => ScalarL("SELECT EngineID FROM List_UpgradeEngine WHERE Ordinal=? AND IsStock=1 LIMIT 1", c.Id))
            .Where(id => id.HasValue).Select(id => id.Value).Distinct().ToArray();
        long[] motorIds = cars.Select(c => ScalarL("SELECT MotorID FROM List_UpgradeMotor WHERE Ordinal=? AND IsStock=1 LIMIT 1", c.Id))
            .Where(id => id.HasValue).Select(id => id.Value).Distinct().ToArray();
        bool boost = engineIds.Any(id => new[] { "List_UpgradeEngineTurboSingle", "List_UpgradeEngineTurboTwin", "List_UpgradeEngineTurboQuad" }
            .Any(table => (ScalarL($"SELECT COUNT(*) FROM {table} WHERE EngineID=?", id) ?? 0) > 0));
        bool redline = engineIds.Any(id => (ScalarL("SELECT COUNT(*) FROM List_UpgradeEngineCamshaft WHERE EngineID=?", id) ?? 0) > 0);
        bool weight = cars.Any(c => (ScalarL(@"SELECT COUNT(*) FROM List_UpgradeCarBodyWeight
                                                   WHERE Level=2 AND CarBodyId IN
                                                     (SELECT CarBodyID FROM List_UpgradeCarBody WHERE Ordinal=?)", c.Id) ?? 0) > 0);
        bool ev = motorIds.Any(id => (ScalarL("SELECT COUNT(*) FROM List_UpgradeMotorParts WHERE MotorID=? AND IsStock=0", id) ?? 0) > 0);

        _updatingPowerTargets = true;
        try
        {
            PowerEngineTarget.ItemsSource = engineIds.Length == 0 ? null :
                new[] { new PowerPartTarget(-1, $"BATCH · {engineIds.Length} stock engine(s)", true) };
            PowerEngineTarget.SelectedIndex = engineIds.Length == 0 ? -1 : 0;
            PowerEngineTarget.IsEnabled = false;
            PowerMotorTarget.ItemsSource = motorIds.Length == 0 ? null :
                new[] { new PowerPartTarget(-1, $"BATCH · {motorIds.Length} stock motor(s)", true) };
            PowerMotorTarget.SelectedIndex = motorIds.Length == 0 ? -1 : 0;
            PowerMotorTarget.IsEnabled = false;
        }
        finally { _updatingPowerTargets = false; }
        SetPowerField(OptPowerBoost, PowerBoostScale, boost, "");
        SetPowerField(OptPowerRedline, PowerRedline, redline, "");
        SetPowerField(OptPowerEngineMass, PowerEngineMass, false, "");
        SetPowerField(OptPowerWeight, PowerWeightDistribution, weight, "");
        SetPowerField(OptPowerEvTorque, PowerEvTorque, ev, "");
        ApplyPowerBtn.IsEnabled = boost || redline || weight || ev;
        ApplyPowerBtn.ToolTip = "Batch mode applies engine and motor settings to each selected car's stock unit. Shared IDs can also affect unselected cars.";
    }

    void PopulatePowerFields(bool engine = true, bool motor = true, bool weightField = true)
    {
        if (_conn == null || _car == null) return;
        long carId = _car.Id;
        long? engineId = SelectedPowerEngineId();
        long? motorId = SelectedPowerMotorId();

        object boost = null, redline = null, engineMass = null, evScale = null;
        if (engineId.HasValue)
        {
            boost = HighestTurboScaleTarget(engineId.Value)?.Scale0;
            redline = Scalar("SELECT RedlineRPM FROM List_UpgradeEngineCamshaft WHERE EngineID=? ORDER BY Level DESC,Id DESC LIMIT 1", engineId.Value);
            engineMass = Scalar("SELECT \"EngineMass-kg\" FROM Data_Engine WHERE EngineID=?", engineId.Value);
        }
        if (motorId.HasValue)
            evScale = Scalar("SELECT MAX(TorqueScale) FROM List_UpgradeMotorParts WHERE MotorID=? AND IsStock=0", motorId.Value);

        object weight = Scalar(@"SELECT CMBackFront FROM List_UpgradeCarBodyWeight
                                 WHERE Level=2 AND CarBodyId IN
                                   (SELECT CarBodyID FROM List_UpgradeCarBody WHERE Ordinal=?)
                                 ORDER BY CarBodyId,Id LIMIT 1", carId);

        if (engine)
        {
            SetPowerField(OptPowerBoost, PowerBoostScale, boost != null, PowerText(boost, "0.####"));
            SetPowerField(OptPowerRedline, PowerRedline, redline != null, PowerText(redline, "0"));
            SetPowerField(OptPowerEngineMass, PowerEngineMass, engineMass != null, PowerText(engineMass, "0.###"));
        }
        if (weightField)
            SetPowerField(OptPowerWeight, PowerWeightDistribution, weight != null, PowerText(weight, "0.####"));
        if (motor)
            SetPowerField(OptPowerEvTorque, PowerEvTorque, evScale != null, PowerText(evScale, "0.####"));
        ApplyPowerBtn.IsEnabled = OptPowerBoost.IsEnabled || OptPowerRedline.IsEnabled || OptPowerEngineMass.IsEnabled ||
                                  OptPowerWeight.IsEnabled || OptPowerEvTorque.IsEnabled;
    }

    TurboScaleTarget HighestTurboScaleTarget(long engineId)
    {
        TurboScaleTarget best = null;
        foreach (string table in new[] { "List_UpgradeEngineTurboSingle", "List_UpgradeEngineTurboTwin", "List_UpgradeEngineTurboQuad" })
        {
            var row = Query($@"SELECT Id,Level,TorqueDropOffScale0 s0,TorqueDropOffScale1 s1
                                FROM {table} WHERE EngineID=? ORDER BY Level DESC,Id DESC LIMIT 1", engineId).FirstOrDefault();
            if (row == null) continue;
            var candidate = new TurboScaleTarget(table, Convert.ToInt64(row["Id"]), Convert.ToInt64(row["Level"]),
                Convert.ToDouble(row["s0"]), Convert.ToDouble(row["s1"]));
            if (best == null || candidate.Level > best.Level || candidate.Level == best.Level && candidate.Id > best.Id)
                best = candidate;
        }
        return best;
    }

    TurboScaleTarget ApplyHighestTurboScale(long engineId, double requestedScale0)
    {
        var target = HighestTurboScaleTarget(engineId);
        if (target == null) return null;
        double ratio = Math.Abs(target.Scale0) > 0.000000001 ? target.Scale1 / target.Scale0 : 1d;
        double requestedScale1 = Math.Round(requestedScale0 * ratio, 6);
        Exec($"UPDATE {target.Table} SET TorqueDropOffScale0=?,TorqueDropOffScale1=? WHERE Id=? AND EngineID=?",
            requestedScale0, requestedScale1, target.Id, engineId);
        return target with { Scale0 = requestedScale0, Scale1 = requestedScale1 };
    }

    bool TryPowerNumber(TextBox box, string label, double minimum, double maximum, out double value)
    {
        string text = box.Text.Trim().TrimEnd('%');
        bool valid = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                     double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        if (valid && value >= minimum && value <= maximum) return true;
        Log($"{label} must be a number from {minimum:0.##} to {maximum:0.##}", "warn");
        value = 0;
        return false;
    }

    void ApplyPowerBuilder()
    {
        if (_conn == null || _car == null) { Log("pick a car first", "warn"); return; }
        var batchCars = SelectedCars();
        if (batchCars.Count > 1) { ApplyBatchPowerBuilder(batchCars); return; }
        bool doBoost = OptPowerBoost.IsChecked == true;
        bool doRedline = OptPowerRedline.IsChecked == true;
        bool doEngineMass = OptPowerEngineMass.IsChecked == true;
        bool doWeight = OptPowerWeight.IsChecked == true;
        bool doEv = OptPowerEvTorque.IsChecked == true;
        if (!doBoost && !doRedline && !doEngineMass && !doWeight && !doEv)
        { Log("select at least one power-builder setting", "warn"); return; }

        double boostScale = 0, redlineRpm = 0, engineMass = 0, weightDistribution = 0, evMaxScale = 0;
        if (doBoost && !TryPowerNumber(PowerBoostScale, "boost scale", 0.01, 100, out boostScale)) return;
        if (doRedline && !TryPowerNumber(PowerRedline, "redline", 500, 30000, out redlineRpm)) return;
        if (doEngineMass && !TryPowerNumber(PowerEngineMass, "engine mass", 0, 5000, out engineMass)) return;
        if (doWeight)
        {
            if (!TryPowerNumber(PowerWeightDistribution, "weight distribution", 0, 100, out weightDistribution)) return;
            if (weightDistribution > 1) weightDistribution /= 100d;
            if (weightDistribution < 0 || weightDistribution > 1)
            { Log("weight distribution must be between 0.0 and 1.0, or 0% and 100%", "warn"); return; }
        }
        if (doEv && !TryPowerNumber(PowerEvTorque, "EV torque scale", 0.01, 100, out evMaxScale)) return;

        long carId = _car.Id;
        long? engineId = SelectedPowerEngineId();
        long? motorId = SelectedPowerMotorId();
        if ((doBoost || doRedline || doEngineMass) && (!engineId.HasValue ||
            (ScalarL("SELECT COUNT(*) FROM List_UpgradeEngine WHERE Ordinal=? AND EngineID=?", carId, engineId.Value) ?? 0) == 0))
        { Log("choose an engine currently linked to this car", "warn"); return; }
        if (doEv && (!motorId.HasValue ||
            (ScalarL("SELECT COUNT(*) FROM List_UpgradeMotor WHERE Ordinal=? AND MotorID=?", carId, motorId.Value) ?? 0) == 0))
        { Log("choose a motor currently linked to this car", "warn"); return; }
        var affectedCars = new HashSet<long> { carId };
        try
        {
            Exec("SAVEPOINT power_builder");

            if (doBoost)
            {
                var boost = ApplyHighestTurboScale(engineId.Value, boostScale);
                int rows = boost == null ? 0 : 1;
                foreach (long id in CarsUsingEngine(engineId.Value)) affectedCars.Add(id);
                Log(boost == null
                    ? "boost scale skipped: this engine has no turbo upgrade row"
                    : $"boost scale applied to highest turbo Level {boost.Level}: {boost.Scale0:0.####} / {boost.Scale1:0.####} (original ratio preserved)",
                    rows > 0 ? "ok" : "warn");
            }

            if (doRedline)
            {
                int rows = 0, capped = 0;
                foreach (var cam in Query("SELECT Id,TorqueCurveMaxRPM FROM List_UpgradeEngineCamshaft WHERE EngineID=?", engineId.Value))
                {
                    double curveMax = Convert.ToDouble(cam["TorqueCurveMaxRPM"] ?? redlineRpm);
                    double applied = Math.Min(redlineRpm, curveMax);
                    if (applied < redlineRpm) capped++;
                    rows += Exec("UPDATE List_UpgradeEngineCamshaft SET RedlineRPM=? WHERE Id=?", applied, cam["Id"]);
                }
                foreach (long id in CarsUsingEngine(engineId.Value)) affectedCars.Add(id);
                Log($"redline requested {redlineRpm:0} RPM across {rows} camshaft row(s){(capped > 0 ? $" · {capped} capped by TorqueCurveMaxRPM" : "")}", rows > 0 ? "ok" : "warn");
            }

            if (doEngineMass)
            {
                int rows = Exec("UPDATE Data_Engine SET \"EngineMass-kg\"=? WHERE EngineID=?", engineMass, engineId.Value);
                foreach (long id in CarsUsingEngine(engineId.Value)) affectedCars.Add(id);
                Log($"engine mass set to {engineMass:0.###} kg for EngineID {engineId.Value}", rows > 0 ? "ok" : "warn");
            }

            if (doWeight)
            {
                int rows = Exec(@"UPDATE List_UpgradeCarBodyWeight SET CMBackFront=?
                                  WHERE Level=2 AND CarBodyId IN
                                    (SELECT CarBodyID FROM List_UpgradeCarBody WHERE Ordinal=?)",
                                weightDistribution, carId);
                Log($"Level 2 weight distribution set to {weightDistribution:0.####} ({weightDistribution * 100:0.##}%) on {rows} body row(s)" +
                    (weightDistribution < 0.40 ? " · wheelie-prone" : ""), rows > 0 ? "ok" : "warn");
            }

            if (doEv)
            {
                var parts = Query("SELECT Id,Level,IsStock FROM List_UpgradeMotorParts WHERE MotorID=? ORDER BY Level,Id", motorId.Value);
                long maxLevel = parts.Where(r => Convert.ToInt64(r["IsStock"] ?? 0L) == 0)
                    .Select(r => Convert.ToInt64(r["Level"] ?? 0L)).DefaultIfEmpty(0).Max();
                if (maxLevel <= 0) throw new InvalidOperationException("selected motor has no non-stock motor-parts upgrades to scale");
                foreach (var part in parts)
                {
                    bool stock = Convert.ToInt64(part["IsStock"] ?? 0L) == 1;
                    long level = Convert.ToInt64(part["Level"] ?? 0L);
                    double scale = stock ? 1d : 1d + (evMaxScale - 1d) * Math.Clamp(level / (double)maxLevel, 0d, 1d);
                    Exec("UPDATE List_UpgradeMotorParts SET TorqueScale=? WHERE Id=?", Math.Round(scale, 6), part["Id"]);
                }
                foreach (long id in CarsUsingMotor(motorId.Value)) affectedCars.Add(id);
                Log($"EV torque ladder spread evenly from 1.0× to {evMaxScale:0.####}× across {parts.Count} row(s)", "ok");
            }

            Exec("RELEASE power_builder");
            MarkModified(affectedCars.ToArray());
            LoadMotors();
            RenderEngines();
            PopulatePowerBuilder();
            RenderCars();
            Log($"power builder complete · {_modifiedCars.Count} car(s) marked modified this session", "ok");
        }
        catch (Exception ex)
        {
            try { Exec("ROLLBACK TO power_builder"); Exec("RELEASE power_builder"); } catch { }
            Log("power builder failed: " + ex.Message, "err");
        }
    }

    void ApplyBatchPowerBuilder(IReadOnlyCollection<CarItem> cars, bool confirm = true)
    {
        bool doBoost = OptPowerBoost.IsChecked == true;
        bool doRedline = OptPowerRedline.IsChecked == true;
        bool doWeight = OptPowerWeight.IsChecked == true;
        bool doEv = OptPowerEvTorque.IsChecked == true;
        if (!doBoost && !doRedline && !doWeight && !doEv)
        { Log("select at least one power-builder setting", "warn"); return; }

        double boostScale = 0, redlineRpm = 0, weightDistribution = 0, evMaxScale = 0;
        if (doBoost && !TryPowerNumber(PowerBoostScale, "boost scale", 0.01, 100, out boostScale)) return;
        if (doRedline && !TryPowerNumber(PowerRedline, "redline", 500, 30000, out redlineRpm)) return;
        if (doWeight)
        {
            if (!TryPowerNumber(PowerWeightDistribution, "weight distribution", 0, 100, out weightDistribution)) return;
            if (weightDistribution > 1) weightDistribution /= 100d;
        }
        if (doEv && !TryPowerNumber(PowerEvTorque, "EV torque scale", 0.01, 100, out evMaxScale)) return;
        if (confirm && MessageBox.Show($"Apply the selected power settings to the stock powertrain of all {cars.Count} selected cars?\n\n" +
                                       "Engine and motor records are shared by ID, so unselected cars using those IDs may also change.",
                                       "Confirm batch power edit", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        long[] engineIds = cars.Select(c => ScalarL("SELECT EngineID FROM List_UpgradeEngine WHERE Ordinal=? AND IsStock=1 LIMIT 1", c.Id))
            .Where(id => id.HasValue).Select(id => id.Value).Distinct().ToArray();
        long[] motorIds = cars.Select(c => ScalarL("SELECT MotorID FROM List_UpgradeMotor WHERE Ordinal=? AND IsStock=1 LIMIT 1", c.Id))
            .Where(id => id.HasValue).Select(id => id.Value).Distinct().ToArray();
        var affectedCars = cars.Select(c => c.Id).ToHashSet();
        try
        {
            Exec("SAVEPOINT batch_power_builder");
            int boostRows = 0, camRows = 0, capped = 0, weightRows = 0, motorRows = 0;
            if (doBoost)
                foreach (long engineId in engineIds)
                {
                    if (ApplyHighestTurboScale(engineId, boostScale) != null) boostRows++;
                    foreach (long id in CarsUsingEngine(engineId)) affectedCars.Add(id);
                }
            if (doRedline)
                foreach (long engineId in engineIds)
                {
                    foreach (var cam in Query("SELECT Id,TorqueCurveMaxRPM FROM List_UpgradeEngineCamshaft WHERE EngineID=?", engineId))
                    {
                        double curveMax = Convert.ToDouble(cam["TorqueCurveMaxRPM"] ?? redlineRpm);
                        double applied = Math.Min(redlineRpm, curveMax);
                        if (applied < redlineRpm) capped++;
                        camRows += Exec("UPDATE List_UpgradeEngineCamshaft SET RedlineRPM=? WHERE Id=?", applied, cam["Id"]);
                    }
                    foreach (long id in CarsUsingEngine(engineId)) affectedCars.Add(id);
                }
            if (doWeight)
                foreach (var car in cars)
                    weightRows += Exec(@"UPDATE List_UpgradeCarBodyWeight SET CMBackFront=?
                                         WHERE Level=2 AND CarBodyId IN
                                           (SELECT CarBodyID FROM List_UpgradeCarBody WHERE Ordinal=?)",
                                       weightDistribution, car.Id);
            if (doEv)
                foreach (long motorId in motorIds)
                {
                    var parts = Query("SELECT Id,Level,IsStock FROM List_UpgradeMotorParts WHERE MotorID=? ORDER BY Level,Id", motorId);
                    long maxLevel = parts.Where(r => Convert.ToInt64(r["IsStock"] ?? 0L) == 0)
                        .Select(r => Convert.ToInt64(r["Level"] ?? 0L)).DefaultIfEmpty(0).Max();
                    if (maxLevel <= 0) continue;
                    foreach (var part in parts)
                    {
                        bool stock = Convert.ToInt64(part["IsStock"] ?? 0L) == 1;
                        long level = Convert.ToInt64(part["Level"] ?? 0L);
                        double scale = stock ? 1d : 1d + (evMaxScale - 1d) * Math.Clamp(level / (double)maxLevel, 0d, 1d);
                        motorRows += Exec("UPDATE List_UpgradeMotorParts SET TorqueScale=? WHERE Id=?", Math.Round(scale, 6), part["Id"]);
                    }
                    foreach (long id in CarsUsingMotor(motorId)) affectedCars.Add(id);
                }
            Exec("RELEASE batch_power_builder");
            MarkModified(affectedCars.ToArray());
            LoadMotors(); RenderEngines(); RenderCars(); PickCar();
            Log($"batch power complete · boost {boostRows} row(s) · redline {camRows} row(s)" +
                (capped > 0 ? $" ({capped} capped)" : "") + $" · weight {weightRows} row(s) · motor {motorRows} row(s)", "ok");
        }
        catch (Exception ex)
        {
            try { Exec("ROLLBACK TO batch_power_builder"); Exec("RELEASE batch_power_builder"); } catch { }
            Log("batch power builder failed: " + ex.Message, "err");
        }
    }

    void Autoshow_Click(object s, RoutedEventArgs e)
    {
        if (_car == null) return;
        bool avail = OptAutoshow.IsChecked == true;
        var targets = SelectedCars();
        foreach (var car in targets)
            Exec("UPDATE Data_Car SET NotAvailableInAutoshow=? WHERE Id=?", avail ? 0 : 1, car.Id);
        RefreshModifiedAfterExcludedEdit();
        string subject = targets.Count > 1 ? $"{targets.Count} selected cars" : _car.Media;
        Log(avail ? $"✓ {subject} now show in the Autoshow" : $"{subject} hidden from the Autoshow", avail ? "ok" : "warn");
        if (targets.Count > 1) PickCar(); else ShowCarInfo();
    }

    void SetFeCarsAutoshowAvailability(bool available)
    {
        if (_conn == null) return;
        const string feCars = @"MediaName GLOB '*FE_[0-9][0-9]'
                                OR MediaName GLOB '*FE_[0-9][0-9][0-9][0-9]'";
        long total = ScalarL($"SELECT COUNT(*) FROM Data_Car WHERE {feCars}") ?? 0;
        if (total == 0) { Log("no Forza Edition cars found", "warn"); return; }

        int unavailable = available ? 0 : 1;
        int changed = Exec($@"UPDATE Data_Car SET NotAvailableInAutoshow=?
                              WHERE ({feCars}) AND NotAvailableInAutoshow<>?", unavailable, unavailable);
        RefreshModifiedAfterExcludedEdit();
        Log(available
                ? $"made all {total} FE cars available in the Autoshow · {changed} newly enabled"
                : $"hid all {total} FE cars from the Autoshow · {changed} newly hidden",
            available ? "ok" : "warn");
        if (_car != null) ShowCarInfo();
    }

    void SetAllCarPricesToOne()
    {
        if (_conn == null) return;
        int changed = Exec("UPDATE Data_Car SET BaseCost=1 WHERE BaseCost<>1");
        RefreshModifiedAfterExcludedEdit();
        Log($"set {changed} car prices to 1 CR", "ok");
    }

    void RevertAllCarPrices()
    {
        if (_conn == null || _loadedBaseCosts.Count == 0) return;
        try
        {
            Exec("BEGIN IMMEDIATE");
            foreach (var pair in _loadedBaseCosts)
                Exec("UPDATE Data_Car SET BaseCost=? WHERE Id=?", pair.Value, pair.Key);
            Exec("COMMIT");
            RefreshModifiedAfterExcludedEdit();
            Log($"restored {_loadedBaseCosts.Count} loaded car prices", "ok");
        }
        catch (Exception ex)
        {
            try { Exec("ROLLBACK"); } catch { }
            Log("price restore failed: " + ex.Message, "err");
        }
    }

    Dictionary<string, object> BestHandlingDonor()
    {
        string cols = string.Join(",", HandlingColumns.Select(c => $"\"{c}\""));
        return Query($@"SELECT Id,MediaName,{cols} FROM Data_Car
                        WHERE MediaName='LOT_00_ExigeWTA_18' LIMIT 1").FirstOrDefault();
    }

    static string QuotedColumns(IEnumerable<string> columns) =>
        string.Join(",", columns.Select(c => $"\"{c.Replace("\"", "\"\"")}\""));

    void CaptureRows(HandlingSnapshot snapshot, string table, string keyColumn, string[] columns, string whereSql, params object[] args)
    {
        foreach (var row in Query($"SELECT \"{keyColumn}\",{QuotedColumns(columns)} FROM \"{table}\" WHERE {whereSql}", args))
        {
            var values = columns.ToDictionary(c => c, c => row[c]);
            snapshot.Fields.Add(new DbFieldSnapshot(table, keyColumn, row[keyColumn], values));
        }
    }

    HandlingSnapshot CaptureHandlingSnapshot(long carId)
    {
        var snapshot = new HandlingSnapshot();
        CaptureRows(snapshot, "Data_Car", "Id", HandlingColumns, "Id=?", carId);
        CaptureRows(snapshot, "List_SpringDamperPhysics", "SpringDamperPhysicsID", SpringHandlingColumns,
            @"SpringDamperPhysicsID IN (
                SELECT FrontSpringDamperPhysicsID FROM List_UpgradeSpringDamper WHERE Ordinal=?
                UNION SELECT RearSpringDamperPhysicsID FROM List_UpgradeSpringDamper WHERE Ordinal=?)", carId, carId);
        CaptureRows(snapshot, "List_AntiSwayPhysics", "AntiSwayPhysicsID", AntiSwayHandlingColumns,
            @"AntiSwayPhysicsID IN (
                SELECT AntiSwayPhysicsID FROM List_UpgradeAntiSwayFront WHERE Ordinal=?
                UNION SELECT AntiSwayPhysicsID FROM List_UpgradeAntiSwayRear WHERE Ordinal=?)", carId, carId);
        CaptureRows(snapshot, "List_UpgradeCarBodyWeight", "Id", WeightHandlingColumns,
            "CarBodyId IN (SELECT CarBodyID FROM List_UpgradeCarBody WHERE Ordinal=?)", carId);
        CaptureRows(snapshot, "List_UpgradeCarBodyChassisStiffness", "Id", ChassisHandlingColumns,
            "CarbodyId IN (SELECT CarBodyID FROM List_UpgradeCarBody WHERE Ordinal=?)", carId);
        CaptureRows(snapshot, "List_UpgradeTireCompound", "Id", TireHandlingColumns, "Ordinal=?", carId);
        CaptureRows(snapshot, "List_UpgradeTireCompoundFictionModOverride", "PartId", TireOverrideColumns,
            "PartId IN (SELECT Id FROM List_UpgradeTireCompound WHERE Ordinal=?)", carId);
        return snapshot;
    }

    void SetColumns(string table, string keyColumn, object keyValue, string[] columns,
                    Dictionary<string, object> source, double scale = 1d, HashSet<string> scaledColumns = null)
    {
        string setters = string.Join(",", columns.Select(c => $"\"{c}\"=?"));
        object[] values = columns.Select(c =>
        {
            object value = source[c];
            if (value != null && scaledColumns?.Contains(c) == true)
                value = Math.Round(Convert.ToDouble(value) * scale, 6);
            return value;
        }).Append(keyValue).ToArray();
        Exec($"UPDATE \"{table}\" SET {setters} WHERE \"{keyColumn}\"=?", values);
    }

    Dictionary<string, object> RowByKey(string table, string keyColumn, object keyValue, string[] columns) =>
        Query($"SELECT {QuotedColumns(columns)} FROM \"{table}\" WHERE \"{keyColumn}\"=?", keyValue).FirstOrDefault();

    static double RowDouble(Dictionary<string, object> row, string column) => Convert.ToDouble(row[column]);

    static Dictionary<string, object> EnhancedSpringPhysics(Dictionary<string, object> source)
    {
        var result = new Dictionary<string, object>(source);
        foreach (string column in MassScaledSpringColumns)
        {
            double multiplier = column.Contains("Spring", StringComparison.OrdinalIgnoreCase)
                ? EnhancedSpringRate : EnhancedDamperRate;
            result[column] = Math.Round(RowDouble(source, column) * multiplier, 6);
        }
        return result;
    }

    static void EnhanceLotusDataCar(Dictionary<string, object> donor)
    {
        // The WTA rows establish 2.0 as a game-used scalar ceiling. Keeping the
        // values at that observed ceiling avoids arbitrary out-of-range grip.
        foreach (string column in new[]
        {
            "LatFrontScalarMax", "LatFrontScalarClamp", "LatRearScalarMax", "LatRearScalarClamp",
            "LongAccelFrontScalarMax", "LongAccelFrontScalarClamp", "LongAccelRearScalarMax", "LongAccelRearScalarClamp",
            "LongBrakeFrontScalarMax", "LongBrakeFrontScalarClamp", "LongBrakeRearScalarMax", "LongBrakeRearScalarClamp"
        }) donor[column] = 2d;

        donor["BodyAeroForwardDownforceFront"] = Math.Round(RowDouble(donor, "BodyAeroForwardDownforceFront") * EnhancedAeroForce, 6);
        donor["BodyAeroForwardDownforceRear"] = Math.Round(RowDouble(donor, "BodyAeroForwardDownforceRear") * EnhancedAeroForce, 6);
        donor["BodyAeroLateralDragFront"] = Math.Round(RowDouble(donor, "BodyAeroLateralDragFront") * EnhancedAeroForce, 6);
        donor["BodyAeroLateralDragRear"] = Math.Round(RowDouble(donor, "BodyAeroLateralDragRear") * EnhancedAeroForce, 6);
        donor["FrontDownforceClampKG"] = Math.Round(RowDouble(donor, "FrontDownforceClampKG") * EnhancedDownforceScale, 6);
        donor["RearDownforceClampKG"] = Math.Round(RowDouble(donor, "RearDownforceClampKG") * EnhancedDownforceScale, 6);
        donor["GameDownforceScale"] = EnhancedDownforceScale;

        // Display/PI simulation values follow the physical multipliers rather
        // than still advertising the untouched donor's figures.
        donor["SimLatGees60MPH"] = Math.Round(RowDouble(donor, "SimLatGees60MPH") * EnhancedTireLateralGrip, 4);
        donor["SimLatGees120MPH"] = Math.Round(RowDouble(donor, "SimLatGees120MPH") * EnhancedTireLateralGrip * EnhancedAeroForce * EnhancedDownforceScale, 4);
        donor["HandlingRating"] = 10d;
        donor["BrakingRating"] = Math.Min(10d, Math.Round(RowDouble(donor, "BrakingRating") * EnhancedTireLongitudinalGrip, 4));
        donor["Traction_Road"] = Math.Min(1d, Math.Round(RowDouble(donor, "Traction_Road") * EnhancedTireLateralGrip, 4));
    }

    void ApplyBestHandling()
    {
        if (_conn == null || _car == null) { Log("pick a car first", "warn"); return; }
        var donor = BestHandlingDonor();
        if (donor == null) { Log("Lotus handling skipped: LOT_00_ExigeWTA_18 is not in this DB", "warn"); return; }
        EnhanceLotusDataCar(donor);

        long carId = _car.Id, donorId = Convert.ToInt64(donor["Id"]);
        bool firstApply = !_handlingSnapshots.TryGetValue(carId, out var snapshot);
        if (firstApply)
        {
            snapshot = CaptureHandlingSnapshot(carId);
            _handlingSnapshots[carId] = snapshot;
        }

        try
        {
            Exec("SAVEPOINT lotus_handling");
            if (firstApply)
            {
                EnsureSuspensionUpgrade(carId, 3, snapshot.Created);
                EnsureSuspensionUpgrade(carId, 4, snapshot.Created);
                EnsureSuspensionUpgrade(carId, 5, snapshot.Created);
                EnsureAntiSwayUpgrade(carId, false, snapshot.Created);
                EnsureAntiSwayUpgrade(carId, true, snapshot.Created);
                EnsureRoadTireRow(carId, donorId, snapshot.Created);
                EnsureBodyHandlingUpgrades(carId, snapshot.Created);
            }

            SetColumns("Data_Car", "Id", carId, HandlingColumns, donor);
            double targetMass = Convert.ToDouble(Scalar("SELECT CurbWeight FROM Data_Car WHERE Id=?", carId) ?? 1d);
            double donorMass = Convert.ToDouble(Scalar("SELECT CurbWeight FROM Data_Car WHERE Id=?", donorId) ?? 1d);
            double massScale = Math.Clamp(targetMass / Math.Max(0.01, donorMass), 0.25, 6.0);

            var donorSuspensions = Query("SELECT * FROM List_UpgradeSpringDamper WHERE Ordinal=? ORDER BY IsStock DESC,Level", donorId);
            var donorStockSuspension = donorSuspensions.First(r => Convert.ToInt64(r["IsStock"] ?? 0L) == 1);
            foreach (var target in Query("SELECT * FROM List_UpgradeSpringDamper WHERE Ordinal=?", carId))
            {
                long level = Convert.ToInt64(target["Level"] ?? 0L);
                var source = donorSuspensions.FirstOrDefault(r => Convert.ToInt64(r["Level"] ?? 0L) == level) ?? donorStockSuspension;
                foreach (var pair in new[] { ("FrontSpringDamperPhysicsID", "FrontSpringDamperPhysicsID"), ("RearSpringDamperPhysicsID", "RearSpringDamperPhysicsID") })
                {
                    long sourceId = Convert.ToInt64(source[pair.Item1]);
                    long targetId = Convert.ToInt64(target[pair.Item2]);
                    var sourcePhysics = RowByKey("List_SpringDamperPhysics", "SpringDamperPhysicsID", sourceId, SpringHandlingColumns);
                    if (sourcePhysics != null)
                    {
                        sourcePhysics = EnhancedSpringPhysics(sourcePhysics);
                        SetColumns("List_SpringDamperPhysics", "SpringDamperPhysicsID", targetId,
                                   SpringHandlingColumns, sourcePhysics, massScale, MassScaledSpringColumns);
                    }
                }
            }

            int antiRows = 0;
            foreach (var (table, rear) in new[] { ("List_UpgradeAntiSwayFront", false), ("List_UpgradeAntiSwayRear", true) })
            {
                string donorTable = rear ? "List_UpgradeAntiSwayRear" : "List_UpgradeAntiSwayFront";
                long? sourceId = ScalarL($"SELECT AntiSwayPhysicsID FROM {donorTable} WHERE Ordinal=? AND IsStock=1", donorId);
                if (sourceId == null) continue;
                var sourcePhysics = RowByKey("List_AntiSwayPhysics", "AntiSwayPhysicsID", sourceId.Value, AntiSwayHandlingColumns);
                if (sourcePhysics == null) continue;
                foreach (var target in Query($"SELECT AntiSwayPhysicsID FROM {table} WHERE Ordinal=?", carId))
                {
                    SetColumns("List_AntiSwayPhysics", "AntiSwayPhysicsID", target["AntiSwayPhysicsID"],
                               AntiSwayHandlingColumns, sourcePhysics, massScale * EnhancedAntiRollRate,
                               new HashSet<string> { "DefSwaybarStiffness", "MinSwaybarStiffness", "MaxSwaybarStiffness", "BeamStiffness" });
                    antiRows++;
                }
            }

            long donorBody = ScalarL("SELECT CarBodyID FROM List_UpgradeCarBody WHERE Ordinal=? AND IsStock=1", donorId) ?? donorId * 1000;
            var donorWeight = Query("SELECT CMHeight,CMBackFront FROM List_UpgradeCarBodyWeight WHERE CarBodyId=? AND IsStock=1", donorBody).FirstOrDefault();
            int weightRows = 0;
            if (donorWeight != null)
            {
                // Rollover threshold is inversely proportional to CG height.
                // 70% of the Lotus value is 0.2066, still above the DB's 0.17 minimum.
                donorWeight["CMHeight"] = Math.Max(0.20, Math.Round(RowDouble(donorWeight, "CMHeight") * 0.70, 4));
                foreach (var target in Query("SELECT Id FROM List_UpgradeCarBodyWeight WHERE CarBodyId IN (SELECT CarBodyID FROM List_UpgradeCarBody WHERE Ordinal=?)", carId))
                { SetColumns("List_UpgradeCarBodyWeight", "Id", target["Id"], WeightHandlingColumns, donorWeight); weightRows++; }
            }

            var donorChassis = Query($"SELECT {QuotedColumns(ChassisHandlingColumns)} FROM List_UpgradeCarBodyChassisStiffness WHERE CarbodyId=? AND IsStock=1", donorBody).FirstOrDefault();
            int chassisRows = 0;
            if (donorChassis != null)
            {
                foreach (string column in new[] { "FrontLatFrictionScale", "RearLatFrictionScale", "FrontLongFrictionScale", "RearLongFrictionScale" })
                    donorChassis[column] = 1.05d;
                foreach (var target in Query("SELECT Id FROM List_UpgradeCarBodyChassisStiffness WHERE CarbodyId IN (SELECT CarBodyID FROM List_UpgradeCarBody WHERE Ordinal=?)", carId))
                { SetColumns("List_UpgradeCarBodyChassisStiffness", "Id", target["Id"], ChassisHandlingColumns, donorChassis); chassisRows++; }
            }

            var donorTire = Query("SELECT TireCompoundID,FrontTirePressure,RearTirePressure FROM List_UpgradeTireCompound WHERE Ordinal=? AND IsStock=1", donorId).FirstOrDefault();
            int tireRows = 0;
            if (donorTire != null)
                foreach (var target in Query(@"SELECT Id FROM List_UpgradeTireCompound WHERE Ordinal=?
                                               AND Level NOT IN (5,7,8,9)", carId))
                { SetColumns("List_UpgradeTireCompound", "Id", target["Id"], TireHandlingColumns, donorTire); tireRows++; }

            int gripRows = ApplyEnhancedTireGrip(carId, snapshot.Created);

            Exec("RELEASE lotus_handling");
            MarkModified(carId);
            Log($"✓ enhanced Lotus handling applied · all suspension levels · {antiRows} anti-roll · {weightRows} low-CG · {chassisRows} chassis · {gripRows}/{tireRows} grip/tire rows", "ok");
            ShowCarInfo();
        }
        catch (Exception ex)
        {
            try { Exec("ROLLBACK TO lotus_handling"); Exec("RELEASE lotus_handling"); } catch { }
            if (firstApply) _handlingSnapshots.Remove(carId);
            Log("Lotus handling failed: " + ex.Message, "err");
        }
    }

    void RevertHandling()
    {
        if (_conn == null || _car == null) { Log("pick a car first", "warn"); return; }
        if (!_handlingSnapshots.TryGetValue(_car.Id, out var snapshot))
        { Log("handling revert skipped: this car has no handling snapshot in this session", "warn"); return; }

        try
        {
            Exec("SAVEPOINT revert_lotus_handling");
            foreach (var created in snapshot.Created.AsEnumerable().Reverse())
                Exec($"DELETE FROM \"{created.Table}\" WHERE \"{created.KeyColumn}\"=?", created.KeyValue);
            foreach (var saved in snapshot.Fields)
                SetColumns(saved.Table, saved.KeyColumn, saved.KeyValue, saved.Values.Keys.ToArray(), saved.Values);
            Exec("RELEASE revert_lotus_handling");
            _handlingSnapshots.Remove(_car.Id);
            MarkModified(_car.Id);
            Log($"✓ restored pre-Lotus handling values · removed {snapshot.Created.Count} generated rows", "ok");
            ShowCarInfo();
        }
        catch (Exception ex)
        {
            try { Exec("ROLLBACK TO revert_lotus_handling"); Exec("RELEASE revert_lotus_handling"); } catch { }
            Log("handling revert failed: " + ex.Message, "err");
        }
    }

    // On selecting a car, show its current tire-width / offset / sidewall upgrade values so they're easy to tweak.
    // ---- dynamic fitment boxes (up to 99 per axle ID block) + per-body targets ----
    readonly List<TextBox> _rf = new(), _wf = new(), _wr = new(), _sw = new(), _ofF = new(), _ofR = new();
    readonly List<TabItem> _bodyTabs = new();
    const int MaxFit = 99;   // structural cap: each body owns a 100-wide Id block (bodyIndex*100)

    // Row Id for a body-fitment upgrade, matching the game's own scheme:
    //   Id = carId*1000 + bodyIndex*100 + unique row slot
    // where carBase = carId*1000 and bodyIndex = CarBodyId - carBase (0 = stock, 1/2/3 = kits).
    // The widebody's rows therefore live in the +100 block (4144100…), not CarBodyId+Level,
    // which would collide with the stock body's rows.
    static long FitId(long carBodyId, int slot)
    {
        long carBase = (carBodyId / 1000) * 1000;
        long bodyIndex = carBodyId - carBase;
        return carBase + bodyIndex * 100 + slot;
    }

    TextBox MakeNum(string text = "")
    {
        var tb = new TextBox { Width = 54, Margin = new Thickness(0, 0, 5, 4), Text = text };
        if (TryFindResource("NumBox") is Style st) tb.Style = st;
        return tb;
    }
    void AddNum(System.Windows.Controls.Panel panel, List<TextBox> list, string text = "")
    {
        if (list.Count >= MaxFit) return;
        var tb = MakeNum(text);
        panel.Children.Add(tb);
        list.Add(tb);
    }
    void AddWF(object s, RoutedEventArgs e) => AddNum(WFBoxes, _wf);
    void AddWR(object s, RoutedEventArgs e) => AddNum(WRBoxes, _wr);
    void AddRF(object s, RoutedEventArgs e) => AddNum(RFBoxes, _rf);
    void AddSW(object s, RoutedEventArgs e) => AddNum(SWBoxes, _sw);
    void AddOF(object s, RoutedEventArgs e) => AddNum(OFBoxes, _ofF);
    void AddOR(object s, RoutedEventArgs e) => AddNum(ORBoxes, _ofR);

    void AddBatchFitmentPreset(System.Windows.Controls.Panel panel, List<TextBox> values,
                               string label, double step, string toolTip)
    {
        int clicks = 0;
        var queuedValues = new List<TextBox>();
        var preset = new Button
        {
            Content = label,
            ToolTip = toolTip + " Each left-click queues the next level; right-click removes the last one.",
        };
        if (TryFindResource("BatchStepButton") is Style style) preset.Style = style;

        void RefreshButton()
        {
            preset.Content = clicks == 0 ? label : $"{label}   ×{clicks}";
            if (clicks > 0)
            {
                preset.Background = new SolidColorBrush(Color.FromArgb(0x55, 0xE8, 0x17, 0x5D));
                if (TryFindResource("Pink") is Brush pink) preset.BorderBrush = pink;
            }
            else
            {
                preset.ClearValue(Control.BackgroundProperty);
                preset.ClearValue(Control.BorderBrushProperty);
            }
        }

        preset.Click += (_, _) =>
        {
            if (values.Count >= MaxFit) return;
            clicks++;
            var backingValue = MakeNum((step * clicks).ToString("0.###", CultureInfo.InvariantCulture));
            backingValue.Visibility = Visibility.Collapsed;
            values.Add(backingValue);
            queuedValues.Add(backingValue);
            RefreshButton();
        };
        preset.MouseRightButtonUp += (_, e) =>
        {
            if (queuedValues.Count == 0) return;
            var backingValue = queuedValues[^1];
            queuedValues.RemoveAt(queuedValues.Count - 1);
            values.Remove(backingValue);
            clicks--;
            RefreshButton();
            e.Handled = true;
        };
        panel.Children.Add(preset);
    }

    void ShowSingleCarFitmentAddButtons(bool show)
    {
        var visibility = show ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { AddRFBtn, AddWFBtn, AddWRBtn, AddSWBtn, AddOFBtn, AddORBtn })
            button.Visibility = visibility;
    }

    void PrepareBatchFitmentTemplate()
    {
        foreach (var (panel, list) in new (System.Windows.Controls.Panel Panel, List<TextBox> Boxes)[]
                 {
                     (RFBoxes, _rf), (WFBoxes, _wf), (WRBoxes, _wr),
                     (SWBoxes, _sw), (OFBoxes, _ofF), (ORBoxes, _ofR),
                 })
        {
            panel.Children.Clear();
            list.Clear();
        }

        AddBatchFitmentPreset(RFBoxes, _rf, "−1  smaller", -1, "Add progressively smaller rim options from each car's stock size.");
        AddBatchFitmentPreset(RFBoxes, _rf, "+1  larger", 1, "Add progressively larger rim options from each car's stock size.");

        AddBatchFitmentPreset(WFBoxes, _wf, "−10  narrower", -10, "Add progressively narrower front tire options from stock.");
        AddBatchFitmentPreset(WFBoxes, _wf, "+10  wider", 10, "Add progressively wider front tire options from stock.");
        AddBatchFitmentPreset(WRBoxes, _wr, "−10  narrower", -10, "Add progressively narrower rear tire options from stock.");
        AddBatchFitmentPreset(WRBoxes, _wr, "+10  wider", 10, "Add progressively wider rear tire options from stock.");

        AddBatchFitmentPreset(SWBoxes, _sw, "−2  lower", -2, "Add progressively lower tire-profile options from stock.");
        AddBatchFitmentPreset(SWBoxes, _sw, "+2  taller", 2, "Add progressively taller tire-profile options from stock.");

        AddBatchFitmentPreset(OFBoxes, _ofF, "+0.02 m  wider", 0.02, "Add progressive front-track extensions from the widest existing option.");
        AddBatchFitmentPreset(ORBoxes, _ofR, "+0.02 m  wider", 0.02, "Add progressive rear-track extensions from the widest existing option.");
        ShowSingleCarFitmentAddButtons(false);
        TrackExampleText.Visibility = Visibility.Collapsed;
    }

    // Add a complete body target rather than only a display tab. Each car owns a
    // 1000-wide ID block; body indices 1-9 each receive a 100-wide child-row block.
    // Clone the stock body's dependent rows so the new kit has usable wheels,
    // fitment, aero/body-part menus, weight and chassis data from the start.
    void AddBodyKit_Click(object s, RoutedEventArgs e)
    {
        if (_car == null) { Log("pick a car first", "warn"); return; }
        if (SelectedCars().Count > 1) { Log("bodykits cannot be added in batch mode", "warn"); return; }

        long carId = _car.Id;
        var stock = Query("SELECT * FROM List_UpgradeCarBody WHERE Ordinal=? AND IsStock=1 ORDER BY Id LIMIT 1", carId).FirstOrDefault();
        if (stock == null) { Log("bodykit skipped: this car has no stock body row to clone", "warn"); return; }

        long stockBodyId = Convert.ToInt64(stock["CarBodyID"]);
        long newBodyId = 0;
        for (int bodyIndex = 1; bodyIndex <= 9; bodyIndex++)
        {
            long candidate = stockBodyId + bodyIndex;
            if ((ScalarL("SELECT COUNT(*) FROM List_UpgradeCarBody WHERE Ordinal=? AND CarBodyID=?", carId, candidate) ?? 0) == 0)
            {
                newBodyId = candidate;
                break;
            }
        }
        if (newBodyId == 0)
        {
            Log("bodykit skipped: all nine bodykit slots are already used for this car", "warn");
            return;
        }

        const string savepoint = "add_bodykit";
        try
        {
            Exec("SAVEPOINT " + savepoint);

            stock["Id"] = FreeBodyUpgradeId("List_UpgradeCarBody", newBodyId, 0);
            stock["Ordinal"] = carId;
            stock["Level"] = 1L;
            stock["CarBodyID"] = newBodyId;
            stock["IsStock"] = 0L;
            stock["ManufacturerID"] = 492L;
            stock["MassDiff"] = 0d;
            stock["Price"] = 5000L;
            stock["RequiresGraphics"] = 1L;
            stock["releaseOrder"] = 0L;
            InsertClonedRow("List_UpgradeCarBody", stock);

            int cloned = 0;
            foreach (var (table, bodyColumn) in new[]
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
                ("List_UpgradeCarBodyWeight", "CarBodyId")
            })
            {
                int slot = 0;
                foreach (var row in Query($"SELECT * FROM \"{table}\" WHERE \"{bodyColumn}\"=? ORDER BY Id", stockBodyId))
                {
                    row["Id"] = FreeBodyUpgradeId(table, newBodyId, slot++);
                    row[bodyColumn] = newBodyId;
                    InsertClonedRow(table, row);
                    cloned++;
                }
            }

            Exec("RELEASE " + savepoint);
            MarkModified(carId);
            PopulateBodyTargets(carId);
            var newTab = _bodyTabs.FirstOrDefault(t => Convert.ToInt64(t.Tag) == newBodyId);
            if (newTab != null) BodyTargets.SelectedItem = newTab;
            FillFitment();
            Log($"✓ added bodykit {newBodyId - stockBodyId} (CarBodyID {newBodyId}) · cloned {cloned} stock body rows", "ok");
        }
        catch (Exception ex)
        {
            try { Exec("ROLLBACK TO " + savepoint); Exec("RELEASE " + savepoint); } catch { }
            Log("bodykit creation failed: " + ex.Message, "err");
        }
    }

    // Build one tab per real CarBody row. Only the active tab is edited.
    void PopulateBodyTargets(long carId)
    {
        BodyTargets.Items.Clear(); _bodyTabs.Clear();
        var bodies = Query("SELECT CarBodyID, IsStock FROM List_UpgradeCarBody WHERE Ordinal=? ORDER BY IsStock DESC, CarBodyID", carId);
        if (bodies.Count == 0) { AddBodyTab("Stock body", carId * 1000, true); return; }
        int kitCount = bodies.Count(b => Convert.ToInt64(b["IsStock"]) != 1), kit = 0;
        foreach (var b in bodies)
        {
            long id = Convert.ToInt64(b["CarBodyID"]);
            bool stock = Convert.ToInt64(b["IsStock"]) == 1;
            string label = stock ? "Stock body" : (kitCount > 1 ? $"Widebody {++kit}" : "Widebody");
            AddBodyTab(label, id, stock);
        }
    }
    void AddBodyTab(string label, long bodyId, bool selected)
    {
        var tab = new TabItem { Header = label, Tag = bodyId, IsSelected = selected };
        BodyTargets.Items.Add(tab); _bodyTabs.Add(tab);
    }
    long[] SelectedBodies()
    {
        if (_batchRunning && _car != null) return new[] { BodyId(_car.Id) };
        return BodyTargets.SelectedItem is TabItem tab
            ? new[] { (long)tab.Tag }
            : (_car != null ? new[] { BodyId(_car.Id) } : System.Array.Empty<long>());
    }
    long FirstBody() { var s = SelectedBodies(); return s.Length > 0 ? s[0] : (_car != null ? BodyId(_car.Id) : 0); }
    void BodyTarget_Changed(object s, SelectionChangedEventArgs e)
    {
        if (_car != null && BodyTargets.SelectedItem is TabItem) FillFitment();
    }

    // Fill one axle's boxes from the non-stock rows on `body`; always show at least 3 boxes.
    void FillDyn(System.Windows.Controls.Panel panel, List<TextBox> list, string sql, string fmt, long body, int minimumBoxes = 3)
    {
        panel.Children.Clear(); list.Clear();
        var vals = Query(sql, body).Select(r => Convert.ToDouble(r["v"])).ToList();
        foreach (var v in vals.Take(MaxFit)) AddNum(panel, list, v.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture));
        while (list.Count < minimumBoxes) AddNum(panel, list, "");
    }
    void FillFitment()
    {
        if (_car == null || _batchRunning) return;
        long body = FirstBody();
        bool widebody = BodyTargets.SelectedItem is TabItem selected &&
                        !string.Equals(selected.Header?.ToString(), "Stock body", StringComparison.OrdinalIgnoreCase);
        TrackExampleText.Visibility = widebody ? Visibility.Visible : Visibility.Collapsed;
        // Value-first ordering keeps the editor readable even for a database
        // created by an older build whose generated row IDs were appended.
        FillDyn(WFBoxes, _wf, "SELECT FrontTireWidth v FROM List_UpgradeCarBodyTireWidthFront WHERE CarBodyId=? AND IsStock=0 ORDER BY v,Id", "0", body);
        FillDyn(WRBoxes, _wr, "SELECT RearTireWidth v FROM List_UpgradeCarBodyTireWidthRear WHERE CarBodyId=? AND IsStock=0 ORDER BY v,Id", "0", body);
        FillDyn(SWBoxes, _sw, "SELECT FrontTireAspectRatioOffset v FROM List_UpgradeCarBodyTireAspectRatioFront WHERE CarBodyId=? AND IsStock=0 ORDER BY v,Id", "0.##", body);
        // New widebodies have no non-stock rows, so these begin blank. Once Apply
        // creates custom offsets, switching back to the tab reads them from the DB.
        FillDyn(OFBoxes, _ofF, "SELECT Spacing v FROM List_UpgradeCarBodyTrackSpacingFront WHERE CarBodyId=? AND IsStock=0 ORDER BY v,Id", "0.###", body);
        FillDyn(ORBoxes, _ofR, "SELECT Spacing v FROM List_UpgradeCarBodyTrackSpacingRear WHERE CarBodyId=? AND IsStock=0 ORDER BY v,Id", "0.###", body);
    }
    void PopulateFitmentFields()
    {
        if (_car == null || _batchRunning) return;
        PopulateBodyTargets(_car.Id);
        FillRims(_car.Id);
        FillFitment();
    }

    void FillRims(long carId)
    {
        int boxCount = (int)(ScalarL(@"WITH sizes AS (
                                          SELECT Ordinal,FrontWheelDiameter v FROM List_UpgradeRimSizeFront WHERE IsStock=0
                                          UNION ALL
                                          SELECT Ordinal,RearWheelDiameter v FROM List_UpgradeRimSizeRear WHERE IsStock=0
                                        ), perCar AS (
                                          SELECT Ordinal,COUNT(DISTINCT v) n FROM sizes GROUP BY Ordinal
                                        ) SELECT MAX(n) FROM perCar") ?? 0);
        boxCount = Math.Max(3, boxCount);
        FillDyn(RFBoxes, _rf, @"WITH target(Ordinal) AS (VALUES (?))
                                SELECT v FROM (
                                  SELECT FrontWheelDiameter v FROM List_UpgradeRimSizeFront WHERE Ordinal=(SELECT Ordinal FROM target) AND IsStock=0
                                  UNION
                                  SELECT RearWheelDiameter v FROM List_UpgradeRimSizeRear WHERE Ordinal=(SELECT Ordinal FROM target) AND IsStock=0
                                ) ORDER BY v", "0", carId, boxCount);
    }
    void ResetFitment_Click(object s, RoutedEventArgs e) => RunForSelectedCars("reset stock-body fitment", ResetCurrentFitment);

    void ResetCurrentFitment()
    {
        if (_car == null) { Log("pick a car first", "warn"); return; }
        var bodies = SelectedBodies();
        try
        {
            foreach (var bd in bodies)
                foreach (var t in new[] {
                    "List_UpgradeCarBodyTireWidthFront","List_UpgradeCarBodyTireWidthRear",
                    "List_UpgradeCarBodyTireAspectRatioFront","List_UpgradeCarBodyTireAspectRatioRear",
                    "List_UpgradeCarBodyTrackSpacingFront","List_UpgradeCarBodyTrackSpacingRear" })
                    Exec($"DELETE FROM \"{t}\" WHERE CarBodyId=? AND IsStock=0", bd);
            MarkModified(_car.Id);
            Log($"↺ fitment reset to stock on {bodies.Length} body/bodies", "ok");
            FillFitment();
        }
        catch (Exception ex) { Log("reset failed: " + ex.Message, "err"); }
    }

    HashSet<long> UsedEngines() => Query("SELECT EngineID FROM List_UpgradeEngine WHERE Ordinal=?", _car.Id).Select(r => Convert.ToInt64(r["EngineID"])).ToHashSet();
    EngItem SelEng() => EngList.SelectedItem as EngItem;
    MotorItem SelMot() => EngList.SelectedItem as MotorItem;
    bool MotorMode => ModeCombo?.SelectedIndex == 1;
    string MotorName(long id) { var m = _motors.FirstOrDefault(x => x.Id == id); return m == null ? ("#" + id) : (string.IsNullOrWhiteSpace(m.Name) ? m.Media : m.Name); }

    void RenderMotors()
    {
        var term = (EngSearch.Text ?? "").ToLower();
        EngList.ItemsSource = _motors.Where(m => term.Length == 0 || (m.Name + " " + m.Media + " " + Naming.Friendly(m.Media)).ToLower().Contains(term)).ToList();
    }
    void RenderEngines()
    {
        if (EngList == null || _batchRunning) return;
        if (MotorMode) { RenderMotors(); return; }
        if (_car == null) return;
        var term = (EngSearch.Text ?? "").ToLower();
        var mk = MakeFilter.SelectedIndex > 0 ? (string)MakeFilter.SelectedItem : "";
        var ty = TypeFilter?.SelectedIndex > 0 ? (string)TypeFilter.SelectedItem : "";
        var hide = HideUsed.IsChecked == true;
        var used = UsedEngines();
        EngList.ItemsSource = _engines.Where(en =>
        {
            if (term.Length > 0 && !(en.Name + " " + en.Media + " " + Naming.Friendly(en.Media)).ToLower().Contains(term)) return false;
            if (mk.Length > 0 && en.Media.Split('_')[0] != mk) return false;
            if (ty.Length > 0 && en.EngType != ty) return false;
            if (hide && used.Contains(en.Id)) return false;
            return true;
        }).Take(600).ToList();
    }

    // ============================================================ operations
    long NextId(string table, long carId) => (ScalarL($"SELECT MAX(Id) FROM \"{table}\" WHERE Id>=? AND Id<?", carId * 1000, carId * 1000 + 1000) ?? carId * 1000) + 1;
    long NextLevel(string table, string keyCol, long keyVal) => (ScalarL($"SELECT MAX(Level) FROM \"{table}\" WHERE \"{keyCol}\"=?", keyVal) ?? 0) + 1;

    // Upgrade menus use Id as their stable row order. Batch additions can be
    // smaller than native choices, so merely appending them puts the values out
    // of order. Reassign the existing non-stock Id set to the same rows in value
    // order. No rows or stock IDs are removed, and these leaf tables declare no
    // foreign keys to their Id columns.
    void SortUpgradeRowsByValue(string table, string ownerColumn, long ownerId, string valueColumn)
    {
        var rows = Query($"SELECT Id,{valueColumn} v FROM \"{table}\" WHERE \"{ownerColumn}\"=? AND IsStock=0 ORDER BY {valueColumn},Id", ownerId);
        if (rows.Count < 2) return;

        var targetIds = rows.Select(row => Convert.ToInt64(row["Id"])).OrderBy(id => id).ToList();
        if (rows.Select(row => Convert.ToInt64(row["Id"])).SequenceEqual(targetIds)) return;

        const string savepoint = "sort_fitment_rows";
        try
        {
            Exec("SAVEPOINT " + savepoint);
            var temporaryIds = new List<long>(rows.Count);
            long candidate = long.MinValue + 1024;
            foreach (var row in rows)
            {
                while ((ScalarL($"SELECT COUNT(*) FROM \"{table}\" WHERE Id=?", candidate) ?? 0) != 0)
                    candidate++;
                temporaryIds.Add(candidate);
                Exec($"UPDATE \"{table}\" SET Id=? WHERE Id=?", candidate, row["Id"]);
                candidate++;
            }
            for (int i = 0; i < rows.Count; i++)
                Exec($"UPDATE \"{table}\" SET Id=? WHERE Id=?", targetIds[i], temporaryIds[i]);
            Exec("RELEASE " + savepoint);
        }
        catch
        {
            try { Exec("ROLLBACK TO " + savepoint); Exec("RELEASE " + savepoint); } catch { }
            throw;
        }
    }

    long EngManuf(long eid) => ScalarL("SELECT ManufacturerID FROM List_UpgradeEngine WHERE EngineID=? AND IsStock=1 LIMIT 1", eid) ?? 0;
    string EngName(long eid) => _engines.FirstOrDefault(x => x.Id == eid)?.Name ?? ("#" + eid);

    bool AddEngineSwap(long eid, bool quiet)
    {
        var carId = _car.Id;
        if (_electricSpecCars.Contains(carId))
        { if (!quiet) Log("set a stock combustion engine before adding engine swaps to an EV", "warn"); return false; }
        if ((ScalarL("SELECT COUNT(*) FROM List_UpgradeEngine WHERE Ordinal=? AND EngineID=?", carId, eid) ?? 0) > 0)
        { if (!quiet) Log("already on car: " + EngName(eid), "warn"); return false; }
        long id = NextId("List_UpgradeEngine", carId), lvl = NextLevel("List_UpgradeEngine", "Ordinal", carId);
        Exec(@"INSERT INTO List_UpgradeEngine (Id,Ordinal,Level,EngineID,IsStock,ManufacturerID,Price,MassDiff,WeightDistDiff,DragScale,WindInstabilityScale,releaseOrder)
               VALUES (?,?,?,?,0,?,50,0,0,1,1,0)", id, carId, lvl, eid, EngManuf(eid));
        MarkModified(carId);
        if (!quiet) Log("＋ swap added: " + EngName(eid), "ok");
        return true;
    }
    void AddAllMake()
    {
        if (_car == null) { Log("pick a car first", "warn"); return; }
        if (_electricSpecCars.Contains(_car.Id)) { Log("convert this EV to combustion before adding engine swaps", "warn"); return; }
        var mk = MakeFilter.SelectedIndex > 0 ? (string)MakeFilter.SelectedItem : "";
        if (mk.Length == 0) { Log("choose a make first", "warn"); return; }
        int n = 0; foreach (var en in _engines.Where(en => en.Media.Split('_')[0] == mk)) if (AddEngineSwap(en.Id, true)) n++;
        Log($"＋ added {n} {mk} engines", "ok"); RefreshCar();
    }
    void AddAllType()
    {
        if (_car == null) { Log("pick a car first", "warn"); return; }
        if (_electricSpecCars.Contains(_car.Id)) { Log("convert this EV to combustion before adding engine swaps", "warn"); return; }
        string type = TypeFilter.SelectedIndex > 0 ? (string)TypeFilter.SelectedItem : "";
        if (string.IsNullOrEmpty(type)) { Log("choose an engine type first (I4, I6, V6, V8, etc.)", "warn"); return; }

        var matches = _engines.Where(en => en.EngType == type).ToArray();
        int added = 0;
        foreach (var engine in matches)
            if (AddEngineSwap(engine.Id, true)) added++;

        Log($"＋ added {added} {type} engine{(added == 1 ? "" : "s")} ({matches.Length - added} already on car)", "ok");
        RefreshCar();
        RenderEngines();
    }
    void AddAllEngines()
    {
        if (_car == null) { Log("pick a car first", "warn"); return; }
        if (_electricSpecCars.Contains(_car.Id)) { Log("convert this EV to combustion before adding engine swaps", "warn"); return; }
        if (!_batchRunning && MessageBox.Show($"Add every engine in the game ({_engines.Count}) to this car?", "Confirm", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        int n = 0; foreach (var en in _engines) if (AddEngineSwap(en.Id, true)) n++;
        Log($"＋ added {n} engines (all)", "ok"); RefreshCar();
    }

    void SetStockEngine()
    {
        if (_car == null) { Log("pick a car first", "warn"); return; }
        if (SelEng() is not EngItem e) { Log("pick an engine first", "warn"); return; }
        var carId = _car.Id; var eid = e.Id;
        bool wasEV = _electricSpecCars.Contains(carId);
        long hadMotor = ScalarL("SELECT COUNT(*) FROM List_UpgradeMotor WHERE Ordinal=?", carId) ?? 0;
        var donor = Query("SELECT Id,PowertrainID,NumGears,EngineConfigID,CylinderID,AspirationTypeId,Displacement FROM Data_Car WHERE MediaName=? AND EngineConfigID<>6", e.Media).FirstOrDefault()
            ?? Query(@"SELECT c.Id,c.PowertrainID,c.NumGears,c.EngineConfigID,c.CylinderID,c.AspirationTypeId,c.Displacement
                       FROM Data_Car c JOIN List_UpgradeEngine le ON le.Ordinal=c.Id
                       WHERE le.EngineID=? AND le.IsStock=1 AND c.EngineConfigID<>6 ORDER BY c.Id LIMIT 1", eid).FirstOrDefault();
        if (wasEV && donor == null)
        { Log("cannot convert EV to ICE: this engine has no combustion donor car for a complete Data_Car spec", "warn"); return; }
        long manufacturer = EngManuf(eid);
        try
        {
            Exec("SAVEPOINT set_stock_engine");
            if (donor != null)
                Exec("UPDATE Data_Car SET PowertrainID=?,NumGears=?,EngineConfigID=?,CylinderID=?,AspirationTypeId=?,Displacement=? WHERE Id=?",
                    donor["PowertrainID"], donor["NumGears"], donor["EngineConfigID"], donor["CylinderID"], donor["AspirationTypeId"], donor["Displacement"], carId);
            else
            {
                var de = Query("SELECT ConfigID,CylinderID FROM Data_Engine WHERE EngineID=?", eid).First();
                Exec("UPDATE Data_Car SET EngineConfigID=?,CylinderID=? WHERE Id=?", de["ConfigID"], de["CylinderID"], carId);
            }

            long? existingLink = ScalarL("SELECT Id FROM List_UpgradeEngine WHERE Ordinal=? AND EngineID=? ORDER BY IsStock DESC,Id LIMIT 1", carId, eid);
            Exec("DELETE FROM List_UpgradeEngine WHERE Ordinal=? AND IsStock=1 AND EngineID<>?", carId, eid);
            if (existingLink.HasValue)
                Exec("UPDATE List_UpgradeEngine SET IsStock=1,Level=0,Price=0 WHERE Id=?", existingLink.Value);
            else
            {
                long id = NextId("List_UpgradeEngine", carId);
                Exec(@"INSERT INTO List_UpgradeEngine (Id,Ordinal,Level,EngineID,IsStock,ManufacturerID,Price,MassDiff,WeightDistDiff,DragScale,WindInstabilityScale,releaseOrder)
                       VALUES (?,?,0,?,1,?,0,0,0,1,1,0)", id, carId, eid, manufacturer);
            }
            // ICE and EV menus are mutually exclusive, including non-stock swap options.
            Exec("DELETE FROM List_UpgradeMotor WHERE Ordinal=?", carId);
            if (donor != null)
            {
                var ddt = Query("SELECT DrivetrainID,PowertrainId,ManufacturerId FROM List_UpgradeDrivetrain WHERE Ordinal=? AND IsStock=1", donor["Id"]).FirstOrDefault();
                if (ddt != null && (ScalarL("SELECT COUNT(*) FROM List_UpgradeDrivetrain WHERE Ordinal=? AND IsStock=1", carId) ?? 0) > 0)
                    Exec("UPDATE List_UpgradeDrivetrain SET DrivetrainID=?,PowertrainId=?,ManufacturerId=? WHERE Ordinal=? AND IsStock=1",
                        ddt["DrivetrainID"], ddt["PowertrainId"], ddt["ManufacturerId"], carId);
            }
            Exec("RELEASE set_stock_engine");

            _electricSpecCars.Remove(carId);
            int idx = _cars.FindIndex(x => x.Id == carId);
            if (idx >= 0) { _cars[idx] = _cars[idx] with { Type = TypeFor(carId, _car.Media) }; _car = _cars[idx]; }
            MarkModified(carId);
            _powerTargetCarId = null;
            RefreshCar(); RenderCars();
            Log((wasEV ? "⚡→⛽ EV converted to ICE. " : "★ stock engine set. ") + "Stock = " + EngName(eid), "ok");
            if (hadMotor > 0) Log($"   removed all {hadMotor} motor option(s); combustion spec and gearbox fitted", "info");
            if (donor == null) Log("no donor car for this engine — copied basic config only", "warn");
        }
        catch (Exception ex)
        {
            try { Exec("ROLLBACK TO set_stock_engine"); Exec("RELEASE set_stock_engine"); } catch { }
            Log("stock engine conversion failed: " + ex.Message, "err");
        }
    }

    // add a motor as a selectable (non-stock) swap option in the car's motor menu
    bool AddMotorOption(long motorId, bool quiet = false)
    {
        if (_car == null) { if (!quiet) Log("pick a car first", "warn"); return false; }
        long carId = _car.Id;
        if (!_electricSpecCars.Contains(carId))
        { if (!quiet) Log("convert this ICE car to electric before adding motor options", "warn"); return false; }
        if ((ScalarL("SELECT COUNT(*) FROM List_UpgradeMotor WHERE Ordinal=? AND MotorID=?", carId, motorId) ?? 0) > 0)
        { if (!quiet) Log("already on car: " + MotorName(motorId), "warn"); return false; }
        long id = NextId("List_UpgradeMotor", carId), lvl = NextLevel("List_UpgradeMotor", "Ordinal", carId);
        long manu = ScalarL("SELECT ManufacturerID FROM List_UpgradeMotor WHERE MotorID=? AND IsStock=1 LIMIT 1", motorId) ?? 0;
        Exec(@"INSERT INTO List_UpgradeMotor (Id,Ordinal,Level,MotorID,IsStock,ManufacturerID,Price,MassDiff,WeightDistDiff,releaseOrder)
               VALUES (?,?,?,?,0,?,50,0,0,0)", id, carId, lvl, motorId, manu);
        MarkModified(carId);
        if (!quiet) { Log("＋ motor option added: " + MotorName(motorId), "ok"); RefreshCar(); }
        return true;
    }
    void AddAllMotors()
    {
        if (_car == null) { Log("pick a car first", "warn"); return; }
        if (!_electricSpecCars.Contains(_car.Id)) { Log("convert this ICE car to electric before adding motor options", "warn"); return; }
        int n = 0; foreach (var mo in _motors) if (AddMotorOption(mo.Id, true)) n++;
        Log($"＋ added {n} motor options (all)", "ok"); RefreshCar();
    }

    // motor list + its upgrade-ladder summary (List_UpgradeMotorParts keyed by MotorID; TorqueScale = power multiplier)
    void LoadMotors()
    {
        _motors = Query(@"SELECT m.MotorID id, m.MediaName media, m.MotorName name, m.MotorGraphingMaxPower pw,
                            (SELECT COUNT(*) FROM List_UpgradeMotorParts p WHERE p.MotorID=m.MotorID AND p.IsStock=0) upg,
                            (SELECT MAX(TorqueScale) FROM List_UpgradeMotorParts p WHERE p.MotorID=m.MotorID) maxs
                          FROM Data_Motor m ORDER BY m.MotorGraphingMaxPower DESC")
            .Select(r => new MotorItem(Convert.ToInt64(r["id"]), (string)(r["media"] ?? ""), (r["name"] as string) ?? "",
                r["pw"] == null ? 0 : Convert.ToDouble(r["pw"]),
                r["upg"] == null ? 0 : Convert.ToInt32(Convert.ToInt64(r["upg"])),
                r["maxs"] == null ? 1.0 : Convert.ToDouble(r["maxs"])))
            .OrderByDescending(m => m.Pw * m.MaxScale).ThenByDescending(m => m.Pw).ToList();
    }

    // ============================================================ ICE → EV (electric swap)
    void ConvertToElectric(long? pickMotorId = null)
    {
        if (_car == null) { Log("pick a car first", "warn"); return; }
        long carId = _car.Id;
        // the chosen motor, or the highest-output motor in the DB (Rimac Nevera on stock data)
        var motor = (pickMotorId is long pmid
            ? Query("SELECT MotorID,MediaName,MotorName,MotorGraphingMaxPower pw FROM Data_Motor WHERE MotorID=?", pmid)
            : Query("SELECT MotorID,MediaName,MotorName,MotorGraphingMaxPower pw FROM Data_Motor ORDER BY MotorGraphingMaxPower DESC LIMIT 1")).FirstOrDefault();
        if (motor == null) { Log("motor not found in this DB", "err"); return; }
        long motorId = Convert.ToInt64(motor["MotorID"]);
        string mmedia = (string)(motor["MediaName"] ?? "");
        double mpw = motor["pw"] == null ? 0 : Convert.ToDouble(motor["pw"]);
        string mname = motor["MotorName"] as string;
        string label = string.IsNullOrWhiteSpace(mname) ? mmedia : mname;

        const string evSpec = "Id,PowertrainID,NumGears,EngineConfigID,CylinderID,AspirationTypeId,Displacement";
        var donor = Query($"SELECT {evSpec} FROM Data_Car WHERE MediaName=? AND EngineConfigID=6 AND Displacement=0", mmedia).FirstOrDefault()
            ?? Query($@"SELECT c.{evSpec.Replace(",", ",c.")} FROM Data_Car c
                        JOIN List_UpgradeMotor lm ON lm.Ordinal=c.Id
                        WHERE lm.MotorID=? AND lm.IsStock=1 AND c.EngineConfigID=6 AND c.Displacement=0
                        ORDER BY c.Id LIMIT 1", motorId).FirstOrDefault()
            ?? Query($"SELECT {evSpec} FROM Data_Car WHERE EngineConfigID=6 AND Displacement=0 ORDER BY Id LIMIT 1").FirstOrDefault();
        if (donor == null) { Log("cannot convert to EV: no electric Data_Car donor spec is available", "warn"); return; }
        long manu = 0; Dictionary<string, object> ddt = null;
        long dId = Convert.ToInt64(donor["Id"]);
        manu = ScalarL("SELECT ManufacturerID FROM List_UpgradeMotor WHERE Ordinal=? AND IsStock=1 LIMIT 1", dId) ?? 0;
        ddt = Query("SELECT DrivetrainID,PowertrainId,ManufacturerId FROM List_UpgradeDrivetrain WHERE Ordinal=? AND IsStock=1", dId).FirstOrDefault();
        try
        {
            Exec("SAVEPOINT convert_to_electric");
            // 1. copy the EV spec fields from the donor (single-speed, electric powertrain, 0 displacement)
            Exec("UPDATE Data_Car SET PowertrainID=?,NumGears=?,EngineConfigID=?,CylinderID=?,AspirationTypeId=?,Displacement=? WHERE Id=?",
                donor["PowertrainID"], donor["NumGears"], donor["EngineConfigID"], donor["CylinderID"], donor["AspirationTypeId"], donor["Displacement"], carId);
            // 2. drop the combustion engine menu — a pure EV has no List_UpgradeEngine rows
            Exec("DELETE FROM List_UpgradeEngine WHERE Ordinal=?", carId);
            // 3. install the motor as the stock (and only) powertrain
            Exec("DELETE FROM List_UpgradeMotor WHERE Ordinal=?", carId);
            long mid = NextId("List_UpgradeMotor", carId);
            Exec(@"INSERT INTO List_UpgradeMotor (Id,Ordinal,Level,MotorID,IsStock,ManufacturerID,Price,MassDiff,WeightDistDiff,releaseOrder)
                   VALUES (?,?,0,?,1,?,0,0,0,0)", mid, carId, motorId, manu);
            // 4. fit the EV single-speed drivetrain (RWD/FWD upgrade options can still be added on top via Apply)
            if (ddt != null && (ScalarL("SELECT COUNT(*) FROM List_UpgradeDrivetrain WHERE Ordinal=? AND IsStock=1", carId) ?? 0) > 0)
                Exec("UPDATE List_UpgradeDrivetrain SET DrivetrainID=?,PowertrainId=?,ManufacturerId=? WHERE Ordinal=? AND IsStock=1",
                    ddt["DrivetrainID"], ddt["PowertrainId"], ddt["ManufacturerId"], carId);
            Exec("RELEASE convert_to_electric");

            _electricSpecCars.Add(carId);
            int idx = _cars.FindIndex(x => x.Id == carId);
            if (idx >= 0) { _cars[idx] = _cars[idx] with { Type = TypeFor(carId, _car.Media) }; _car = _cars[idx]; }
            Log($"⛽→⚡ converted to electric — {label} (output ~{mpw:0})", "ok");
            Log("   engine menu removed, single-speed EV drivetrain fitted. Drivetrain swaps can still be added.", "info");
            MarkModified(carId);
            _powerTargetCarId = null;
            RefreshCar(); RenderCars();
        }
        catch (Exception ex)
        {
            try { Exec("ROLLBACK TO convert_to_electric"); Exec("RELEASE convert_to_electric"); } catch { }
            Log("electric swap failed: " + ex.Message, "err");
        }
    }
    void Electric_Click(object s, RoutedEventArgs e) => RunForSelectedCars("convert to electric", () => ConvertToElectric());

    void ApplyOptions()
    {
        if (_car == null) { Log("pick a car first", "warn"); return; }
        long carId = _car.Id;
        try
        {
            double driftSteeringAngle = 0;
            if (OptSlam.IsChecked == true && !TryGetDriftSteeringAngle(out driftSteeringAngle)) return;

            if (OptRWD.IsChecked == true)
            {
                if ((ScalarL("SELECT COUNT(*) FROM List_UpgradeDrivetrain WHERE Ordinal=? AND IsStock=0 AND DrivetrainID=2170", carId) ?? 0) > 0)
                    Log("RWD conversion already present — no duplicate added", "info");
                else if ((ScalarL("SELECT COUNT(*) FROM Data_Drivetrain WHERE DrivetrainID=2170") ?? 0) > 0)
                {
                    long id = NextId("List_UpgradeDrivetrain", carId), lvl = NextLevel("List_UpgradeDrivetrain", "Ordinal", carId);
                    Exec(@"INSERT INTO List_UpgradeDrivetrain (Id,Ordinal,DrivetrainID,PowertrainId,MassDiff,WeightDistDiff,Level,ManufacturerId,Price,IsStock,releaseOrder)
                           VALUES (?,?,2170,2,-61.962985,-0.02,?,492,50,0,0)", id, carId, lvl);
                    Log("✓ RWD conversion option added", "ok");
                }
                else Log("RWD skipped: drivetrain 2170 not in this DB", "warn");
            }

            if (OptFWD.IsChecked == true)
            {
                // find a proven FWD gearbox: a stock drivetrain row whose Data_Drivetrain is DrivetypeID 1 (FWD)
                var fwd = Query(@"SELECT ud.DrivetrainID d, ud.PowertrainId p, ud.ManufacturerId m
                                  FROM List_UpgradeDrivetrain ud JOIN Data_Drivetrain dd ON dd.DrivetrainID=ud.DrivetrainID
                                  WHERE dd.DrivetypeID=1 AND ud.IsStock=1 LIMIT 1").FirstOrDefault();
                if (fwd != null &&
                    (ScalarL("SELECT COUNT(*) FROM List_UpgradeDrivetrain WHERE Ordinal=? AND IsStock=0 AND DrivetrainID=?", carId, fwd["d"]) ?? 0) == 0)
                {
                    long id = NextId("List_UpgradeDrivetrain", carId), lvl = NextLevel("List_UpgradeDrivetrain", "Ordinal", carId);
                    Exec(@"INSERT INTO List_UpgradeDrivetrain (Id,Ordinal,DrivetrainID,PowertrainId,MassDiff,WeightDistDiff,Level,ManufacturerId,Price,IsStock,releaseOrder)
                           VALUES (?,?,?,?,0,0,?,?,50,0,0)", id, carId, fwd["d"], fwd["p"], lvl, fwd["m"]);
                    Log("✓ FWD conversion option added (experimental)", "ok");
                }
                else if (fwd != null) Log("FWD conversion already present — no duplicate added", "info");
                else Log("FWD skipped: no FWD gearbox found in this DB", "warn");
            }

            if (OptManual.IsChecked == true) ApplyManualTransmission(carId);

            if (OptRims.IsChecked == true)
            {
                foreach (var (t, col, list) in new[] {
                    ("List_UpgradeRimSizeFront", "FrontWheelDiameter", _rf),
                    ("List_UpgradeRimSizeRear",  "RearWheelDiameter",  _rf) })
                {
                    long? stock = ScalarL($"SELECT {col} FROM \"{t}\" WHERE Ordinal=? AND IsStock=1", carId);
                    long terminalLevel = ScalarL($"SELECT MAX(Level) FROM \"{t}\" WHERE Ordinal<>? AND IsStock=0", carId) ?? 8;
                    var entered = list.Select(OptNum).Where(v => v.HasValue)
                        .Select(v => (int)Math.Round(v!.Value)).Distinct().ToList();
                    var diameters = (_batchRunning && stock.HasValue
                            ? entered.Select(delta => checked((int)stock.Value + delta))
                            : entered)
                        .Where(v => v > 0 && (!stock.HasValue || v != stock.Value))
                        .Distinct().OrderBy(v => v).ToList();
                    var existingRows = Query($"SELECT {col} v FROM \"{t}\" WHERE Ordinal=? AND IsStock=0", carId)
                        .Select(row => Convert.ToInt32(row["v"])).ToList();
                    var existing = existingRows.ToHashSet();
                    if (!_batchRunning)
                    {
                        // A second Apply with the same single-car values is a true
                        // no-op. Do not churn IDs or recreate identical menu tiles.
                        if (existingRows.Count == diameters.Count && existing.SetEquals(diameters))
                        {
                            SortUpgradeRowsByValue(t, "Ordinal", carId, col);
                            continue;
                        }
                        Exec($"DELETE FROM \"{t}\" WHERE Ordinal=? AND IsStock=0", carId);
                        existing.Clear();
                    }
                    int slot = _batchRunning ? existing.Count + 1 : 1;
                    foreach (int dia in diameters)
                    {
                        if (!existing.Add(dia)) continue;
                        long level = Math.Min(slot, terminalLevel);
                        Exec($"INSERT INTO \"{t}\" (Id,Ordinal,Level,IsStock,{col},Price,MassDiff,DragScale,WindInstabilityScale,RequiresGraphics) VALUES (?,?,?,0,?,?,?,1,1,0)",
                            _batchRunning ? NextId(t, carId) : carId * 1000 + slot,
                            carId, level, dia, 50, Math.Round(1.36 * (dia - (stock ?? dia)), 2));
                        slot++;
                    }
                    SortUpgradeRowsByValue(t, "Ordinal", carId, col);
                }
                string sizes = string.Join(", ", _rf.Select(OptNum).Where(v => v.HasValue).Select(v => Math.Round(v!.Value)));
                Log($"✓ shared front/rear rim sizes {(_batchRunning ? "added from stock-relative deltas" : "set")}: [{sizes}]", "ok");
            }

            var fitBodies = SelectedBodies();   // active Stock/Widebody tab in the fitment card

            if (OptWidth.IsChecked == true)
            {
                // Tire width drives a rendered car part, so each body must reuse
                // its own highest valid native geometry Level for extra choices.
                // Unique Ids distinguish the menu tiles; Level selects geometry.
                foreach (var bd in fitBodies)
                foreach (var (t, col, list) in new[] {
                    ("List_UpgradeCarBodyTireWidthFront", "FrontTireWidth", _wf),
                    ("List_UpgradeCarBodyTireWidthRear",  "RearTireWidth",  _wr) })
                {
                    var oldLevels = Query($"SELECT {col} v, Level FROM \"{t}\" WHERE CarBodyId=? AND IsStock=0 ORDER BY Id", bd);
                    int preservedLevelCount = oldLevels.Count;
                    long terminalLevel = preservedLevelCount > 0
                        ? oldLevels.Max(r => Convert.ToInt64(r["Level"]))
                        : 3;
                    var existingRows = oldLevels.Select(row => Convert.ToInt64(row["v"])).ToList();
                    var existing = existingRows.ToHashSet();
                    long stockWidth = ScalarL($"SELECT {col} FROM \"{t}\" WHERE CarBodyId=? AND IsStock=1 ORDER BY Id LIMIT 1", bd) ?? 0;
                    var requestedWidths = list.Select(OptNum)
                        .Where(value => value.HasValue && (_batchRunning || value.Value > 0))
                        .Select(value => _batchRunning ? stockWidth + (long)Math.Round(value!.Value) : (long)value!.Value)
                        .Where(value => value > 0)
                        .Distinct().ToList();
                    if (_batchRunning) requestedWidths.Sort();
                    if (!_batchRunning)
                    {
                        var requested = requestedWidths.ToHashSet();
                        if (existingRows.Count == requested.Count && existing.SetEquals(requested))
                        {
                            SortUpgradeRowsByValue(t, "CarBodyId", bd, col);
                            continue;
                        }
                        Exec($"DELETE FROM \"{t}\" WHERE CarBodyId=? AND IsStock=0", bd);
                        existing.Clear();
                    }
                    int slot = _batchRunning ? oldLevels.Count + 1 : 1;
                    int firstAdded = 0;
                    foreach (long width in requestedWidths)
                    {
                        if (!existing.Add(width)) continue;
                        long level = !_batchRunning
                            ? (slot <= preservedLevelCount
                                ? Convert.ToInt64(oldLevels[slot - 1]["Level"])
                                : (preservedLevelCount == 0 && slot <= 3 ? slot : terminalLevel))
                            : (oldLevels.Count == 0 ? Math.Min(++firstAdded, 3) : terminalLevel);
                        Exec($"INSERT INTO \"{t}\" (Id,CarBodyId,Level,IsStock,{col},Price,MassDiff,DragScale,WindInstabilityScale,RequiresGraphics,releaseOrder) VALUES (?,?,?,0,?,?,?,1,1,0,0)",
                            _batchRunning ? FreeBodyUpgradeId(t, bd, slot) : FitId(bd, slot),
                            bd, level, width, 2000 + 200 * slot, Math.Round(0.54 * slot, 2));
                        slot++;
                    }
                    SortUpgradeRowsByValue(t, "CarBodyId", bd, col);
                }
                Log($"✓ tire widths {(_batchRunning ? "added from stock-relative deltas to" : "set on")} {fitBodies.Length} body/bodies", "ok");
            }

            if (OptAspect.IsChecked == true)
            {
                var vals = _sw.Select(OptNum).Where(v => v.HasValue).Select(v => v.Value).ToList();
                foreach (var bd in fitBodies)
                foreach (var (t, col) in new[] { ("List_UpgradeCarBodyTireAspectRatioFront", "FrontTireAspectRatioOffset"), ("List_UpgradeCarBodyTireAspectRatioRear", "RearTireAspectRatioOffset") })
                {
                    var existing = Query($"SELECT {col} v FROM \"{t}\" WHERE CarBodyId=? AND IsStock=0", bd)
                        .Select(row => Convert.ToDouble(row["v"])).ToList();
                    double stockValue = Convert.ToDouble(Scalar($"SELECT {col} FROM \"{t}\" WHERE CarBodyId=? AND IsStock=1 ORDER BY Id LIMIT 1", bd) ?? 0d);
                    var enteredValues = vals.Select(enteredValue => Math.Round(
                            _batchRunning ? stockValue + enteredValue : enteredValue, 2)).ToList();
                    var requestedValues = enteredValues.Distinct().ToList();
                    if (_batchRunning) requestedValues.Sort();
                    if (!_batchRunning)
                    {
                        bool unchanged = existing.Select(value => Math.Round(value, 2)).OrderBy(value => value)
                            .SequenceEqual(enteredValues.OrderBy(value => value));
                        if (unchanged)
                        {
                            SortUpgradeRowsByValue(t, "CarBodyId", bd, col);
                            continue;
                        }
                        Exec($"DELETE FROM \"{t}\" WHERE CarBodyId=? AND IsStock=0", bd);
                        existing.Clear();
                    }
                    int slot = _batchRunning ? existing.Count + 1 : 1;
                    foreach (double value in requestedValues)
                    {
                        if (existing.Any(current =>
                                Math.Abs(Math.Round(current, 2) - value) < 0.000001)) continue;
                        // Working extended databases keep every profile option at
                        // Level 1; the unique Id is what distinguishes each tile.
                        Exec($"INSERT INTO \"{t}\" (Id,CarBodyId,{col},IsStock,Level,Price,releaseOrder) VALUES (?,?,?,0,1,0,0)",
                            _batchRunning ? FreeBodyUpgradeId(t, bd, slot) : FitId(bd, slot), bd, value);
                        existing.Add(value);
                        slot++;
                    }
                    SortUpgradeRowsByValue(t, "CarBodyId", bd, col);
                }
                Log($"✓ tire profile offsets {(_batchRunning ? "added from stock-relative deltas" : "set")}: " + string.Join(", ", vals), "ok");
            }

            if (OptTrack.IsChecked == true)
            {
                // Track spacing has three meaningful levels. Further uniquely-ID'd
                // entries repeat terminal level 3 so every menu tile remains valid.
                foreach (var bd in fitBodies)
                foreach (var (t, list) in new[] {
                    ("List_UpgradeCarBodyTrackSpacingFront", _ofF),
                    ("List_UpgradeCarBodyTrackSpacingRear",  _ofR) })
                {
                    var existing = Query($"SELECT Spacing v FROM \"{t}\" WHERE CarBodyId=? AND IsStock=0", bd)
                        .Select(row => Convert.ToDouble(row["v"])).ToList();
                    var enteredSteps = list.Select(OptNum).Where(value => value.HasValue)
                        .Select(value => Math.Round(value!.Value, 3)).ToList();
                    var requestedSteps = enteredSteps.Distinct().ToList();
                    if (_batchRunning)
                    {
                        requestedSteps = requestedSteps.Where(value => value > 0).ToList();
                        requestedSteps.Sort();
                    }
                    if (!_batchRunning)
                    {
                        bool unchanged = existing.Select(value => Math.Round(value, 3)).OrderBy(value => value)
                            .SequenceEqual(enteredSteps.OrderBy(value => value));
                        if (unchanged)
                        {
                            SortUpgradeRowsByValue(t, "CarBodyId", bd, "Spacing");
                            continue;
                        }
                        Exec($"DELETE FROM \"{t}\" WHERE CarBodyId=? AND IsStock=0", bd);
                        existing.Clear();
                    }
                    double stockValue = Convert.ToDouble(Scalar($"SELECT Spacing FROM \"{t}\" WHERE CarBodyId=? AND IsStock=1 ORDER BY Id LIMIT 1", bd) ?? 0d);
                    double batchBase = Math.Max(stockValue, existing.DefaultIfEmpty(stockValue).Max());
                    int lvl = _batchRunning ? existing.Count + 1 : 1;
                    foreach (double requestedValue in requestedSteps)
                    {
                        double v = requestedValue;
                        if (_batchRunning)
                            v = batchBase + v;
                        v = Math.Round(v, 3);
                        if (existing.Any(current =>
                                Math.Abs(Math.Round(current, 3) - v) < 0.000001)) continue;
                        int level = Math.Min(lvl, 3);
                        Exec($"INSERT INTO \"{t}\" (Id,CarBodyId,Spacing,IsStock,Level,Price,releaseOrder) VALUES (?,?,?,0,?,?,0)",
                            _batchRunning ? FreeBodyUpgradeId(t, bd, lvl) : FitId(bd, lvl), bd, v, level, 100 * lvl);
                        existing.Add(v);
                        lvl++;
                    }
                    SortUpgradeRowsByValue(t, "CarBodyId", bd, "Spacing");
                }
                Log($"✓ track width / offset {(_batchRunning ? "extended from each car's widest existing value on" : "set on")} {fitBodies.Length} body/bodies", "ok");
            }

            if (OptWhite.IsChecked == true) ApplyWhitewalls(carId);

            if (OptFeTires.IsChecked == true) ApplyFeTires(carId);

            if (OptLift.IsChecked == true) ApplyLiftKit(carId);

            if (OptSlam.IsChecked == true) ApplySlamKit(carId, driftSteeringAngle);

            MarkModified(carId);
            RefreshCar();
            Log("⚙ apply complete — export when ready", "ok");
        }
        catch (Exception ex) { Log("apply error: " + ex.Message, "err"); }
    }

    bool TryGetDriftSteeringAngle(out double angle)
    {
        string text = DriftSteeringAngle.Text.Trim();
        bool valid = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out angle) ||
                     double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out angle);
        if (valid && angle >= 0 && angle <= 180) return true;

        Log("drift steering angle must be a number from 0 to 180 degrees", "warn");
        angle = 0;
        return false;
    }

    void InsertClonedRow(string table, Dictionary<string, object> row)
    {
        string[] cols = row.Keys.ToArray();
        string names = string.Join(",", cols.Select(c => $"\"{c.Replace("\"", "\"\"")}\""));
        object[] vals = cols.Select(c => row[c]).ToArray();
        Exec($"INSERT INTO \"{table}\" ({names}) VALUES ({string.Join(",", cols.Select(_ => "?"))})", vals);
    }

    long FreeSpringPhysicsId(long preferred, long endExclusive)
    {
        for (long id = preferred; id < endExclusive; id++)
            if ((ScalarL("SELECT COUNT(*) FROM List_SpringDamperPhysics WHERE SpringDamperPhysicsID=?", id) ?? 0) == 0)
                return id;
        throw new InvalidOperationException("no free spring/damper physics ID remains in this car's block");
    }

    long FreeAntiSwayPhysicsId(long preferred, long endExclusive)
    {
        for (long id = preferred; id < endExclusive; id++)
            if ((ScalarL("SELECT COUNT(*) FROM List_AntiSwayPhysics WHERE AntiSwayPhysicsID=?", id) ?? 0) == 0)
                return id;
        throw new InvalidOperationException("no free anti-roll physics ID remains in this car's block");
    }

    long FreeBodyUpgradeId(string table, long carBodyId, int preferredSlot)
    {
        long blockStart = FitId(carBodyId, 0), blockEnd = blockStart + 100;
        for (long id = blockStart + preferredSlot; id < blockEnd; id++)
            if ((ScalarL($"SELECT COUNT(*) FROM \"{table}\" WHERE Id=?", id) ?? 0) == 0)
                return id;
        for (long id = blockStart; id < blockStart + preferredSlot; id++)
            if ((ScalarL($"SELECT COUNT(*) FROM \"{table}\" WHERE Id=?", id) ?? 0) == 0)
                return id;
        throw new InvalidOperationException($"no free {table} ID remains for body {carBodyId}");
    }

    bool EnsureStockSuspensionRow(long carId, List<DbCreatedRow> created)
    {
        if ((ScalarL("SELECT COUNT(*) FROM List_UpgradeSpringDamper WHERE Ordinal=? AND IsStock=1", carId) ?? 0) > 0)
            return true;

        long donorId = ScalarL("SELECT Id FROM Data_Car WHERE MediaName='LOT_00_ExigeWTA_18' LIMIT 1") ?? 0;
        var stock = Query("SELECT * FROM List_UpgradeSpringDamper WHERE Ordinal=? AND IsStock=1 ORDER BY Id LIMIT 1", donorId).FirstOrDefault();
        if (stock == null) { Log("suspension creation skipped: no stock baseline exists", "warn"); return false; }
        var front = Query("SELECT * FROM List_SpringDamperPhysics WHERE SpringDamperPhysicsID=?", stock["FrontSpringDamperPhysicsID"]).FirstOrDefault();
        var rear = Query("SELECT * FROM List_SpringDamperPhysics WHERE SpringDamperPhysicsID=?", stock["RearSpringDamperPhysicsID"]).FirstOrDefault();
        if (front == null || rear == null) { Log("suspension creation skipped: Lotus stock physics rows are missing", "warn"); return false; }

        long carBase = carId * 1000;
        long frontId = FreeSpringPhysicsId(carBase, carBase + 100);
        long rearId = FreeSpringPhysicsId(carBase + 100, carBase + 200);
        front["SpringDamperPhysicsID"] = frontId;
        front["Ordinal"] = carId;
        rear["SpringDamperPhysicsID"] = rearId;
        rear["Ordinal"] = carId;
        InsertClonedRow("List_SpringDamperPhysics", front);
        InsertClonedRow("List_SpringDamperPhysics", rear);

        long upgradeId = (ScalarL("SELECT COUNT(*) FROM List_UpgradeSpringDamper WHERE Id=?", carBase) ?? 0) == 0
            ? carBase : NextId("List_UpgradeSpringDamper", carId);
        stock["Id"] = upgradeId;
        stock["Ordinal"] = carId;
        stock["IsStock"] = 1L;
        stock["Price"] = 0L;
        stock["FrontSpringDamperPhysicsID"] = frontId;
        stock["RearSpringDamperPhysicsID"] = rearId;
        InsertClonedRow("List_UpgradeSpringDamper", stock);

        created?.Add(new DbCreatedRow("List_SpringDamperPhysics", "SpringDamperPhysicsID", frontId));
        created?.Add(new DbCreatedRow("List_SpringDamperPhysics", "SpringDamperPhysicsID", rearId));
        created?.Add(new DbCreatedRow("List_UpgradeSpringDamper", "Id", upgradeId));
        Log("\u2713 added missing stock suspension baseline", "ok");
        return true;
    }

    // Add a selectable Race anti-roll bar when a car only has its stock bar.
    // The Lotus values are applied later to every linked physics row, including
    // this generated one. Front and rear use separate 100-wide physics blocks.
    bool EnsureAntiSwayUpgrade(long carId, bool rear, List<DbCreatedRow> created)
    {
        string table = rear ? "List_UpgradeAntiSwayRear" : "List_UpgradeAntiSwayFront";
        if ((ScalarL($"SELECT COUNT(*) FROM {table} WHERE Ordinal=? AND Level=3 AND IsStock=0", carId) ?? 0) > 0)
            return true;

        var stock = Query($"SELECT * FROM {table} WHERE Ordinal=? AND IsStock=1 ORDER BY Id LIMIT 1", carId).FirstOrDefault();
        if (stock == null)
        {
            long lotusId = ScalarL("SELECT Id FROM Data_Car WHERE MediaName='LOT_00_ExigeWTA_18' LIMIT 1") ?? 0;
            var lotusStock = Query($"SELECT * FROM {table} WHERE Ordinal=? AND IsStock=1 ORDER BY Id LIMIT 1", lotusId).FirstOrDefault();
            if (lotusStock != null)
            {
                var stockPhysics = Query("SELECT * FROM List_AntiSwayPhysics WHERE AntiSwayPhysicsID=?", lotusStock["AntiSwayPhysicsID"]).FirstOrDefault();
                if (stockPhysics != null)
                {
                    long stockCarBase = carId * 1000;
                    long stockPhysicsId = FreeAntiSwayPhysicsId(stockCarBase + (rear ? 100 : 0), stockCarBase + (rear ? 200 : 100));
                    stockPhysics["AntiSwayPhysicsID"] = stockPhysicsId;
                    stockPhysics["Ordinal"] = carId;
                    InsertClonedRow("List_AntiSwayPhysics", stockPhysics);

                    long stockId = (ScalarL($"SELECT COUNT(*) FROM {table} WHERE Id=?", stockCarBase) ?? 0) == 0
                        ? stockCarBase : NextId(table, carId);
                    lotusStock["Id"] = stockId;
                    lotusStock["Ordinal"] = carId;
                    lotusStock["IsStock"] = 1L;
                    lotusStock["Price"] = 0L;
                    lotusStock["AntiSwayPhysicsID"] = stockPhysicsId;
                    InsertClonedRow(table, lotusStock);
                    created?.Add(new DbCreatedRow("List_AntiSwayPhysics", "AntiSwayPhysicsID", stockPhysicsId));
                    created?.Add(new DbCreatedRow(table, "Id", stockId));
                    stock = lotusStock;
                    Log($"\u2713 added missing stock {(rear ? "rear" : "front")} anti-roll baseline", "ok");
                }
            }
        }
        if (stock == null) { Log($"{(rear ? "rear" : "front")} anti-roll creation skipped: no source row", "warn"); return false; }

        long sourcePhysicsId = Convert.ToInt64(stock["AntiSwayPhysicsID"]);
        var physics = Query("SELECT * FROM List_AntiSwayPhysics WHERE AntiSwayPhysicsID=?", sourcePhysicsId).FirstOrDefault();
        if (physics == null) { Log("anti-roll creation skipped: source physics row is missing", "warn"); return false; }

        long carBase = carId * 1000;
        long physicsId = FreeAntiSwayPhysicsId(carBase + (rear ? 103 : 3), carBase + (rear ? 200 : 100));
        physics["AntiSwayPhysicsID"] = physicsId;
        physics["Ordinal"] = carId;
        InsertClonedRow("List_AntiSwayPhysics", physics);

        long preferredId = carBase + 3;
        long upgradeId = (ScalarL($"SELECT COUNT(*) FROM {table} WHERE Id=?", preferredId) ?? 0) == 0
            ? preferredId : NextId(table, carId);
        stock["Id"] = upgradeId;
        stock["Ordinal"] = carId;
        stock["Level"] = 3L;
        stock["ManufacturerID"] = 206L;
        stock["MassDiff"] = 0d;
        stock["Price"] = 1900L;
        stock["IsStock"] = 0L;
        stock["AntiSwayPhysicsID"] = physicsId;
        InsertClonedRow(table, stock);

        created?.Add(new DbCreatedRow("List_AntiSwayPhysics", "AntiSwayPhysicsID", physicsId));
        created?.Add(new DbCreatedRow(table, "Id", upgradeId));
        Log($"\u2713 added missing Race {(rear ? "rear" : "front")} anti-roll upgrade", "ok");
        return true;
    }

    // A few special cars ship without a tire-compound menu at all. A cloned
    // Lotus stock slick row gives the package a valid road tire entry without
    // disturbing cars that already have their own tire choices.
    bool EnsureRoadTireRow(long carId, long donorId, List<DbCreatedRow> created)
    {
        if ((ScalarL("SELECT COUNT(*) FROM List_UpgradeTireCompound WHERE Ordinal=?", carId) ?? 0) > 0)
            return true;
        var tire = Query("SELECT * FROM List_UpgradeTireCompound WHERE Ordinal=? AND IsStock=1 ORDER BY Id LIMIT 1", donorId).FirstOrDefault();
        if (tire == null) { Log("tire creation skipped: Lotus stock tire row is missing", "warn"); return false; }

        long preferredId = carId * 1000;
        long tireId = (ScalarL("SELECT COUNT(*) FROM List_UpgradeTireCompound WHERE Id=?", preferredId) ?? 0) == 0
            ? preferredId : NextId("List_UpgradeTireCompound", carId);
        tire["Id"] = tireId;
        tire["Ordinal"] = carId;
        tire["IsStock"] = 1L;
        tire["Price"] = 0L;
        InsertClonedRow("List_UpgradeTireCompound", tire);
        created?.Add(new DbCreatedRow("List_UpgradeTireCompound", "Id", tireId));
        Log("\u2713 added missing road tire entry", "ok");
        return true;
    }

    int ApplyEnhancedTireGrip(long carId, List<DbCreatedRow> created)
    {
        int changed = 0;
        foreach (var tire in Query(@"SELECT Id FROM List_UpgradeTireCompound WHERE Ordinal=?
                                    AND Level NOT IN (5,7,8,9)", carId))
        {
            long partId = Convert.ToInt64(tire["Id"]);
            if ((ScalarL("SELECT COUNT(*) FROM List_UpgradeTireCompoundFictionModOverride WHERE PartId=?", partId) ?? 0) == 0)
            {
                Exec(@"INSERT INTO List_UpgradeTireCompoundFictionModOverride
                       (PartId,FrontLatFrictionMult,FrontLongFrictionMult,RearLatFrictionMult,RearLongFrictionMult)
                       VALUES (?,?,?,?,?)", partId, EnhancedTireLateralGrip, EnhancedTireLongitudinalGrip,
                                             EnhancedTireLateralGrip, EnhancedTireLongitudinalGrip);
                created?.Add(new DbCreatedRow("List_UpgradeTireCompoundFictionModOverride", "PartId", partId));
            }
            else
            {
                Exec(@"UPDATE List_UpgradeTireCompoundFictionModOverride
                       SET FrontLatFrictionMult=?,FrontLongFrictionMult=?,RearLatFrictionMult=?,RearLongFrictionMult=?
                       WHERE PartId=?", EnhancedTireLateralGrip, EnhancedTireLongitudinalGrip,
                                        EnhancedTireLateralGrip, EnhancedTireLongitudinalGrip, partId);
            }
            changed++;
        }
        return changed;
    }

    // Ensure cars with stock-only body upgrade trees can actually select the
    // Lotus-derived Race chassis and weight-reduction values in the garage.
    void EnsureBodyHandlingUpgrades(long carId, List<DbCreatedRow> created)
    {
        foreach (var body in Query("SELECT CarBodyID FROM List_UpgradeCarBody WHERE Ordinal=? ORDER BY IsStock DESC,CarBodyID", carId))
        {
            long bodyId = Convert.ToInt64(body["CarBodyID"]);

            if ((ScalarL("SELECT COUNT(*) FROM List_UpgradeCarBodyChassisStiffness WHERE CarbodyId=? AND IsStock=0", bodyId) ?? 0) == 0)
            {
                var chassis = Query("SELECT * FROM List_UpgradeCarBodyChassisStiffness WHERE CarbodyId=? AND IsStock=1 ORDER BY Id LIMIT 1", bodyId).FirstOrDefault();
                if (chassis != null)
                {
                    long id = FreeBodyUpgradeId("List_UpgradeCarBodyChassisStiffness", bodyId, 3);
                    chassis["Id"] = id;
                    chassis["Level"] = 3L;
                    chassis["ManufacturerID"] = 206L;
                    chassis["MassDiff"] = 0d;
                    chassis["WeightDistDiff"] = 0d;
                    chassis["Price"] = 2100L;
                    chassis["IsStock"] = 0L;
                    chassis["RequiresGraphics"] = 0L;
                    InsertClonedRow("List_UpgradeCarBodyChassisStiffness", chassis);
                    created?.Add(new DbCreatedRow("List_UpgradeCarBodyChassisStiffness", "Id", id));
                    Log("\u2713 added missing Race chassis upgrade", "ok");
                }
            }

            if ((ScalarL("SELECT COUNT(*) FROM List_UpgradeCarBodyWeight WHERE CarBodyId=? AND IsStock=0", bodyId) ?? 0) == 0)
            {
                var weight = Query("SELECT * FROM List_UpgradeCarBodyWeight WHERE CarBodyId=? AND IsStock=1 ORDER BY Id LIMIT 1", bodyId).FirstOrDefault();
                if (weight != null)
                {
                    long id = FreeBodyUpgradeId("List_UpgradeCarBodyWeight", bodyId, 3);
                    double stockMass = Convert.ToDouble(weight["Mass"]);
                    weight["Id"] = id;
                    weight["Level"] = 3L;
                    weight["IsStock"] = 0L;
                    weight["ManufacturerID"] = 206L;
                    weight["Mass"] = Math.Round(stockMass * 0.80, 4);
                    weight["Price"] = 2350L;
                    weight["RequiresGraphics"] = 0L;
                    InsertClonedRow("List_UpgradeCarBodyWeight", weight);
                    created?.Add(new DbCreatedRow("List_UpgradeCarBodyWeight", "Id", id));
                    Log("\u2713 added missing Race weight-reduction upgrade", "ok");
                }
            }
        }
    }

    // Cars with no upgrade menu still have one stock suspension and two stock
    // physics rows. Clone those car-specific values instead of borrowing another
    // vehicle's geometry, then turn the clones into Race (3), Rally (4) or Drift (5).
    bool EnsureSuspensionUpgrade(long carId, int level, List<DbCreatedRow> created = null)
    {
        if ((ScalarL("SELECT COUNT(*) FROM List_UpgradeSpringDamper WHERE Ordinal=? AND Level=? AND IsStock=0", carId, level) ?? 0) > 0)
            return true;

        if (!EnsureStockSuspensionRow(carId, created)) return false;

        var stock = Query("SELECT * FROM List_UpgradeSpringDamper WHERE Ordinal=? AND IsStock=1 ORDER BY Id LIMIT 1", carId).FirstOrDefault();
        if (stock == null) { Log("suspension upgrade skipped: car has no stock suspension row", "warn"); return false; }
        long stockFrontId = Convert.ToInt64(stock["FrontSpringDamperPhysicsID"]);
        long stockRearId = Convert.ToInt64(stock["RearSpringDamperPhysicsID"]);
        var front = Query("SELECT * FROM List_SpringDamperPhysics WHERE SpringDamperPhysicsID=?", stockFrontId).FirstOrDefault();
        var rear = Query("SELECT * FROM List_SpringDamperPhysics WHERE SpringDamperPhysicsID=?", stockRearId).FirstOrDefault();
        if (front == null || rear == null) { Log("suspension upgrade skipped: stock physics rows are missing", "warn"); return false; }

        string label = level switch { 3 => "Race", 4 => "Rally", _ => "Drift" };
        string savepoint = "add_suspension_" + level;
        try
        {
            Exec("SAVEPOINT " + savepoint);
            long carBase = carId * 1000;
            long frontId = FreeSpringPhysicsId(carBase + level, carBase + 100);
            long rearId = FreeSpringPhysicsId(carBase + 100 + level, carBase + 200);

            front["SpringDamperPhysicsID"] = frontId;
            front["Ordinal"] = carId;
            front["SuspensionPhysicsTypeID"] = level switch { 3 => 125L, 4 => 302L, _ => 701L };
            front["UseBlowOffDamper"] = level == 4 ? 1L : 0L;
            rear["SpringDamperPhysicsID"] = rearId;
            rear["Ordinal"] = carId;
            rear["SuspensionPhysicsTypeID"] = level switch { 3 => 126L, 4 => 303L, _ => 702L };
            rear["UseBlowOffDamper"] = level == 4 ? 1L : 0L;
            InsertClonedRow("List_SpringDamperPhysics", front);
            InsertClonedRow("List_SpringDamperPhysics", rear);

            long preferredUpgradeId = carBase + level;
            long upgradeId = (ScalarL("SELECT COUNT(*) FROM List_UpgradeSpringDamper WHERE Id=?", preferredUpgradeId) ?? 0) == 0
                ? preferredUpgradeId : NextId("List_UpgradeSpringDamper", carId);
            stock["Id"] = upgradeId;
            stock["Ordinal"] = carId;
            stock["Level"] = (long)level;
            stock["ManufacturerID"] = 206L;
            stock["MassDiff"] = 0d;
            stock["Price"] = 2050L;
            stock["IsStock"] = 0L;
            stock["FrontSpringDamperPhysicsID"] = frontId;
            stock["RearSpringDamperPhysicsID"] = rearId;
            stock["PreloadAndDroopDamperID"] = level == 4 ? 1L : 0L;
            if (level == 3)
            {
                stock["SteerMaxAngle"] = 37d;
                stock["SteerMaxAngleFiltered"] = 29d;
                stock["SteeringSettingsProfileID"] = 5L;
            }
            else if (level == 5)
            {
                stock["SteerMaxAngle"] = 50d;
                stock["SteerMaxAngleFiltered"] = 50d;
                stock["SteeringSettingsProfileID"] = 1L;
            }
            InsertClonedRow("List_UpgradeSpringDamper", stock);
            Exec("RELEASE " + savepoint);
            created?.Add(new DbCreatedRow("List_SpringDamperPhysics", "SpringDamperPhysicsID", frontId));
            created?.Add(new DbCreatedRow("List_SpringDamperPhysics", "SpringDamperPhysicsID", rearId));
            created?.Add(new DbCreatedRow("List_UpgradeSpringDamper", "Id", upgradeId));
            Log($"✓ added missing {label} suspension upgrade from this car's stock physics", "ok");
            return true;
        }
        catch (Exception ex)
        {
            try { Exec("ROLLBACK TO " + savepoint); Exec("RELEASE " + savepoint); } catch { }
            Log($"{label} suspension creation failed: {ex.Message}", "err");
            return false;
        }
    }

    // Extend the Rally suspension's upper ride-height range. Resolve the actual
    // linked physics rows instead of assuming every database uses ×004/×104.
    // The target is derived from stock, so pressing Apply repeatedly cannot
    // accumulate another 0.10 m each time.
    void ApplyLiftKit(long carId)
    {
        if (!EnsureSuspensionUpgrade(carId, 4)) return;
        var rows = Query(@"SELECT rally.FrontSpringDamperPhysicsID rf,
                                 rally.RearSpringDamperPhysicsID rr,
                                 stock.FrontSpringDamperPhysicsID sf,
                                 stock.RearSpringDamperPhysicsID sr
                          FROM List_UpgradeSpringDamper rally
                          JOIN List_UpgradeSpringDamper stock
                            ON stock.Ordinal=rally.Ordinal AND stock.IsStock=1
                          WHERE rally.Ordinal=? AND rally.Level=4 AND rally.IsStock=0
                          ORDER BY rally.Id LIMIT 1", carId);
        if (rows.Count == 0)
        {
            Log("lift skipped: this car has no Rally suspension upgrade", "warn");
            return;
        }

        var row = rows[0];
        long frontRally = Convert.ToInt64(row["rf"]), rearRally = Convert.ToInt64(row["rr"]);
        long frontStock = Convert.ToInt64(row["sf"]), rearStock = Convert.ToInt64(row["sr"]);
        double frontBase = Convert.ToDouble(Scalar("SELECT MaxRideHeight FROM List_SpringDamperPhysics WHERE SpringDamperPhysicsID=?", frontStock) ?? 0d);
        double rearBase = Convert.ToDouble(Scalar("SELECT MaxRideHeight FROM List_SpringDamperPhysics WHERE SpringDamperPhysicsID=?", rearStock) ?? 0d);
        double frontLift = Math.Round(frontBase + 0.10, 4), rearLift = Math.Round(rearBase + 0.10, 4);

        Exec("UPDATE List_SpringDamperPhysics SET MaxRideHeight=? WHERE SpringDamperPhysicsID=?", frontLift, frontRally);
        Exec("UPDATE List_SpringDamperPhysics SET MaxRideHeight=? WHERE SpringDamperPhysicsID=?", rearLift, rearRally);
        Log($"✓ lift kit: Rally max ride height front {frontLift:0.####} · rear {rearLift:0.####}", "ok");
    }

    void ApplySlamKit(long carId, double steeringAngle)
    {
        if (!EnsureSuspensionUpgrade(carId, 5)) return;
        var drift = Query(@"SELECT Id,FrontSpringDamperPhysicsID f,RearSpringDamperPhysicsID r
                            FROM List_UpgradeSpringDamper
                            WHERE Ordinal=? AND Level=5 AND IsStock=0
                            ORDER BY Id LIMIT 1", carId).FirstOrDefault();
        if (drift == null) { Log("slam skipped: Drift suspension row is missing", "warn"); return; }
        int changed = Exec(@"UPDATE List_SpringDamperPhysics
                             SET MinRideHeight=0.01,MaxCompressHeight=0.005
                             WHERE SpringDamperPhysicsID IN (?,?)", drift["f"], drift["r"]);
        Exec("UPDATE List_UpgradeSpringDamper SET SteerMaxAngle=?,SteerMaxAngleFiltered=? WHERE Id=?",
             steeringAngle, steeringAngle, drift["Id"]);
        Log(changed == 2
            ? $"✓ slammed (Drift suspension lowered · steering angle {steeringAngle:0.##}°)"
            : $"slam incomplete: updated {changed} physics row(s) · steering angle {steeringAngle:0.##}°",
            changed == 2 ? "ok" : "warn");
    }

    // Give an electric car a real multi-speed gearbox (the "manual EV" effect). Gears come from the
    // drivetrain + NumGears; the car stays electric because its stock motor is untouched.
    void ApplyManualTransmission(long carId)
    {
        bool isEv = (ScalarL("SELECT COUNT(*) FROM List_UpgradeMotor WHERE Ordinal=? AND IsStock=1", carId) ?? 0) > 0;
        if (!isEv) { Log("manual transmission skipped: only applies to electric cars (car has no stock motor)", "warn"); return; }
        if ((ScalarL("SELECT COUNT(*) FROM List_UpgradeDrivetrain WHERE Ordinal=? AND IsStock=1", carId) ?? 0) == 0)
        { Log("manual transmission skipped: car has no stock drivetrain row", "warn"); return; }
        long drive = ScalarL("SELECT DriveTypeID FROM Data_Car WHERE Id=?", carId) ?? 2;
        // A 6-forward-gear combustion donor whose DrivetrainID carries a rich transmission upgrade tree.
        // List_UpgradeDrivetrainTransmission is keyed by DrivetrainID, so the Street/Sport/Race gearbox
        // tiers follow the DrivetrainID automatically — no rows to copy.
        const string donorSql = @"SELECT dc.PowertrainID pid, ud.DrivetrainID dt, ud.PowertrainId dpw, ud.ManufacturerId dm,
                                    (SELECT COUNT(*) FROM List_UpgradeDrivetrainTransmission t WHERE t.DrivetrainID=ud.DrivetrainID AND t.IsStock=0) upg
                                  FROM Data_Car dc
                                  JOIN List_UpgradeDrivetrain ud ON ud.Ordinal=dc.Id AND ud.IsStock=1
                                  WHERE dc.NumGears=6 {0}
                                    AND EXISTS(SELECT 1 FROM List_UpgradeEngine e WHERE e.Ordinal=dc.Id AND e.IsStock=1)
                                    AND (SELECT COUNT(*) FROM List_UpgradeDrivetrainTransmission t WHERE t.DrivetrainID=ud.DrivetrainID AND t.IsStock=0) >= 4
                                  ORDER BY upg DESC LIMIT 1";
        // prefer a donor with the same drive type; fall back to any 6-speed with an upgrade tree
        var donor = Query(string.Format(donorSql, "AND dc.DriveTypeID=?"), drive).FirstOrDefault()
                 ?? Query(string.Format(donorSql, "")).FirstOrDefault();
        if (donor == null) { Log("manual transmission skipped: no 6-speed donor with an upgrade tree in this DB", "warn"); return; }
        Exec("UPDATE Data_Car SET PowertrainID=?, NumGears=6 WHERE Id=?", donor["pid"], carId);
        Exec("UPDATE List_UpgradeDrivetrain SET DrivetrainID=?,PowertrainId=?,ManufacturerId=? WHERE Ordinal=? AND IsStock=1",
            donor["dt"], donor["dpw"], donor["dm"], carId);
        Log($"⚙ 6-speed manual transmission fitted (stays electric) — {donor["upg"]} transmission upgrade tiers included", "ok");
    }

    void ApplyWhitewalls(long carId)
    {
        long? refCar = ScalarL("SELECT Ordinal FROM List_UpgradeTireCompound WHERE TireModelName='Vintage_WhiteWall' LIMIT 1");
        if (refCar == null) { Log("whitewalls skipped: no reference car with Vintage_WhiteWall", "warn"); return; }
        Exec("UPDATE Data_Car SET TireBrandID=4 WHERE Id=?", carId);
        var refRows = Query("SELECT * FROM List_UpgradeTireCompound WHERE Ordinal=? ORDER BY Level", refCar.Value);
        Exec("DELETE FROM List_UpgradeTireCompound WHERE Ordinal=?", carId);
        string[] cols = { "Id","Ordinal","Level","IsStock","TireCompoundID","ManufacturerID","Price","MassDiff","DragScale","WindInstabilityScale","RequiresGraphics","TireModelName","WetTireModelName","FrontTirePressure","RearTirePressure","FrontTrackSpacerOffset","RearTrackSpacerOffset","releaseOrder" };
        int i = 0;
        foreach (var r in refRows.Take(13))
        {
            r["Ordinal"] = carId; r["Id"] = carId * 1000 + i;
            r["IsStock"] = ((string)r["TireModelName"] == "Sport") ? 1L : 0L;
            r["Price"] = Convert.ToInt64(r["IsStock"]) == 1 ? 0L : 50L;
            var vals = cols.Select(c => r.ContainsKey(c) ? r[c] : null).ToArray();
            Exec($"INSERT INTO List_UpgradeTireCompound ({string.Join(",", cols)}) VALUES ({string.Join(",", cols.Select(_ => "?"))})", vals);
            i++;
        }
        if ((ScalarL("SELECT COUNT(*) FROM List_UpgradeTireCompound WHERE Ordinal=? AND IsStock=1", carId) ?? 0) == 0)
            Exec("UPDATE List_UpgradeTireCompound SET IsStock=1 WHERE Id=(SELECT MIN(Id) FROM List_UpgradeTireCompound WHERE Ordinal=?)", carId);
        Log("✓ whitewalls + vintage tires added (brand set to 4)", "ok");
    }

    void ApplyFeTires(long carId)
    {
        long? refCar = ScalarL(@"SELECT Ordinal FROM List_UpgradeTireCompound
                                 WHERE TireModelName GLOB '*_FE'
                                 GROUP BY Ordinal ORDER BY COUNT(*) DESC LIMIT 1");
        if (refCar == null) { Log("FE tires skipped: no generic _FE tire reference found", "warn"); return; }

        var refRows = Query(@"SELECT * FROM List_UpgradeTireCompound
                              WHERE Ordinal=? AND TireModelName GLOB '*_FE'
                              ORDER BY Level,Id", refCar.Value);
        if (refRows.Count == 0) { Log("FE tires skipped: reference set is empty", "warn"); return; }

        // Preserve the target car's stock and ordinary compounds. Reapplying only
        // refreshes FE rows previously added by this option.
        Exec("DELETE FROM List_UpgradeTireCompound WHERE Ordinal=? AND IsStock=0 AND TireModelName GLOB '*_FE'", carId);
        bool needsStock = (ScalarL("SELECT COUNT(*) FROM List_UpgradeTireCompound WHERE Ordinal=? AND IsStock=1", carId) ?? 0) == 0;
        bool stockAdded = false;
        string[] cols = { "Id","Ordinal","Level","IsStock","TireCompoundID","ManufacturerID","Price","MassDiff","DragScale","WindInstabilityScale","RequiresGraphics","TireModelName","WetTireModelName","FrontTirePressure","RearTirePressure","FrontTrackSpacerOffset","RearTrackSpacerOffset","releaseOrder" };
        int added = 0;
        foreach (var r in refRows)
        {
            bool sourceStock = Convert.ToInt64(r["IsStock"] ?? 0L) == 1;
            bool makeStock = needsStock && !stockAdded && sourceStock;
            r["Id"] = NextId("List_UpgradeTireCompound", carId);
            r["Ordinal"] = carId;
            r["IsStock"] = makeStock ? 1L : 0L;
            r["Price"] = makeStock ? 0L : 50L;
            var vals = cols.Select(c => r.ContainsKey(c) ? r[c] : null).ToArray();
            Exec($"INSERT INTO List_UpgradeTireCompound ({string.Join(",", cols)}) VALUES ({string.Join(",", cols.Select(_ => "?"))})", vals);
            stockAdded |= makeStock;
            added++;
        }
        string donor = Convert.ToString(Scalar("SELECT MediaName FROM Data_Car WHERE Id=?", refCar.Value));
        Log($"✓ added {added} Forza Edition tire choices from {Naming.Friendly(donor)}", "ok");
    }

    // ---- numeric field helpers ----
    static int GetInt(TextBox t, int def) => int.TryParse(t.Text, out var v) ? v : def;
    static double? OptNum(TextBox t) => double.TryParse(t.Text, out var v) ? v : (double?)null;  // blank/invalid -> skip
}
