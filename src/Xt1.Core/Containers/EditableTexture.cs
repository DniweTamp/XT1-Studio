using Xt1.Core.Format;
using Xt1.Core.Util;
using Xt1.Core.Xt1;

namespace Xt1.Core.Containers;

/// <summary>How a texture's <c>table3</c> hash is produced on save.</summary>
public enum HashMode
{
    /// <summary>Keep the stored value verbatim (default).</summary>
    Preserve,

    /// <summary>Recompute <c>crc32(name) &amp; 0x7FFFFFFF</c> from <see cref="EditableTexture.Name"/> on save.</summary>
    AutoFromName,

    /// <summary>Write <see cref="EditableTexture.Hash"/> verbatim and never recompute.</summary>
    Forced,
}

/// <summary>
/// One WTB texture: its XT1 header, its per-texture table entries (offset/size/flags/hash) and
/// its raw payload bytes. All original bytes are retained so unknown fields survive edits.
/// </summary>
public sealed class EditableTexture
{
    public int Index { get; set; }

    public Xt1Header Header { get; }

    /// <summary>Optional texture name. WTB does not store names; used for hash computation/UI.</summary>
    public string? Name { get; set; }

    /// <summary>Raw <c>table2</c> bytes exactly as stored, <c>[b0][b1][b2][b3]</c>.</summary>
    public byte[] Table2Bytes { get; }

    public byte[] OriginalTable2Bytes { get; }

    /// <summary>Stored <c>table3</c> value (little-endian on disk).</summary>
    public uint Hash { get; set; }

    public HashMode HashMode { get; set; } = HashMode.Preserve;

    /// <summary>The hash value as loaded from disk (for dirty tracking).</summary>
    public uint OriginalHash { get; }

    public HashMode OriginalHashMode { get; } = HashMode.Preserve;

    /// <summary>Stored <c>table0</c> payload offset (stream-relative for split banks, absolute for standalone).</summary>
    public uint PayloadOffset { get; set; }

    /// <summary>Stored <c>table1</c> payload size. Defaults to <see cref="Xt1Header.ImageSize"/>.</summary>
    public uint PayloadSize { get; set; }

    /// <summary>Raw stored payload bytes for this texture (block-linear, sized to the payload).</summary>
    public byte[] Payload { get; set; }

    public byte[] OriginalPayload { get; private set; }

    public uint OriginalPayloadOffset { get; }

    public uint OriginalPayloadSize { get; }

    public EditableTexture(
        Xt1Header header,
        byte[] table2Bytes,
        uint hash,
        uint payloadOffset,
        uint payloadSize,
        byte[] payload,
        string? name = null,
        bool trackOriginal = true)
    {
        Header = header;
        Table2Bytes = (byte[])table2Bytes.Clone();
        OriginalTable2Bytes = (byte[])table2Bytes.Clone();
        Hash = hash;
        OriginalHash = hash;
        PayloadOffset = payloadOffset;
        PayloadSize = payloadSize;
        OriginalPayloadOffset = payloadOffset;
        OriginalPayloadSize = payloadSize;
        Payload = payload;
        OriginalPayload = trackOriginal ? (byte[])payload.Clone() : payload;
        Name = name;
    }

    public FormatInfo? FormatInfo => Header.Info;

    private LayoutResult? _layout;
    private ulong _layoutStamp;

    /// <summary>
    /// Cached payload layout. The layout is derived from the header bytes only, so it is
    /// invalidated whenever the header changes; callers that decode many subresources (cube
    /// faces, volume slices) therefore pay for the computation once.
    /// </summary>
    public LayoutResult Layout
    {
        get
        {
            ulong stamp = ComputeLayoutStamp();
            if (_layout is null || stamp != _layoutStamp)
            {
                _layout = SurfaceLayout.Compute(Header);
                _layoutStamp = stamp;
            }
            return _layout;
        }
    }

