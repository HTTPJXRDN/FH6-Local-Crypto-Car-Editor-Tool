using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace FH6LocalCryptoTool;

/// <summary>FM2023 TransformIT GameDB codec. Uses data-only, verified offline tables;
/// never attaches to the game, executes captured code, or modifies an input file.</summary>
public static class MotorsportGameDb
{
    private const int Chunk = GameDbContainerFormat.DataChunkSize;
    private const int Slot = Chunk + 16;
    private static readonly Lazy<Cipher> Data = new(() => Cipher.Load("fm2023-data.json", true));
    private static readonly Lazy<Cipher> Mac = new(() => Cipher.Load("fm2023-mac.json", false));
    private static readonly uint[] Crc = BuildCrc();
    private static readonly ParallelOptions Workers = new() { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) };

    public static byte[] Decrypt(byte[] encrypted)
    {
        byte[] padded = DecryptPadded(encrypted);
        int size = SqliteLength(padded);
        return padded.AsSpan(0, size).ToArray();
    }

    public static byte[] Encrypt(byte[] sqlite, byte[] template)
    {
        if (SqliteLength(sqlite) != sqlite.Length)
            throw new InvalidDataException("SQLite length does not match its page count. Checkpoint the database before encrypting.");
        byte[] old = DecryptPadded(template); // Authenticate the selected game/build and all template chunks.
        int length = checked(Math.Max(old.Length, checked((sqlite.Length + Chunk - 1) / Chunk * Chunk)));
        byte[] scrambled = new byte[length];
        old.CopyTo(scrambled, 0); // Preserve template padding, including an unchanged byte-for-byte round trip.
        sqlite.CopyTo(scrambled, 0);
        Scramble(scrambled);
        byte[] result = new byte[checked(32 + length / Chunk * Slot)];
        template.AsSpan(0, 16).CopyTo(result);
        Write(result.AsSpan(16), HeaderMac(length, result.AsSpan(0, 16)));
        UInt128 previous = Read(result);
        var tags = new UInt128[length / Chunk];
        Cipher mac = Mac.Value;
        Parallel.For(0, tags.Length, Workers, chunk => tags[chunk] = mac.Cmac(scrambled.AsSpan(chunk * Chunk, Chunk)));
        for (int chunk = 0; chunk < length / Chunk; chunk++)
        {
            int output = 32 + chunk * Slot;
            ReadOnlySpan<byte> plain = scrambled.AsSpan(chunk * Chunk, Chunk);
            UInt128 tag = tags[chunk];
            for (int i = 0; i < Chunk; i += 16)
            {
                previous = Data.Value.Inverse(Read(plain[i..]) ^ previous);
                Write(result.AsSpan(output + i), previous);
            }
            previous = Data.Value.Inverse(tag ^ previous);
            Write(result.AsSpan(output + Chunk), previous);
        }
        return result;
    }

    private static byte[] DecryptPadded(byte[] encrypted)
    {
        if (GameDbContainerFormat.Detect(encrypted) != GameDbContainerFormat.Kind.ForzaMotorsportTransformIt32)
            throw new InvalidDataException("Not a 32-byte Motorsport GameDB container.");
        int count = (encrypted.Length - 32) / Slot;
        int length = checked(count * Chunk);
        Span<byte> expected = stackalloc byte[16];
        Write(expected, HeaderMac(length, encrypted.AsSpan(0, 16)));
        if (!CryptographicOperations.FixedTimeEquals(expected, encrypted.AsSpan(16, 16)))
            throw new InvalidDataException("Motorsport header authentication failed. This game build/key is unsupported, or the file is damaged.");
        byte[] result = new byte[length];
        Cipher dataCipher = Data.Value, macCipher = Mac.Value;
        try
        {
            // CBC decryption can start at any slot using the preceding ciphertext
            // MAC. Each worker writes its own disjoint plaintext chunk.
            Parallel.For(0, count, Workers, chunk =>
            {
            UInt128 previous = Read(encrypted.AsSpan(chunk == 0 ? 0 : 32 + chunk * Slot - 16));
            Span<byte> expectedTag = stackalloc byte[16];
            Span<byte> actualTag = stackalloc byte[16];
            int input = 32 + chunk * Slot;
            Span<byte> plain = result.AsSpan(chunk * Chunk, Chunk);
            for (int i = 0; i < Chunk; i += 16)
            {
                UInt128 current = Read(encrypted.AsSpan(input + i));
                Write(plain[i..], dataCipher.Forward(current) ^ previous);
                previous = current;
            }
            UInt128 tagCipher = Read(encrypted.AsSpan(input + Chunk));
            Write(expectedTag, dataCipher.Forward(tagCipher) ^ previous);
            Write(actualTag, macCipher.Cmac(plain));
            if (!CryptographicOperations.FixedTimeEquals(expectedTag, actualTag))
                throw new InvalidDataException($"Motorsport data authentication failed in chunk {chunk + 1}. No output was written.");
            });
        }
        catch (AggregateException error)
        {
            ExceptionDispatchInfo.Capture(error.Flatten().InnerExceptions[0]).Throw();
            throw;
        }
        Scramble(result);
        _ = SqliteLength(result);
        return result;
    }

    private static UInt128 HeaderMac(int length, ReadOnlySpan<byte> iv)
    {
        Span<byte> header = stackalloc byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(header, checked((uint)length));
        iv.CopyTo(header[4..]);
        return Mac.Value.Cmac(header);
    }

    private static int SqliteLength(ReadOnlySpan<byte> data)
    {
        if (data.Length < 100 || !data[..16].SequenceEqual("SQLite format 3\0"u8))
            throw new InvalidDataException("Motorsport payload is not SQLite.");
        int page = BinaryPrimitives.ReadUInt16BigEndian(data[16..]);
        if (page == 1) page = 65536;
        uint pages = BinaryPrimitives.ReadUInt32BigEndian(data[28..]);
        long length = (long)page * pages;
        if (page < 512 || (page & (page - 1)) != 0 || pages == 0 || length > data.Length)
            throw new InvalidDataException("Invalid SQLite page size/count in Motorsport payload.");
        return checked((int)length);
    }

    private static void Scramble(Span<byte> data)
    {
        const uint seed = 0xF378C144;
        uint step = seed;
        for (int offset = 0; offset < data.Length; offset += 4, step = unchecked(step + seed + 1))
        {
            uint crc = uint.MaxValue;
            for (int i = 0; i < 4; i++)
            {
                byte value = (byte)(step >> (i * 8));
                if (value is >= 65 and <= 90) value += 32;
                crc = Crc[(crc ^ value) & 255] ^ (crc >> 8);
            }
            uint value32 = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]) ^ ~crc;
            BinaryPrimitives.WriteUInt32LittleEndian(data[offset..], value32);
        }
    }

    private static uint[] BuildCrc()
    {
        uint[] result = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) != 0 ? 0xEDB88320u : 0);
            result[i] = value;
        }
        return result;
    }

    private static UInt128 Read(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt128LittleEndian(bytes);
    private static void Write(Span<byte> bytes, UInt128 value) => BinaryPrimitives.WriteUInt128LittleEndian(bytes, value);

    internal sealed class Cipher
    {
        private readonly Round[] _rounds;
        private readonly byte[] _substitutions;
        private readonly byte[] _inverseSubstitutions = new byte[4096];
        private readonly UInt128 _k1, _k2;

        private Cipher(JsonElement root, bool inverse)
        {
            if (root.GetProperty("formatVersion").GetInt32() != 1) throw new InvalidDataException("Unsupported Motorsport context version.");
            _rounds = root.GetProperty("rounds").EnumerateArray().Select(r => new Round(r, inverse)).ToArray();
            _substitutions = root.GetProperty("substitutions").EnumerateArray().Select(v => v.GetByte()).ToArray();
            if (_substitutions.Length != 4096) throw new InvalidDataException("Invalid Motorsport substitutions.");
            for (int i = 0; i < 16; i++)
            {
                bool[] seen = new bool[256];
                for (int v = 0; v < 256; v++)
                {
                    byte output = _substitutions[i * 256 + v];
                    if (seen[output]) throw new InvalidDataException("Non-invertible Motorsport substitution.");
                    seen[output] = true;
                    _inverseSubstitutions[i * 256 + output] = (byte)v;
                }
            }
            _k1 = Double(Forward(0));
            _k2 = Double(_k1);
        }

        public static Cipher Load(string name, bool inverse)
        {
#if PUBLIC_CRYPTO_RUNTIME
            using Stream stream = CryptoContextResources.Open(name);
#else
            using Stream stream = typeof(MotorsportGameDb).Assembly.GetManifestResourceStream("FH6LocalCryptoTool." + name)
                ?? throw new InvalidDataException("Missing embedded Motorsport crypto context.");
#endif
            using JsonDocument document = JsonDocument.Parse(stream);
            return new Cipher(document.RootElement, inverse);
        }

        public UInt128 Forward(UInt128 value)
        {
            foreach (Round round in _rounds) value = round.Forward(value);
            return Substitute(value, _substitutions);
        }

        public UInt128 Inverse(UInt128 value)
        {
            value = Substitute(value, _inverseSubstitutions);
            for (int i = _rounds.Length - 1; i >= 0; i--) value = _rounds[i].Inverse(value);
            return value;
        }

        public UInt128 Cmac(ReadOnlySpan<byte> data)
        {
            UInt128 state = 0;
            int last = data.Length == 0 ? 0 : (data.Length - 1) / 16 * 16;
            for (int i = 0; i < last; i += 16) state = Forward(state ^ Read(data[i..]));
            Span<byte> tail = stackalloc byte[16];
            tail.Clear();
            data[last..].CopyTo(tail);
            bool complete = data.Length != 0 && data.Length % 16 == 0;
            if (!complete) tail[data.Length - last] = 0x80;
            return Forward(state ^ Read(tail) ^ (complete ? _k1 : _k2));
        }

        private static UInt128 Double(UInt128 value)
        {
            Span<byte> block = stackalloc byte[16];
            Write(block, value);
            int carry = 0;
            for (int i = 15; i >= 0; i--)
            {
                int next = block[i] >> 7;
                block[i] = (byte)((block[i] << 1) | carry);
                carry = next;
            }
            if (carry != 0) block[15] ^= 0x87;
            return Read(block);
        }

        private static UInt128 Substitute(UInt128 value, byte[] table)
        {
            UInt128 result = 0;
            for (int i = 0; i < 16; i++, value >>= 8) result |= (UInt128)table[i * 256 + (byte)value] << (i * 8);
            return result;
        }
    }

    private sealed class Round
    {
        private readonly UInt128 _constant;
        private readonly UInt128[] _tables = new UInt128[4096];
        private readonly UInt128[]? _input, _undoInput, _undoOutput;
        private readonly byte[]? _undoSubstitution;

        public Round(JsonElement r, bool inverse)
        {
            uint[] constants = r.GetProperty("constants").EnumerateArray().Select(v => v.GetUInt32()).ToArray();
            for (int j = 0; j < 4; j++) _constant |= (UInt128)constants[j] << (32 * j);
            if (r.TryGetProperty("wideTables", out JsonElement wide))
            {
                int i = 0;
                foreach (JsonElement row in wide.EnumerateArray())
                    foreach (JsonElement entry in row.EnumerateArray())
                    {
                        int j = 0;
                        foreach (JsonElement word in entry.EnumerateArray()) _tables[i] |= (UInt128)word.GetUInt32() << (32 * j++);
                        i++;
                    }
            }
            else
            {
                int[] group = new int[16];
                int j = 0;
                foreach (JsonElement g in r.GetProperty("groups").EnumerateArray())
                {
                    foreach (JsonElement i in g.EnumerateArray()) group[i.GetInt32()] = j;
                    j++;
                }
                int row = 0;
                foreach (JsonElement table in r.GetProperty("tables").EnumerateArray())
                {
                    int v = 0;
                    foreach (JsonElement word in table.EnumerateArray()) _tables[row * 256 + v++] = (UInt128)word.GetUInt32() << (32 * group[row]);
                    row++;
                }
            }
            if (r.TryGetProperty("inputColumns", out JsonElement input))
            {
                UInt128[] columns = input.EnumerateArray().Select(v => UInt128.Parse("0" + v.GetString()![2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
                _input = LinearTable(columns);
                if (inverse) _undoInput = LinearTable(InvertColumns(columns));
            }
            if (!inverse) return;
            List<UInt128> outputColumns = new();
            _undoSubstitution = new byte[4096];
            for (int i = 0; i < 16; i++)
            {
                List<UInt128> columns = new();
                Basis basis = new();
                for (int v = 0; v < 256; v++)
                {
                    UInt128 entry = _tables[i * 256 + v];
                    if (basis.Add(entry, (UInt128)1 << columns.Count)) columns.Add(entry);
                }
                if (columns.Count != 8) throw new InvalidDataException("Invalid Motorsport round rank.");
                bool[] seen = new bool[256];
                for (int v = 0; v < 256; v++)
                {
                    int code = (int)basis.Solve(_tables[i * 256 + v]);
                    if (seen[code]) throw new InvalidDataException("Invalid Motorsport round permutation.");
                    seen[code] = true;
                    _undoSubstitution[i * 256 + code] = (byte)v;
                }
                outputColumns.AddRange(columns);
            }
            _undoOutput = LinearTable(InvertColumns(outputColumns.ToArray()));
        }

        public UInt128 Forward(UInt128 value)
        {
            if (_input is not null) value = Lookup(_input, value);
            return _constant ^ Lookup(_tables, value);
        }

        public UInt128 Inverse(UInt128 value)
        {
            value = Lookup(_undoOutput!, value ^ _constant);
            UInt128 result = 0;
            for (int i = 0; i < 16; i++, value >>= 8)
                result |= (UInt128)_undoSubstitution![i * 256 + (byte)value] << (8 * i);
            return _undoInput is null ? result : Lookup(_undoInput, result);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static UInt128 Lookup(UInt128[] table, UInt128 value)
        {
            ulong lo = (ulong)value, hi = (ulong)(value >> 64);
            return table[(byte)lo] ^ table[256 + (byte)(lo >> 8)] ^ table[512 + (byte)(lo >> 16)] ^ table[768 + (byte)(lo >> 24)] ^
                table[1024 + (byte)(lo >> 32)] ^ table[1280 + (byte)(lo >> 40)] ^ table[1536 + (byte)(lo >> 48)] ^ table[1792 + (byte)(lo >> 56)] ^
                table[2048 + (byte)hi] ^ table[2304 + (byte)(hi >> 8)] ^ table[2560 + (byte)(hi >> 16)] ^ table[2816 + (byte)(hi >> 24)] ^
                table[3072 + (byte)(hi >> 32)] ^ table[3328 + (byte)(hi >> 40)] ^ table[3584 + (byte)(hi >> 48)] ^ table[3840 + (byte)(hi >> 56)];
        }

        private static UInt128[] LinearTable(UInt128[] columns)
        {
            UInt128[] result = new UInt128[4096];
            for (int i = 0; i < 16; i++)
                for (int v = 1; v < 256; v++)
                    result[i * 256 + v] = result[i * 256 + (v & (v - 1))] ^ columns[i * 8 + BitOperations.TrailingZeroCount((uint)v)];
            return result;
        }

        private static UInt128[] InvertColumns(UInt128[] columns)
        {
            Basis basis = new();
            for (int bit = 0; bit < 128; bit++)
                if (!basis.Add(columns[bit], (UInt128)1 << bit)) throw new InvalidDataException("Singular Motorsport round encoding.");
            return Enumerable.Range(0, 128).Select(bit => basis.Solve((UInt128)1 << bit)).ToArray();
        }
    }

    private sealed class Basis
    {
        private readonly UInt128[] _values = new UInt128[128], _codes = new UInt128[128];
        public bool Add(UInt128 value, UInt128 code)
        {
            while (value != 0)
            {
                int bit = Highest(value);
                if (_values[bit] == 0) { _values[bit] = value; _codes[bit] = code; return true; }
                value ^= _values[bit]; code ^= _codes[bit];
            }
            return false;
        }
        public UInt128 Solve(UInt128 value)
        {
            UInt128 result = 0;
            while (value != 0)
            {
                int bit = Highest(value);
                if (_values[bit] == 0) throw new InvalidDataException("Invalid Motorsport linear encoding.");
                value ^= _values[bit]; result ^= _codes[bit];
            }
            return result;
        }
        private static int Highest(UInt128 value) => (ulong)(value >> 64) != 0
            ? 127 - BitOperations.LeadingZeroCount((ulong)(value >> 64))
            : 63 - BitOperations.LeadingZeroCount((ulong)value);
    }
}
