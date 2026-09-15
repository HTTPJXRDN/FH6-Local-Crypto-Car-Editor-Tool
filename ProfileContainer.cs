using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace FH6LocalCryptoTool;

/// <summary>
/// The decrypted FH6 <c>C_ProfileData</c> payload (what <see cref="ProfileData.Decrypt"/>
/// returns after the outer CryptoContainer + zlib envelope are removed) is a stream of
/// four length-prefixed sections:
/// <code>[u32 marker][u32 size][payload]  ×4</code>
/// The markers are the Forza name hash of "profile", "savestate", "binary" and
/// "database". Section 3 ("database") is an embedded SQLite database, which is where a
/// profile's credits, cars and garage live.
///
/// This class parses that stream, exposes the SQLite section for editing, and rebuilds
/// the stream. The other three sections (typed properties, BXML save-state, binary
/// career state) are carried through as opaque bytes so an edited save is byte-identical
/// outside the database section.
/// </summary>
public sealed class ProfileContainer
{
    // Forza name hash of the four section names (verified against the reference codec).
    public const uint MarkerProfile   = 0x4A8BF2B6;
    public const uint MarkerSaveState = 0xB60261FC;
    public const uint MarkerBinary    = 0x986CA5CB;
    public const uint MarkerDatabase  = 0xA9F1F729;

    static readonly uint[] ExpectedMarkers = { MarkerProfile, MarkerSaveState, MarkerBinary, MarkerDatabase };
    static readonly string[] SectionKinds = { "Typed properties", "BXML save state", "Binary career state", "SQLite database" };

    public sealed class Section
    {
        public uint Marker;
        public byte[] Payload = System.Array.Empty<byte>();
    }

    public List<Section> Sections { get; } = new();

    /// <summary>Index of the SQLite ("database") section.</summary>
    public const int DatabaseIndex = 3;

    /// <summary>The embedded SQLite database bytes (section 3).</summary>
    public byte[] Database
    {
        get => Sections[DatabaseIndex].Payload;
        set => Sections[DatabaseIndex].Payload = value ?? throw new System.ArgumentNullException(nameof(value));
    }

    /// <summary>Index of the typed-property ("profile") section.</summary>
    public const int ProfileIndex = 0;

    PropertyTree? _properties;
    bool _propertiesTried;

    /// <summary>
    /// The typed property tree from section 0 ("profile"), or <c>null</c> when it cannot be
    /// safely edited. It is materialised lazily and only exposed when a parse succeeds AND the
    /// freshly parsed tree re-serializes to the exact original bytes — so committing an edited
    /// tree can never corrupt section 0 through a format-porting gap. Edited values are folded
    /// back into section 0 automatically by <see cref="Serialize"/>.
    /// </summary>
    public PropertyTree? Properties
    {
        get
        {
            if (!_propertiesTried)
            {
                _propertiesTried = true;
                try
                {
                    var tree = PropertyTree.Parse(Sections[ProfileIndex].Payload);
                    if (tree.RoundTripsCleanly(Sections[ProfileIndex].Payload))
                        _properties = tree;
                }
                catch
                {
                    _properties = null;
                }
            }
            return _properties;
        }
    }

    /// <summary>Parse the inflated four-section stream produced by <see cref="ProfileData.Decrypt"/>.</summary>
    public static ProfileContainer Parse(byte[] inflated)
    {
        if (inflated is null) throw new System.ArgumentNullException(nameof(inflated));
        var container = new ProfileContainer();
        int offset = 0;
        while (offset < inflated.Length)
        {
            if (inflated.Length - offset < 8)
                throw new InvalidDataException($"Truncated ProfileData section header at 0x{offset:X}.");
            uint marker = BinaryPrimitives.ReadUInt32LittleEndian(inflated.AsSpan(offset, 4));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(inflated.AsSpan(offset + 4, 4));
            long end = (long)offset + 8 + size;
            if (end > inflated.Length)
                throw new InvalidDataException($"ProfileData section 0x{marker:X8} at 0x{offset:X} overruns the payload.");
            container.Sections.Add(new Section { Marker = marker, Payload = inflated[(offset + 8)..(int)end] });
            offset = (int)end;
        }

        if (container.Sections.Count != 4)
            throw new InvalidDataException(
                $"Expected 4 ProfileData sections, found {container.Sections.Count}. This may not be an FH6 ProfileData save.");
        for (int i = 0; i < 4; i++)
            if (container.Sections[i].Marker != ExpectedMarkers[i])
                throw new InvalidDataException(
                    $"ProfileData {SectionKinds[i]} section marker mismatch: expected 0x{ExpectedMarkers[i]:X8}, found 0x{container.Sections[i].Marker:X8}.");
        if (!LooksLikeSqlite(container.Sections[DatabaseIndex].Payload))
            throw new InvalidDataException("The ProfileData database section is not a SQLite database.");
        return container;
    }

