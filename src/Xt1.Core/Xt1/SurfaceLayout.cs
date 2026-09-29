using System.Numerics;
using Xt1.Core.Format;
using Xt1.Core.Util;

namespace Xt1.Core.Xt1;

/// <summary>Per-mip block-linear surface geometry, validated against real BT3 data.</summary>
public sealed class MipInfo
{
    public int Index { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Depth { get; init; }
    public int WidthBlocks { get; init; }
    public int HeightBlocks { get; init; }

    /// <summary>Block height in 8-row GOBs (lines / 8).</summary>
    public int BlockHeight { get; init; }

    /// <summary>Number of depth slices stored inside one block (Z interleave); 1 for 2D.</summary>
    public int BlockDepth { get; init; } = 1;

    /// <summary>Height padding unit in rows (8 * BlockHeight).</summary>
    public int Lines { get; init; }

    public int Pitch { get; init; }

    /// <summary>Byte size of a single 2D surface (one depth slice) of this mip.</summary>
    public int SliceSurfaceSize { get; init; }

    /// <summary>Number of depth slices (1 for 2D/cube, mip-reduced depth for 3D).</summary>
    public int SliceCount { get; init; }

    /// <summary>Offset of this mip's first byte within the texture (or within a cube face).</summary>
    public long Offset { get; init; }

    public long TotalSize => (long)SliceSurfaceSize * SliceCount;
}

/// <summary>Result of running the payload-layout algorithm over an XT1 header.</summary>
public sealed class LayoutResult
{
    public required TextureType Type { get; init; }
    public FormatInfo? Format { get; init; }

    /// <summary>False when the format is unknown, so no layout could be computed.</summary>
    public bool Applicable { get; init; }

    public long ComputedImageSize { get; init; }
    public ulong StoredImageSize { get; init; }
    public bool SizeMatches { get; init; }
    public IReadOnlyList<MipInfo> Mips { get; init; } = Array.Empty<MipInfo>();

    public int FaceCount { get; init; } = 1;

    /// <summary>Byte size of one face's full mip chain (before cube face alignment).</summary>
    public long FaceSize { get; init; }

    /// <summary>Distance between cube faces in bytes (see <see cref="FaceAlignment"/>).</summary>
    public long FaceStride { get; init; }

    /// <summary>Cube face alignment (4096), or 0 when not a cube or the face fits in one page.</summary>
    public int FaceAlignment { get; init; }

    /// <summary>Human-readable note about a mismatch or a recovered pitch.</summary>
    public string? Note { get; init; }

    /// <summary>
    /// True when the row pitch had to be recovered from the stored <c>imageSize</c> because the
    /// standard formula disagreed (the ASTC large-dimension anomaly class).
    /// </summary>
    public bool PitchRecoveredFromStoredSize { get; init; }

    /// <summary>
    /// True when the record uses Tegra <c>tile_width_spacing = 4</c> (the ASTC multi-mip anomaly) and
    /// at least one mip actually took the widened row pitch: each mip that is not "small" gets its row
    /// pitch widened to a 1024-byte (16-GOB) boundary, while small mips keep the plain 64-byte pitch.
    /// False when the record-level trigger fires but every mip is small (widening is a no-op), and
    /// also false when the pitch came from the stored-size back-solve, since no mip then takes the
    /// widened pitch.
    /// </summary>
    public bool WidePitchApplied { get; init; }

    public long FaceOffset(int face)
    {
        if (Type.IsCube())
            return (long)face * (FaceStride > 0 ? FaceStride : FaceSize);
        return 0;
    }
}

/// <summary>
/// XT1 payload layout. Validated against real Bayonetta 3 data: 2D mip chains, cubes as six faces
/// each aligned to 4096, and 3D volumes whose slices are interleaved by the block depth.
/// </summary>
public static class SurfaceLayout
{
    private const int MipAlignment = 512;
    private const int CubeFaceAlignment = 4096;

    /// <summary>Largest texture edge any real bank uses (the widest observed is 2050).</summary>
    private const uint MaxDimension = 16384;

    /// <summary>Texel budget for mip 0, so a crafted header cannot size arrays in the gigabytes.</summary>
    private const long MaxMipTexels = 64L * 1024 * 1024;

    public static LayoutResult Compute(Xt1Header header)
    {
        var info = FormatTable.Get(header.FormatValue);
        return Compute(header.Width, header.Height, header.Depth, header.MipCount, header.Type,
            header.Layout, info, header.ImageSize);
    }

