using System.Buffers.Binary;
using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FH6LocalCryptoTool;

/// <summary>
/// Converts the already-unencrypted BSI skeleton format to editable JSON and back.
/// The original bytes are carried in the JSON so unknown BSI fields and padding survive.
/// Only existing bone IDs, parents, and 32-byte transforms are editable.
/// </summary>
public static class SkeldCodec
{
    private const uint Magic = 0xB1A414CC;
    private const ulong SkeletonHash = 0xB107888537A7DF23;
    private const string Format = "FH6-SKELD-BSI-v1";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public sealed class Bone
    {
        public int Index { get; set; }
        public string Id { get; set; } = "";
        public short Parent { get; set; }
        public float[] Translation { get; set; } = [];
        public float Scale { get; set; }
        public float[] Rotation { get; set; } = [];
    }

    public sealed class Document
    {
        public string Format { get; set; } = SkeldCodec.Format;
        public string OriginalSha256 { get; set; } = "";
        public string OriginalBase64 { get; set; } = "";
        public List<Bone> Bones { get; set; } = [];
    }

    private sealed record Layout(int Count, int Ids, int Parents, int Transforms);

    public static bool HasSkeldMagic(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == Magic;

    public static string Decode(byte[] source)
    {
        Layout layout = Parse(source);
        var document = new Document
        {
            OriginalSha256 = Convert.ToHexString(SHA256.HashData(source)),
            OriginalBase64 = Convert.ToBase64String(source)
        };
        for (int i = 0; i < layout.Count; i++)
        {
            int offset = layout.Transforms + i * 32;
            document.Bones.Add(new Bone
            {
                Index = i,
                Id = $"0x{BinaryPrimitives.ReadUInt64LittleEndian(source.AsSpan(layout.Ids + i * 8)):X16}",
                Parent = BinaryPrimitives.ReadInt16LittleEndian(source.AsSpan(layout.Parents + i * 2)),
                Translation = [ReadFloat(source, offset), ReadFloat(source, offset + 4), ReadFloat(source, offset + 8)],
                Scale = ReadFloat(source, offset + 12),
                Rotation = [ReadFloat(source, offset + 16), ReadFloat(source, offset + 20),
                    ReadFloat(source, offset + 24), ReadFloat(source, offset + 28)]
            });
        }
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    public static byte[] Encode(string json)
    {
        Document document = JsonSerializer.Deserialize<Document>(json, JsonOptions)
            ?? throw new InvalidDataException("SKELD JSON is empty.");
        if (document.Format != Format)
            throw new InvalidDataException("This is not a supported FH6 SKELD JSON file.");
        byte[] output;
        try { output = Convert.FromBase64String(document.OriginalBase64); }
        catch (Exception ex) when (ex is FormatException or ArgumentNullException)
        { throw new InvalidDataException("The embedded original SKELD bytes are missing or damaged.", ex); }
        if (!Convert.ToHexString(SHA256.HashData(output)).Equals(document.OriginalSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The embedded original SKELD bytes do not match their checksum.");
        Layout layout = Parse(output);
        if (document.Bones is null || document.Bones.Count != layout.Count)
            throw new InvalidDataException($"Expected {layout.Count} bones. Adding or removing bones is not supported here.");

        var ids = new HashSet<ulong>();
        for (int i = 0; i < layout.Count; i++)
        {
            Bone bone = document.Bones[i];
            if (bone is null || bone.Index != i || bone.Translation?.Length != 3 || bone.Rotation?.Length != 4)
                throw new InvalidDataException($"Bone {i} has an invalid index or transform vector.");
            string idText = bone.Id?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true ? bone.Id[2..] : bone.Id ?? "";
            if (!ulong.TryParse(idText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong id) || !ids.Add(id))
                throw new InvalidDataException($"Bone {i} has an invalid or duplicate hexadecimal ID.");
            if (bone.Parent < -1 || bone.Parent >= layout.Count || bone.Parent == i)
                throw new InvalidDataException($"Bone {i} has an invalid parent index.");
            if (bone.Translation.Any(x => !float.IsFinite(x)) || !float.IsFinite(bone.Scale) ||
                bone.Rotation.Any(x => !float.IsFinite(x)))
                throw new InvalidDataException($"Bone {i} has a non-finite transform value.");
            BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(layout.Ids + i * 8), id);
            BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(layout.Parents + i * 2), bone.Parent);
            int offset = layout.Transforms + i * 32;
            for (int j = 0; j < 3; j++) WriteFloat(output, offset + j * 4, bone.Translation[j]);
            WriteFloat(output, offset + 12, bone.Scale);
            for (int j = 0; j < 4; j++) WriteFloat(output, offset + 16 + j * 4, bone.Rotation[j]);
        }
        // No parent cycles: a cyclic hierarchy can crash the game even if the file parses.
        for (int i = 0; i < layout.Count; i++)
        {
            int parent = document.Bones[i].Parent;
            for (int hops = 0; parent >= 0; hops++)
            {
                if (hops >= layout.Count) throw new InvalidDataException($"Bone {i} has a cyclic parent chain.");
                parent = document.Bones[parent].Parent;
            }
        }
        if (Parse(output).Count != layout.Count)
            throw new InvalidDataException("Rebuilt SKELD failed structural validation.");
        return output;
    }

    private static Layout Parse(byte[] bytes)
    {
        if (bytes.Length < 128 || !HasSkeldMagic(bytes) || bytes[4] != 1)
            throw new InvalidDataException("Unsupported BSI SKELD header.");
        int root = checked(9 + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(5, 4)));
        if (root < 9 || root > bytes.Length - 12) throw new InvalidDataException("Invalid SKELD root offset.");
        int footer = checked(root + 12 + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(root, 4)));
        if (footer > bytes.Length - 4 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(footer, 4)) != Magic)
            throw new InvalidDataException("Invalid SKELD footer.");
        int first = root + 12;
        int second = checked(first + 4 + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(first, 4)));
        if (second > footer - 4 || second + 4L + BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(second, 4)) != footer)
            throw new InvalidDataException("Unsupported SKELD root layout.");
        int obj = -1;
        for (int i = second + 4; i <= footer - 12; i++)
        {
            if (BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(i + 4, 8)) != SkeletonHash) continue;
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i, 4));
            if (size > 1000 && i + 12L + size <= footer) { obj = i; break; }
        }
        if (obj < 0) throw new InvalidDataException("No supported SKELD skeleton object was found.");
        int end = checked(obj + 12 + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(obj, 4)));
        var fields = new List<(int Start, int Length)>();
        for (int p = obj + 12; p < end;)
        {
            if (p > end - 4) throw new InvalidDataException("Truncated SKELD field.");
            int size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p, 4)));
            p += 4;
            if (size < 0 || size > end - p) throw new InvalidDataException("SKELD field is out of bounds.");
            fields.Add((p, size));
            p += size;
        }
        if (fields.Count < 3 || fields[0].Length < 4) throw new InvalidDataException("Unsupported SKELD fields.");
        int count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(fields[0].Start, 4)));
        if (count < 1 || fields[0].Length != 4L + count * 8L ||
            fields[1].Length != 4L + count * 2L || fields[2].Length != 4L + count * 32L ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(fields[1].Start, 4)) != count ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(fields[2].Start, 4)) != count)
            throw new InvalidDataException("Unsupported SKELD bone vectors.");
        return new Layout(count, fields[0].Start + 4, fields[1].Start + 4, fields[2].Start + 4);
    }

    private static float ReadFloat(byte[] bytes, int offset) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4)));

    private static void WriteFloat(byte[] bytes, int offset, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), BitConverter.SingleToInt32Bits(value));
}
