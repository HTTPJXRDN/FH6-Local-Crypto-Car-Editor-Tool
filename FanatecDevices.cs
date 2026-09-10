using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;

namespace FH6LocalCryptoTool;

/// <summary>
/// Fanatec.Devices.bin's self-keyed NaCl crypto_box wrapper:
/// X25519 key agreement followed by XSalsa20-Poly1305.
/// </summary>
public static class FanatecDevices
{
    public sealed record ContainerInfo(int EncryptedBoundary, int PlaintextLength, byte[] PrivateKey, byte[] PublicKey);

    public static ContainerInfo Inspect(byte[] file)
    {
        if (file.Length < 200) throw new InvalidDataException("Fanatec file is too small.");
        int boundary = FindEncryptedBoundary(file);
        if (boundary <= 64 + 16 + 8 || boundary > file.Length)
            throw new InvalidDataException("Fanatec encrypted boundary is invalid.");
        return new(boundary, boundary - 64 - 16 - 8, file[..32], file[32..64]);
    }

    public static byte[] Decrypt(byte[] file)
    {
        ContainerInfo info = Inspect(file);
        ReadOnlySpan<byte> payload = file.AsSpan(64, info.EncryptedBoundary - 64);
        ReadOnlySpan<byte> nonce8 = payload[^8..];
        ReadOnlySpan<byte> tag = payload[..16];
        ReadOnlySpan<byte> ciphertext = payload[16..^8];
        byte[] key = DeriveBoxKey(info.PrivateKey, info.PublicKey);
        return Open(ciphertext, tag, nonce8, key);
    }

    public static byte[] Encrypt(byte[] plaintext, byte[] template)
    {
        ContainerInfo info = Inspect(template);
        if (plaintext.Length != info.PlaintextLength)
            throw new InvalidDataException(
                $"Fanatec edits must remain {info.PlaintextLength:n0} bytes so the signed footer offsets stay valid.");

        byte[] result = template.ToArray();
        ReadOnlySpan<byte> nonce8 = template.AsSpan(info.EncryptedBoundary - 8, 8);
        byte[] key = DeriveBoxKey(info.PrivateKey, info.PublicKey);
        (byte[] tag, byte[] ciphertext) = Seal(plaintext, nonce8, key);
        tag.CopyTo(result, 64);
        ciphertext.CopyTo(result, 80);
        return result;
    }

    private static byte[] DeriveBoxKey(byte[] privateKey, byte[] publicKey)
    {
        byte[] shared = X25519(privateKey, publicKey);
        return HSalsa20(shared, new byte[16]);
    }

    private static byte[] Open(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag,
                               ReadOnlySpan<byte> nonce8, byte[] boxKey)
    {
        byte[] nonce24 = new byte[24];
        nonce8.CopyTo(nonce24);
        byte[] subkey = HSalsa20(boxKey, nonce24.AsSpan(0, 16));
        byte[] keystream = Salsa20Keystream(subkey, nonce24.AsSpan(16, 8), ciphertext.Length + 32);
        byte[] expected = Poly1305(ciphertext, keystream.AsSpan(0, 32));
        if (!CryptographicOperations.FixedTimeEquals(expected, tag))
            throw new CryptographicException("Fanatec Poly1305 authentication failed.");
        byte[] plaintext = new byte[ciphertext.Length];
        for (int i = 0; i < plaintext.Length; i++) plaintext[i] = (byte)(ciphertext[i] ^ keystream[i + 32]);
        return plaintext;
    }

    private static (byte[] Tag, byte[] Ciphertext) Seal(ReadOnlySpan<byte> plaintext,
                                                        ReadOnlySpan<byte> nonce8, byte[] boxKey)
    {
        byte[] nonce24 = new byte[24];
        nonce8.CopyTo(nonce24);
        byte[] subkey = HSalsa20(boxKey, nonce24.AsSpan(0, 16));
        byte[] keystream = Salsa20Keystream(subkey, nonce24.AsSpan(16, 8), plaintext.Length + 32);
        byte[] ciphertext = new byte[plaintext.Length];
        for (int i = 0; i < plaintext.Length; i++) ciphertext[i] = (byte)(plaintext[i] ^ keystream[i + 32]);
        return (Poly1305(ciphertext, keystream.AsSpan(0, 32)), ciphertext);
    }

    private static byte[] X25519(ReadOnlySpan<byte> scalarBytes, ReadOnlySpan<byte> pointBytes)
    {
        byte[] scalar = scalarBytes.ToArray();
        scalar[0] &= 248;
        scalar[31] &= 127;
        scalar[31] |= 64;
        BigInteger p = (BigInteger.One << 255) - 19;
        BigInteger x1 = new(pointBytes, isUnsigned: true, isBigEndian: false);
        BigInteger x2 = BigInteger.One, z2 = BigInteger.Zero, x3 = x1, z3 = BigInteger.One;
        int swap = 0;
        for (int bit = 254; bit >= 0; bit--)
        {
            int current = (scalar[bit >> 3] >> (bit & 7)) & 1;
            swap ^= current;
            ConditionalSwap(ref x2, ref x3, swap);
            ConditionalSwap(ref z2, ref z3, swap);
            swap = current;

            BigInteger a = Mod(x2 + z2, p);
            BigInteger aa = Mod(a * a, p);
            BigInteger b = Mod(x2 - z2, p);
            BigInteger bb = Mod(b * b, p);
            BigInteger e = Mod(aa - bb, p);
            BigInteger c = Mod(x3 + z3, p);
            BigInteger d = Mod(x3 - z3, p);
            BigInteger da = Mod(d * a, p);
            BigInteger cb = Mod(c * b, p);
            x3 = Mod((da + cb) * (da + cb), p);
            z3 = Mod(x1 * Mod((da - cb) * (da - cb), p), p);
            x2 = Mod(aa * bb, p);
            z2 = Mod(e * Mod(aa + 121665 * e, p), p);
        }
        ConditionalSwap(ref x2, ref x3, swap);
        ConditionalSwap(ref z2, ref z3, swap);
        BigInteger result = Mod(x2 * BigInteger.ModPow(z2, p - 2, p), p);
        byte[] raw = result.ToByteArray(isUnsigned: true, isBigEndian: false);
        Array.Resize(ref raw, 32);
        return raw;
    }

