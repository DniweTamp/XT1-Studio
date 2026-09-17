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

    public long MipOffset(int mip) => mip >= 0 && mip < Mips.Count ? Mips[mip].Offset : 0;

    /// <summary>
    /// Offset of a 3D slice. Meaningful only when <see cref="MipInfo.BlockDepth"/> is 1; when the
    /// block depth interleaves Z the slices are not individually contiguous, so the whole volume
    /// must be untiled at once (see <see cref="GobSwizzle.DeswizzleVolume"/>).
    /// </summary>
    public long SliceOffset(int mip, int slice)
    {
        if (mip < 0 || mip >= Mips.Count) return 0;
        var info = Mips[mip];
        if (slice < 0 || slice >= info.SliceCount) return info.Offset;
        if (info.BlockDepth > 1) return info.Offset;
        return info.Offset + (long)slice * info.SliceSurfaceSize;
    }

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
        bool is3D = type.Is3D();
        int baseDepth = (int)Math.Max(1u, depth);

        // Bits 4..6 of the layout hold blockDepthLog2; when zero, fall back to the driver's depth
        // heuristic (they agree for every 3D surface observed in the game).
        int blockDepthLog2 = (int)((layout >> 4) & 7);
        int gobDepth = is3D
            ? blockDepthLog2 > 0 ? 1 << blockDepthLog2 : GobSwizzle.BlockDepth(baseDepth)
            : 1;

        var mipList = BuildMips(null, out long cursor, out long faceSize);
        int faceCount = type.FaceCount();

        // Cube faces are normally separated by 4096-byte alignment, but a face that already fits
        // inside one page is stored densely (8x8 BC3s faces occupy 512 bytes, and the record's
        // imageSize is 6*512 rather than 6*4096). Verified against all four cube groups observed:
        // 512 -> 512, 7680 -> 8192, 23552 -> 24576, 89600 -> 90112.
        long faceStride = faceSize <= CubeFaceAlignment
            ? faceSize
            : Mem.Align(faceSize, CubeFaceAlignment);
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
            long units = (long)Mem.Align(mip.HeightBlocks, mip.Lines) * mip.SliceCount;
            int needed = mip.WidthBlocks * fmt.BytesPerBlock;
            if (units > 0 && storedImageSize % (ulong)units == 0)
            {
                long candidate = (long)(storedImageSize / (ulong)units);
                if (candidate != mip.Pitch && candidate >= needed && candidate % 64 == 0 &&
                    candidate <= int.MaxValue)
                {
                    mipList = BuildMips((int)candidate, out cursor, out _);
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
            FaceAlignment = type.IsCube() && faceSize > CubeFaceAlignment ? CubeFaceAlignment : 0,
            Note = note,
            PitchRecoveredFromStoredSize = pitchRecovered,
        };

        // The mip chain, optionally forcing a row pitch (used by the recovery above).
        List<MipInfo> BuildMips(int? pitchOverride, out long cursorOut, out long faceSizeOut)
        {
            int lpbh = 8 << blkLog2;
            int shift = 0;
            long cursor = 0;
            var list = new List<MipInfo>(mips);

            for (int m = 0; m < mips; m++)
            {
                int w = Math.Max(1, (int)(width >> m));
                int h = Math.Max(1, (int)(height >> m));
                int d = is3D ? Math.Max(1, baseDepth >> m) : 1;

                int wb = Mem.DivUp(w, fmt.BlockWidth);
                int hb = Mem.DivUp(h, fmt.BlockHeight);

                if (Mem.Pow2RoundUp(hb) < lpbh)
                    shift++;

                int lines = 8 * Math.Max(1, (1 << blkLog2) >> shift);
                int pitch = pitchOverride ?? Mem.Align(wb * fmt.BytesPerBlock, 64);
                int surface = pitch * Mem.Align(hb, lines);
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
            return list;
        }
    }

    /// <summary>Compute the stored payload size for a header without a full parse.</summary>
    public static long ComputeImageSize(Xt1Header header) => Compute(header).ComputedImageSize;
}
