using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;

namespace FH6LocalCryptoTool;

/// <summary>
/// Section 0 of a decrypted FH6 <c>C_ProfileData</c> ("profile") is a typed
/// <b>CPropertyTree</b>: a length-prefixed forest of <see cref="PropertyNode"/>s
/// followed by a trailer (two sorted hash-sets). This is a faithful port of the
/// reference codec's <c>format.py</c> PropertyTree.
///
/// <para>Wire layout, little-endian throughout:</para>
/// <code>
/// [u32 rootCount]
/// rootCount × property:
///     [u8  caseSensitive]   (0 or 1)
///     [u32 hashBits]        (always 32)
///     [u32 nameLength][name UTF-8]
///     [u32 typeId]
///     value, by typeId:
///         fixed scalar   -> N raw bytes (see the variant table)
///         StringNarrow   -> [u32 byteLen][UTF-8]
///         StringWide     -> [u32 charCount][UTF-16LE]
///         Matrix         -> 64 raw bytes        (not editable)
///         Vector         -> 16 raw bytes        (not editable)
///         PropertyBag    -> [u32 childCount] children…
/// [trailer bytes]           (hash-sets; carried through verbatim)
/// </code>
///
/// <para>Only <b>value edits on editable leaf nodes</b> are supported. Names, the
/// tree shape and the trailer are never touched, so the carried-through hash-sets
/// stay valid. Every untouched leaf re-serializes from the exact bytes it was read
/// from, so a tree with no edits is byte-identical to its input — which
/// <see cref="RoundTripsCleanly"/> asserts before editing is ever allowed.</para>
/// </summary>
public sealed class PropertyTree
{
    public sealed class VariantSpec
    {
        public string Name = "";
        /// <summary>Fixed value size in bytes for scalar variants; 0 for framed/composite types.</summary>
        public int FixedSize;
        public bool Editable = true;
        public bool Confirmed = true;
        public string? DisplayName;
        public string? Description;
        public bool IsFloat;
        public bool IsSignedInt;
        public bool IsUnsignedInt;
    }

    // Horizon CVariant layout (== CLASSIC 0-16 plus 17 = ProtectedUInt32).
    public static readonly IReadOnlyDictionary<int, VariantSpec> Variants = new Dictionary<int, VariantSpec>
    {
        [0]  = new VariantSpec { Name = "Bool",       FixedSize = 1, IsUnsignedInt = true },
        [1]  = new VariantSpec { Name = "UInt8",      FixedSize = 1, IsUnsignedInt = true },
        [2]  = new VariantSpec { Name = "UInt16",     FixedSize = 2, IsUnsignedInt = true },
        [3]  = new VariantSpec { Name = "UInt32",     FixedSize = 4, IsUnsignedInt = true },
        [4]  = new VariantSpec { Name = "UInt64",     FixedSize = 8, IsUnsignedInt = true },
        [5]  = new VariantSpec { Name = "Int8",       FixedSize = 1, IsSignedInt = true },
        [6]  = new VariantSpec { Name = "Int16",      FixedSize = 2, IsSignedInt = true },
        [7]  = new VariantSpec { Name = "Int32",      FixedSize = 4, IsSignedInt = true },
        [8]  = new VariantSpec { Name = "Int64",      FixedSize = 8, IsSignedInt = true },
        [9]  = new VariantSpec { Name = "Float32",    FixedSize = 4, IsFloat = true },
        [10] = new VariantSpec { Name = "Float64",    FixedSize = 8, IsFloat = true },
        [11] = new VariantSpec { Name = "StringNarrow" },
        [12] = new VariantSpec { Name = "StringWide" },
        [13] = new VariantSpec { Name = "Matrix", Editable = false },
        [14] = new VariantSpec { Name = "Vector", Editable = false },
        [15] = new VariantSpec { Name = "PropertyBag", Editable = false },
        [16] = new VariantSpec { Name = "DatabasePropertyBag", Editable = false, Confirmed = false },
        [17] = new VariantSpec
        {
            Name = "U32_Obfuscated", FixedSize = 4, IsUnsignedInt = true,
            DisplayName = "ProtectedUInt32",
            Description = "Logical unsigned 32-bit integer. The game protects this value only in " +
                          "runtime memory; ProfileData stores it directly as little-endian UInt32."
        },
    };

