using System;

namespace FH6LocalCryptoTool;

/// <summary>
/// GameDB second layer: the container plaintext is XOR-scrambled with a CRC32
/// keystream that the game peels off in its in-memory SQLite VFS. Self-inverse,
/// so applying it once turns container plaintext into the SQLite image and
/// applying it again turns the SQLite image back into container plaintext.
/// Verified against GameDB containers whose decoded payload is a SQLite database.
/// </summary>
public static class Scramble
{
    private const uint PageConst = Fh6Keys.GamedbScramblePageConst; // 0xE910D5B8

    private static readonly uint[] Crc = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1u) != 0 ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
            t[n] = c;
        }
        return t;
    }

    // The game folds input bytes through ASCII tolower before the CRC step.
    private static byte Fold(byte b) => (b >= (byte)'A' && b <= (byte)'Z') ? (byte)(b + 32) : b;

    // ~CRC32 of the four little-endian bytes of `seed`.
    private static uint KeystreamDword(uint seed)
    {
        uint v = 0xFFFFFFFFu;
        for (int i = 0; i < 4; i++)
        {
            byte b = (byte)(seed >> (8 * i));
            v = Crc[(v ^ Fold(b)) & 0xFFu] ^ (v >> 8);
        }
        return ~v;
    }

    private static uint SeedFor(uint p)
    {
        uint q = p >> 2;
        unchecked { return q + PageConst * (q + 1); } // wraps mod 2^32
    }

    /// <summary>XOR the buffer in place with the keystream, starting at logical position <paramref name="startPos"/> (0 for a whole file).</summary>
    public static void Apply(byte[] data, uint startPos = 0)
    {
        uint pos = startPos;
        uint ks = KeystreamDword(SeedFor(pos)) >> (int)(8 * (pos & 3));
        for (long i = 0; i < data.LongLength; i++)
        {
            data[i] ^= (byte)(ks & 0xFFu);
            pos++;
            if ((pos & 3) != 0) ks >>= 8;
            else ks = KeystreamDword(SeedFor(pos));
        }
    }
}
