using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace FH6LocalCryptoTool;

/// <summary>Extracts FH6 ZIPs whose method-22 entries contain 512-byte CryptoContainers.</summary>
public static class ForzaZip
{
    private const uint LocalHeaderSignature = 0x04034B50;
    private const int LocalHeaderSize = 30;
    public const int ContainerHeaderSize = 36;
    public const int ChunkSize = 512;
    private const int SlotSize = ChunkSize + 16;

    public sealed record ExtractResult(int EntryCount, long OutputBytes, string OutputDirectory);

    public static ExtractResult Extract(string zipPath, string outputDirectory, byte[] dataKey, Action<string>? log = null)
    {
        byte[] zip = File.ReadAllBytes(zipPath);
        Directory.CreateDirectory(outputDirectory);
        string outputRoot = Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        int pos = 0, count = 0;
        long outputBytes = 0;

        while (pos + LocalHeaderSize <= zip.Length && ReadU32(zip, pos) == LocalHeaderSignature)
        {
            ushort flags = ReadU16(zip, pos + 6);
            ushort method = ReadU16(zip, pos + 8);
            uint compressedSize = ReadU32(zip, pos + 18);
            uint uncompressedSize = ReadU32(zip, pos + 22);
            ushort nameLength = ReadU16(zip, pos + 26);
            ushort extraLength = ReadU16(zip, pos + 28);
            if ((flags & 0x08) != 0) throw new InvalidDataException("ZIP data descriptors are not supported by the FH6 archive format.");

            long dataOffset64 = (long)pos + LocalHeaderSize + nameLength + extraLength;
            long endOffset64 = dataOffset64 + compressedSize;
            if (dataOffset64 > zip.Length || endOffset64 > zip.Length) throw new InvalidDataException("A ZIP entry extends past the end of the archive.");

            string entryName = Encoding.UTF8.GetString(zip, pos + LocalHeaderSize, nameLength).Replace('/', Path.DirectorySeparatorChar);
            int dataOffset = checked((int)dataOffset64);
            byte[] container = zip.AsSpan(dataOffset, checked((int)compressedSize)).ToArray();
            byte[] decrypted = DecryptContainer(container, dataKey);
            byte[] finalData = method == 22 ? Inflate(decrypted) : TrimStored(decrypted, uncompressedSize);
            string destination = SafeDestination(outputRoot, entryName);

            if (entryName.EndsWith(Path.DirectorySeparatorChar)) Directory.CreateDirectory(destination);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllBytes(destination, finalData);
                outputBytes += finalData.Length;
                log?.Invoke($"{entryName} ({finalData.Length:n0} bytes)");
            }
            count++;
            pos = checked((int)endOffset64);
        }

