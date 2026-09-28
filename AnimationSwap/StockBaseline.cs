using System.Reflection;
using System.Text.Json;

namespace ClipdScissorTool;

// Stock "node-stream-end" (real animation data size) per car ID, built from unmodified game files.
// One value per car catches any grow/shrink of any channel — used to detect a file that was
// already modified before you even loaded it (the case the load-time size check couldn't see).
public static class StockBaseline
{
    static Dictionary<string, int>? _b;

    public static void EnsureLoaded()
    {
        if (_b != null) return;
        _b = new();
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("stock_baseline.json"));
        if (name == null) return;
        using var s = asm.GetManifestResourceStream(name)!;
        using var doc = JsonDocument.Parse(s);
        foreach (var p in doc.RootElement.EnumerateObject())
            _b[p.Name] = p.Value.GetInt32();
    }

    // Stock data size for a car ID, or null if the car isn't in the table (e.g. renamed file).
    public static int? StockEnd(string? carId)
    {
        EnsureLoaded();
        return carId != null && _b != null && _b.TryGetValue(carId, out var v) ? v : null;
    }
}
