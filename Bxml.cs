using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace FH6LocalCryptoTool;

/// <summary>
/// Section 1 of a decrypted FH6 <c>C_ProfileData</c> ("savestate") is a <b>BXML</b>
/// document — a binary XML: a string table followed by a token stream of nodes that
/// reference the table by index. Faithful port of the reference codec's
/// <c>BxmlDocument</c> in <c>format.py</c>.
///
/// <para>Layout, little-endian:</para>
/// <code>
/// "BXML"
/// [u8 version]
/// [u32 stringCount][u32 stringBlockSize]
/// stringCount × [u16 byteLen][UTF-8]
/// [u8 documentFlag]
/// root node (recursive):
///     [u8 flags]                       (bit1=has attrs, bit2=has children; others preserved)
///     [idx nameIndex]                  (idx = u8 / u16 / u32 by stringCount)
///     if flags &amp; 0x02: [u8 attrCount] × ([idx key][idx value])
///     if flags &amp; 0x04: [u16 childCount] × node
/// </code>
///
/// <para>Only <b>string-table values</b> are editable here — every node/attribute keeps
/// its index, so the tree shape is untouched. An unedited parse re-serializes to the exact
/// input bytes, which <see cref="RoundTripsCleanly"/> asserts before editing is allowed.</para>
/// </summary>
public sealed class Bxml
{
    public sealed class Attribute
    {
        public int KeyIndex;
        public int ValueIndex;
    }

    public sealed class Node
    {
        public byte Flags;
        public int NameIndex;
        public List<Attribute> Attributes = new();
        public List<Node> Children = new();

        public IEnumerable<(int Depth, Node Node)> Walk(int depth = 0)
        {
            yield return (depth, this);
            foreach (var c in Children)
                foreach (var pair in c.Walk(depth + 1))
                    yield return pair;
        }
    }

    public byte Version { get; private set; }
    public byte DocumentFlag { get; private set; }
    public List<string> Strings { get; } = new();
    public Node Root { get; private set; } = new();

    /// <summary>Any string-table value changed since parse.</summary>
    public bool Dirty { get; private set; }

    /// <summary>Replace one string-table value (the only supported edit).</summary>
    public void SetString(int index, string value)
    {
        if (index < 0 || index >= Strings.Count) return;
        if (Strings[index] == value) return;
        Strings[index] = value;
        Dirty = true;
    }

    /// <summary>Read one attribute from a parsed node by its friendly key name.</summary>
    public string? GetAttribute(Node node, string key)
    {
        foreach (var attribute in node.Attributes)
            if (Strings[attribute.KeyIndex] == key)
                return Strings[attribute.ValueIndex];
        return null;
    }

    /// <summary>
    /// Change one node attribute without changing other attributes that happen to share
    /// the same string-table entry. A fresh value is appended and only this attribute is
    /// redirected to it, which is safer than editing a raw shared string from the advanced UI.
    /// </summary>
    public bool SetAttribute(Node node, string key, string value)
    {
        foreach (var attribute in node.Attributes)
        {
            if (Strings[attribute.KeyIndex] != key) continue;
            if (Strings[attribute.ValueIndex] == value) return true;
            Strings.Add(value);
            attribute.ValueIndex = Strings.Count - 1;
            Dirty = true;
            return true;
        }
        return false;
    }

    public static Bxml Parse(byte[] data)
    {
        if (data.Length < 13 || data[0] != (byte)'B' || data[1] != (byte)'X' || data[2] != (byte)'M' || data[3] != (byte)'L')
            throw new InvalidDataException("save-state section is not BXML.");
        var doc = new Bxml { Version = data[4] };
        int offset = 5;
        uint count = ReadU32(data, ref offset);
        uint blockSize = ReadU32(data, ref offset);
        int blockStart = 13;
        long blockEnd = (long)blockStart + blockSize;
        if (count > 1_000_000 || blockEnd > data.Length)
            throw new InvalidDataException("invalid BXML string table bounds.");

        int p = blockStart;
        for (uint i = 0; i < count; i++)
        {
            ushort len = ReadU16(data, ref p);
            doc.Strings.Add(Encoding.UTF8.GetString(data, p, len));
            p += len;
        }
        if (p != blockEnd)
            throw new InvalidDataException($"BXML string block has {blockEnd - p} unparsed bytes.");

        int t = (int)blockEnd;
        doc.DocumentFlag = data[t]; t += 1;
        int indexSize = IndexSize(count);
        doc.Root = ReadNode(data, ref t, indexSize, count);
        if (t != data.Length)
            throw new InvalidDataException($"BXML document has {data.Length - t} trailing bytes.");
        return doc;
    }

