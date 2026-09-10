using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace FH6LocalCryptoTool;

/// <summary>FH6 C_ProfileData blob: CryptoContainer -> size header -> zlib -> save payload.</summary>
public static class ProfileData
{
    private static readonly byte[] AccountBoundaryProperty = Encoding.ASCII.GetBytes("ActivationLocationSaveState");

    public static byte[] Decrypt(byte[] encrypted)
    {
        byte[] raw = ForzaZip.DecryptContainer(encrypted, Fh6Keys.Get("ProfileData").DataKey);
        if (raw.Length < 8) throw new InvalidDataException("ProfileData blob header is missing.");
        uint compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0, 4));
        uint expectedSize = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4, 4));
        if (compressedSize == 0 || compressedSize > raw.Length - 8) throw new InvalidDataException("ProfileData compressed-size header is invalid.");
        using var input = new MemoryStream(raw, 8, checked((int)compressedSize), writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = expectedSize <= int.MaxValue ? new MemoryStream((int)expectedSize) : new MemoryStream();
        zlib.CopyTo(output);
        byte[] result = output.ToArray();
        if (result.Length != expectedSize) throw new InvalidDataException($"ProfileData expanded to {result.Length:n0} bytes; expected {expectedSize:n0}.");
        return result;
    }

    public static byte[] Encrypt(byte[] payload, byte[] targetTemplate)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(payload);
        byte[] z = compressed.ToArray();
        byte[] raw = new byte[8 + z.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0, 4), checked((uint)z.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4, 4), checked((uint)payload.Length));
        Buffer.BlockCopy(z, 0, raw, 8, z.Length);
        var key = Fh6Keys.Get("ProfileData");
        return ForzaZip.EncryptContainer(raw, targetTemplate, key.DataKey, key.MacKey);
    }

    public static byte[] Swap(byte[] donorEncrypted, byte[] targetEncrypted)
    {
        byte[] donorPayload = Decrypt(donorEncrypted);
        byte[] targetPayload = Decrypt(targetEncrypted);
        var donorAccount = LocateCanonicalAccount(donorPayload);
        var targetAccount = LocateCanonicalAccount(targetPayload);

        byte[] swappedPayload = (byte[])donorPayload.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(swappedPayload.AsSpan(donorAccount.Offset, 8), targetAccount.Xuid);

        byte[] swapped = Encrypt(swappedPayload, targetEncrypted);
        byte[] check = Decrypt(swapped);
        if (!check.AsSpan().SequenceEqual(swappedPayload)) throw new CryptographicException("Swapped save failed payload verification.");
        if (LocateCanonicalAccount(check).Xuid != targetAccount.Xuid) throw new CryptographicException("Swapped save failed account verification.");
        return swapped;
    }

    private static (int Offset, ulong Xuid) LocateCanonicalAccount(byte[] payload)
    {
        var candidates = new List<(int Offset, ulong Xuid)>();
        for (int nameOffset = 12; nameOffset <= payload.Length - AccountBoundaryProperty.Length; nameOffset++)
        {
            if (payload[nameOffset] != AccountBoundaryProperty[0]) continue;
            if (!payload.AsSpan(nameOffset, AccountBoundaryProperty.Length).SequenceEqual(AccountBoundaryProperty)) continue;
            if (BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(nameOffset - 4, 4)) != AccountBoundaryProperty.Length) continue;

            int xuidOffset = nameOffset - 12;
            ulong xuid = BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(xuidOffset, 8));
            if (xuid is >= 100_000_000_000_000UL and <= 9_999_999_999_999_999UL)
                candidates.Add((xuidOffset, xuid));
        }

        if (candidates.Count != 1)
            throw new InvalidDataException($"ProfileData canonical account field was not uniquely identified ({candidates.Count} candidates). No swapped save was written.");
        return candidates[0];
    }
}