    public static LayoutResult Compute(
        uint width, uint height, uint depth, int mipCount, TextureType type,
        uint layout, FormatInfo? format, ulong storedImageSize)
    {
        if (format is null)
        {
            return new LayoutResult
            {
                Type = type,
                Format = null,
                Applicable = false,
                ComputedImageSize = 0,
                StoredImageSize = storedImageSize,
                SizeMatches = false,
                FaceCount = type.FaceCount(),
                Note = "unknown format; payload layout not computed",
            };
        }

        var fmt = format;
        int mips = Math.Clamp(mipCount <= 0 ? 1 : mipCount, 1, 32);
        int blkLog2 = (int)(layout & 7);
        int bppLog2 = BitOperations.Log2((uint)fmt.BytesPerBlock);
        bool is3D = type.Is3D();
        int baseDepth = (int)Math.Max(1u, depth);

        // Plausibility limits. A 56-byte header can advertise dimensions whose derived block counts
        // would allocate gigabytes and spin the swizzle loops for minutes, so implausible geometry is
        // refused the same way an unknown format is: no layout, and every decoder bails out with this
        // note instead. Real textures top out around 2K on a side.
        ulong w0 = Math.Max(1u, width);
        ulong h0 = Math.Max(1u, height);
        ulong d0 = is3D ? (ulong)baseDepth : 1;
        if (w0 > MaxDimension || h0 > MaxDimension || d0 > MaxDimension ||
            w0 * h0 * d0 > (ulong)MaxMipTexels)
        {
            return new LayoutResult
            {
                Type = type,
                Format = fmt,
                Applicable = false,
                ComputedImageSize = 0,
                StoredImageSize = storedImageSize,
                SizeMatches = false,
                FaceCount = type.FaceCount(),
                Note = $"implausible dimensions {width}x{height}x{depth}; layout not computed",
            };
        }

        // Bits 4..6 of the layout hold blockDepthLog2; when zero, fall back to the driver's depth
        // heuristic (they agree for every 3D surface observed in the game).
        int blockDepthLog2 = (int)((layout >> 4) & 7);
        int gobDepth = is3D
            ? blockDepthLog2 > 0 ? 1 << blockDepthLog2 : GobSwizzle.BlockDepth(baseDepth)
            : 1;

        // Bit 0x400 identifies the tile_width_spacing = 4 layout class. The low bits alone are not
        // enough: ordinary layout 0x3 textures can become non-small when recompressed from ASTC to
        // BC, and treating those as wide produces a payload external Tegra readers deswizzle with
        // the wrong pitch. Every widened record in the ROM census is 0x403; no 0x3 record is wide.
        bool widePitch = (layout & 0x400) != 0 && blkLog2 == 3
            && (Mem.DivUp((int)Math.Max(1u, width), fmt.BlockWidth) * fmt.BytesPerBlock) % 1024 != 0;

        var mipList = BuildMips(null, out long cursor, out long faceSize, out bool anyWide);
        int faceCount = type.FaceCount();

        // Cube faces are page-aligned only when the base mip itself reaches a page: a chain whose
        // mip 0 (the largest mip's single slice) is under 4096 bytes is stored densely. This keeps
        // 512 -> 512, 4096 -> 4096, 7680 -> 8192, 65536 -> 65536 and 89600 -> 90112 unchanged while
        // fixing the 13 BC1_SRGB 64x64 7-mip cubes (face 5120, base mip 2048 -> dense 5120 = 30720).
        long baseMipSurface = mipList.Count > 0 ? mipList[0].SliceSurfaceSize : 0;
        bool cubePageAlign = type.IsCube() && baseMipSurface >= CubeFaceAlignment;
        long faceStride = cubePageAlign ? Mem.Align(faceSize, CubeFaceAlignment) : faceSize;
        long computed = type.IsCube()
            ? (long)faceCount * faceStride
            : cursor;

        // A few single-mip records (the documented ASTC 6x6 / 5x5 large-dimension cases) store more
        // than the formula predicts, and the surplus is a wider row pitch, not extra rows. The
        // stored imageSize is authoritative, so back-solve the pitch from it. Verified visually on
        // ui_core.dat's "core.wta" #1 (1026x1026 ASTC 6x6 needs 48 GOBs, not 43): the recovered
        // pitch gives a coherent image, the formula's pitch shears it.
        bool pitchRecovered = false;
        if (computed != (long)storedImageSize && mips == 1 && !type.IsCube() && mipList.Count == 1)
        {
            var mip = mipList[0];
            // Padded rows per slice, kept in a long so the pitch bound below cannot itself overflow
            // and so the recovered pitch is refused before it could wrap the per-mip surface product.
            long paddedRows = Mem.Align(mip.HeightBlocks, mip.Lines);
            long units = paddedRows * mip.SliceCount;
            int needed = mip.WidthBlocks * fmt.BytesPerBlock;
            if (units > 0 && storedImageSize % (ulong)units == 0)
            {
                long candidate = (long)(storedImageSize / (ulong)units);
                if (candidate != mip.Pitch && candidate >= needed && candidate % 64 == 0 &&
                    candidate <= int.MaxValue / Math.Max(1L, paddedRows))
                {
                    mipList = BuildMips((int)candidate, out cursor, out _, out anyWide);
                    computed = cursor;
                    pitchRecovered = computed == (long)storedImageSize;
                }
            }
        }

        bool matches = computed == (long)storedImageSize;
        string? note = null;
        if (!matches)
        {
            note = $"stored imageSize {storedImageSize} != computed {computed}" +
                   (fmt.Codec == CodecKind.Astc && blkLog2 == 3
                       ? " (ASTC large-dimension anomaly; see XT1_FORMAT.md section 6)"
                       : string.Empty);
        }
        else if (pitchRecovered)
        {
            note = $"row pitch recovered from the stored imageSize ({mipList[0].Pitch} bytes)";
        }

        return new LayoutResult
        {
            Type = type,
            Format = format,
            Applicable = true,
            ComputedImageSize = computed,
            StoredImageSize = storedImageSize,
            SizeMatches = matches,
            Mips = mipList,
            FaceCount = faceCount,
            FaceSize = faceSize,
            FaceStride = faceStride,
            FaceAlignment = cubePageAlign ? CubeFaceAlignment : 0,
            Note = note,
            PitchRecoveredFromStoredSize = pitchRecovered,
            WidePitchApplied = anyWide,
        };

        // The mip chain, optionally forcing a row pitch (used by the recovery above).
        List<MipInfo> BuildMips(int? pitchOverride, out long cursorOut, out long faceSizeOut, out bool wideOut)
        {
            // Mip 0's block height comes from the header's low three bits. 3D surfaces always use 1
            // (the driver forces BlockHeight::One for volumes); every later mip halves it exactly as
            // the driver/Ryujinx/tegra_swizzle do.
            int blockHeightMip0 = is3D ? 1 : Math.Max(1, 1 << blkLog2);
            long cursor = 0;
            bool anyWideApplied = false;
            var list = new List<MipInfo>(mips);

            for (int m = 0; m < mips; m++)
            {
                int w = Math.Max(1, (int)(width >> m));
                int h = Math.Max(1, (int)(height >> m));
                int d = is3D ? Math.Max(1, baseDepth >> m) : 1;

                int wb = Mem.DivUp(w, fmt.BlockWidth);
                int hb = Mem.DivUp(h, fmt.BlockHeight);

                int lines = 8 * GobSwizzle.MipBlockHeight(hb, blockHeightMip0);

                // yuzu's IsSmallerThanGobSize gate: the width term is in bytes, the height term in
                // rows and the depth term (3D only) in slices. A record with tile_width_spacing = 4
                // rounds every non-small mip's row up to 16 GOBs (1024 bytes); small mips (and every
                // record without the trigger) keep the plain 64-byte rounded pitch.
                int widthBytes = wb * fmt.BytesPerBlock;
                bool isSmall = (long)widthBytes <= (1L << (6 - bppLog2 + 4))
                            || hb <= (1 << (3 + blkLog2))
                            || (is3D && d < (1 << blockDepthLog2));
                // The widened pitch only counts as applied when it is the pitch actually selected;
                // a pitchOverride (the stored-size back-solve) supplies the pitch instead.
                bool usedWide = widePitch && !isSmall;
                anyWideApplied |= usedWide && pitchOverride is null;
                int pitch = pitchOverride
                    ?? (usedWide
                        ? Mem.Align(widthBytes, 1024)
                        : Mem.Align(widthBytes, 64));
                long surfaceLong = (long)pitch * Mem.Align(hb, lines);
                // pitch is bounded by the recovery gate above, so this clamp never triggers for a
                // valid record; it only stops a crafted pitch from wrapping the surface negative.
                int surface = (int)Math.Min(surfaceLong, int.MaxValue);
                long offset = Mem.Align(cursor, MipAlignment);

                list.Add(new MipInfo
                {
                    Index = m,
                    Width = w,
                    Height = h,
                    Depth = d,
                    WidthBlocks = wb,
                    HeightBlocks = hb,
                    BlockHeight = Math.Max(1, lines / 8),
                    BlockDepth = is3D ? GobSwizzle.MipBlockDepth(d, gobDepth) : 1,
                    Lines = lines,
                    Pitch = pitch,
                    SliceSurfaceSize = surface,
                    SliceCount = d,
                    Offset = offset,
                });

                cursor = offset + (long)surface * d;
            }

            cursorOut = cursor;
            faceSizeOut = cursor;
            wideOut = anyWideApplied;
            return list;
        }
    }

}