    /// <summary>Invalidate the cached layout (called when the header bytes are replaced wholesale).</summary>
    public void InvalidateLayout() => _layout = null;

    private ulong ComputeLayoutStamp()
    {
        // FNV-1a over the header fields the layout depends on (0x08..0x38 inclusive of imageSize).
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        var raw = Header.Raw;
        for (int i = 0x08; i < Xt1Header.Size && i < raw.Length; i++)
        {
            hash ^= raw[i];
            hash *= prime;
        }
        return hash;
    }

    public bool PayloadDirty => !Payload.AsSpan().SequenceEqual(OriginalPayload);

    /// <summary>Set the payload and treat it as the unmodified baseline (used when the payload
    /// stream is attached after the WTB is parsed).</summary>
    public void ResetOriginalPayload(byte[] bytes)
    {
        Payload = bytes;
        OriginalPayload = (byte[])bytes.Clone();
    }

    public bool Table2Dirty => !Table2Bytes.AsSpan().SequenceEqual(OriginalTable2Bytes);

    public bool HeaderDirty => Header.IsDirty;

    public bool OffsetsDirty => PayloadOffset != OriginalPayloadOffset || PayloadSize != OriginalPayloadSize;

    public bool IsDirty => PayloadDirty || Table2Dirty || HeaderDirty || OffsetsDirty ||
                           Hash != OriginalHash || HashMode != OriginalHashMode;

    // --- table2 decoded bits (XT1_FORMAT.md section 4) ---

    /// <summary>First table2 byte: 0x20 when the record carries a CRC32 hash (model/UI/character
    /// banks), 0x00 otherwise (effect banks).</summary>
    public byte Table2B0
    {
        get => Table2Bytes[0];
        set => Table2Bytes[0] = value;
    }

    /// <summary>Fourth table2 byte: 0x60 base | 0x04 sRGB | 0x08 the "af" single-channel format | 0x80 cube usage.</summary>
    public byte Table2B3
    {
        get => Table2Bytes[3];
        set => Table2Bytes[3] = value;
    }

    /// <summary>table2 read as the big-endian u32 the spec describes.</summary>
    public uint Table2WordBE => Mem.ReadU32BE(Table2Bytes, 0);

    /// <summary>The "Has CRC32 hash" flag: set when the record carries a CRC32 hash in table3.</summary>
    public bool IsModelBankClass => (Table2B0 & 0x20) != 0;
    public bool IsSrgbFlag => (Table2B3 & 0x04) != 0;

    /// <summary>
    /// Bit 0x08, kept under its original name because it is a stored bit rather than a behavior.
    /// It was first read as "second-layer compressed", but per XT1_FORMAT.md section 4 it tracks the
    /// 0x01 ("af") single-channel format, whose payloads are uncompressed; the UI does not offer it
    /// as a toggle.
    /// </summary>
    public bool IsCompressedFlag => (Table2B3 & 0x08) != 0;

    public bool IsCubeUsageFlag => (Table2B3 & 0x80) != 0;

    public void SetSrgbFlag(bool value) => Table2B3 = (byte)(value ? Table2B3 | 0x04 : Table2B3 & ~0x04);
    public void SetCompressedFlag(bool value) => Table2B3 = (byte)(value ? Table2B3 | 0x08 : Table2B3 & ~0x08);
    public void SetCubeUsageFlag(bool value) => Table2B3 = (byte)(value ? Table2B3 | 0x80 : Table2B3 & ~0x80);
    public void SetModelBankClass(bool value) => Table2B0 = (byte)(value ? Table2B0 | 0x20 : Table2B0 & ~0x20);

    /// <summary>The value that will be written to <c>table3</c>.</summary>
    public uint EffectiveHash()
    {
        return HashMode switch
        {
            HashMode.AutoFromName when !string.IsNullOrEmpty(Name) => Crc32NameHash.ComputeMasked(Name),
            HashMode.Forced => Hash,
            _ => Hash,
        };
    }