    static Node ReadNode(byte[] data, ref int offset, int indexSize, uint stringCount)
    {
        byte flags = data[offset]; offset += 1;
        if ((flags & ~0x07) != 0)
            throw new InvalidDataException($"invalid BXML node flags 0x{flags:X2} at 0x{offset - 1:X}.");
        int nameIndex = ReadIndex(data, ref offset, indexSize);
        if (nameIndex >= stringCount) throw new InvalidDataException($"BXML name index {nameIndex} out of range.");
        var node = new Node { Flags = flags, NameIndex = nameIndex };
        if ((flags & 0x02) != 0)
        {
            byte attrCount = data[offset]; offset += 1;
            for (int i = 0; i < attrCount; i++)
            {
                int key = ReadIndex(data, ref offset, indexSize);
                int val = ReadIndex(data, ref offset, indexSize);
                if (key >= stringCount || val >= stringCount)
                    throw new InvalidDataException("BXML attribute index out of range.");
                node.Attributes.Add(new Attribute { KeyIndex = key, ValueIndex = val });
            }
        }
        if ((flags & 0x04) != 0)
        {
            ushort childCount = ReadU16(data, ref offset);
            for (int i = 0; i < childCount; i++)
                node.Children.Add(ReadNode(data, ref offset, indexSize, stringCount));
        }
        return node;
    }

    public byte[] Serialize()
    {
        using var block = new MemoryStream();
        foreach (var value in Strings)
        {
            byte[] enc = Encoding.UTF8.GetBytes(value);
            if (enc.Length > 0xFFFF) throw new InvalidDataException("BXML string is larger than 65535 bytes.");
            WriteU16(block, (ushort)enc.Length);
            block.Write(enc);
        }
        byte[] stringData = block.ToArray();

        using var ms = new MemoryStream();
        ms.Write("BXML"u8);
        ms.WriteByte(Version);
        WriteU32(ms, (uint)Strings.Count);
        WriteU32(ms, (uint)stringData.Length);
        ms.Write(stringData);
        ms.WriteByte(DocumentFlag);
        int indexSize = IndexSize((uint)Strings.Count);
        WriteNode(ms, Root, indexSize);
        return ms.ToArray();
    }

    static void WriteNode(Stream s, Node node, int indexSize)
    {
        byte flags = node.Flags;
        flags = node.Attributes.Count > 0 ? (byte)(flags | 0x02) : (byte)(flags & ~0x02);
        flags = node.Children.Count > 0 ? (byte)(flags | 0x04) : (byte)(flags & ~0x04);
        s.WriteByte(flags);
        WriteIndex(s, node.NameIndex, indexSize);
        if (node.Attributes.Count > 0)
        {
            if (node.Attributes.Count > 0xFF) throw new InvalidDataException("BXML node has more than 255 attributes.");
            s.WriteByte((byte)node.Attributes.Count);
            foreach (var a in node.Attributes)
            {
                WriteIndex(s, a.KeyIndex, indexSize);
                WriteIndex(s, a.ValueIndex, indexSize);
            }
        }
        if (node.Children.Count > 0)
        {
            if (node.Children.Count > 0xFFFF) throw new InvalidDataException("BXML node has more than 65535 children.");
            WriteU16(s, (ushort)node.Children.Count);
            foreach (var c in node.Children) WriteNode(s, c, indexSize);
        }
    }

    /// <summary>Render the document as indented XML text (for the read-only preview).</summary>
    public string ToXml()
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        Emit(Root, 0, sb);
        return sb.ToString();
    }

    void Emit(Node node, int depth, StringBuilder sb)
    {
        string name = Strings[node.NameIndex];
        string prefix = new string(' ', depth * 2);
        sb.Append(prefix).Append('<').Append(name);
        foreach (var a in node.Attributes)
            sb.Append(' ').Append(Strings[a.KeyIndex]).Append("=\"").Append(EscapeAttr(Strings[a.ValueIndex])).Append('"');
        if (node.Children.Count == 0) { sb.Append(" />\n"); return; }
        sb.Append(">\n");
        foreach (var c in node.Children) Emit(c, depth + 1, sb);
        sb.Append(prefix).Append("</").Append(name).Append(">\n");
    }

    static string EscapeAttr(string v) =>
        v.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    /// <summary>True iff a freshly parsed document re-serializes to the input byte-for-byte.</summary>
    public bool RoundTripsCleanly(byte[] original) => Serialize().AsSpan().SequenceEqual(original);

    static int IndexSize(uint stringCount) => stringCount > 0xFFFF ? 4 : stringCount > 0xFF ? 2 : 1;

    static int ReadIndex(byte[] data, ref int offset, int size)
    {
        int v = size switch
        {
            1 => data[offset],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2)),
            _ => (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4)),
        };
        offset += size;
        return v;
    }

    static void WriteIndex(Stream s, int value, int size)
    {
        Span<byte> b = stackalloc byte[4];
        switch (size)
        {
            case 1: s.WriteByte((byte)value); return;
            case 2: BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)value); s.Write(b[..2]); return;
            default: BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)value); s.Write(b[..4]); return;
        }
    }

    static ushort ReadU16(byte[] data, ref int offset)
    {
        ushort v = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
        offset += 2; return v;
    }

    static uint ReadU32(byte[] data, ref int offset)
    {
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
        offset += 4; return v;
    }

    static void WriteU16(Stream s, ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, value);
        s.Write(b);
    }

    static void WriteU32(Stream s, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        s.Write(b);
    }
}