    public sealed class PropertyNode
    {
        public string Name = "";
        public int TypeId;
        public byte CaseSensitive;
        public uint HashBits = 32;

        /// <summary>Children (only for PropertyBag / DatabasePropertyBag).</summary>
        public List<PropertyNode> Children = new();

        /// <summary>
        /// For leaf types, the exact value-framing bytes as read from the stream
        /// (scalar bytes; for strings the [u32 length] prefix + payload; Matrix/Vector raw).
        /// Written back verbatim when the node is not edited, guaranteeing byte-identity.
        /// </summary>
        public byte[] RawValue = System.Array.Empty<byte>();

        public bool Edited;

        public VariantSpec Spec => Variants[TypeId];
        public string TypeName => Spec.DisplayName ?? Spec.Name;
        public bool IsBag => Spec.Name is "PropertyBag" or "DatabasePropertyBag";

        /// <summary>Whether this node's value can be edited in the UI.</summary>
        public bool IsEditable => Spec.Editable && !IsBag;

        /// <summary>Human-readable current value (decoded from <see cref="RawValue"/>).</summary>
        public string DisplayValue()
        {
            var s = Spec;
            if (IsBag) return $"{{{Children.Count} propert{(Children.Count == 1 ? "y" : "ies")}}}";
            switch (s.Name)
            {
                case "Bool":    return RawValue.Length >= 1 && RawValue[0] != 0 ? "true" : "false";
                case "UInt8":   return RawValue[0].ToString(CultureInfo.InvariantCulture);
                case "Int8":    return ((sbyte)RawValue[0]).ToString(CultureInfo.InvariantCulture);
                case "UInt16":  return BinaryPrimitives.ReadUInt16LittleEndian(RawValue).ToString(CultureInfo.InvariantCulture);
                case "Int16":   return BinaryPrimitives.ReadInt16LittleEndian(RawValue).ToString(CultureInfo.InvariantCulture);
                case "UInt32":
                case "U32_Obfuscated":
                                return BinaryPrimitives.ReadUInt32LittleEndian(RawValue).ToString(CultureInfo.InvariantCulture);
                case "Int32":   return BinaryPrimitives.ReadInt32LittleEndian(RawValue).ToString(CultureInfo.InvariantCulture);
                case "UInt64":  return BinaryPrimitives.ReadUInt64LittleEndian(RawValue).ToString(CultureInfo.InvariantCulture);
                case "Int64":   return BinaryPrimitives.ReadInt64LittleEndian(RawValue).ToString(CultureInfo.InvariantCulture);
                case "Float32": return BinaryPrimitives.ReadSingleLittleEndian(RawValue).ToString("R", CultureInfo.InvariantCulture);
                case "Float64": return BinaryPrimitives.ReadDoubleLittleEndian(RawValue).ToString("R", CultureInfo.InvariantCulture);
                case "StringNarrow":
                {
                    int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(RawValue);
                    return Encoding.UTF8.GetString(RawValue, 4, len);
                }
                case "StringWide":
                {
                    int chars = (int)BinaryPrimitives.ReadUInt32LittleEndian(RawValue);
                    return Encoding.Unicode.GetString(RawValue, 4, chars * 2);
                }
                case "Matrix":  return "[4×4 matrix]";
                case "Vector":  return "[vector4]";
                default:        return $"<{s.Name}>";
            }
        }

