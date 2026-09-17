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