        if (count == 0) throw new InvalidDataException("No local ZIP entries were found.");
        return new ExtractResult(count, outputBytes, outputDirectory);
    }

    public static byte[] DecryptContainer(byte[] file, byte[] dataKey, int chunkSize = ChunkSize)
    {
        if (file.Length < ContainerHeaderSize) throw new InvalidDataException("Encrypted ZIP entry is too small for a CryptoContainer.");
        int payloadLength = file.Length - ContainerHeaderSize;
        if (payloadLength == 0 || payloadLength % 16 != 0) throw new InvalidDataException("Encrypted ZIP entry payload is not AES block-aligned.");

        using var aes = Aes.Create();
        aes.Key = dataKey;
        byte[] iv = file[..16];
        using var output = new MemoryStream(payloadLength);
        int slotSize = checked(chunkSize + 16);
        if (payloadLength >= slotSize && payloadLength % slotSize == 0)
        {
            int chunks = payloadLength / slotSize;
            for (int i = 0; i < chunks; i++)
            {
                int offset = ContainerHeaderSize + i * slotSize;
                output.Write(aes.DecryptCbc(file.AsSpan(offset, chunkSize), iv, PaddingMode.None));
                iv = NextIv(iv);
                iv = NextIv(iv);
            }
        }
        else output.Write(aes.DecryptCbc(file.AsSpan(ContainerHeaderSize), iv, PaddingMode.None));
        return output.ToArray();
    }

    /// <summary>Re-encrypts plaintext using an original CryptoContainer as the framing template.</summary>
    public static byte[] EncryptContainer(byte[] plaintext, byte[] originalFile, byte[] dataKey, byte[] macKey, int chunkSize = ChunkSize)
    {
        if (originalFile.Length < ContainerHeaderSize) throw new InvalidDataException("Original template is too small for a CryptoContainer.");
        int paddedLength = Math.Max(chunkSize, checked((plaintext.Length + chunkSize - 1) / chunkSize * chunkSize));
        byte[] padded = new byte[paddedLength];
        Buffer.BlockCopy(plaintext, 0, padded, 0, plaintext.Length);

        using var aes = Aes.Create();
        aes.Key = dataKey;
        byte[] iv = originalFile[..16];
        int slotSize = checked(chunkSize + 16);
        using var output = new MemoryStream(ContainerHeaderSize + paddedLength / chunkSize * slotSize);
        output.Write(originalFile, 0, ContainerHeaderSize);
        for (int i = 0; i < paddedLength / chunkSize; i++)
        {
            ReadOnlySpan<byte> chunk = padded.AsSpan(i * chunkSize, chunkSize);
            output.Write(aes.EncryptCbc(chunk, iv, PaddingMode.None));
            iv = NextIv(iv);

            // Each trailing block is AES-CBC-encrypted AES-CMAC over the
            // corresponding 512-byte plaintext chunk. Reusing the template's
            // tag only works when that chunk is byte-identical; any edit makes
            // the game reject the container before parsing its contents.
            byte[] mac = ComputeAesCmac(macKey, chunk);
            output.Write(aes.EncryptCbc(mac, iv, PaddingMode.None));
            iv = NextIv(iv);
        }

        byte[] result = output.ToArray();
        // Header field @0x10 (uint32 LE) is the number of unused zero-padding bytes in
        // the final 512-byte chunk. The game reads the real decrypted length as
        // (slotCount * 512 - thisField), so it MUST match the edited plaintext size.
        // Copying the template's stale value leaves the game reading the wrong number
        // of bytes (junk/zeros past the real text, or a truncated tail), which makes it
        // reject the file. Recompute it from the plaintext we actually just wrote.
        uint padBytes = (uint)(paddedLength - plaintext.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16, 4), padBytes);

        // Header MAC @0x14 (16 bytes) = AES-CMAC(macKey, u32_LE(paddedLength) || H1 || H2),
        // where H1 is the 16-byte IV seed at 0x00 and H2 is the 4-byte pad field at 0x10.
        // It authenticates the padded plaintext length and the header, so it MUST be
        // recomputed whenever an edit changes the length or the pad count — reusing the
        // template's value leaves a stale tag and the game rejects the whole file
        // (it hangs on an endless load). Verified against real PhysicsSettings.ini and
        // AnimResourceConfig containers.
        byte[] headerMacInput = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(headerMacInput.AsSpan(0, 4), (uint)paddedLength);
        Buffer.BlockCopy(result, 0, headerMacInput, 4, 16);   // H1 (IV seed)
        Buffer.BlockCopy(result, 16, headerMacInput, 20, 4);  // H2 (pad field just written)
        byte[] headerMac = ComputeAesCmac(macKey, headerMacInput);
        Buffer.BlockCopy(headerMac, 0, result, 20, 16);
        return result;
    }

    /// <summary>
    /// Authenticate a chunked CryptoContainer and recover its exact unpadded
    /// plaintext length from the signed header. This is intentionally stronger
    /// than checking file size or whether decryption happens to look readable.
    /// </summary>
    public static bool ValidateContainer(byte[] file, byte[] dataKey, byte[] macKey,
                                         out int plaintextLength, int chunkSize = ChunkSize)
    {
        plaintextLength = 0;
        if (file.Length < ContainerHeaderSize || chunkSize <= 0 || chunkSize % 16 != 0) return false;
        int payloadLength = file.Length - ContainerHeaderSize;
        int slotSize = chunkSize + 16;
        if (payloadLength < slotSize || payloadLength % slotSize != 0) return false;

        int chunks = payloadLength / slotSize;
        int paddedLength;
        try { paddedLength = checked(chunks * chunkSize); }
        catch (OverflowException) { return false; }
        uint padBytes = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(16, 4));
        if (padBytes >= chunkSize || padBytes > paddedLength) return false;

        byte[] headerMacInput = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(headerMacInput.AsSpan(0, 4), (uint)paddedLength);
        Buffer.BlockCopy(file, 0, headerMacInput, 4, 16);
        Buffer.BlockCopy(file, 16, headerMacInput, 20, 4);
        byte[] expectedHeaderMac = ComputeAesCmac(macKey, headerMacInput);
        if (!CryptographicOperations.FixedTimeEquals(expectedHeaderMac, file.AsSpan(20, 16))) return false;

        using var aes = Aes.Create();
        aes.Key = dataKey;
        byte[] iv = file[..16];
        for (int i = 0; i < chunks; i++)
        {
            int offset = ContainerHeaderSize + i * slotSize;
            byte[] plaintext = aes.DecryptCbc(file.AsSpan(offset, chunkSize), iv, PaddingMode.None);
            iv = NextIv(iv);
            byte[] storedMac = aes.DecryptCbc(file.AsSpan(offset + chunkSize, 16), iv, PaddingMode.None);
            byte[] expectedMac = ComputeAesCmac(macKey, plaintext);
            if (!CryptographicOperations.FixedTimeEquals(storedMac, expectedMac)) return false;
            iv = NextIv(iv);
        }

        plaintextLength = paddedLength - checked((int)padBytes);
        return true;
    }

    public static bool ValidateContainerHeader(ReadOnlySpan<byte> header, long fileLength,
                                                byte[] macKey, out long plaintextLength,
                                                int chunkSize = ChunkSize)
    {
        plaintextLength = 0;
        if (header.Length < ContainerHeaderSize || fileLength < ContainerHeaderSize ||
            chunkSize <= 0 || chunkSize % 16 != 0)
            return false;

        long payloadLength = fileLength - ContainerHeaderSize;
        long slotSize = chunkSize + 16L;
        if (payloadLength < slotSize || payloadLength % slotSize != 0)
            return false;

        long chunks = payloadLength / slotSize;
        long paddedLength;
        try { paddedLength = checked(chunks * chunkSize); }
        catch (OverflowException) { return false; }
        if (paddedLength > uint.MaxValue)
            return false;

        uint padBytes = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(16, 4));
        if (padBytes >= chunkSize || padBytes > paddedLength)
            return false;

        byte[] headerMacInput = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(headerMacInput.AsSpan(0, 4), (uint)paddedLength);
        header.Slice(0, 16).CopyTo(headerMacInput.AsSpan(4, 16));
        header.Slice(16, 4).CopyTo(headerMacInput.AsSpan(20, 4));
        byte[] expectedHeaderMac = ComputeAesCmac(macKey, headerMacInput);
        if (!CryptographicOperations.FixedTimeEquals(expectedHeaderMac, header.Slice(20, 16)))
            return false;

        plaintextLength = paddedLength - padBytes;
        return true;
    }

    private static byte[] ComputeAesCmac(byte[] key, ReadOnlySpan<byte> message)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        byte[] l = aes.EncryptEcb(new byte[16], PaddingMode.None);
        byte[] k1 = DoubleCmacBlock(l);
        byte[] k2 = DoubleCmacBlock(k1);
        int blockCount = Math.Max(1, (message.Length + 15) / 16);
        bool complete = message.Length != 0 && message.Length % 16 == 0;
        byte[] last = new byte[16];

        if (complete)
        {
            message.Slice((blockCount - 1) * 16, 16).CopyTo(last);
            XorBlock(last, k1);
        }
        else
        {
            int remaining = message.Length - (blockCount - 1) * 16;
            if (remaining > 0) message.Slice((blockCount - 1) * 16, remaining).CopyTo(last);
            last[remaining] = 0x80;
            XorBlock(last, k2);
        }

        byte[] state = new byte[16];
        for (int i = 0; i < blockCount - 1; i++)
        {
            byte[] block = message.Slice(i * 16, 16).ToArray();
            XorBlock(block, state);
            state = aes.EncryptEcb(block, PaddingMode.None);
        }
        XorBlock(last, state);
        return aes.EncryptEcb(last, PaddingMode.None);
    }

    private static byte[] DoubleCmacBlock(byte[] input)
    {
        byte[] output = new byte[16];
        int carry = 0;
        for (int i = 15; i >= 0; i--)
        {
            int value = (input[i] << 1) | carry;
            output[i] = (byte)value;
            carry = (input[i] & 0x80) != 0 ? 1 : 0;
        }
        if (carry != 0) output[15] ^= 0x87;
        return output;
    }

    private static void XorBlock(byte[] target, byte[] other)
    {
        for (int i = 0; i < 16; i++) target[i] ^= other[i];
    }

    private static byte[] NextIv(byte[] iv) => HKDF.DeriveKey(HashAlgorithmName.SHA256, iv, 16, Fh6Keys.TransportKey1, Fh6Keys.TransportKey2);
    private static byte[] Inflate(byte[] data)
    {
        using var input = new MemoryStream(data, writable: false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }
    private static byte[] TrimStored(byte[] data, uint size) => size <= data.Length ? data[..checked((int)size)] : data;
    private static string SafeDestination(string outputRoot, string entryName)
    {
        string destination = Path.GetFullPath(Path.Combine(outputRoot, entryName));
        if (!destination.StartsWith(outputRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Unsafe ZIP entry path: {entryName}");
        return destination;
    }
    private static ushort ReadU16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
    private static uint ReadU32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
}