        /// <summary>
        /// Parse and store a new value from user text. Returns false (and leaves the node
        /// unchanged) if the text is not valid for this variant.
        /// </summary>
        public bool TrySetFromText(string text)
        {
            if (!IsEditable) return false;
            var s = Spec;
            try
            {
                byte[] framed;
                if (s.FixedSize > 0)
                {
                    framed = new byte[s.FixedSize];
                    if (s.Name == "Bool")
                    {
                        bool b = text.Trim() is "1" or "true" or "True" or "TRUE" or "yes";
                        if (!b && text.Trim() is not ("0" or "false" or "False" or "FALSE" or "no"))
                            if (!bool.TryParse(text.Trim(), out b)) return false;
                        framed[0] = (byte)(b ? 1 : 0);
                    }
                    else if (s.IsFloat)
                    {
                        double d = double.Parse(text.Trim(), CultureInfo.InvariantCulture);
                        if (s.Name == "Float32") BinaryPrimitives.WriteSingleLittleEndian(framed, (float)d);
                        else BinaryPrimitives.WriteDoubleLittleEndian(framed, d);
                    }
                    else if (s.IsUnsignedInt)
                    {
                        ulong u = ParseUnsigned(text.Trim());
                        switch (s.FixedSize)
                        {
                            case 1: if (u > byte.MaxValue) return false; framed[0] = (byte)u; break;
                            case 2: if (u > ushort.MaxValue) return false; BinaryPrimitives.WriteUInt16LittleEndian(framed, (ushort)u); break;
                            case 4: if (u > uint.MaxValue) return false; BinaryPrimitives.WriteUInt32LittleEndian(framed, (uint)u); break;
                            case 8: BinaryPrimitives.WriteUInt64LittleEndian(framed, u); break;
                            default: return false;
                        }
                    }
                    else // signed
                    {
                        long v = long.Parse(text.Trim(), CultureInfo.InvariantCulture);
                        switch (s.FixedSize)
                        {
                            case 1: if (v < sbyte.MinValue || v > sbyte.MaxValue) return false; framed[0] = (byte)(sbyte)v; break;
                            case 2: if (v < short.MinValue || v > short.MaxValue) return false; BinaryPrimitives.WriteInt16LittleEndian(framed, (short)v); break;
                            case 4: if (v < int.MinValue || v > int.MaxValue) return false; BinaryPrimitives.WriteInt32LittleEndian(framed, (int)v); break;
                            case 8: BinaryPrimitives.WriteInt64LittleEndian(framed, v); break;
                            default: return false;
                        }
                    }
                }
                else if (s.Name == "StringNarrow")
                {
                    byte[] enc = Encoding.UTF8.GetBytes(text);
                    framed = new byte[4 + enc.Length];
                    BinaryPrimitives.WriteUInt32LittleEndian(framed, (uint)enc.Length);
                    Buffer.BlockCopy(enc, 0, framed, 4, enc.Length);
                }
                else if (s.Name == "StringWide")
                {
                    byte[] enc = Encoding.Unicode.GetBytes(text);
                    framed = new byte[4 + enc.Length];
                    BinaryPrimitives.WriteUInt32LittleEndian(framed, (uint)(enc.Length / 2));
                    Buffer.BlockCopy(enc, 0, framed, 4, enc.Length);
                }
                else return false;

                RawValue = framed;
                Edited = true;
                return true;
            }
            catch
            {
                return false;
            }
        }

        static ulong ParseUnsigned(string t)
        {
            if (t.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase))
                return ulong.Parse(t.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return ulong.Parse(t, CultureInfo.InvariantCulture);
        }
    }

    public List<PropertyNode> Roots { get; } = new();
    byte[] _trailer = System.Array.Empty<byte>();

    /// <summary>Any editable value changed since parse.</summary>
    public bool Dirty
    {
        get
        {
            foreach (var (_, n) in Walk()) if (n.Edited) return true;
            return false;
        }
    }

    public IEnumerable<(string Path, PropertyNode Node)> Walk()
    {
        foreach (var r in Roots)
            foreach (var pair in WalkNode(r, ""))
                yield return pair;
    }

    static IEnumerable<(string, PropertyNode)> WalkNode(PropertyNode node, string parent)
    {
        string path = parent + "/" + node.Name;
        yield return (path, node);
        foreach (var c in node.Children)
            foreach (var pair in WalkNode(c, path))
                yield return pair;
    }

    public static PropertyTree Parse(byte[] data)
    {
        var tree = new PropertyTree();
        int offset = 0;
        uint count = ReadU32(data, ref offset);
        if (count > 10_000) throw new InvalidDataException($"Unreasonable PropertyTree root count: {count}.");
        for (uint i = 0; i < count; i++)
            tree.Roots.Add(ReadProperty(data, ref offset));
        tree._trailer = data[offset..];
        return tree;
    }

