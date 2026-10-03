using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;

namespace FH6LocalCryptoTool;

/// <summary>Read-only ZIP32/ZIP64 LZX extraction with exact Forza C0E3 dedupe IDs.</summary>
public static class MotorsportTrackArchive
{
    public const string ReportName = "forza-extract-only.json";
    private const int MaxEntries = 250_000, MaxDirectory = 64 * 1024 * 1024, MaxEntry = 256 * 1024 * 1024;
    private const long MaxArchive = 64L * 1024 * 1024 * 1024, MaxOutput = 16L * 1024 * 1024 * 1024;
    public sealed record ExtractResult(int EntryCount, int SharedEntries, long OutputBytes, string OutputDirectory, string? DedupePath);
    public sealed record ExtractionProgress(int Completed, int EntryCount, long OutputBytes, string EntryName);
    private sealed record Entry(string Name, byte[] RawName, long Offset, int PackedSize, int Size, uint Crc,
        ushort Method, ushort Flags, ulong? Reference, long? DataOffset);
    private sealed record DirectoryInfo(List<Entry> Entries, long Offset, string Comment);

    public static bool IsExtractOnlyFolder(string path) => Directory.Exists(path)
        ? File.Exists(Path.Combine(path, ReportName))
        : Path.GetFileName(path).Equals(ReportName, StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    public static bool HasExtractOnlyEntries(string path)
    {
        try
        {
            using var stream = Open(path);
            var end = ReadEnd(stream);
            if (end.Comment.Equals("\\Dedupe\\", StringComparison.OrdinalIgnoreCase)) return true;
            var entries = ReadDirectory(stream).Entries;
            // Normal car/camera ZIPs retain round trips within the writer's bounds.
            return entries.Any(e => e.Method == 21) && (end.IsZip64 || entries.Any(e => e.Reference.HasValue)
                || stream.Length > MotorsportLzxArchive.MaxArchive || entries.Count > MotorsportLzxArchive.MaxEntries
                || entries.Any(e => e.Size > MotorsportLzxArchive.MaxEntry || e.PackedSize > MotorsportLzxArchive.MaxEntry * 2)
                || entries.Sum(e => (long)e.Size) > MotorsportLzxArchive.MaxTotal);
        }
        catch (InvalidDataException) { return false; }
    }

    internal static bool HasMethod21Entries(string path)
    {
        // Inspect the central methods without opening payloads or hiding writer limits.
        using var stream = Open(path);
        var end = ReadEnd(stream); byte[] bytes = ReadAt(stream, end.Offset, end.Size); int at = 0;
        for (int number = 0; number < end.Count; number++)
        {
            Bounds(bytes, at, 46);
            if (U32(bytes, at) != 0x02014b50) throw new InvalidDataException("Invalid central ZIP entry.");
            if (U16(bytes, at + 10) == 21) return true;
            int length = 46 + U16(bytes, at + 28) + U16(bytes, at + 30) + U16(bytes, at + 32);
            Bounds(bytes, at, length); at += length;
        }
        return false;
    }

    public static string? FindDedupeArchive(string archivePath)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(archivePath));
        for (int level = 0; directory != null && level < 8; level++, directory = Path.GetDirectoryName(directory))
        {
            string candidate = Path.Combine(directory, "Dedupe.zip");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static ExtractResult Extract(string path, string outputDirectory, string? dedupePath = null,
        IProgress<ExtractionProgress>? progress = null)
    {
        string sourcePath = Path.GetFullPath(path);
        using var source = Open(sourcePath);
        var directory = ReadDirectory(source);
        if (!directory.Entries.Any(e => e.Method == 21)) throw new InvalidDataException("Not an LZX track archive.");
        long declaredTotal = directory.Entries.Sum(e => (long)e.Size);
        if (declaredTotal > MaxOutput) throw new InvalidDataException("Track extraction exceeds the 16 GiB output limit.");
        ValidateLayout(source, directory);
        int sharedCount = directory.Entries.Count(e => e.Reference.HasValue);
        dedupePath = sharedCount == 0 ? null : dedupePath ?? FindDedupeArchive(sourcePath);
        if (sharedCount != 0 && dedupePath == null)
            throw new InvalidDataException("This ZIP uses shared assets. Keep the matching Dedupe.zip in pcfamily (or beside the input ZIP), then extract again. No partial output was kept.");
        using var shared = dedupePath == null ? null : Open(dedupePath);
        var lookup = new Dictionary<ulong, Entry>();
        long sharedDirectoryOffset = 0;
        if (shared != null)
        {
            var sharedDirectory = ReadDirectory(shared); sharedDirectoryOffset = sharedDirectory.Offset;
            foreach (var entry in sharedDirectory.Entries)
            {
                if (entry.Reference.HasValue) throw new InvalidDataException("Nested Dedupe.zip references are unsupported.");
                if (!lookup.TryAdd(ReferenceId(entry.RawName), entry)) throw new InvalidDataException("Dedupe.zip has colliding reference IDs.");
            }
            foreach (var entry in directory.Entries.Where(e => e.Reference.HasValue))
            {
                if (!lookup.TryGetValue(entry.Reference!.Value, out var target))
                    throw new InvalidDataException("Shared reference is missing from the matching Dedupe.zip: " + entry.Name);
                if ((entry.Size, entry.PackedSize, entry.Crc, entry.Method) != (target.Size, target.PackedSize, target.Crc, target.Method))
                    throw new InvalidDataException("Dedupe.zip reference metadata does not match: " + entry.Name);
            }
        }

        string targetDirectory = Path.GetFullPath(outputDirectory);
        if (File.Exists(targetDirectory) || Directory.Exists(targetDirectory)) throw new IOException("Extraction requires a new output directory; nothing is overwritten.");
        string staging = targetDirectory + "." + Guid.NewGuid().ToString("N") + ".partial";
        bool owned = false;
        try
        {
            Directory.CreateDirectory(staging); owned = true;
            long total = 0; int completed = 0;
            foreach (var entry in directory.Entries.OrderBy(e => e.Offset))
            {
                string destination = MotorsportLzxArchive.Destination(staging, entry.Name);
                if (entry.Name.EndsWith('/')) Directory.CreateDirectory(destination);
                else
                {
                    var storedEntry = entry.Reference.HasValue ? lookup[entry.Reference.Value] : entry;
                    var stream = entry.Reference.HasValue ? shared! : source;
                    byte[] packed = ReadPayload(stream, storedEntry, entry.Reference.HasValue ? sharedDirectoryOffset : directory.Offset);
                    byte[] plain = MotorsportLzxArchive.DecodePayload(packed, entry.Size, entry.Crc, entry.Method, entry.Name);
                    total = checked(total + plain.Length);
                    if (total > MaxOutput) throw new InvalidDataException("Track extraction exceeds the output limit.");
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    output.Write(plain);
                }
                completed++;
                if (completed == 1 || completed % 32 == 0 || completed == directory.Entries.Count)
                    progress?.Report(new(completed, directory.Entries.Count, total, entry.Name));
            }
            byte[] report = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Format = "Forza-LZX-extract-only-v1", Mode = "extract-only", Source = sourcePath,
                EntryCount = directory.Entries.Count, SharedEntries = sharedCount, OutputBytes = total,
                DedupeArchive = dedupePath, Verification = "Every decoded entry passed its declared length and CRC32.",
                Note = "No ZIP template is stored. Repacking/re-encryption is not supported for this folder. Game files were not changed."
            }, new JsonSerializerOptions { WriteIndented = true });
            using (var file = new FileStream(Path.Combine(staging, ReportName), FileMode.CreateNew)) file.Write(report);
            Directory.Move(staging, targetDirectory); owned = false;
            return new(directory.Entries.Count, sharedCount, total, targetDirectory, dedupePath);
        }
        finally
        {
            // Only our exact, GUID-named staging path can be removed on failure.
            if (owned && Path.GetFullPath(staging) == staging && staging.StartsWith(targetDirectory + ".", StringComparison.OrdinalIgnoreCase) && Directory.Exists(staging))
                Directory.Delete(staging, true);
        }
    }

    private static FileStream Open(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Archive links/junctions are unsupported.");
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxArchive) { stream.Dispose(); throw new InvalidDataException("ZIP exceeds the 64 GiB input limit."); }
        return stream;
    }

    private sealed record End(int Count, long Offset, int Size, bool IsZip64, string Comment);
    private static End ReadEnd(FileStream stream)
    {
        int tailLength = (int)Math.Min(stream.Length, 65557);
        byte[] tail = ReadAt(stream, stream.Length - tailLength, tailLength); int end = -1;
        for (int at = tail.Length - 22; at >= 0; at--)
            if (U32(tail, at) == 0x06054b50 && at + 22 + U16(tail, at + 20) == tail.Length) { end = at; break; }
        if (end < 0 || U16(tail, end + 4) != 0 || U16(tail, end + 6) != 0 || U16(tail, end + 8) != U16(tail, end + 10))
            throw new InvalidDataException("Expected a complete, single-disk ZIP.");
        long recordsEnd = stream.Length - tailLength + end;
        ulong count = U16(tail, end + 10), size = U32(tail, end + 12), offset = U32(tail, end + 16);
        byte[] locator = recordsEnd >= 20 ? ReadAt(stream, recordsEnd - 20, 20) : [];
        bool hasLocator = locator.Length == 20 && U32(locator, 0) == 0x07064b50;
        if (hasLocator)
        {
            if (U32(locator, 4) != 0 || U32(locator, 16) != 1) throw new InvalidDataException("Multi-disk ZIP64 is unsupported.");
            long zip64Offset = ToLong(U64(locator, 8));
            byte[] zip64 = ReadAt(stream, zip64Offset, 56);
            ulong recordSize = U64(zip64, 4);
            if (U32(zip64, 0) != 0x06064b50 || recordSize is < 44 or > 4096 || zip64Offset + 12 + (long)recordSize != recordsEnd - 20
                || U32(zip64, 16) != 0 || U32(zip64, 20) != 0 || U64(zip64, 24) != U64(zip64, 32))
                throw new InvalidDataException("Invalid ZIP64 end record.");
            ulong count64 = U64(zip64, 32), size64 = U64(zip64, 40), offset64 = U64(zip64, 48);
            if ((count != ushort.MaxValue && count != count64) || (size != uint.MaxValue && size != size64) || (offset != uint.MaxValue && offset != offset64))
                throw new InvalidDataException("ZIP32/ZIP64 end records disagree.");
            count = count64; size = size64; offset = offset64; recordsEnd = zip64Offset;
        }
        else if (count == ushort.MaxValue || size == uint.MaxValue || offset == uint.MaxValue)
            throw new InvalidDataException("Missing ZIP64 end record.");
        if (count is 0 or > MaxEntries || size > MaxDirectory || offset > (ulong)recordsEnd || size != (ulong)recordsEnd - offset)
            throw new InvalidDataException("Invalid/bounded ZIP directory size, count, or location.");
        return new((int)count, ToLong(offset), (int)size, hasLocator, Encoding.ASCII.GetString(tail, end + 22, U16(tail, end + 20)));
    }

    private static DirectoryInfo ReadDirectory(FileStream stream)
    {
        var end = ReadEnd(stream); byte[] bytes = ReadAt(stream, end.Offset, end.Size);
        var entries = new List<Entry>(end.Count); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); int at = 0;
        for (int number = 0; number < end.Count; number++)
        {
            Bounds(bytes, at, 46);
            if (U32(bytes, at) != 0x02014b50) throw new InvalidDataException("Invalid central ZIP entry.");
            ushort flags = U16(bytes, at + 8), method = U16(bytes, at + 10);
            if ((flags & ~0x0800) != 0 || method is not (0 or 8 or 21) || U16(bytes, at + 34) != 0)
                throw new InvalidDataException("Unsupported ZIP compression, disk, encryption, or data descriptor.");
            int nameLength = U16(bytes, at + 28), extraLength = U16(bytes, at + 30), commentLength = U16(bytes, at + 32);
            Bounds(bytes, at, 46 + nameLength + extraLength + commentLength);
            byte[] rawName = bytes.AsSpan(at + 46, nameLength).ToArray();
            string name = (flags == 0x0800 ? new UTF8Encoding(false, true) : Encoding.GetEncoding(437)).GetString(rawName);
            MotorsportLzxArchive.SafeName(name);
            if (!names.Add(name.TrimEnd('/')) || name.Equals(ReportName, StringComparison.OrdinalIgnoreCase)
                || name.Equals(MotorsportLzxArchive.ManifestName, StringComparison.OrdinalIgnoreCase) || name.Equals(".__original.zip", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Colliding/reserved ZIP output name.");
            ulong packed = U32(bytes, at + 20), size = U32(bytes, at + 24), offset = U32(bytes, at + 42);
            bool needPacked = packed == uint.MaxValue, needSize = size == uint.MaxValue, needOffset = offset == uint.MaxValue;
            var extras = Extras(bytes, at + 46 + nameLength, extraLength);
            if (needSize || needPacked || needOffset)
            {
                if (!extras.TryGetValue(1, out var zip64)) throw new InvalidDataException("Missing ZIP64 entry sizes/offset.");
                int next = 0;
                if (needSize) { size = Next64(zip64, ref next); }
                if (needPacked) { packed = Next64(zip64, ref next); }
                if (needOffset) { offset = Next64(zip64, ref next); }
                if (next != zip64.Length) throw new InvalidDataException("Unsupported ZIP64 entry extra data.");
            }
            if (size > MaxEntry || packed > MaxEntry || offset > (ulong)end.Offset || end.Offset - ToLong(offset) < 30)
                throw new InvalidDataException("ZIP entry exceeds supported bounds/256 MiB size limit: " + name);
            ulong? reference = null; long? dataOffset = null;
            if (extras.TryGetValue(0xc0e3, out var dedupe))
            {
                if (dedupe.Length != 8) throw new InvalidDataException("Invalid Forza dedupe reference.");
                reference = U64(dedupe, 0);
            }
            if (extras.TryGetValue(0x1123, out var data))
            {
                if (data.Length is not (4 or 8)) throw new InvalidDataException("Invalid Forza data-offset field.");
                dataOffset = ToLong(data.Length == 4 ? U32(data, 0) : U64(data, 0));
            }
            uint crc = U32(bytes, at + 16);
            if (name.EndsWith('/') && (size != 0 || packed != 0 || crc != 0 || method != 0 || reference.HasValue))
                throw new InvalidDataException("Invalid directory entry.");
            entries.Add(new(name, rawName, ToLong(offset), (int)packed, (int)size, crc, method, flags, reference, dataOffset));
            at += 46 + nameLength + extraLength + commentLength;
        }
        if (at != bytes.Length) throw new InvalidDataException("ZIP central count/length mismatch.");
        var files = entries.Where(e => !e.Name.EndsWith('/')).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            string[] parts = entry.Name.TrimEnd('/').Split('/');
            for (int length = 1; length < parts.Length; length++)
                if (files.Contains(string.Join('/', parts.Take(length)))) throw new InvalidDataException("ZIP file/directory path collision.");
        }
        return new(entries, end.Offset, end.Comment);
    }

    private static long LocalDataOffset(FileStream stream, Entry entry)
    {
        byte[] header = ReadAt(stream, entry.Offset, 30);
        int nameLength = U16(header, 26), extraLength = U16(header, 28);
        if (U32(header, 0) != 0x04034b50 || U16(header, 6) != entry.Flags || U16(header, 8) != entry.Method
            || U32(header, 14) != entry.Crc || nameLength != entry.RawName.Length)
            throw new InvalidDataException("Local/central ZIP metadata disagree: " + entry.Name);
        byte[] variable = ReadAt(stream, entry.Offset + 30, nameLength + extraLength);
        if (!variable.AsSpan(0, nameLength).SequenceEqual(entry.RawName)) throw new InvalidDataException("Local/central ZIP names disagree.");
        ulong packed = U32(header, 18), size = U32(header, 22);
        var extras = Extras(variable, nameLength, extraLength);
        if (size == uint.MaxValue || packed == uint.MaxValue)
        {
            if (!extras.TryGetValue(1, out var zip64)) throw new InvalidDataException("Missing local ZIP64 size data.");
            int next = 0;
            if (size == uint.MaxValue) size = Next64(zip64, ref next);
            if (packed == uint.MaxValue) packed = Next64(zip64, ref next);
            if (next != zip64.Length) throw new InvalidDataException("Unsupported local ZIP64 extra data.");
        }
        if (size != (ulong)entry.Size || packed != (ulong)entry.PackedSize) throw new InvalidDataException("Local/central ZIP sizes disagree.");
        long dataOffset = entry.Offset + 30 + variable.Length;
        if (entry.DataOffset.HasValue && entry.DataOffset != dataOffset) throw new InvalidDataException("Forza ZIP data offset disagrees with local header.");
        return dataOffset;
    }

    private static byte[] ReadPayload(FileStream stream, Entry entry, long directoryOffset)
    {
        if (entry.Reference.HasValue) throw new InvalidDataException("Unresolved shared ZIP entry.");
        long dataOffset = LocalDataOffset(stream, entry);
        if (dataOffset > directoryOffset || entry.PackedSize > directoryOffset - dataOffset) throw new InvalidDataException("ZIP payload overlaps its central directory.");
        return ReadAt(stream, dataOffset, entry.PackedSize);
    }

    private static void ValidateLayout(FileStream stream, DirectoryInfo directory)
    {
        long next = 0;
        foreach (var entry in directory.Entries.OrderBy(e => e.Offset))
        {
            if (entry.Offset < next) throw new InvalidDataException("Overlapping ZIP local entries.");
            ValidatePadding(stream, next, entry.Offset);
            long start = LocalDataOffset(stream, entry);
            // C0E3 entries are header-only stubs: their declared compressed size lives in Dedupe.zip.
            next = checked(start + (entry.Reference.HasValue ? 0 : entry.PackedSize));
            if (next > directory.Offset) throw new InvalidDataException("ZIP payload overlaps its central directory.");
        }
        ValidatePadding(stream, next, directory.Offset);
    }

    private static void ValidatePadding(FileStream stream, long start, long end)
    {
        if (start == end) return;
        long length = end - start;
        if (length is < 6 or > 4101 || end % 4096 != 0) throw new InvalidDataException("Unsupported ZIP padding/gap.");
        byte[] padding = ReadAt(stream, start, (int)length);
        if (U32(padding, 0) != uint.MaxValue || U16(padding, 4) != length - 6 || padding.AsSpan(6).ContainsAnyExcept((byte)0))
            throw new InvalidDataException("Invalid Forza ZIP padding.");
    }

    // Native ZIP index uses two case-folded 32-bit passes (seed 5381), packed low/high.
    // Independently matched every one of Road Atlanta's 2,053 C0E3 IDs, never CRC guessing.
    internal static ulong ReferenceId(ReadOnlySpan<byte> name)
    {
        uint low = NameHash(name, 5381), high = NameHash(name, low);
        return low | ((ulong)high << 32);
    }
    private static uint NameHash(ReadOnlySpan<byte> name, uint hash)
    {
        foreach (byte raw in name)
        {
            if (raw >= 128) throw new InvalidDataException("Non-ASCII Dedupe.zip IDs are unsupported.");
            uint value = raw is >= (byte)'A' and <= (byte)'Z' ? (uint)(raw + 32) : raw;
            hash = unchecked(hash ^ ((hash << 5) + (hash >> 2) + value));
        }
        return hash;
    }
    private static Dictionary<ushort, byte[]> Extras(byte[] bytes, int start, int length)
    {
        Bounds(bytes, start, length); var result = new Dictionary<ushort, byte[]>(); int end = start + length;
        for (int at = start; at < end;)
        {
            if (end - at < 4) throw new InvalidDataException("Truncated ZIP extra field.");
            ushort tag = U16(bytes, at); int size = U16(bytes, at + 2);
            if (end - at - 4 < size || !result.TryAdd(tag, bytes.AsSpan(at + 4, size).ToArray())) throw new InvalidDataException("Malformed/duplicate ZIP extra field.");
            at += 4 + size;
        }
        return result;
    }
    private static ulong Next64(byte[] bytes, ref int at) { Bounds(bytes, at, 8); ulong value = U64(bytes, at); at += 8; return value; }
    private static long ToLong(ulong value) => value <= long.MaxValue ? (long)value : throw new InvalidDataException("ZIP64 offset is out of range.");
    private static byte[] ReadAt(FileStream stream, long at, int length)
    {
        if (at < 0 || length < 0 || at > stream.Length || length > stream.Length - at) throw new InvalidDataException("Truncated ZIP structure/payload.");
        stream.Position = at; byte[] bytes = new byte[length]; stream.ReadExactly(bytes); return bytes;
    }
    private static void Bounds(byte[] bytes, int at, int length) { if (at < 0 || length < 0 || length > bytes.Length - at) throw new InvalidDataException("Truncated ZIP metadata."); }
    private static ushort U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at, 2));
    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at, 4));
    private static ulong U64(byte[] b, int at) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(at, 8));
}
