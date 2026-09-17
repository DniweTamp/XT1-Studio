using Xt1.Core.Format;
using Xt1.Core.Xt1;
using Xunit;

namespace Xt1.Core.Tests;

public class LayoutTests
{
    private static LayoutResult Compute(uint fmt, uint w, uint h, uint d, int mips, TextureType type, uint layout,
        ulong stored = 0)
    {
        var info = FormatTable.Get(fmt);
        return SurfaceLayout.Compute(w, h, d, mips, type, layout, info, stored);
    }

    private static long Size(uint fmt, uint w, uint h, uint d, int mips, TextureType type, uint layout) =>
        Compute(fmt, w, h, d, mips, type, layout).ComputedImageSize;

    // Documented rows from XT1_FORMAT.md section 6 (verified against real data).
    [Theory]
    [InlineData(0x8Eu, 256u, 256u, 8, 2u, 24064L)]
    [InlineData(0x8Eu, 512u, 512u, 9, 3u, 89600L)]
    [InlineData(0x87u, 256u, 256u, 8, 0x403u, 89088L)]
    [InlineData(0x44u, 2048u, 1024u, 1, 4u, 2097152L)]
    [InlineData(0x44u, 1024u, 128u, 1, 2u, 131072L)]
    [InlineData(0x38u, 512u, 512u, 1, 0x304u, 1048576L)]
    [InlineData(0x84u, 1024u, 1024u, 10, 4u, 288256L)]
    [InlineData(0x89u, 2048u, 2048u, 6, 4u, 4511744L)]
    public void TwoDimensionalImageSize_MatchesSpec(uint fmt, uint w, uint h, int mips, uint layout, long expected)
    {
        Assert.Equal(expected, Size(fmt, w, h, 1, mips, TextureType.T2D, layout));
    }

    // Validated against core.dat 3D textures.
    [Theory]
    [InlineData(0x25u, 128u, 128u, 128u, 8, 0x40u, 9592320L)]
    [InlineData(0x25u, 256u, 256u, 16u, 3, 0x40u, 4784128L)]
    [InlineData(0x25u, 256u, 256u, 16u, 1, 0x40u, 4194304L)]
    [InlineData(0x25u, 128u, 128u, 64u, 1, 0x40u, 4194304L)]
    [InlineData(0x6du, 16u, 16u, 32u, 1, 0x40u, 32768L)]
    public void ThreeDimensionalImageSize_MatchesRealData(uint fmt, uint w, uint h, uint d, int mips, uint layout, long expected)
    {
        Assert.Equal(expected, Size(fmt, w, h, d, mips, TextureType.T3D, layout));
    }

    [Fact]
    public void ThreeD_DepthIsReducedPerMip()
    {
        var result = Compute(0x25, 128, 128, 128, 8, TextureType.T3D, 0x40);
        Assert.Equal(8, result.Mips.Count);
        Assert.Equal(128, result.Mips[0].SliceCount);
        Assert.Equal(64, result.Mips[1].SliceCount);
        Assert.Equal(1, result.Mips[7].SliceCount);
    }

    [Theory]
    [InlineData(0x50u, 64u, 64u, 7, 1u, 49152L)]     // CubeMap.wtb BC6H
    [InlineData(0x25u, 64u, 64u, 7, 3u, 147456L)]    // core.dat RGBA8 cube
    [InlineData(0x50u, 256u, 256u, 9, 3u, 540672L)]  // core.dat BC6H cube
    public void CubeImageSize_AlignsEachFaceTo4096(uint fmt, uint w, uint h, int mips, uint layout, long expected)
    {
        var result = Compute(fmt, w, h, 1, mips, TextureType.TCubeArray, layout);
        Assert.Equal(expected, result.ComputedImageSize);
        Assert.Equal(6, result.FaceCount);
        Assert.Equal(4096, result.FaceAlignment);
        Assert.Equal(7680, Compute(0x50, 64, 64, 1, 7, TextureType.TCubeArray, 1).FaceSize);
    }

