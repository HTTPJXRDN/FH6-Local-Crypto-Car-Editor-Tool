using System.IO;

namespace ClipdScissorTool;

public sealed class Node
{
    public byte[] TypeHash = new byte[8];
    public bool IsLeaf;
    public byte[] LeafBytes = Array.Empty<byte>();
    public List<Node> Children = new();
    public byte[] Serialize()
    {
        byte[] content = IsLeaf ? LeafBytes : Children.SelectMany(c => c.Serialize()).ToArray();
        using var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes((uint)content.Length));
        ms.Write(TypeHash);
        ms.Write(content);
        return ms.ToArray();
    }
}

public sealed class Channel
{
    public required Node Desc;
    public required Node Curve;
    public string IdHash => Hex(Desc.LeafBytes);
    public int Size => Curve.LeafBytes.Length;
    public string Sig => ClipdFile.SegSignature(Curve.LeafBytes);
    public static string Hex(byte[] b) => string.Concat(b.Select(x => x.ToString("x2")));
}

public sealed class ClipdFile
{
    static readonly byte[] Magic = { 0xcc, 0x14, 0xa4, 0xb1 };
    public byte[] Header = Array.Empty<byte>();
    public List<Node> Roots = new();
    public byte[] Trailer = Array.Empty<byte>();
    public int PadTo;
    public string Path = "";

    public static uint Fnv1a(string s)
    { uint h = 0x811c9dc5; foreach (char c in s) { h ^= (byte)c; h *= 0x01000193; } return h; }
    public static string PartHash(string n, string a) =>
        Channel.Hex(BitConverter.GetBytes(Fnv1a(n.ToLowerInvariant() + "_" + a)));

    static readonly string[] Parts =
        { "doorlf","doorrf","doorlr","doorrr","hood","trunk","wing","spoiler","deck","boot",
          "headlightl","headlightr","roof","targa" };
    static readonly Dictionary<string,string> Labels =
        Parts.SelectMany(p => new[]{("open",p),("close",p)})
             .ToDictionary(t => PartHash(t.Item2, t.Item1), t => $"{t.Item2}_{t.Item1}");
    public static string Label(string id) => id.ToLowerInvariant() switch
    {
        "5a0ce0f3" => "speedometer",
        "776a27c9" => "tachometer",
        _ => Labels.TryGetValue(id, out var v) ? v : id
    };

    static uint U32(byte[] d, int o) => BitConverter.ToUInt32(d, o);

    static (Node,int) ParseNode(byte[] data, int off)
    {
        uint size = U32(data, off);
        var th = new byte[8]; Array.Copy(data, off + 4, th, 0, 8);
        var content = new byte[size]; Array.Copy(data, off + 12, content, 0, (int)size);
        var node = new Node { TypeHash = th };
        var kids = TryChildren(content);
        if (kids != null) { node.IsLeaf = false; node.Children = kids; }
        else { node.IsLeaf = true; node.LeafBytes = content; }
        return (node, 12 + (int)size);
    }
    static List<Node>? TryChildren(byte[] c)
    {
        if (c.Length < 12) return null;
        var kids = new List<Node>(); int p = 0, n = c.Length;
        while (p < n)
        {
            if (p + 12 > n) return null;
            uint size = U32(c, p);
            if (p + 12L + size > n) return null;
            var (child, adv) = ParseNode(c, p); kids.Add(child); p += adv;
        }
        return p == n ? kids : null;
    }
    static bool MagicAt(byte[] d, int p) =>
        p + 4 <= d.Length && d[p]==Magic[0]&&d[p+1]==Magic[1]&&d[p+2]==Magic[2]&&d[p+3]==Magic[3];

    public static ClipdFile Load(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        var cf = TryParse(data);
        if (cf == null) throw new InvalidDataException("Not a recognizable .clipd file.");
        cf.Path = path;
        return cf;
    }

    // Parse from raw bytes; returns null if the data isn't a valid clipd (used to verify output).
    public static ClipdFile? TryParse(byte[] data)
    {
        for (int start = 8; start < 64; start++)
        {
            var cf = TryLoad(data, start);
            if (cf != null) return cf;
        }
        return null;
    }
    static ClipdFile? TryLoad(byte[] data, int start)
    {
        var roots = new List<Node>(); int p = start;
        try
        {
            while (p + 12 <= data.Length)
            {
                if (MagicAt(data, p)) break;
                uint size = U32(data, p);
                if (size == 0 || p + 12L + size > data.Length) break;
                var (node, adv) = ParseNode(data, p); roots.Add(node); p += adv;
            }
        }
        catch { return null; }
        if (roots.Count == 0) return null;
        int nz = data.Length; while (nz > p && data[nz-1]==0) nz--;
        int ci = p; if (MagicAt(data, ci)) ci += 4;
        for (int k = ci; k < nz; k++) if (data[k] != 0) return null;
        return new ClipdFile { Header = data[..start], Roots = roots, Trailer = data[p..nz], PadTo = data.Length };
    }