    /// <summary>Index of the BXML save-state section.</summary>
    public const int SaveStateIndex = 1;
    /// <summary>Index of the binary career-state section.</summary>
    public const int BinaryIndex = 2;

    Bxml? _saveState;
    bool _saveStateTried;
    BinaryCareer? _career;
    bool _careerTried;

    /// <summary>
    /// The BXML save-state document from section 1, or <c>null</c> when it can't be safely
    /// edited. Same round-trip guard as <see cref="Properties"/>: only exposed when a fresh
    /// parse re-serializes to the exact original bytes. Edited strings are folded back into
    /// section 1 by <see cref="Serialize"/>.
    /// </summary>
    public Bxml? SaveState
    {
        get
        {
            if (!_saveStateTried)
            {
                _saveStateTried = true;
                try
                {
                    var doc = Bxml.Parse(Sections[SaveStateIndex].Payload);
                    if (doc.RoundTripsCleanly(Sections[SaveStateIndex].Payload)) _saveState = doc;
                }
                catch { _saveState = null; }
            }
            return _saveState;
        }
    }

    /// <summary>
    /// The binary career document from section 2, or <c>null</c> when it can't be safely
    /// edited. Same round-trip guard; the account XUID is editable and folded back into
    /// section 2 by <see cref="Serialize"/>. Record payloads are carried through opaque.
    /// </summary>
    public BinaryCareer? Career
    {
        get
        {
            if (!_careerTried)
            {
                _careerTried = true;
                byte[] payload = Sections[BinaryIndex].Payload;
                foreach (bool hasXuid in new[] { true, false })
                {
                    try
                    {
                        var doc = BinaryCareer.Parse(payload, hasXuid);
                        if (doc.RoundTripsCleanly(payload)) { _career = doc; break; }
                    }
                    catch { /* try the other XUID mode */ }
                }
            }
            return _career;
        }
    }

    /// <summary>Rebuild the inflated section stream (sections 0-2 untouched, section 3 = current <see cref="Database"/>).</summary>
    public byte[] Serialize()
    {
        // Fold any edited values back into their sections before writing.
        if (_properties is not null && _properties.Dirty)
            Sections[ProfileIndex].Payload = _properties.Serialize();
        if (_saveState is not null && _saveState.Dirty)
            Sections[SaveStateIndex].Payload = _saveState.Serialize();
        if (_career is not null && _career.Dirty)
            Sections[BinaryIndex].Payload = _career.Serialize();

        using var ms = new MemoryStream();
        Span<byte> header = stackalloc byte[8];
        foreach (var section in Sections)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header[..4], section.Marker);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)section.Payload.Length));
            ms.Write(header);
            ms.Write(section.Payload);
        }
        return ms.ToArray();
    }

    /// <summary>Per-section summary for the UI: (index, marker, size, kind).</summary>
    public IReadOnlyList<(int Index, uint Marker, int Size, string Kind)> SectionInfo()
    {
        var list = new List<(int, uint, int, string)>(Sections.Count);
        for (int i = 0; i < Sections.Count; i++)
            list.Add((i, Sections[i].Marker, Sections[i].Payload.Length,
                      i < SectionKinds.Length ? SectionKinds[i] : "unknown"));
        return list;
    }

    /// <summary>The "djb2-xor, two passes" Forza name hash used for section markers and binary record tags.</summary>
    public static uint ForzaNameHash(string value, uint seed = 0x1505)
    {
        uint result = seed;
        byte[] encoded = Encoding.UTF8.GetBytes(value);
        for (int pass = 0; pass < 2; pass++)
            foreach (byte b in encoded)
                result = unchecked(result ^ ((uint)b + (result >> 2) + 32u * result));
        return result;
    }

    static bool LooksLikeSqlite(byte[] data) =>
        data.Length >= 16 && Encoding.ASCII.GetString(data, 0, 16) == "SQLite format 3\0";
}
