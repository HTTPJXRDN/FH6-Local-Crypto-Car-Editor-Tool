using System.Reflection;
using System.Text.Json;

namespace ClipdScissorTool;

public sealed class Donor
{
    public string CarName { get; set; } = "";
    public string Type { get; set; } = "";     // lift / swing / pivot / other / none
    public string Sig  { get; set; } = "";
    public int Size    { get; set; }
    public bool Exotic { get; set; }            // opposite-handed door mechanism (opens the wrong way)
    public byte[] Curve { get; set; } = Array.Empty<byte>();
    public string IdHash { get; set; } = "";    // the channel id this donor is for
    public string Motion => Type;
    public override string ToString() => CarName;
}

// Loads donor_library.json (embedded): every car's version of each animation channel, keyed by
// the channel id-hash, each donor tagged with the real car name, motion type, structure and size.
public static class DonorLibrary
{
    static Dictionary<string, List<Donor>>? _lib;

    public static void EnsureLoaded()
    {
        if (_lib != null) return;
        _lib = new();
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("donor_library.json"));
        if (name == null) return;
        using var s = asm.GetManifestResourceStream(name)!;
        using var doc = JsonDocument.Parse(s);
        foreach (var chan in doc.RootElement.EnumerateObject())
        {
            var list = new List<Donor>();
            foreach (var d in chan.Value.EnumerateArray())
            {
                list.Add(new Donor
                {
                    IdHash  = chan.Name,
                    CarName = d.GetProperty("name").GetString() ?? "",
                    Type    = d.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
                    Sig     = d.TryGetProperty("sig",  out var g) ? g.GetString() ?? "" : "",
                    Size    = d.TryGetProperty("size", out var z) ? z.GetInt32() : 0,
                    Exotic  = d.TryGetProperty("exotic", out var x) && x.GetBoolean(),
                    Curve   = Convert.FromBase64String(d.GetProperty("b64").GetString() ?? "")
                });
            }
            _lib[chan.Name] = list;
        }
    }

    // Donors for a channel, EXCLUDING opposite-handed (exotic) mechanisms that open the wrong way.
    public static List<Donor> ForChannel(string idHash)
    {
        EnsureLoaded();
        var all = _lib != null && _lib.TryGetValue(idHash, out var v) ? v : new List<Donor>();
        return all.Where(d => !d.Exotic).ToList();
    }

    // A specific car's donor for a channel (searches all, incl. exotic — used when applying a
    // chosen car's full door set, e.g. mapping its front door onto a rear channel).
    public static Donor? Find(string idHash, string carName)
    {
        EnsureLoaded();
        return _lib != null && _lib.TryGetValue(idHash, out var v)
            ? v.FirstOrDefault(d => d.CarName == carName) : null;
    }
}