    private static void ConditionalSwap(ref BigInteger a, ref BigInteger b, int swap)
    {
        if (swap == 0) return;
        (a, b) = (b, a);
    }

    private static BigInteger Mod(BigInteger value, BigInteger modulus)
    {
        value %= modulus;
        return value.Sign < 0 ? value + modulus : value;
    }

    private static byte[] HSalsa20(ReadOnlySpan<byte> key, ReadOnlySpan<byte> input)
    {
        uint[] x = new uint[16];
        x[0] = 0x61707865; x[5] = 0x3320646e; x[10] = 0x79622d32; x[15] = 0x6b206574;
        for (int i = 0; i < 4; i++) x[1 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(i * 4, 4));
        for (int i = 0; i < 4; i++) x[11 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(16 + i * 4, 4));
        for (int i = 0; i < 4; i++) x[6 + i] = BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(i * 4, 4));
        SalsaRounds(x);
        int[] words = [0, 5, 10, 15, 6, 7, 8, 9];
        byte[] result = new byte[32];
        for (int i = 0; i < words.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(i * 4, 4), x[words[i]]);
        return result;
    }

    private static byte[] Salsa20Keystream(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, int length)
    {
        byte[] output = new byte[length];
        byte[] block = new byte[64];
        ulong counter = 0;
        int offset = 0;
        while (offset < length)
        {
            uint[] state = new uint[16];
            state[0] = 0x61707865; state[5] = 0x3320646e; state[10] = 0x79622d32; state[15] = 0x6b206574;
            for (int i = 0; i < 4; i++) state[1 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(i * 4, 4));
            for (int i = 0; i < 4; i++) state[11 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(16 + i * 4, 4));
            state[6] = BinaryPrimitives.ReadUInt32LittleEndian(nonce[..4]);
            state[7] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(4, 4));
            state[8] = (uint)counter;
            state[9] = (uint)(counter >> 32);
            uint[] working = state.ToArray();
            SalsaRounds(working);
            for (int i = 0; i < 16; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(i * 4, 4), unchecked(working[i] + state[i]));
            int take = Math.Min(64, length - offset);
            block.AsSpan(0, take).CopyTo(output.AsSpan(offset, take));
            offset += take;
            counter++;
        }
        return output;
    }

    private static void SalsaRounds(uint[] x)
    {
        for (int i = 0; i < 10; i++)
        {
            Quarter(x, 0, 4, 8, 12); Quarter(x, 5, 9, 13, 1);
            Quarter(x, 10, 14, 2, 6); Quarter(x, 15, 3, 7, 11);
            Quarter(x, 0, 1, 2, 3); Quarter(x, 5, 6, 7, 4);
            Quarter(x, 10, 11, 8, 9); Quarter(x, 15, 12, 13, 14);
        }
    }

    private static void Quarter(uint[] x, int a, int b, int c, int d)
    {
        x[b] ^= BitOperations.RotateLeft(unchecked(x[a] + x[d]), 7);
        x[c] ^= BitOperations.RotateLeft(unchecked(x[b] + x[a]), 9);
        x[d] ^= BitOperations.RotateLeft(unchecked(x[c] + x[b]), 13);
        x[a] ^= BitOperations.RotateLeft(unchecked(x[d] + x[c]), 18);
    }

    private static byte[] Poly1305(ReadOnlySpan<byte> message, ReadOnlySpan<byte> key)
    {
        byte[] rBytes = key[..16].ToArray();
        rBytes[3] &= 15; rBytes[7] &= 15; rBytes[11] &= 15; rBytes[15] &= 15;
        rBytes[4] &= 252; rBytes[8] &= 252; rBytes[12] &= 252;
        BigInteger r = new(rBytes, isUnsigned: true, isBigEndian: false);
        BigInteger s = new(key[16..32], isUnsigned: true, isBigEndian: false);
        BigInteger p = (BigInteger.One << 130) - 5;
        BigInteger accumulator = BigInteger.Zero;
        for (int offset = 0; offset < message.Length; offset += 16)
        {
            int length = Math.Min(16, message.Length - offset);
            byte[] block = new byte[length + 1];
            message.Slice(offset, length).CopyTo(block);
            block[length] = 1;
            BigInteger n = new(block, isUnsigned: true, isBigEndian: false);
            accumulator = ((accumulator + n) * r) % p;
        }
        BigInteger tag = (accumulator + s) & ((BigInteger.One << 128) - 1);
        byte[] result = tag.ToByteArray(isUnsigned: true, isBigEndian: false);
        Array.Resize(ref result, 16);
        return result;
    }

    private static int FindEncryptedBoundary(byte[] data)
    {
        for (int pos = data.Length - 32; pos > 40; pos--)
        {
            ulong v0 = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos, 8));
            ulong v1 = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos + 8, 8));
            ulong v2 = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos + 16, 8));
            ulong previous = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos - 8, 8));
            ulong computed = v2 ^ ~unchecked(v0 * v1);
            if (computed != previous || v1 > (ulong)(pos - 8)) continue;
            return pos - 8;
        }
        return data.Length - 160;
    }
}
