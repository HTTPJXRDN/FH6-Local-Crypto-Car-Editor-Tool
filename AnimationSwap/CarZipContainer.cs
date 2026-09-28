using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace ClipdScissorTool;

public sealed record LoadedCarZip(string ClipdEntryName, byte[] ClipdBytes);

public static class CarZipContainer
{
    public static LoadedCarZip Load(string zipPath)
    {
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        ZipArchiveEntry[] clips = archive.Entries
            .Where(e => e.FullName.EndsWith(".clipd", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (clips.Length != 1)
            throw new InvalidDataException($"Expected exactly one .clipd inside the car ZIP; found {clips.Length}.");

        using Stream input = clips[0].Open();
        using var memory = new MemoryStream();
        input.CopyTo(memory);
        byte[] bytes = memory.ToArray();
        if (ClipdFile.TryParse(bytes) == null)
            throw new InvalidDataException($"The embedded file '{clips[0].FullName}' is not a recognizable CLIPD.");
        return new LoadedCarZip(clips[0].FullName, bytes);
    }

    public static void SavePatched(string sourceZipPath, string outputZipPath, string clipdEntryName, byte[] patchedClipd)
    {
        string sourceFull = Path.GetFullPath(sourceZipPath);
        string outputFull = Path.GetFullPath(outputZipPath);
        if (string.Equals(sourceFull, outputFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a different output ZIP so the original car archive remains available as a backup.");

        using (ZipArchive source = ZipFile.OpenRead(sourceFull))
        using (FileStream outputStream = File.Open(outputFull, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        using (var output = new ZipArchive(outputStream, ZipArchiveMode.Create))
        {
            bool replaced = false;
            foreach (ZipArchiveEntry sourceEntry in source.Entries)
            {
                ZipArchiveEntry destination = output.CreateEntry(sourceEntry.FullName, CompressionLevel.Optimal);
                destination.LastWriteTime = sourceEntry.LastWriteTime;
                destination.ExternalAttributes = sourceEntry.ExternalAttributes;
                if (sourceEntry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;

                using Stream destinationStream = destination.Open();
                if (string.Equals(sourceEntry.FullName, clipdEntryName, StringComparison.Ordinal))
                {
                    destinationStream.Write(patchedClipd);
                    replaced = true;
                }
                else
                {
                    using Stream sourceStream = sourceEntry.Open();
                    sourceStream.CopyTo(destinationStream);
                }
            }
            if (!replaced) throw new InvalidDataException($"The source ZIP no longer contains '{clipdEntryName}'.");
        }

        ValidateOutput(sourceFull, outputFull, clipdEntryName, patchedClipd);
    }

    static void ValidateOutput(string sourceZipPath, string outputZipPath, string clipdEntryName, byte[] patchedClipd)
    {
        using ZipArchive source = ZipFile.OpenRead(sourceZipPath);
        using ZipArchive output = ZipFile.OpenRead(outputZipPath);
        if (source.Entries.Count != output.Entries.Count)
            throw new InvalidDataException("Saved ZIP entry count does not match the source archive.");

        var outputByName = output.Entries.ToDictionary(e => e.FullName, StringComparer.Ordinal);
        foreach (ZipArchiveEntry sourceEntry in source.Entries)
        {
            if (!outputByName.TryGetValue(sourceEntry.FullName, out ZipArchiveEntry? outputEntry))
                throw new InvalidDataException($"Saved ZIP is missing '{sourceEntry.FullName}'.");
            if (sourceEntry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;

            using Stream outputStream = outputEntry.Open();
            byte[] outputHash = SHA256.HashData(outputStream);
            if (string.Equals(sourceEntry.FullName, clipdEntryName, StringComparison.Ordinal))
            {
                if (!outputHash.SequenceEqual(SHA256.HashData(patchedClipd)))
                    throw new InvalidDataException("The embedded CLIPD changed while the ZIP was being written.");
            }
            else
            {
                using Stream sourceStream = sourceEntry.Open();
                if (!outputHash.SequenceEqual(SHA256.HashData(sourceStream)))
                    throw new InvalidDataException($"A non-CLIPD ZIP entry changed unexpectedly: '{sourceEntry.FullName}'.");
            }
        }

        ZipArchiveEntry clipEntry = outputByName[clipdEntryName];
        using Stream clipStream = clipEntry.Open();
        using var memory = new MemoryStream();
        clipStream.CopyTo(memory);
        ClipdFile reparsed = ClipdFile.TryParse(memory.ToArray())
            ?? throw new InvalidDataException("The saved ZIP contains an invalid CLIPD.");
        if (reparsed.PreambleValue() != reparsed.Root1ContentLen() - 16)
            throw new InvalidDataException("The saved ZIP contains a CLIPD with a stale Mojo size field.");
    }
}
