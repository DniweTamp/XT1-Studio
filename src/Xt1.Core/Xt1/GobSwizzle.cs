namespace Xt1.Core.Xt1;

/// <summary>
/// Tegra X1 block-linear (GOB) tiling.
///
/// The addressing follows the <c>tegra_swizzle</c> crate (itself ported from Ryujinx's
/// <c>BlockLinearLayout</c> and the Tegra TRM v1.3), which is the reference implementation for
/// this layout. Two properties matter:
///
/// * A GOB is 64x8 bytes. <c>blockHeight</c> stacks that many GOBs vertically into a block, and
///   <c>blockDepth</c> stacks that many slices into the same block — so for 3D surfaces the Z
///   axis is <b>interleaved</b>, not concatenated. Treating each slice as its own 2D surface
///   produces visible block tiles, which is why <see cref="DeswizzleVolume"/> exists.
/// * Compressed formats pass their dimensions in <b>blocks</b>, not pixels; uncompressed formats
///   are 1x1 blocks, so pixels and blocks coincide.
/// </summary>
public static class GobSwizzle
{
    public const int GobWidthInBytes = 64;
    public const int GobHeightInBytes = 8;
    public const int GobSizeInBytes = GobWidthInBytes * GobHeightInBytes;

    /// <summary>
    /// Default block depth for a surface of the given depth, per the crate's port of the driver
    /// code. A depth of 1 (any 2D surface) yields 1, i.e. no Z interleaving.
    /// </summary>
    public static int BlockDepth(int depth)
    {
        int depthAndHalf = depth + depth / 2;
        if (depthAndHalf >= 16) return 16;
        if (depthAndHalf >= 8) return 8;
        if (depthAndHalf >= 4) return 4;
        if (depthAndHalf >= 2) return 2;
        return 1;
    }

    /// <summary>Reduce the block depth for a mip whose depth shrank below it.</summary>
    public static int MipBlockDepth(int mipDepth, int gobDepth)
    {
        while (mipDepth <= gobDepth / 2 && gobDepth > 1)
            gobDepth /= 2;
        return gobDepth;
    }

    /// <summary>Byte offset of the first byte of block (x, y) inside one slice of a 2D surface.</summary>
    public static int BlockLinearOffset(int x, int y, int widthBlocks, int bytesPerBlock, int blockHeight)
    {
        int imageWidthInGobs = (widthBlocks * bytesPerBlock + GobWidthInBytes - 1) / GobWidthInBytes;
        int gob = GobAddressY(y, GobHeightInBytes * blockHeight, GobSizeInBytes * blockHeight,
                        imageWidthInGobs)
                + GobAddressX(x * bytesPerBlock, GobSizeInBytes * blockHeight);
        return gob + GobOffset(x * bytesPerBlock, y);
    }

    /// <summary>The offset within a GOB of the byte at (x, y), x in bytes within the row.</summary>
    private static int GobOffset(int x, int y) =>
        (x % 64) / 32 * 256
        + (y % 8) / 2 * 64
        + (x % 32) / 16 * 32
        + (y % 2) * 16
        + (x % 16);

    private static int GobAddressX(int xBytes, int blockSizeInBytes) =>
        xBytes / GobWidthInBytes * blockSizeInBytes;

    private static int GobAddressY(int y, int blockHeightInBytes, int blockSizeInBytes, int imageWidthInGobs) =>
        (y / blockHeightInBytes) * blockSizeInBytes * imageWidthInGobs
        + (y % blockHeightInBytes) / GobHeightInBytes * GobSizeInBytes;

    /// <summary>Bytes spanned by one depth-block's worth of GOB rows.</summary>
    private static int SliceSize(int widthInGobs, int heightBlocks, int blockHeight, int blockDepth)
    {
        int robSize = GobSizeInBytes * blockHeight * blockDepth * widthInGobs;
        return (heightBlocks + blockHeight * GobHeightInBytes - 1) / (blockHeight * GobHeightInBytes) * robSize;
    }

    private static int GobAddressZ(int z, int blockHeight, int blockDepth, int sliceSize) =>
        z / blockDepth * sliceSize + (z & (blockDepth - 1)) * GobSizeInBytes * blockHeight;

    /// <summary>
    /// Convert one mip's block-linear data (as stored) into linear block order. For depth &gt; 1 the
    /// whole volume is untiled at once, because Z is interleaved inside each block.
    /// <paramref name="pitchBytes"/> overrides the row stride in bytes; pass the layout's pitch,
    /// which is normally <c>align(widthBlocks * bytesPerBlock, 64)</c> but is wider for a few
    /// records whose stored <c>imageSize</c> disagrees with the formula.
    /// </summary>
    public static void DeswizzleVolume(
        ReadOnlySpan<byte> source, Span<byte> destination,
        int widthBlocks, int heightBlocks, int depthBlocks,
        int bytesPerBlock, int blockHeight, int blockDepth, int pitchBytes = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(blockHeight, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockDepth, 1);

        int widthInGobs = PitchInGobs(widthBlocks, bytesPerBlock, pitchBytes);
        int sliceSize = SliceSize(widthInGobs, heightBlocks, blockHeight, blockDepth);
        int blockWidth = 1;
        int blockSizeInBytes = GobSizeInBytes * blockWidth * blockHeight * blockDepth;
        int blockHeightInBytes = GobHeightInBytes * blockHeight;
        int rowBytes = widthBlocks * bytesPerBlock;

        for (int z = 0; z < depthBlocks; z++)
        {
            int offsetZ = GobAddressZ(z, blockHeight, blockDepth, sliceSize);
            for (int y0 = 0; y0 < heightBlocks; y0 += GobHeightInBytes)
            {
                int offsetY = GobAddressY(y0, blockHeightInBytes, blockSizeInBytes, widthInGobs);
                for (int x0 = 0; x0 < rowBytes; x0 += GobWidthInBytes)
                {
                    int gobAddress = offsetZ + offsetY + GobAddressX(x0, blockSizeInBytes);
                    int xLimit = Math.Min(GobWidthInBytes, rowBytes - x0);
                    int yLimit = Math.Min(GobHeightInBytes, heightBlocks - y0);
                    for (int y = 0; y < yLimit; y++)
                    {
                        int linearRow = (z * heightBlocks + y0 + y) * rowBytes + x0;
                        for (int x = 0; x < xLimit; x++)
                            destination[linearRow + x] = source[gobAddress + GobOffset(x, y)];
                    }
                }
            }
        }
    }