    /// <summary>Set the hash to the auto-computed value for the current name (still fully overridable).</summary>
    public void ComputeHashFromName()
    {
        if (!string.IsNullOrEmpty(Name))
        {
            Hash = Crc32NameHash.ComputeMasked(Name);
            HashMode = HashMode.Forced;
        }
    }

    /// <summary>Advisory validation specific to this texture (header + size + table values).</summary>
    public IReadOnlyList<string> Validate()
    {
        var warnings = new List<string>(Header.Validate());
        if (FormatInfo is null)
            return warnings;

        if (!FormatInfo.Verified)
            warnings.Add($"format {FormatInfo.Name} was never observed in Bayonetta 3 data; " +
                         "round-tripping is untested for it");

        var layout = Layout;
        if (layout.Applicable && !layout.SizeMatches)
            warnings.Add($"imageSize: {layout.Note}");

        if (PayloadSize != Header.ImageSize)
            warnings.Add($"table1 size {PayloadSize} != XT1 imageSize {Header.ImageSize}");

        if ((ulong)Payload.Length < Header.ImageSize)
            warnings.Add($"payload is {Payload.Length} bytes but XT1 imageSize is {Header.ImageSize}");

        if (Header.ImageSize > int.MaxValue)
            warnings.Add("imageSize exceeds the addressable payload range");

        return warnings;
    }

    /// <summary>
    /// Create a brand-new 2D texture with valid constants, for adding to a bank.
    /// <paramref name="layout"/> defaults to the driver-derived block height for the given geometry
    /// (the header's low three bits). The game reads that field as the surface's block height, so a
    /// fresh record must advertise the canonical value rather than an arbitrary constant; pass an
    /// explicit <paramref name="layout"/> only to reproduce a specific record's word.
    /// </summary>
    public static EditableTexture CreateNew(Xt1Format format, uint width, uint height, uint? layout = null)
    {
        var header = new Xt1Header();
        header.Magic = Xt1Header.MagicValue;
        header.Flags = Xt1Header.FlagsValue;
        header.HeaderSize = Xt1Header.Size;
        header.Layout2 = Xt1Header.Layout2Value;
        header.MipCount = 1;
        header.Width = width;
        header.Height = height;
        header.Depth = 1;
        header.Type = TextureType.T2D;
        header.Format = format;
        header.Layout = layout ?? DerivedLayout(format, height);
        return new EditableTexture(header, new byte[4], 0, 0, 0, Array.Empty<byte>());
    }

    /// <summary>Canonical blockHeightLog2 for a new surface, or 0 when the format is unknown.</summary>
    private static uint DerivedLayout(Xt1Format format, uint height)
    {
        var info = FormatTable.Get(format);
        return info is null ? 0u : (uint)GobSwizzle.BlockHeightLog2(info.HeightBlocks((int)height));
    }

    /// <summary>
    /// Wrap a raw `.xt1` blob as an editable texture so the normal preview/edit path works for it.
    /// The header and payload are shared with <paramref name="raw"/> (not copied), so edits made
    /// through the wrapper are reflected when the raw blob is serialized.
    /// </summary>
    public static EditableTexture FromRaw(RawXt1 raw) =>
        new(raw.Header, new byte[4], 0, 0, (uint)Math.Min(raw.Payload.Length, uint.MaxValue), raw.Payload);

    /// <summary>Create a non-editable wrapper without cloning the payload for dirty tracking.</summary>
    public static EditableTexture FromRawReadOnly(RawXt1 raw) =>
        new(raw.Header, new byte[4], 0, 0, (uint)Math.Min(raw.Payload.Length, uint.MaxValue), raw.Payload,
            trackOriginal: false);

    public EditableTexture Clone()
    {
        var clone = new EditableTexture(Header.Clone(), Table2Bytes, Hash, PayloadOffset, PayloadSize,
            (byte[])Payload.Clone(), Name)
        {
            Index = Index,
            HashMode = HashMode,
        };
        return clone;
    }
}