    public byte[] Build()
    {
        SyncPreamble();                       // keep the "outer Mojo body size" trailer correct
        using var ms = new MemoryStream();
        ms.Write(Header);
        foreach (var r in Roots) ms.Write(r.Serialize());
        ms.Write(Trailer);
        var body = ms.ToArray();
        int target = PadTo;
        if (body.Length > target)             // grew past the original allocation -> next power of two
        {
            target = 0x1000;
            while (target < body.Length) target <<= 1;
        }
        var outp = new byte[Math.Max(target, body.Length)];
        Array.Copy(body, outp, body.Length);
        return outp;
    }

    // The first child of root1 (the "preamble") holds a u32 == root1-content-length minus 16.
    // FH6 validates this; if it's stale after a size change the car null-cars ("Ford of Doom").
    public void SyncPreamble()
    {
        if (Roots.Count == 0) return;
        var root1 = Roots[^1];
        if (root1.Children.Count == 0) return;
        var pre = root1.Children[0];
        if (!pre.IsLeaf || pre.LeafBytes.Length < 4) return;
        int contentLen = root1.Children.Sum(k => k.Serialize().Length);
        var b = (byte[])pre.LeafBytes.Clone();
        BitConverter.GetBytes((uint)(contentLen - 16)).CopyTo(b, 0);
        pre.LeafBytes = b;
    }

    // Read the current preamble value (for verification).
    public long PreambleValue()
    {
        if (Roots.Count == 0) return -1;
        var r1 = Roots[^1];
        if (r1.Children.Count == 0 || !r1.Children[0].IsLeaf || r1.Children[0].LeafBytes.Length < 4) return -1;
        return BitConverter.ToUInt32(r1.Children[0].LeafBytes, 0);
    }
    public long Root1ContentLen() => Roots.Count == 0 ? -1 : Roots[^1].Children.Sum(k => k.Serialize().Length);
    public void Save(string path) => File.WriteAllBytes(path, Build());

    // Offset where the real node data ends (before the trailing sentinel/padding). A single value
    // that changes if ANY channel grew or shrank — used against the stock baseline.
    public int NodeStreamEnd() => Header.Length + Roots.Sum(r => r.Serialize().Length);

    // Pull the car ID out of a carclips_<ID>.clipd filename (null if it doesn't match).
    public static string? CarIdFromPath(string path)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            System.IO.Path.GetFileName(path), @"carclips[_-]?(\w+)\.clipd",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    public List<Channel> Channels()
    {
        var kids = Roots[^1].Children;
        var list = new List<Channel>();
        for (int i = 1; i + 1 < kids.Count; i += 2)
            list.Add(new Channel { Desc = kids[i], Curve = kids[i+1] });
        return list;
    }

    // Import complete animation records. This does not synthesize GR2 data or
    // redirect a donor animation to a different part; its channel ID is kept.
    public int AddMissingChannelsFrom(ClipdFile donor, IEnumerable<string> channelIds)
    {
        ArgumentNullException.ThrowIfNull(donor);
        Node root = Roots[^1];
        if (root.Children.Count == 0 || !root.Children[0].IsLeaf || root.Children[0].LeafBytes.Length < 8)
            throw new InvalidDataException("Target CLIPD dictionary metadata is missing or truncated.");
        var source = donor.Channels().ToDictionary(c => c.IdHash, StringComparer.OrdinalIgnoreCase);
        var existing = Channels().Select(c => c.IdHash).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wanted = channelIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (string id in wanted)
            if (!source.ContainsKey(id)) throw new InvalidDataException($"Donor CLIPD has no channel {id}.");

        var additions = wanted.Where(id => !existing.Contains(id)).ToArray();
        if (additions.Length == 0) return 0;
        foreach (string id in additions)
        {
            root.Children.Add(CloneNode(source[id].Desc));
            root.Children.Add(CloneNode(source[id].Curve));
        }
        byte[] metadata = (byte[])root.Children[0].LeafBytes.Clone();
        BitConverter.GetBytes((uint)Channels().Count).CopyTo(metadata, 4);
        root.Children[0].LeafBytes = metadata;
        SyncPreamble();
        return additions.Length;
    }

    private static Node CloneNode(Node node) => new()
    {
        TypeHash = (byte[])node.TypeHash.Clone(),
        IsLeaf = node.IsLeaf,
        LeafBytes = (byte[])node.LeafBytes.Clone(),
        Children = node.Children.Select(CloneNode).ToList()
    };

    public int DictionaryChannelCount()
    {
        if (Roots.Count == 0 || Roots[^1].Children.Count == 0 ||
            !Roots[^1].Children[0].IsLeaf || Roots[^1].Children[0].LeafBytes.Length < 8)
            return -1;
        return checked((int)BitConverter.ToUInt32(Roots[^1].Children[0].LeafBytes, 4));
    }

