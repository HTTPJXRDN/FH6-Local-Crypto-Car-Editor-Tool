using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace FH6LocalCryptoTool;

/// <summary>FM2023 General text assets: authenticated 32-byte header and
/// 512-byte data/16-byte MAC slots. Not the GameDB or CMS archive codec.</summary>
public static class MotorsportAssetContainer
{
    private const int Chunk = 512, Slot = 528;
    private static readonly Lazy<MotorsportGameDb.Cipher> Data = new(() => MotorsportGameDb.Cipher.Load("fm2023-general-data.json", true));
    private static readonly Lazy<MotorsportGameDb.Cipher> Mac = new(() => MotorsportGameDb.Cipher.Load("fm2023-general-mac.json", false));

    public static bool HasFraming(long length) => length >= 32 + Slot && (length - 32) % Slot == 0;

    public static bool HasHeaderAuthentication(byte[] encrypted) => HasHeaderAuthentication(encrypted, Mac.Value);

    internal static bool HasHeaderAuthentication(byte[] encrypted, MotorsportGameDb.Cipher mac)
    {
        if (!HasFraming(encrypted.Length)) return false;
        Span<byte> tag = stackalloc byte[16];
        Write(tag, HeaderMac((encrypted.Length - 32) / Slot * Chunk, encrypted.AsSpan(0, 16), mac));
        return CryptographicOperations.FixedTimeEquals(tag, encrypted.AsSpan(16, 16));
    }

    public static byte[] Decrypt(byte[] encrypted) => Decrypt(encrypted, Data.Value, Mac.Value);

    internal static byte[] Decrypt(byte[] encrypted, MotorsportGameDb.Cipher data, MotorsportGameDb.Cipher mac)
    {
        byte[] padded = DecryptPadded(encrypted, data, mac);
        int length = padded.Length;
        while (length > 0 && padded[length - 1] == 0) length--;
        return padded.AsSpan(0, length).ToArray();
    }

    public static byte[] Encrypt(byte[] plaintext, byte[] template) => Encrypt(plaintext, template, Data.Value, Mac.Value);

    internal static byte[] Encrypt(byte[] plaintext, byte[] template, MotorsportGameDb.Cipher data, MotorsportGameDb.Cipher mac)
    {
        _ = DecryptPadded(template, data, mac); // Authenticate the complete template, not only its shape.
        if (plaintext.Length == 0 || plaintext.AsSpan().Contains((byte)0))
            throw new InvalidDataException("Motorsport text assets must be nonempty and contain no NUL bytes.");
        int length = checked((plaintext.Length + Chunk - 1) / Chunk * Chunk);
        byte[] padded = new byte[length];
        plaintext.CopyTo(padded, 0);
        byte[] result = new byte[checked(32 + length / Chunk * Slot)];
        template.AsSpan(0, 16).CopyTo(result);
        Write(result.AsSpan(16), HeaderMac(length, result.AsSpan(0, 16), mac));
        UInt128 previous = Read(result);
        for (int chunk = 0; chunk < length / Chunk; chunk++)
        {
            int output = 32 + chunk * Slot;
            ReadOnlySpan<byte> plain = padded.AsSpan(chunk * Chunk, Chunk);
            for (int i = 0; i < Chunk; i += 16)
            {
                previous = data.Inverse(Read(plain[i..]) ^ previous);
                Write(result.AsSpan(output + i), previous);
            }
            previous = data.Inverse(mac.Cmac(plain) ^ previous);
            Write(result.AsSpan(output + Chunk), previous);
        }
        if (!Decrypt(result, data, mac).AsSpan().SequenceEqual(plaintext))
            throw new InvalidDataException("Motorsport asset round-trip verification failed. No output was written.");
        return result;
    }

    private static byte[] DecryptPadded(byte[] encrypted, MotorsportGameDb.Cipher data, MotorsportGameDb.Cipher mac)
    {
        if (!HasFraming(encrypted.Length))
            throw new InvalidDataException("Not a 32-byte Motorsport slotted text container.");
        int count = (encrypted.Length - 32) / Slot;
        int length = checked(count * Chunk);
        Span<byte> expected = stackalloc byte[16];
        Span<byte> actual = stackalloc byte[16];
        Write(expected, HeaderMac(length, encrypted.AsSpan(0, 16), mac));
        if (!CryptographicOperations.FixedTimeEquals(expected, encrypted.AsSpan(16, 16)))
            throw new InvalidDataException("Motorsport asset header authentication failed. Unsupported key/build or damaged file.");
        byte[] result = new byte[length];
        UInt128 previous = Read(encrypted);
        for (int chunk = 0; chunk < count; chunk++)
        {
            int input = 32 + chunk * Slot;
            Span<byte> plain = result.AsSpan(chunk * Chunk, Chunk);
            for (int i = 0; i < Chunk; i += 16)
            {
                UInt128 current = Read(encrypted.AsSpan(input + i));
                Write(plain[i..], data.Forward(current) ^ previous);
                previous = current;
            }
            UInt128 tagCipher = Read(encrypted.AsSpan(input + Chunk));
            UInt128 tag = data.Forward(tagCipher) ^ previous;
            previous = tagCipher;
            Write(expected, mac.Cmac(plain));
            Write(actual, tag);
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
                throw new InvalidDataException($"Motorsport asset authentication failed in chunk {chunk + 1}. No output was written.");
        }
        return result;
    }

    private static UInt128 HeaderMac(int length, ReadOnlySpan<byte> iv, MotorsportGameDb.Cipher mac)
    {
        Span<byte> header = stackalloc byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(header, checked((uint)length));
        iv.CopyTo(header[4..]);
        return mac.Cmac(header);
    }

    private static UInt128 Read(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadUInt128LittleEndian(value);
    private static void Write(Span<byte> destination, UInt128 value) => BinaryPrimitives.WriteUInt128LittleEndian(destination, value);
}
