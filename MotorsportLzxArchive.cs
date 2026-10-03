using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FH6LocalCryptoTool;

/// <summary>Template-preserving ZIP32 method-21 LZX workspaces, not encrypted CMS.</summary>
public static class MotorsportLzxArchive
{
    public const string ManifestName = "forza-zip-roundtrip.json";
    private const string TemplateName = ".__original.zip";
    private const string Format = "Forza-LZX-ZIP-workspace-v1";
    internal const int MaxArchive = 256 * 1024 * 1024, MaxEntry = 32 * 1024 * 1024, MaxTotal = 512 * 1024 * 1024, MaxEntries = 4096;
    public sealed record ExtractResult(int EntryCount, long OutputBytes, string OutputDirectory);
    public sealed record RepackResult(byte[] Archive, int EntryCount, int ChangedEntries, string OriginalName);
    private sealed record Manifest(string Format, string OriginalName, string TemplateSha256);
    internal sealed record Entry(string Name, int Offset, byte[] Header, byte[] Central, ReadOnlyMemory<byte> Compressed, int Size, uint Crc, ushort Method)
    {
        public bool AlignBefore { get; set; }
    }
    internal sealed record Archive(byte[] Bytes, List<Entry> Entries, byte[] End, bool AlignCentral);

    public static bool IsWorkspace(string path) => Directory.Exists(path)
        ? File.Exists(Path.Combine(path, ManifestName))
        : Path.GetFileName(path).Equals(ManifestName, StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    public static bool HasLzxEntries(string path)
    {
        // Detect from the directory, not a bounded full decode. A rejected LZX ZIP
        // must not fall through to the legacy encrypted local-header reader.
        return MotorsportTrackArchive.HasMethod21Entries(path);
    }

    public static ExtractResult Extract(string path, string outputDirectory)
    {
        byte[] bytes = ReadBounded(path, MaxArchive);
        var archive = Parse(bytes);
        if (!archive.Entries.Any(e => e.Method == 21)) throw new InvalidDataException("Not a method-21 LZX ZIP.");
        string target = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Extraction requires a new directory; outputs are never overwritten.");
        string staging = target + "." + Guid.NewGuid().ToString("N") + ".partial";
        bool owned = false;
        try
        {
            Directory.CreateDirectory(staging); owned = true;
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                string destination = Destination(staging, entry.Name);
                if (entry.Name.EndsWith('/')) { Directory.CreateDirectory(destination); continue; }
                byte[] plain = Decode(entry);
                total = checked(total + plain.Length);
                if (total > MaxTotal) throw new InvalidDataException("ZIP extraction exceeds the supported size limit.");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                WriteNew(destination, plain);
            }
            WriteNew(Path.Combine(staging, TemplateName), bytes);
            WriteNew(Path.Combine(staging, ManifestName), JsonSerializer.SerializeToUtf8Bytes(new Manifest(Format, Path.GetFileName(path), Convert.ToHexString(SHA256.HashData(bytes))), new JsonSerializerOptions { WriteIndented = true }));
            Directory.Move(staging, target); owned = false;
            return new(archive.Entries.Count, total, target);
        }
        finally
        {
            // Delete only this exact, newly-created private staging directory.
            if (owned && Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    public static RepackResult Repack(string workspace)
    {
        if (MotorsportTrackArchive.IsExtractOnlyFolder(workspace)) throw new InvalidDataException("This is an extract-only ZIP folder; repacking is not supported.");
        string root = Path.GetFullPath(Directory.Exists(workspace) ? workspace : Path.GetDirectoryName(workspace)!);
        RejectReparse(root);
        RejectReparse(Path.Combine(root, ManifestName));
        RejectReparse(Path.Combine(root, TemplateName));
        var manifest = JsonSerializer.Deserialize<Manifest>(ReadBounded(Path.Combine(root, ManifestName), 64 * 1024))
            ?? throw new InvalidDataException("Missing ZIP workspace metadata.");
        if (manifest.Format != Format || Path.GetFileName(manifest.OriginalName) != manifest.OriginalName || !manifest.OriginalName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsupported or damaged ZIP workspace metadata.");
        SafeName(manifest.OriginalName);
        byte[] source = ReadBounded(Path.Combine(root, TemplateName), MaxArchive);
        if (Convert.ToHexString(SHA256.HashData(source)) != manifest.TemplateSha256) throw new InvalidDataException("ZIP workspace template fingerprint changed.");
        var archive = Parse(source);
        if (!archive.Entries.Any(e => e.Method == 21)) throw new InvalidDataException("Workspace template is not a method-21 LZX ZIP.");
        var expected = archive.Entries.Where(e => !e.Name.EndsWith('/')).Select(e => Destination(root, e.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        expected.Add(Path.Combine(root, ManifestName)); expected.Add(Path.Combine(root, TemplateName));
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new Stack<string>(); directories.Push(root); int directoryCount = 0;
        while (directories.TryPop(out string? directory))
        {
            if (++directoryCount > MaxEntries * 4) throw new InvalidDataException("Too many workspace directories.");
            foreach (string child in Directory.EnumerateFileSystemEntries(directory))
            {
                RejectReparse(child);
                if (Directory.Exists(child)) directories.Push(child);
                else if (!expected.Contains(child) || !found.Add(child)) throw new InvalidDataException("Unexpected workspace file: " + Path.GetRelativePath(root, child));
            }
        }
        if (!expected.SetEquals(found)) throw new InvalidDataException("A ZIP workspace file is missing. Adding/removing entries is not supported.");
        var payloads = new Dictionary<Entry, (int Size, byte[] Hash, ReadOnlyMemory<byte> Compressed, uint Crc)>();
        int changed = 0; long total = 0;
        foreach (var entry in archive.Entries)
        {
            byte[] original = Decode(entry);
            byte[] edited = entry.Name.EndsWith('/') ? original : ReadBounded(Destination(root, entry.Name), MaxEntry);
            total += edited.Length;
            if (total > MaxTotal) throw new InvalidDataException("Edited ZIP exceeds the supported uncompressed size limit.");
            bool different = !edited.AsSpan().SequenceEqual(original);
            ReadOnlyMemory<byte> compressed = different ? Encode(edited, entry.Method) : entry.Compressed;
            if (different) changed++;
            payloads.Add(entry, (edited.Length, SHA256.HashData(edited), compressed, different ? Crc32(edited) : entry.Crc));
        }
        if (changed == 0) return new(source, archive.Entries.Count, 0, manifest.OriginalName);
        using var output = new MemoryStream();
        var centrals = new Dictionary<Entry, byte[]>();
        foreach (var entry in archive.Entries.OrderBy(e => e.Offset))
        {
            if (entry.AlignBefore) WritePadding(output);
            var payload = payloads[entry]; int offset = checked((int)output.Position);
            byte[] header = (byte[])entry.Header.Clone(), central = (byte[])entry.Central.Clone();
            Put(header, 14, payload.Crc); Put(header, 18, (uint)payload.Compressed.Length); Put(header, 22, (uint)payload.Size);
            Put(central, 16, payload.Crc); Put(central, 20, (uint)payload.Compressed.Length); Put(central, 24, (uint)payload.Size); Put(central, 42, (uint)offset);
            PatchExtras(central, 46 + U16(central, 28), U16(central, 30), (uint)(offset + header.Length));
            output.Write(header); output.Write(payload.Compressed.Span); centrals.Add(entry, central);
            if (output.Length > MaxArchive) throw new InvalidDataException("Rebuilt ZIP exceeds the supported size limit.");
        }
        if (archive.AlignCentral) WritePadding(output);
        uint centralOffset = checked((uint)output.Position);
        foreach (var entry in archive.Entries) output.Write(centrals[entry]);
        uint centralSize = checked((uint)output.Position - centralOffset);
        byte[] end = (byte[])archive.End.Clone(); Put(end, 12, centralSize); Put(end, 16, centralOffset); output.Write(end);
        if (output.Length > MaxArchive) throw new InvalidDataException("Rebuilt ZIP exceeds the supported size limit.");
        byte[] result = output.ToArray(); var verify = Parse(result);
        var expectedHashes = payloads.ToDictionary(p => p.Key.Name, p => p.Value.Hash);
        foreach (var entry in verify.Entries)
            if (!SHA256.HashData(Decode(entry)).AsSpan().SequenceEqual(expectedHashes[entry.Name]))
                throw new InvalidDataException("Rebuilt ZIP verification failed: " + entry.Name);
        return new(result, archive.Entries.Count, changed, manifest.OriginalName);
    }

    internal static Archive Parse(byte[] bytes, bool fh6 = false)
    {
        int end = -1;
        for (int at = bytes.Length - 22; at >= Math.Max(0, bytes.Length - 65557); at--)
            if (U32(bytes, at) == 0x06054b50 && at + 22 + U16(bytes, at + 20) == bytes.Length) { end = at; break; }
        if (end < 0 || U16(bytes, end + 4) != 0 || U16(bytes, end + 6) != 0 || U16(bytes, end + 8) != U16(bytes, end + 10))
            throw new InvalidDataException("Only complete single-disk ZIP32 archives are supported.");
        int count = U16(bytes, end + 10);
        uint directorySize = U32(bytes, end + 12), directoryOffset = U32(bytes, end + 16);
        if (count is 0 or > MaxEntries || (long)directoryOffset + directorySize != end) throw new InvalidDataException("Invalid ZIP central directory.");
        var entries = new List<Entry>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0; int pos = checked((int)directoryOffset);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        for (int number = 0; number < count; number++)
        {
            Range(bytes, pos, 46);
            if (U32(bytes, pos) != 0x02014b50) throw new InvalidDataException("Invalid ZIP central entry.");
            int nameLength = U16(bytes, pos + 28), extraLength = U16(bytes, pos + 30), commentLength = U16(bytes, pos + 32);
            int recordLength = 46 + nameLength + extraLength + commentLength; Range(bytes, pos, recordLength);
            ushort flags = U16(bytes, pos + 8), method = U16(bytes, pos + 10);
            if ((flags & ~0x0800) != 0 || (fh6 ? method is not (0 or 8 or 22) : method is not (0 or 8 or 21)) || U16(bytes, pos + 34) != 0)
                throw new InvalidDataException("Unsupported ZIP flags, encryption, descriptor, disk, or compression method.");
            string name = (flags == 0x0800 ? new UTF8Encoding(false, true) : Encoding.GetEncoding(437)).GetString(bytes, pos + 46, nameLength);
            SafeName(name);
            if (!names.Add(name.TrimEnd('/')) || name.Equals(ManifestName, StringComparison.OrdinalIgnoreCase) || name.Equals(TemplateName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("ZIP contains colliding or reserved workspace names.");
            if (!name.EndsWith('/')) fileNames.Add(name);
            uint compressed = U32(bytes, pos + 20), size = U32(bytes, pos + 24), crc = U32(bytes, pos + 16), offsetValue = U32(bytes, pos + 42);
            if (compressed > MaxEntry * 2 || size > MaxEntry || offsetValue > int.MaxValue) throw new InvalidDataException("ZIP entry exceeds supported limits.");
            int offset = (int)offsetValue; Range(bytes, offset, 30);
            if (U32(bytes, offset) != 0x04034b50 || U16(bytes, offset + 6) != flags || U16(bytes, offset + 8) != method
                || U32(bytes, offset + 14) != crc || U32(bytes, offset + 18) != compressed || U32(bytes, offset + 22) != size || U16(bytes, offset + 26) != nameLength)
                throw new InvalidDataException("ZIP local and central entry metadata disagree.");
            int localLength = 30 + nameLength + U16(bytes, offset + 28); Range(bytes, offset, checked(localLength + (int)compressed));
            if (offset + (long)localLength + compressed > directoryOffset || !bytes.AsSpan(offset + 30, nameLength).SequenceEqual(bytes.AsSpan(pos + 46, nameLength)))
                throw new InvalidDataException("Invalid ZIP local entry bounds/name.");
            byte[] central = bytes.AsSpan(pos, recordLength).ToArray();
            PatchExtras(central, 46 + nameLength, extraLength, (uint)(offset + localLength), verify: true);
            byte[] header = bytes.AsSpan(offset, localLength).ToArray(); PatchExtras(header, 30 + nameLength, U16(bytes, offset + 28), null, localPadding: fh6);
            if (name.EndsWith('/') && (size != 0 || compressed != 0 || method != 0 || crc != 0)) throw new InvalidDataException("Invalid ZIP directory entry.");
            total += size;
            if (total > MaxTotal) throw new InvalidDataException("ZIP exceeds supported uncompressed limits.");
            entries.Add(new(name, offset, header, central, bytes.AsMemory(offset + localLength, (int)compressed), (int)size, crc, method));
            pos += recordLength;
        }
        if (pos != end) throw new InvalidDataException("Central directory count/size mismatch.");
        int next = 0;
        foreach (var entry in entries.OrderBy(e => e.Offset))
        {
            if (entry.Offset < next) throw new InvalidDataException("ZIP has overlapping local entries.");
            if (entry.Offset != next)
            {
                ValidatePadding(bytes, next, entry.Offset);
                entry.AlignBefore = true;
            }
            next = checked(entry.Offset + entry.Header.Length + entry.Compressed.Length);
            string[] parts = entry.Name.TrimEnd('/').Split('/');
            for (int length = 1; length < parts.Length; length++)
                if (fileNames.Contains(string.Join('/', parts.Take(length)))) throw new InvalidDataException("ZIP has file/directory path collisions.");
        }
        if (next > directoryOffset) throw new InvalidDataException("ZIP local data overlaps its central directory.");
        bool alignCentral = next != directoryOffset;
        if (alignCentral) ValidatePadding(bytes, next, (int)directoryOffset);
        return new(bytes, entries, bytes.AsSpan(end).ToArray(), alignCentral);
    }

    // Forza padding record: u32 0xFFFFFFFF, u16 zero-byte count, then zeros.
    private static void ValidatePadding(byte[] bytes, int start, int end)
    {
        int length = end - start;
        if (length is < 6 or > 4101 || end % 4096 != 0 || U32(bytes, start) != uint.MaxValue
            || U16(bytes, start + 4) != length - 6 || bytes.AsSpan(start + 6, length - 6).ContainsAnyExcept((byte)0))
            throw new InvalidDataException("Unrecognized ZIP gap/padding record.");
    }
    internal static void WritePadding(MemoryStream output)
    {
        int length = (4096 - (int)(output.Position % 4096)) % 4096;
        if (length == 0) return;
        if (length < 6) length += 4096;
        byte[] padding = new byte[length]; Put(padding, 0, uint.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(padding.AsSpan(4), (ushort)(length - 6));
        output.Write(padding);
    }

    private static byte[] Decode(Entry entry) => DecodePayload(entry.Compressed.ToArray(), entry.Size, entry.Crc, entry.Method, entry.Name);

    // Shared with the stream-based, extract-only track reader. Keep one entry in memory.
    internal static byte[] DecodePayload(byte[] compressed, int size, uint crc, ushort method, string name)
    {
        byte[] plain;
        if (method == 0) plain = compressed;
        else if (method == 21) plain = XMem.Decompress(compressed, size);
        else
        {
            if (method != 8) throw new InvalidDataException("Unsupported ZIP compression method.");
            using var input = new MemoryStream(compressed); using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            plain = new byte[size]; deflate.ReadExactly(plain);
            if (deflate.ReadByte() != -1) throw new InvalidDataException("ZIP entry exceeds its declared size.");
        }
        if (plain.Length != size || Crc32(plain) != crc) throw new InvalidDataException("ZIP length/CRC validation failed: " + name);
        return plain;
    }

    internal static byte[] Encode(byte[] plain, ushort method)
    {
        if (method == 0) return plain;
        if (method == 21) return XMem.Compress(plain);
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, true)) deflate.Write(plain);
        return output.ToArray();
    }

    internal static void PatchExtras(byte[] record, int start, int length, uint? dataOffset, bool verify = false, bool localPadding = false)
    {
        int end = start + length;
        for (int at = start; at < end;)
        {
            if (at + 4 > end) throw new InvalidDataException("Truncated ZIP extra field.");
            int size = U16(record, at + 2); ushort id = U16(record, at);
            if (at + 4 + size > end || id == 1) throw new InvalidDataException("Unsupported ZIP64 or malformed extra field.");
            if (id == 0x1123)
            {
                // FH6 local headers also use this ID for zero-filled data alignment.
                // The central field remains an exact four-byte absolute data offset.
                if (localPadding && !record.AsSpan(at + 4, size).ContainsAnyExcept((byte)0)) { at += 4 + size; continue; }
                if (size != 4) throw new InvalidDataException("Invalid Forza ZIP data-offset extra field.");
                if (dataOffset.HasValue)
                {
                    if (verify && U32(record, at + 4) != dataOffset.Value) throw new InvalidDataException("Forza ZIP data-offset extra field disagrees with its local entry.");
                    if (!verify) Put(record, at + 4, dataOffset.Value);
                }
            }
            at += 4 + size;
        }
    }

    internal static void SafeName(string name)
    {
        string value = name.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(value) || name.StartsWith('/') || name.Contains('\\') || name.Contains(':')) throw new InvalidDataException("Unsafe ZIP path.");
        foreach (string part in value.Split('/'))
        {
            string device = part.Split('.')[0].ToUpperInvariant();
            if (part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || device is "CON" or "PRN" or "AUX" or "NUL" || (device.Length == 4 && (device.StartsWith("COM") || device.StartsWith("LPT")) && device[3] is >= '1' and <= '9'))
                throw new InvalidDataException("Unsafe ZIP path component.");
        }
    }
    internal static string Destination(string root, string name)
    {
        SafeName(name); string prefix = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        string result = Path.GetFullPath(Path.Combine(prefix, name.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("ZIP output escapes the workspace.");
        return result;
    }
    private static void RejectReparse(string path) { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Workspace links/junctions are not supported."); }
    private static byte[] ReadBounded(string path, int maximum)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > maximum) throw new InvalidDataException("File exceeds supported limits: " + Path.GetFileName(path));
        byte[] bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("File changed while reading.");
        return bytes;
    }
    private static void WriteNew(string path, byte[] bytes) { using var file = new FileStream(path, FileMode.CreateNew); file.Write(bytes); }
    private static void Range(byte[] bytes, int start, int length) { if (start < 0 || length < 0 || (long)start + length > bytes.Length) throw new InvalidDataException("Truncated ZIP structure."); }
    private static ushort U16(byte[] bytes, int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at, 2));
    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at, 4));
    internal static void Put(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at, 4), value);
    private static readonly uint[] CrcTable = BuildCrcTable();
    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            uint value = index;
            for (int bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) == 0 ? 0 : 0xedb88320u);
            table[index] = value;
        }
        return table;
    }
    internal static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes) crc = (crc >> 8) ^ CrcTable[(crc ^ value) & 255];
        return ~crc;
    }

    private static class XMem
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
        [DllImport("xcompress64.dll")] private static extern int XMemCreateDecompressionContext(int codec, IntPtr parameters, int flags, out IntPtr context);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
        [DllImport("xcompress64.dll")] private static extern int XMemDecompress(IntPtr context, byte[] destination, ref nuint size, byte[] source, nuint sourceSize);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
        [DllImport("xcompress64.dll")] private static extern void XMemDestroyDecompressionContext(IntPtr context);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
        [DllImport("xcompress64.dll")] private static extern int XMemCreateCompressionContext(int codec, IntPtr parameters, int flags, out IntPtr context);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
        [DllImport("xcompress64.dll")] private static extern int XMemCompress(IntPtr context, byte[] destination, ref nuint size, byte[] source, nuint sourceSize);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
        [DllImport("xcompress64.dll")] private static extern void XMemDestroyCompressionContext(IntPtr context);
        private static void RequireRuntime()
        {
            if (!Environment.Is64BitProcess || !File.Exists(Path.Combine(AppContext.BaseDirectory, "xcompress64.dll"))) throw new InvalidDataException("LZX requires a legally obtained x64 xcompress64.dll beside the executable. See NATIVE_RUNTIME.md for release setup.");
        }
        public static byte[] Decompress(byte[] source, int length)
        {
            RequireRuntime();
            if (length == 0 && source.Length == 0) return [];
            byte[] output = new byte[Math.Max(1, length)]; nuint size = (nuint)length; IntPtr context = IntPtr.Zero;
            try
            {
                if (XMemCreateDecompressionContext(1, IntPtr.Zero, 0, out context) != 0 || context == IntPtr.Zero) throw new InvalidDataException("Cannot initialize LZX decoder.");
                int result = XMemDecompress(context, output, ref size, source, (nuint)source.Length);
                if (result != 0 || size != (nuint)length) throw new InvalidDataException($"LZX decoding failed (0x{result:X8}) or returned the wrong length.");
                return length == 0 ? [] : output;
            }
            finally { if (context != IntPtr.Zero) XMemDestroyDecompressionContext(context); }
        }
        public static byte[] Compress(byte[] source)
        {
            RequireRuntime();
            if (source.Length == 0) return [];
            byte[] output = new byte[checked(source.Length + source.Length / 8 + 65536)]; nuint size = (nuint)output.Length; IntPtr context = IntPtr.Zero;
            try
            {
                if (XMemCreateCompressionContext(1, IntPtr.Zero, 0, out context) != 0 || context == IntPtr.Zero) throw new InvalidDataException("Cannot initialize LZX encoder.");
                int result = XMemCompress(context, output, ref size, source, (nuint)source.Length);
                if (result != 0 || size > (nuint)output.Length) throw new InvalidDataException($"LZX compression failed (0x{result:X8}).");
                byte[] compressed = output.AsSpan(0, (int)size).ToArray();
                if (!Decompress(compressed, source.Length).AsSpan().SequenceEqual(source)) throw new InvalidDataException("LZX compression round-trip verification failed.");
                return compressed;
            }
            finally { if (context != IntPtr.Zero) XMemDestroyCompressionContext(context); }
        }
    }
}