    // Remove complete descriptor/curve channel pairs and keep the dictionary metadata in sync.
    // Used by the DB stance compatibility patch for cars whose authored suspension rig overrides
    // visual ride-height and track-width changes from the game database.
    public int RemoveChannelsByIdHashes(IEnumerable<string> idHashes)
    {
        var remove = new HashSet<string>(idHashes, StringComparer.OrdinalIgnoreCase);
        if (remove.Count == 0 || Roots.Count == 0) return 0;

        Node root = Roots[^1];
        if (root.Children.Count == 0) return 0;
        var kept = new List<Node> { root.Children[0] };
        int removed = 0;

        for (int i = 1; i + 1 < root.Children.Count; i += 2)
        {
            Node desc = root.Children[i];
            Node curve = root.Children[i + 1];
            string id = Channel.Hex(desc.LeafBytes);
            if (remove.Contains(id))
            {
                removed++;
                continue;
            }
            kept.Add(desc);
            kept.Add(curve);
        }

        if (removed == 0) return 0;
        root.Children = kept;

        // Dictionary metadata payload: [u32 body-size-minus-16][u32 channel-count].
        // SyncPreamble/Build updates the first value after the node stream changes.
        Node metadata = root.Children[0];
        if (!metadata.IsLeaf || metadata.LeafBytes.Length < 8)
            throw new InvalidDataException("Clip dictionary metadata is missing or truncated.");
        byte[] payload = (byte[])metadata.LeafBytes.Clone();
        BitConverter.GetBytes((uint)((kept.Count - 1) / 2)).CopyTo(payload, 4);
        metadata.LeafBytes = payload;
        SyncPreamble();
        return removed;
    }

    // ---- structure + operations ----
    public const int RotStart = 148;

    public static string SegSignature(byte[] curve)
    {
        int bh = 44;
        if (curve.Length < bh + 40) return "?";
        return $"{U32(curve, bh + 12)}/{U32(curve, bh + 32)}/{U32(curve, bh + 36)}";
    }

    // Overlay length = rotation region, bounded so we never copy/overwrite past either footer.
    public static int AutoRotationLength(byte[] target, byte[] source, int start = RotStart, int minRun = 48)
    {
        for (int i = start; i + minRun <= target.Length; i++)
            for (int j = start; j + minRun <= source.Length; j++)
            {
                int k = 0;
                while (k < minRun && target[i + k] == source[j + k]) k++;
                if (k >= minRun) return Math.Min(i, j) - start;
            }
        return Math.Max(0, Math.Min(source.Length, target.Length) - 60 - start);
    }

    // Copy a source curve's rotation keyframes into a target channel, keeping the target's exact size.
    // FULL SWAP: replace the target channel's entire curve with the donor's (grow/shrink allowed).
    // Patches the donor curve's embedded id-hash to the target channel's id so it drives the right
    // part even if the donor came from a different channel. Returns the size delta.
    public static int FullSwap(Channel target, byte[] donorCurve, string donorIdHash)
    {
        var nc = (byte[])donorCurve.Clone();
        var tgtId = target.Desc.LeafBytes;                 // 4-byte target id
        var srcId = HexToBytes(donorIdHash);               // 4-byte donor id
        if (!srcId.AsSpan().SequenceEqual(tgtId))
        {
            // replace the LAST occurrence of the donor id (the embedded tail id) with the target id
            int pos = LastIndexOf(nc, srcId);
            if (pos >= 0) Array.Copy(tgtId, 0, nc, pos, 4);
        }
        int delta = nc.Length - target.Curve.LeafBytes.Length;
        target.Curve.LeafBytes = nc;
        return delta;
    }
    static int LastIndexOf(byte[] hay, byte[] needle)
    {
        for (int i = hay.Length - needle.Length; i >= 0; i--)
        {
            bool ok = true;
            for (int k = 0; k < needle.Length; k++) if (hay[i+k] != needle[k]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }
    public static byte[] HexToBytes(string h)
    {
        var b = new byte[h.Length/2];
        for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(h.Substring(i*2,2),16);
        return b;
    }

    public static int OverlayRotation(Channel target, byte[] sourceCurve, int start, int length)
    {
        var t = (byte[])target.Curve.LeafBytes.Clone();
        int len = Math.Min(length, Math.Min(t.Length, sourceCurve.Length) - start);
        if (len < 0) len = 0;
        Array.Copy(sourceCurve, start, t, start, len);
        target.Curve.LeafBytes = t;
        return len;
    }

    public static List<int> DetectAxisOffsets(byte[] curve, int start = RotStart, int window = 64)
    {
        var offs = new List<int>();
        for (int o = start; o + 4 <= Math.Min(curve.Length, start + window); o += 4)
        {
            float v = BitConverter.ToSingle(curve, o);
            if (Math.Abs(v) > 0.05f && Math.Abs(v) < 1.5f) offs.Add(o);
        }
        return offs;
    }

    public static void FlipDirection(Channel ch, IEnumerable<int> offsets)
    {
        var b = (byte[])ch.Curve.LeafBytes.Clone();
        foreach (var o in offsets)
        {
            if (o + 4 > b.Length) continue;
            float v = BitConverter.ToSingle(b, o);
            Array.Copy(BitConverter.GetBytes(-v), 0, b, o, 4);
        }
        ch.Curve.LeafBytes = b;
    }
}
