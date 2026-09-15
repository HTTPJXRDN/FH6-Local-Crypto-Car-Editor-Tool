using System.Buffers.Binary;

namespace FH6LocalCryptoTool;

/// <summary>
/// Small, guarded readers for career payload fields confirmed through controlled FH6 save diffs.
/// Unknown layouts deliberately return false instead of guessing.
/// </summary>
public static class CareerSaveStateInsights
{
    const int CharacterItemCountOffset = 0x1A9;

    // Two independent copies of the score shown by the in-game Season Progress bar.
    // The neighboring integers identify the currently understood FH6 serializer layout.
    static readonly int?[][] SeasonPointSignatures =
    {
        new int?[] { 16, 5, null, 6, 0, 1, 3 },
        new int?[] { 3, 5, 4, 0, null, 1, 0, 2, 0 },
    };

    public static bool TryGetSeasonPoints(byte[] payload, out int points)
    {
        points = 0;
        if (!TryFindSeasonPointOffsets(payload, out int[] offsets)) return false;
        int first = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offsets[0], 4));
        if (first < 0 || offsets.Skip(1).Any(offset => BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)) != first))
            return false;
        points = first;
        return true;
    }

    public static bool TrySetSeasonPoints(byte[] payload, int points, out byte[] edited)
    {
        edited = payload;
        if (points < 0 || !TryFindSeasonPointOffsets(payload, out int[] offsets)) return false;
        int current = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offsets[0], 4));
        if (current < 0 || offsets.Skip(1).Any(offset => BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)) != current))
            return false;

        edited = (byte[])payload.Clone();
        foreach (int offset in offsets)
            BinaryPrimitives.WriteInt32LittleEndian(edited.AsSpan(offset, 4), points);
        return true;
    }

    public static bool TryGetCharacterItemCount(byte[] payload, out int count)
    {
        count = 0;
        if (payload.Length < CharacterItemCountOffset + 4) return false;
        count = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(CharacterItemCountOffset, 4));
        return count is >= 0 and <= 100_000;
    }

    static bool TryFindSeasonPointOffsets(byte[] payload, out int[] offsets)
    {
        var found = new List<int>(SeasonPointSignatures.Length);
        foreach (int?[] signature in SeasonPointSignatures)
        {
            var matches = new List<int>();
            int wildcard = Array.IndexOf(signature, null);
            for (int offset = 0; offset + signature.Length * 4 <= payload.Length; offset++)
            {
                bool match = true;
                for (int i = 0; i < signature.Length; i++)
                {
                    if (signature[i] is int expected &&
                        BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset + i * 4, 4)) != expected)
                    {
                        match = false;
                        break;
                    }
                }
                if (match) matches.Add(offset + wildcard * 4);
            }
            if (matches.Count != 1)
            {
                offsets = Array.Empty<int>();
                return false;
            }
            found.Add(matches[0]);
        }
        offsets = found.Distinct().ToArray();
        return offsets.Length == SeasonPointSignatures.Length;
    }
}
