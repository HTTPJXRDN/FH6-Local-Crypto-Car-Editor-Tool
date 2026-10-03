using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace FH6LocalCryptoTool;

/// <summary>FH6 ZIP32 workspaces: authenticated encrypted-deflate (22), deflate (8), stored (0).</summary>
public static class Fh6ZipArchive
{
    public const string ManifestName = "fh6-zip-roundtrip.json";
    const string TemplateName = ".__original.zip", Format = "FH6-ZIP-workspace-v1";
    sealed record Manifest(string Format, string OriginalName, string TemplateSha256);
    public sealed record ExtractResult(int EntryCount, long OutputBytes, string OutputDirectory);
    public sealed record RepackResult(byte[] Archive, int EntryCount, int ChangedEntries, string OriginalName);

    public static bool IsWorkspace(string path) => Directory.Exists(path)
        ? File.Exists(Path.Combine(path, ManifestName))
        : Path.GetFileName(path).Equals(ManifestName, StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    public static ExtractResult Extract(string source, string outputDirectory, byte[] dataKey, byte[] macKey)
    {
        var bytes = ReadBounded(source, MotorsportLzxArchive.MaxArchive);
        var archive = MotorsportLzxArchive.Parse(bytes, fh6: true);
        CheckNames(archive);
        string target = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Extraction requires a new directory; outputs are never overwritten.");
        string staging = target + "." + Guid.NewGuid().ToString("N") + ".partial";
        bool owned = false;
        try {
            Directory.CreateDirectory(staging); owned = true;
            long total = 0;
            foreach (var entry in archive.Entries) {
                string destination = MotorsportLzxArchive.Destination(staging, entry.Name);
                if (entry.Name.EndsWith('/')) { Directory.CreateDirectory(destination); continue; }
                byte[] plain = Decode(entry, dataKey, macKey); total += plain.Length;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); WriteNew(destination, plain);
            }
            WriteNew(Path.Combine(staging, TemplateName), bytes);
            WriteNew(Path.Combine(staging, ManifestName), JsonSerializer.SerializeToUtf8Bytes(
                new Manifest(Format, Path.GetFileName(source), Convert.ToHexString(SHA256.HashData(bytes))), new JsonSerializerOptions { WriteIndented = true }));
            Directory.Move(staging, target); owned = false;
            return new(archive.Entries.Count, total, target);
        }
        finally { if (owned && Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    public static RepackResult Repack(string workspace, byte[] dataKey, byte[] macKey)
    {
        string root = Path.GetFullPath(Directory.Exists(workspace) ? workspace : Path.GetDirectoryName(workspace)!);
        RejectReparse(root);
        var manifest = JsonSerializer.Deserialize<Manifest>(ReadBounded(Path.Combine(root, ManifestName), 64 * 1024))
            ?? throw new InvalidDataException("Missing FH6 ZIP workspace metadata.");
        if (manifest.Format != Format || Path.GetFileName(manifest.OriginalName) != manifest.OriginalName || !manifest.OriginalName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsupported or damaged FH6 ZIP workspace metadata.");
        MotorsportLzxArchive.SafeName(manifest.OriginalName);
        byte[] source = ReadBounded(Path.Combine(root, TemplateName), MotorsportLzxArchive.MaxArchive);
        if (Convert.ToHexString(SHA256.HashData(source)) != manifest.TemplateSha256) throw new InvalidDataException("FH6 ZIP template fingerprint changed.");
        var archive = MotorsportLzxArchive.Parse(source, fh6: true); CheckNames(archive);
        var expected = archive.Entries.Where(e => !e.Name.EndsWith('/')).Select(e => MotorsportLzxArchive.Destination(root, e.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        expected.Add(Path.Combine(root, ManifestName)); expected.Add(Path.Combine(root, TemplateName));
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new Stack<string>(); directories.Push(root); int directoryCount = 0;
        while (directories.TryPop(out string? directory)) {
            if (++directoryCount > MotorsportLzxArchive.MaxEntries * 4) throw new InvalidDataException("Too many workspace directories.");
            foreach (string child in Directory.EnumerateFileSystemEntries(directory)) {
                RejectReparse(child);
                if (Directory.Exists(child)) directories.Push(child);
                else if (!expected.Contains(child) || !found.Add(child)) throw new InvalidDataException("Unexpected workspace file: " + Path.GetRelativePath(root, child));
            }
        }
        if (!expected.SetEquals(found)) throw new InvalidDataException("A ZIP workspace file is missing. Adding/removing entries is not supported.");
        var payloads = new Dictionary<MotorsportLzxArchive.Entry, (int Size, byte[] Hash, ReadOnlyMemory<byte> Packed, uint Crc)>();
        int changed = 0; long total = 0;
        foreach (var entry in archive.Entries) {
            byte[] original = Decode(entry, dataKey, macKey);
            byte[] edited = entry.Name.EndsWith('/') ? original : ReadBounded(MotorsportLzxArchive.Destination(root, entry.Name), MotorsportLzxArchive.MaxEntry);
            total += edited.Length;
            if (total > MotorsportLzxArchive.MaxTotal) throw new InvalidDataException("Edited ZIP exceeds supported uncompressed limits.");
            bool different = !edited.AsSpan().SequenceEqual(original);
            ReadOnlyMemory<byte> packed = entry.Compressed;
            if (different) {
                byte[] compressed = MotorsportLzxArchive.Encode(edited, entry.Method == 22 ? (ushort)8 : entry.Method);
                packed = entry.Method == 22 ? ForzaZip.EncryptContainer(compressed, entry.Compressed.ToArray(), dataKey, macKey) : compressed;
                changed++;
            }
            payloads.Add(entry, (edited.Length, SHA256.HashData(edited), packed, different ? MotorsportLzxArchive.Crc32(edited) : entry.Crc));
        }
        if (changed == 0) return new(source, archive.Entries.Count, 0, manifest.OriginalName);
        using var output = new MemoryStream(); var centrals = new Dictionary<MotorsportLzxArchive.Entry, byte[]>();
        foreach (var entry in archive.Entries.OrderBy(e => e.Offset)) {
            if (entry.AlignBefore) MotorsportLzxArchive.WritePadding(output);
            var payload = payloads[entry]; int offset = checked((int)output.Position);
            byte[] header = AlignLocalHeader(entry, offset), central = (byte[])entry.Central.Clone();
            MotorsportLzxArchive.Put(header, 14, payload.Crc); MotorsportLzxArchive.Put(header, 18, (uint)payload.Packed.Length); MotorsportLzxArchive.Put(header, 22, (uint)payload.Size);
            MotorsportLzxArchive.Put(central, 16, payload.Crc); MotorsportLzxArchive.Put(central, 20, (uint)payload.Packed.Length); MotorsportLzxArchive.Put(central, 24, (uint)payload.Size); MotorsportLzxArchive.Put(central, 42, (uint)offset);
            PatchOffsets(header, central, (uint)(offset + header.Length));
            output.Write(header); output.Write(payload.Packed.Span); centrals.Add(entry, central);
            if (output.Length > MotorsportLzxArchive.MaxArchive) throw new InvalidDataException("Rebuilt ZIP exceeds supported limits.");
        }
        if (archive.AlignCentral) MotorsportLzxArchive.WritePadding(output);
        uint directoryOffset = checked((uint)output.Position);
        foreach (var entry in archive.Entries) output.Write(centrals[entry]);
        uint directorySize = checked((uint)output.Position - directoryOffset);
        byte[] end = (byte[])archive.End.Clone(); MotorsportLzxArchive.Put(end, 12, directorySize); MotorsportLzxArchive.Put(end, 16, directoryOffset); output.Write(end);
        if (output.Length > MotorsportLzxArchive.MaxArchive) throw new InvalidDataException("Rebuilt ZIP exceeds supported limits.");
        byte[] result = output.ToArray(); var verify = MotorsportLzxArchive.Parse(result, fh6: true);
        var hashes = payloads.ToDictionary(p => p.Key.Name, p => p.Value.Hash);
        foreach (var entry in verify.Entries)
            if (!SHA256.HashData(Decode(entry, dataKey, macKey)).AsSpan().SequenceEqual(hashes[entry.Name]))
                throw new InvalidDataException("Rebuilt FH6 ZIP verification failed: " + entry.Name);
        return new(result, archive.Entries.Count, changed, manifest.OriginalName);
    }

    static byte[] Decode(MotorsportLzxArchive.Entry entry, byte[] dataKey, byte[] macKey)
    {
        byte[] packed = entry.Compressed.ToArray();
        if (entry.Method != 22) return MotorsportLzxArchive.DecodePayload(packed, entry.Size, entry.Crc, entry.Method, entry.Name);
        if (!ForzaZip.ValidateContainer(packed, dataKey, macKey, out int length))
            throw new InvalidDataException("FH6 ZIP entry authentication failed: " + entry.Name + ". Wrong key, unsupported format, or damaged container.");
        byte[] decrypted = ForzaZip.DecryptContainer(packed, dataKey);
        return MotorsportLzxArchive.DecodePayload(decrypted.AsSpan(0, length).ToArray(), entry.Size, entry.Crc, 8, entry.Name);
    }
    static void PatchOffsets(byte[] header, byte[] central, uint offset)
    {
        MotorsportLzxArchive.PatchExtras(header, 30 + U16(header, 26), U16(header, 28), offset, localPadding: true);
        MotorsportLzxArchive.PatchExtras(central, 46 + U16(central, 28), U16(central, 30), offset);
    }
    static void CheckNames(MotorsportLzxArchive.Archive archive)
    {
        foreach (var entry in archive.Entries)
            if (entry.Name.Split('/')[0].Equals(ManifestName, StringComparison.OrdinalIgnoreCase) || entry.Name.Split('/')[0].Equals(TemplateName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("ZIP contains a reserved workspace path.");
    }
    static byte[] AlignLocalHeader(MotorsportLzxArchive.Entry entry, int offset)
    {
        byte[] original = entry.Header;
        int start = 30 + U16(original, 26), end = original.Length;
        using var extras = new MemoryStream(); bool aligned = false;
        for (int at = start; at < end;) {
            int length = U16(original, at + 2);
            if (U16(original, at) == 0x1123 && !original.AsSpan(at + 4, length).ContainsAnyExcept((byte)0)
                && (entry.Offset + original.Length) % 4096 == 0) {
                if (aligned) throw new InvalidDataException("Multiple local alignment fields are not supported.");
                aligned = true;
            }
            else extras.Write(original.AsSpan(at, 4 + length));
            at += 4 + length;
        }
        if (!aligned) return (byte[])original.Clone();
        int padding = (4096 - (offset + start + (int)extras.Length + 4) % 4096) % 4096;
        byte[] field = new byte[4 + padding];
        BinaryPrimitives.WriteUInt16LittleEndian(field, 0x1123);
        BinaryPrimitives.WriteUInt16LittleEndian(field.AsSpan(2), (ushort)padding); extras.Write(field);
        if (extras.Length > ushort.MaxValue) throw new InvalidDataException("Local ZIP alignment exceeds supported limits.");
        byte[] header = new byte[start + (int)extras.Length]; original.AsSpan(0, start).CopyTo(header);
        extras.ToArray().CopyTo(header, start);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(28), (ushort)extras.Length);
        return header;
    }
    static ushort U16(byte[] bytes, int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at, 2));
    static void RejectReparse(string path) { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Workspace links/junctions are not supported."); }
    static byte[] ReadBounded(string path, int maximum)
    {
        RejectReparse(path); using var file = File.OpenRead(path);
        if (file.Length > maximum) throw new InvalidDataException("File exceeds supported limits: " + Path.GetFileName(path));
        byte[] bytes = new byte[(int)file.Length]; file.ReadExactly(bytes);
        if (file.ReadByte() != -1) throw new InvalidDataException("File changed while reading.");
        return bytes;
    }
    static void WriteNew(string path, byte[] bytes) { using var file = new FileStream(path, FileMode.CreateNew); file.Write(bytes); }
}
