namespace Xt1.Core.Format;

/// <summary>Static description of an <see cref="Xt1Format"/>: block geometry and codec class.</summary>
public sealed record FormatInfo(
    Xt1Format Format,
    string Name,
    int BlockWidth,
    int BlockHeight,
    int BytesPerBlock,
    CodecKind Codec,
    bool IsSrgb,
    bool Verified)
{
    public uint Value => (uint)Format;

    /// <summary>
    /// The canonical format this entry is an alternate id for (e.g. the unsigned BC4/BC5 alternates
    /// at 0x49/0x4B), or null for a canonical format. This is the
    /// single source of truth for aliasing: the codec maps, the NVTT capability table and the DDS
    /// DXGI table all derive their alias entries from it instead of listing them again.
    /// </summary>
    public Xt1Format? AliasOf { get; init; }

    /// <summary>True when this entry is an alternate id for another format.</summary>
    public bool IsAlias => AliasOf is not null;

    /// <summary>The format itself, or the canonical format when this is an alias.</summary>
    public Xt1Format Canonical => AliasOf ?? Format;

    /// <summary>Name with its id, so two entries that share a name (canonical vs alias) are distinct
    /// in pickers and reports.</summary>
    public string DisplayName => IsAlias
        ? $"{Name} (0x{Value:X2}, community)"
        : $"{Name} (0x{Value:X2})";

    public int WidthBlocks(int pixelWidth) => Util.Mem.DivUp(Math.Max(1, pixelWidth), BlockWidth);
    public int HeightBlocks(int pixelHeight) => Util.Mem.DivUp(Math.Max(1, pixelHeight), BlockHeight);

    /// <summary>ASTC footprint index 0..13 (enum order), or -1 when not ASTC.</summary>
    public int AstcFootprintIndex
    {
        get
        {
            if (Codec != CodecKind.Astc) return -1;
            uint v = (uint)Format;
            return v <= 0x86 ? (int)(v - 0x79) : (int)(v - 0x87);
        }
    }
}
