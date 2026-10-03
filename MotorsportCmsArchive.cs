using System.IO;
using System.IO.Compression;

namespace FH6LocalCryptoTool;

/// <summary>Local CMS inspection only. Extraction does not deploy an override or
/// update snapshot/cache manifests; those SHA-256 checks are outside the entry codec.</summary>
public static class MotorsportCmsArchive
{
    private const long MaximumArchiveBytes = 128L * 1024 * 1024;
    private const long MaximumOutputBytes = 128L * 1024 * 1024;
    private const int MaximumEntries = 4096;
    public sealed record ExtractedAsset(string PlaintextPath, string TemplatePath);
    public sealed record ExtractResult(int EntryCount, long OutputBytes, string OutputDirectory,
        IReadOnlyList<ExtractedAsset> Assets);

    public static bool HasZipSignature(string path)
    {
        using var file = File.OpenRead(path);
        Span<byte> prefix = stackalloc byte[4];
        return file.Read(prefix) == 4 && prefix.SequenceEqual("PK\x03\x04"u8);
    }

    public static bool HasCmsEntries(string path)
    {
        if (new FileInfo(path).Length > MaximumArchiveBytes) return false;
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.Entries.FirstOrDefault(e => !e.FullName.EndsWith('/') && e.Length > 0);
            if (entry is null || entry.Length < 32 || entry.Length % 16 != 0) return false;
            using var stream = entry.Open();
            Span<byte> prefix = stackalloc byte[16]; stream.ReadExactly(prefix);
            return MotorsportCmsContainer.HasPrefix(prefix);
        }
        catch (InvalidDataException) { return false; } // FH6 method-22 archives use their own reader.
    }

    public static ExtractResult Extract(string path, string outputDirectory)
    {
        if (new FileInfo(path).Length > MaximumArchiveBytes)
            throw new InvalidDataException("CMS archive exceeds the supported size limit.");
        string target = Path.GetFullPath(outputDirectory);
        if (File.Exists(target) || Directory.Exists(target))
            throw new IOException("CMS extraction needs a new output directory; existing output is never overwritten.");
        string staging = target + "." + Guid.NewGuid().ToString("N") + ".partial";
        string stagingRoot = staging + Path.DirectorySeparatorChar;
        bool owned = false;
        try
        {
            using var zip = ZipFile.OpenRead(path);
            if (zip.Entries.Count is 0 or > MaximumEntries)
                throw new InvalidDataException("CMS archive has an unsupported entry count.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var plans = new List<(ZipArchiveEntry Entry, string Original, string Plain)>();
            // Validate every output name before writing any entry.
            foreach (var entry in zip.Entries)
            {
                string original = SafeName(entry.FullName);
                if (entry.FullName.EndsWith('/')) continue;
                if (entry.Length is < 32 or > 17 * 1024 * 1024 || entry.Length % 16 != 0)
                    throw new InvalidDataException("CMS entry has an unsupported encrypted size: " + entry.FullName);
                string plain = Path.Combine(Path.GetDirectoryName(original) ?? "",
                    Path.GetFileNameWithoutExtension(original) + ".decrypted" + Path.GetExtension(original));
                if (!names.Add(original) || !names.Add(plain))
                    throw new InvalidDataException("CMS archive has colliding entry/output names.");
                plans.Add((entry, original, plain));
            }
            if (plans.Count == 0) throw new InvalidDataException("CMS archive contains no encrypted entries.");
            Directory.CreateDirectory(staging); owned = true;
            var assets = new List<ExtractedAsset>(); long total = 0;
            foreach (var plan in plans)
            {
                using var stream = plan.Entry.Open();
                byte[] encrypted = new byte[checked((int)plan.Entry.Length)];
                stream.ReadExactly(encrypted);
                if (stream.ReadByte() != -1) throw new InvalidDataException("CMS entry size does not match its ZIP metadata.");
                byte[] plaintext;
                try { plaintext = MotorsportCmsContainer.Decrypt(encrypted); }
                catch (InvalidDataException ex) { throw new InvalidDataException("CMS entry failed validation: " + plan.Entry.FullName, ex); }
                total = checked(total + plaintext.Length + encrypted.Length);
                if (total > MaximumOutputBytes)
                    throw new InvalidDataException("CMS archive exceeds the supported extraction size limit.");
                string template = Path.GetFullPath(Path.Combine(staging, plan.Original));
                string plain = Path.GetFullPath(Path.Combine(staging, plan.Plain));
                if (!template.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase) ||
                    !plain.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("CMS extraction path escaped its output directory.");
                Directory.CreateDirectory(Path.GetDirectoryName(template)!);
                WriteNew(template, encrypted); WriteNew(plain, plaintext);
                assets.Add(new(Path.Combine(target, plan.Plain), Path.Combine(target, plan.Original)));
            }
            Directory.Move(staging, target); owned = false;
            return new(plans.Count, total, target, assets);
        }
        finally
        {
            // Only the exact private staging directory created above belongs to this operation.
            if (owned && Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static string SafeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains(':') || name.Contains('\\') ||
            name.StartsWith('/') || name.Split('/').Any(p => p is "." or ".."))
            throw new InvalidDataException("CMS archive contains an unsafe entry path.");
        return name.Replace('/', Path.DirectorySeparatorChar);
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes);
    }
}
