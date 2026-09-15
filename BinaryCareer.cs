using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace FH6LocalCryptoTool;

/// <summary>
/// Section 2 of a decrypted FH6 <c>C_ProfileData</c> ("binary") is the binary career
/// state: a count, an optional account XUID, then a flat list of tagged records. Faithful
/// port of the reference codec's <c>BinaryCareerDocument</c> in <c>format.py</c>.
///
/// <para>Layout, little-endian:</para>
/// <code>
/// [u32 recordCount]
/// [u64 xuid]                         (present for FH5/FH6)
/// recordCount × record:
///     [u32 nameLen][name UTF-8]
///     [u32 serializerLen][serializer UTF-8]
///     [u32 tag]                      (= forzaNameHash(name + serializer))
///     [u32 payloadSize][payload]
///     [u32 ordinal]                  (= record index)
/// </code>
///
/// <para>Record payloads are the deeply-structured part of the save (each serializer has
/// its own field schema). This port keeps every payload as opaque bytes — so the records
/// can be listed, inspected and exported, and the <b>account XUID</b> can be edited —
/// while guaranteeing an unedited parse re-serializes to the exact input bytes
/// (<see cref="RoundTripsCleanly"/>). Field-level payload editing is intentionally not done
/// here: some FH6 serializers embed a CRC that must be repaired on edit, so decoding those
/// safely needs the full record schema.</para>
/// </summary>
public sealed class BinaryCareer
{
    public sealed class Record
    {
        public string Name = "";
        public string Serializer = "";
        public uint Tag;
        public byte[] Payload = System.Array.Empty<byte>();
        public int Ordinal;
    }

    public bool HasXuid { get; private set; } = true;
    public ulong Xuid { get; set; }
    public bool XuidEdited { get; private set; }
    bool _recordsEdited;
    public List<Record> Records { get; } = new();

    public void SetXuid(ulong value) { Xuid = value; XuidEdited = true; }

    /// <summary>Replace one parsed record payload and include it in the next serialized career section.</summary>
    public void ReplacePayload(Record record, byte[] payload)
    {
        if (!Records.Contains(record)) throw new ArgumentException("The record does not belong to this career document.", nameof(record));
        ArgumentNullException.ThrowIfNull(payload);
        record.Payload = payload;
        _recordsEdited = true;
    }

    public bool Dirty => XuidEdited || _recordsEdited;

    public static BinaryCareer Parse(byte[] data, bool hasXuid = true)
    {
        var doc = new BinaryCareer { HasXuid = hasXuid };
        int offset = 0;
        uint count = ReadU32(data, ref offset);
        if (count > 100_000) throw new InvalidDataException($"unreasonable binary record count: {count}.");
        if (hasXuid) { doc.Xuid = ReadU64(data, ref offset); }

        for (int ordinal = 0; ordinal < count; ordinal++)
        {
            string name = ReadString(data, ref offset, "binary record name");
            string serializer = ReadString(data, ref offset, "binary serializer name");
            uint tag = ReadU32(data, ref offset);
            uint expected = ProfileContainer.ForzaNameHash(name + serializer);
            if (tag != expected)
                throw new InvalidDataException($"binary record tag mismatch for '{name}': expected 0x{expected:X8}, found 0x{tag:X8}.");
            uint size = ReadU32(data, ref offset);
            byte[] payload = data[offset..(offset + (int)size)]; offset += (int)size;
            uint storedOrdinal = ReadU32(data, ref offset);
            if (storedOrdinal != ordinal)
                throw new InvalidDataException($"binary record ordinal mismatch at 0x{offset - 4:X}: expected {ordinal}, found {storedOrdinal}.");
            doc.Records.Add(new Record { Name = name, Serializer = serializer, Tag = tag, Payload = payload, Ordinal = ordinal });
        }
        if (offset != data.Length)
            throw new InvalidDataException($"binary career stream has {data.Length - offset} trailing bytes.");
        return doc;
    }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        WriteU32(ms, (uint)Records.Count);
        if (HasXuid) WriteU64(ms, Xuid);
        for (int ordinal = 0; ordinal < Records.Count; ordinal++)
        {
            var r = Records[ordinal];
            byte[] name = Encoding.UTF8.GetBytes(r.Name);
            byte[] ser = Encoding.UTF8.GetBytes(r.Serializer);
            r.Tag = ProfileContainer.ForzaNameHash(r.Name + r.Serializer);
            WriteU32(ms, (uint)name.Length); ms.Write(name);
            WriteU32(ms, (uint)ser.Length); ms.Write(ser);
            WriteU32(ms, r.Tag);
            WriteU32(ms, (uint)r.Payload.Length); ms.Write(r.Payload);
            WriteU32(ms, (uint)ordinal);
        }
        return ms.ToArray();
    }

    /// <summary>True iff a freshly parsed document re-serializes to the input byte-for-byte.</summary>
    public bool RoundTripsCleanly(byte[] original) => Serialize().AsSpan().SequenceEqual(original);

    static string ReadString(byte[] data, ref int offset, string label)
    {
        uint len = ReadU32(data, ref offset);
        if (len > 16_384) throw new InvalidDataException($"unreasonable {label} length at 0x{offset - 4:X}.");
        string s = Encoding.UTF8.GetString(data, offset, (int)len);
        offset += (int)len;
        return s;
    }

    static uint ReadU32(byte[] data, ref int offset)
    {
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
        offset += 4; return v;
    }

    static ulong ReadU64(byte[] data, ref int offset)
    {
        ulong v = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, 8));
        offset += 8; return v;
    }

    static void WriteU32(Stream s, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        s.Write(b);
    }

    static void WriteU64(Stream s, ulong value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, value);
        s.Write(b);
    }
}