    static PropertyNode ReadProperty(byte[] data, ref int offset)
    {
        byte caseSensitive = data[offset]; offset += 1;
        uint hashBits = ReadU32(data, ref offset);
        if (hashBits != 32 || caseSensitive > 1)
            throw new InvalidDataException($"Invalid CHashName metadata (case={caseSensitive}, bits={hashBits}) at 0x{offset - 5:X}.");
        uint nameLen = ReadU32(data, ref offset);
        if (nameLen > 16_384) throw new InvalidDataException($"Unreasonable property name length at 0x{offset - 4:X}.");
        string name = Encoding.UTF8.GetString(data, offset, (int)nameLen); offset += (int)nameLen;
        int typeId = (int)ReadU32(data, ref offset);
        if (!Variants.TryGetValue(typeId, out var spec))
            throw new InvalidDataException($"Unknown CVariant type {typeId} for '{name}' at 0x{offset - 4:X}.");

        var node = new PropertyNode { Name = name, TypeId = typeId, CaseSensitive = caseSensitive, HashBits = hashBits };

        if (spec.FixedSize > 0)
        {
            node.RawValue = data[offset..(offset + spec.FixedSize)];
            offset += spec.FixedSize;
        }
        else if (spec.Name == "StringNarrow")
        {
            int start = offset;
            uint len = ReadU32(data, ref offset);
            offset += (int)len;
            node.RawValue = data[start..offset];
        }
        else if (spec.Name == "StringWide")
        {
            int start = offset;
            uint chars = ReadU32(data, ref offset);
            offset += (int)chars * 2;
            node.RawValue = data[start..offset];
        }
        else if (spec.Name == "Matrix")
        {
            node.RawValue = data[offset..(offset + 64)]; offset += 64;
        }
        else if (spec.Name == "Vector")
        {
            node.RawValue = data[offset..(offset + 16)]; offset += 16;
        }
        else if (node.IsBag)
        {
            uint childCount = ReadU32(data, ref offset);
            if (childCount > 100_000) throw new InvalidDataException($"Unreasonable child count in '{name}'.");
            for (uint i = 0; i < childCount; i++)
                node.Children.Add(ReadProperty(data, ref offset));
        }
        else throw new InvalidDataException($"Unsupported CVariant type {typeId} for '{name}'.");

        return node;
    }

    /// <summary>Rebuild the section-0 payload. Untouched leaves reproduce their exact input bytes.</summary>
    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        WriteU32(ms, (uint)Roots.Count);
        foreach (var r in Roots) WriteProperty(ms, r);
        ms.Write(_trailer);
        return ms.ToArray();
    }

    static void WriteProperty(Stream s, PropertyNode node)
    {
        Span<byte> hdr = stackalloc byte[9];
        hdr[0] = node.CaseSensitive;
        BinaryPrimitives.WriteUInt32LittleEndian(hdr[1..5], node.HashBits);
        byte[] name = Encoding.UTF8.GetBytes(node.Name);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr[5..9], (uint)name.Length);
        s.Write(hdr);
        s.Write(name);
        WriteU32(s, (uint)node.TypeId);

        if (node.IsBag)
        {
            WriteU32(s, (uint)node.Children.Count);
            foreach (var c in node.Children) WriteProperty(s, c);
        }
        else
        {
            // Leaf: RawValue already holds the exact framing (scalar bytes, or [len]+payload,
            // or matrix/vector), whether original or freshly re-encoded on edit.
            s.Write(node.RawValue);
        }
    }

    /// <summary>
    /// True iff re-serializing the freshly parsed tree reproduces the input byte-for-byte.
    /// The container only enables property editing when this holds, so a save can never
    /// corrupt section 0 through a format-porting gap.
    /// </summary>
    public bool RoundTripsCleanly(byte[] original) => Serialize().AsSpan().SequenceEqual(original);

    static uint ReadU32(byte[] data, ref int offset)
    {
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
        offset += 4;
        return v;
    }

    static void WriteU32(Stream s, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        s.Write(b);
    }
}
