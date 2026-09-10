using System;
using System.IO;
using System.Security.Cryptography;

namespace FH6LocalCryptoTool;

/// <summary>
/// gamedbRC.slt container crypto.
///
/// Layout: [16-byte IV0][4-byte header][16-byte nonce] then N slots of
/// (131072 data bytes + 16 MAC bytes). Each data slot is AES-256-CBC/NoPadding.
/// The IV is re-derived after EVERY block (data and MAC) via
/// HKDF-SHA256(secret=IV, salt=TransportKey1, info=TransportKey2) -> 16 bytes.
/// Because each IV depends only on the previous IV (not on any data), the whole
/// IV chain is deterministic from IV0. After the container is decrypted, a
/// self-inverse CRC32 keystream (Scramble) turns it into the SQLite image.
///
/// Verified: decrypt(gamedbRC.slt) -> valid "SQLite format 3"; and
/// encrypt(decrypt(x)) == x byte-for-byte.
/// </summary>
public static class GameDb
{
    public const int ChunkSize  = 131072;          // 0x20000 data bytes per slot
    public const int MacSize     = 16;             // trailing MAC block per slot
    public const int SlotSize    = ChunkSize + MacSize;
    public const int HeaderSize  = 36;             // 16 IV + 4 header + 16 nonce

    private static byte[] NextIv(byte[] iv) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, iv, 16, Fh6Keys.TransportKey1, Fh6Keys.TransportKey2);

    /// <summary>Decrypt a gamedb container to its SQLite image.</summary>
    public static byte[] Decrypt(byte[] file, byte[] dataKey)
    {
        if (file.Length < HeaderSize)
            throw new ArgumentException("File too small to be a gamedb container.");
        int payloadLen = file.Length - HeaderSize;
        if (payloadLen <= 0 || payloadLen % SlotSize != 0)
            throw new ArgumentException($"Payload is not a whole number of {SlotSize}-byte slots (not a chunked gamedb?).");
        int n = payloadLen / SlotSize;

        using var aes = Aes.Create();
        aes.Key = dataKey;

        byte[] iv = file[..16];
        var outBuf = new byte[(long)n * ChunkSize];
        int outPos = 0;

        for (int i = 0; i < n; i++)
        {
            int off = HeaderSize + i * SlotSize;
            byte[] dataCt = file.AsSpan(off, ChunkSize).ToArray();
            byte[] pt = aes.DecryptCbc(dataCt, iv, PaddingMode.None);
            Buffer.BlockCopy(pt, 0, outBuf, outPos, ChunkSize);
            outPos += ChunkSize;

            iv = NextIv(iv);   // advance after data block
            iv = NextIv(iv);   // advance after MAC block (content irrelevant to the chain)
        }

        Scramble.Apply(outBuf, 0);   // container plaintext -> SQLite image
        return outBuf;
    }

    /// <summary>
    /// Re-encrypt a (possibly edited) SQLite image back into a loadable container.
    /// The original .slt is the template: its 36-byte header/IV0 is reused, and
    /// its per-slot MAC ciphertext is reused for chunks that existed in the
    /// original. Edits may change the size: the image is padded up to a chunk
    /// boundary and MAC blocks are generated for any new chunks (the MAC is not
    /// keyed over the data and isn't validated on load, so this is safe). When
    /// nothing changed, this reproduces the original byte-for-byte.
    /// </summary>
    public static byte[] Encrypt(byte[] sqliteImage, byte[] originalFile, byte[] dataKey)
    {
        if (originalFile.Length < HeaderSize)
            throw new ArgumentException("Original template file too small.");

        // SQLite image -> container plaintext (self-inverse). Work on a copy,
        // padded up to a whole number of chunks (trailing zero pages are ignored
        // by SQLite on read).
        int paddedLen = (sqliteImage.Length + ChunkSize - 1) / ChunkSize * ChunkSize;
        byte[] pt = new byte[paddedLen];
        Buffer.BlockCopy(sqliteImage, 0, pt, 0, sqliteImage.Length);
        Scramble.Apply(pt, 0);

        int n = paddedLen / ChunkSize;
        int origChunks = (originalFile.Length - HeaderSize) / SlotSize;

        using var aes = Aes.Create();
        aes.Key = dataKey;

        byte[] iv = originalFile[..16];
        using var ms = new MemoryStream(HeaderSize + n * SlotSize);
        ms.Write(originalFile, 0, HeaderSize);          // reuse header/IV0/nonce

        byte[] zeroMac = new byte[MacSize];
        for (int i = 0; i < n; i++)
        {
            byte[] chunk = pt.AsSpan(i * ChunkSize, ChunkSize).ToArray();
            byte[] ct = aes.EncryptCbc(chunk, iv, PaddingMode.None);
            ms.Write(ct, 0, ct.Length);
            iv = NextIv(iv);                            // advance after data block

            if (i < origChunks)
            {
                int macOff = HeaderSize + i * SlotSize + ChunkSize;
                ms.Write(originalFile, macOff, MacSize);   // reuse original MAC
            }
            else
            {
                ms.Write(zeroMac, 0, MacSize);             // generated MAC for a new chunk
            }
            iv = NextIv(iv);                            // advance after MAC block
        }

        return ms.ToArray();
    }

    /// <summary>Convenience: decrypt then re-encrypt and confirm byte-for-byte equality.</summary>
    public static bool RoundTrip(byte[] file, byte[] dataKey, out int size)
    {
        byte[] sqlite = Decrypt(file, dataKey);
        byte[] re = Encrypt(sqlite, file, dataKey);
        size = file.Length;
        return re.AsSpan().SequenceEqual(file);
    }

    public static bool LooksLikeSqlite(byte[] image) =>
        image.Length >= 16 &&
        System.Text.Encoding.ASCII.GetString(image, 0, 15) == "SQLite format 3";
}