    [Fact]
    public void CubeFaceOffsets_Are4096Aligned()
    {
        var result = Compute(0x50, 64, 64, 1, 7, TextureType.TCubeArray, 1);
        Assert.Equal(0, result.FaceOffset(0));
        Assert.Equal(8192, result.FaceOffset(1));
        Assert.Equal(8192 * 5, result.FaceOffset(5));
    }

    [Fact]
    public void AstcLargestDimAnomaly_IsFlaggedNotThrown()
    {
        // pl0080.dat: ASTC 6x6 1024x1024 / 10 mips stores 915456 while the formula gives 772096.
        var result = Compute(0x8b, 1024, 1024, 1, 10, TextureType.T2D, 0x403, stored: 915456);
        Assert.True(result.Applicable);
        Assert.Equal(772096, result.ComputedImageSize);
        Assert.False(result.SizeMatches);
        Assert.NotNull(result.Note);
    }

    [Fact]
    public void TinyCubeFaces_AreStoredWithoutPageAlignment()
    {
        // core.dat has 8x8 BC3s cubes stored as 6*512: a face that already fits inside one page is
        // not padded to 4096, unlike the larger cube groups (7680 -> 8192, 23552 -> 24576,
        // 89600 -> 90112). Getting this wrong reads every face except the first from padding.
        var result = Compute(0x48, 8, 8, 1, 1, TextureType.TCubeArray, 0, stored: 3072);

        Assert.True(result.SizeMatches, result.Note);
        Assert.Equal(512, result.FaceSize);
        Assert.Equal(512, result.FaceStride);
        Assert.Equal(0, result.FaceAlignment);
        Assert.Equal(512, result.FaceOffset(1));
        Assert.Equal(512 * 5, result.FaceOffset(5));
    }

    /// <summary>
    /// Format 0x01 ("af") is a 1-byte-per-pixel uncompressed format, so the section 6 formula
    /// applies with bytesPerBlock = 1. Each expected value is the real stored imageSize, which the
    /// spec had read as a "compressed size".
    /// </summary>
    [Theory]
    [InlineData(1024u, 1024u, 1, 0x205u, 1048576L)] // ui_core*.dat #2
    [InlineData(128u, 128u, 8, 0x4u, 25600L)]       // 055.wta #21/#22
    [InlineData(8u, 512u, 10, 0x4u, 66560L)]        // 055.wta #14
    public void AfFormat_UsesTheLayoutFormulaWithOneBytePerPixel(
        uint width, uint height, int mips, uint layout, long expected)
    {
        var result = Compute(0x01, width, height, 1, mips, TextureType.T2D, layout);
        Assert.True(result.Applicable);
        Assert.Equal(expected, result.ComputedImageSize);
        Assert.Equal(1, result.Format!.BytesPerBlock);
    }

    [Fact]
    public void AfFormat_IsNotTreatedAsCompressed()
    {
        // A 1-byte-per-pixel payload of the computed size must satisfy the layout check.
        var result = Compute(0x01, 1024, 1024, 1, 1, TextureType.T2D, 0x205, stored: 1048576);
        Assert.True(result.SizeMatches);
    }

    [Fact]
    public void Unknown6d_IsUncompressed32bpp_DecodeOnly()
    {
        // 16x16x32 with 4 bytes per texel x 32 slices == 32768, and that is the real imageSize.
        var result = Compute(0x6d, 16, 16, 32, 1, TextureType.T3D, 0x40, stored: 32768);
        Assert.True(result.Applicable);
        Assert.True(result.SizeMatches);
        Assert.Equal(4, result.Format!.BytesPerBlock);
        Assert.Equal(32, result.Mips[0].SliceCount);
    }

    [Fact]
    public void HeaderBlockHeightLog2_ComesFromLowThreeBits()
    {
        var header = new Xt1Header();
        header.Layout = 0x403;
        Assert.Equal(3, header.BlockHeightLog2);
        header.Layout = 0x304;
        Assert.Equal(4, header.BlockHeightLog2);
    }
}