    /// <summary>Inverse of <see cref="DeswizzleVolume"/>.</summary>
    public static void SwizzleVolume(
        ReadOnlySpan<byte> source, Span<byte> destination,
        int widthBlocks, int heightBlocks, int depthBlocks,
        int bytesPerBlock, int blockHeight, int blockDepth, int pitchBytes = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(blockHeight, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockDepth, 1);

        int widthInGobs = PitchInGobs(widthBlocks, bytesPerBlock, pitchBytes);
        int sliceSize = SliceSize(widthInGobs, heightBlocks, blockHeight, blockDepth);
        int blockSizeInBytes = GobSizeInBytes * blockHeight * blockDepth;
        int blockHeightInBytes = GobHeightInBytes * blockHeight;
        int rowBytes = widthBlocks * bytesPerBlock;

        for (int z = 0; z < depthBlocks; z++)
        {
            int offsetZ = GobAddressZ(z, blockHeight, blockDepth, sliceSize);
            for (int y0 = 0; y0 < heightBlocks; y0 += GobHeightInBytes)
            {
                int offsetY = GobAddressY(y0, blockHeightInBytes, blockSizeInBytes, widthInGobs);
                for (int x0 = 0; x0 < rowBytes; x0 += GobWidthInBytes)
                {
                    int gobAddress = offsetZ + offsetY + GobAddressX(x0, blockSizeInBytes);
                    int xLimit = Math.Min(GobWidthInBytes, rowBytes - x0);
                    int yLimit = Math.Min(GobHeightInBytes, heightBlocks - y0);
                    for (int y = 0; y < yLimit; y++)
                    {
                        int linearRow = (z * heightBlocks + y0 + y) * rowBytes + x0;
                        for (int x = 0; x < xLimit; x++)
                            destination[gobAddress + GobOffset(x, y)] = source[linearRow + x];
                    }
                }
            }
        }
    }

    /// <summary>Row stride in GOBs: the explicit pitch when given, else derived from the width.</summary>
    public static int PitchInGobs(int widthBlocks, int bytesPerBlock, int pitchBytes)
    {
        long stride = pitchBytes > 0 ? pitchBytes : (long)widthBlocks * bytesPerBlock;
        return (int)((stride + GobWidthInBytes - 1) / GobWidthInBytes);
    }

    /// <summary>Convert a 2D block-linear surface (as stored) to a linear block array.</summary>
    public static void Deswizzle(
        ReadOnlySpan<byte> source, Span<byte> destination,
        int widthBlocks, int heightBlocks, int bytesPerBlock, int blockHeight, int pitchBytes = 0) =>
        DeswizzleVolume(source, destination, widthBlocks, heightBlocks, 1, bytesPerBlock, blockHeight, 1, pitchBytes);

    /// <summary>Convert a linear block array into a 2D block-linear surface.</summary>
    public static void Swizzle(
        ReadOnlySpan<byte> source, Span<byte> destination,
        int widthBlocks, int heightBlocks, int bytesPerBlock, int blockHeight, int pitchBytes = 0) =>
        SwizzleVolume(source, destination, widthBlocks, heightBlocks, 1, bytesPerBlock, blockHeight, 1, pitchBytes);

    /// <summary>Exact byte size of a block-linear surface including height padding.</summary>
    public static int SurfaceSize(int widthBlocks, int heightBlocks, int bytesPerBlock, int blockHeight,
        int pitchBytes = 0)
    {
        int pitch = pitchBytes > 0 ? pitchBytes : (widthBlocks * bytesPerBlock + 63) / 64 * 64;
        int lines = 8 * blockHeight;
        int padded = (heightBlocks + lines - 1) / lines * lines;
        return pitch * padded;
    }

    /// <summary>
    /// Byte size of a tiled volume: width GOBs x height GOBs x (depth rounded up to the block
    /// depth) x 512. Mirrors <c>swizzled_mip_size</c> in the reference implementation.
    /// </summary>
    public static int VolumeSize(
        int widthBlocks, int heightBlocks, int depthBlocks, int bytesPerBlock, int blockHeight, int blockDepth,
        int pitchBytes = 0)
    {
        if (blockHeight < 1) blockHeight = 1;
        if (blockDepth < 1) blockDepth = 1;
        int widthInGobs = PitchInGobs(widthBlocks, bytesPerBlock, pitchBytes);
        int heightInGobs = (heightBlocks + blockHeight * GobHeightInBytes - 1) / (blockHeight * GobHeightInBytes)
                           * blockHeight;
        int depthInGobs = (depthBlocks + blockDepth - 1) / blockDepth * blockDepth;
        return widthInGobs * heightInGobs * depthInGobs * GobSizeInBytes;
    }
}
