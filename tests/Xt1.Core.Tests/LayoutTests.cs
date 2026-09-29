using Xt1.Core.Containers;
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
    public void AstcLargestDimAnomaly_IsExplainedByWidePitch()
    {
        // pl0080.dat: ASTC 6x6 1024x1024 / 10 mips stores 915456. This is the multi-mip form of
        // Tegra tile_width_spacing = 4 (section 6): every non-small mip gets a 1024-byte row pitch.
        var result = Compute(0x8b, 1024, 1024, 1, 10, TextureType.T2D, 0x403, stored: 915456);
        Assert.True(result.Applicable);
        Assert.Equal(915456, result.ComputedImageSize);
        Assert.True(result.SizeMatches, result.Note);
        Assert.True(result.WidePitchApplied);
        Assert.False(result.PitchRecoveredFromStoredSize);
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

    /// <summary>
    /// Tegra tile_width_spacing = 4 (the former "multi-mip ASTC anomaly"): a record whose
    /// blockHeightLog2 is 3 and whose base-mip row is not 1024-aligned widens every non-small
    /// mip's row pitch to 1024 bytes. Each expected value is a real stored imageSize.
    /// </summary>
    [Theory]
    [InlineData(0x8Bu, 1024u, 1024u, 10, 915456L)]
    [InlineData(0x7Bu, 1025u, 1025u, 10, 1385472L)]
    [InlineData(0x7Du, 2052u, 2052u, 4, 3256320L)]
    [InlineData(0x7Du, 2052u, 2052u, 8, 3273216L)]
    [InlineData(0x84u, 8192u, 4096u, 13, 8468992L)]
    public void WidePitch_IsComputedExactly(uint fmt, uint w, uint h, int mips, long expected)
    {
        var result = Compute(fmt, w, h, 1, mips, TextureType.T2D, 0x403, stored: (ulong)expected);
        Assert.True(result.SizeMatches, result.Note);
        Assert.True(result.WidePitchApplied);
        Assert.Equal(expected, result.ComputedImageSize);
    }

    [Fact]
    public void WidePitch_DoesNotFireWhenBlockHeightLog2IsNotThree()
    {
        // layout 0x4 -> blockHeightLog2 4: the trigger must not fire even though the base row
        // (171 blocks * 16 = 2736) is not 1024-aligned.
        var result = Compute(0x7d, 1026, 2052, 1, 1, TextureType.T2D, 0x4);
        Assert.False(result.WidePitchApplied);
        Assert.Equal(2752, result.Mips[0].Pitch);
    }

    [Fact]
    public void WidePitch_DoesNotFireWithoutTheLayoutFlag_AfterAstcToBc3Conversion()
    {
        // ui_title_us.dat/title.wta #1 is ASTC 12x12 1032x516 with layout 0x3. In ASTC its
        // 43-block height is small, which used to hide the faulty low-bits-only wide-pitch trigger.
        // BC3 has 258x129 blocks and is no longer small, but 0x3 is still an ordinary layout: Tegra
        // readers expect the natural 65-GOB pitch, not a 0x403-style 80-GOB pitch.
        var result = Compute(0x48, 1032, 516, 1, 1, TextureType.T2D, 0x3);

        Assert.False(result.WidePitchApplied);
        Assert.Equal(4160, result.Mips[0].Pitch);
        Assert.Equal(798720, result.ComputedImageSize);
    }

    [Fact]
    public void WidePitch_FiresForTheFlaggedLayoutClass()
    {
        var result = Compute(0x48, 1032, 516, 1, 1, TextureType.T2D, 0x403);

        Assert.True(result.WidePitchApplied);
        Assert.Equal(5120, result.Mips[0].Pitch);
        Assert.Equal(983040, result.ComputedImageSize);
    }

    [Fact]
    public void WidePitch_DoesNotFireWhenTheBaseRowIsAlready1024Aligned()
    {
        // ASTC 8x8 1024x1024 with blockHeightLog2 3 has a 128 * 16 = 2048-byte base row, already a
        // 16-GOB multiple, so the plain 64-byte pitch is used.
        var result = Compute(0x80, 1024, 1024, 1, 4, TextureType.T2D, 0x403);
        Assert.False(result.WidePitchApplied);
        Assert.Equal(2048, result.Mips[0].Pitch);
    }

    [Fact]
    public void WidePitch_LeavesGobHighMipsSmall()
    {
        // ASTC 5x5: height 640 makes mip 1 exactly 320 px = 64 blocks, which the isSmall gate keeps
        // at the natural pitch; height 650 makes it 65 blocks, which the gate widens.
        var atBoundary = Compute(0x7b, 325, 640, 1, 2, TextureType.T2D, 0x403);
        Assert.True(atBoundary.WidePitchApplied);
        Assert.Equal(64, atBoundary.Mips[1].HeightBlocks);
        Assert.Equal(576, atBoundary.Mips[1].Pitch);   // align(33 * 16, 64), not 1024
        Assert.Equal(2048, atBoundary.Mips[0].Pitch);

        var aboveBoundary = Compute(0x7b, 325, 650, 1, 2, TextureType.T2D, 0x403);
        Assert.True(aboveBoundary.WidePitchApplied);
        Assert.Equal(65, aboveBoundary.Mips[1].HeightBlocks);
        Assert.Equal(1024, aboveBoundary.Mips[1].Pitch); // align(33 * 16, 1024)
    }

    [Fact]
    public void CubeFaces_AreDenseBelowOnePageAndPageAlignedWhenTheBaseMipReachesOnePage()
    {
        // BC1 64x64/7 has a 5120-byte chain but a 2048-byte base mip, so its faces are dense.
        var bc1 = Compute(0x46, 64, 64, 1, 7, TextureType.TCubeArray, 0, stored: 30720);
        Assert.True(bc1.SizeMatches, bc1.Note);
        Assert.Equal(5120, bc1.FaceStride);
        Assert.Equal(0, bc1.FaceAlignment);
        Assert.Equal(30720, bc1.ComputedImageSize);

        // BC3 64x64/7 has a 4096-byte base mip, so its 7680-byte chain is page-aligned.
        var bc3 = Compute(0x48, 64, 64, 1, 7, TextureType.TCubeArray, 0, stored: 49152);
        Assert.True(bc3.SizeMatches, bc3.Note);
        Assert.Equal(8192, bc3.FaceStride);
        Assert.Equal(4096, bc3.FaceAlignment);
        Assert.Equal(49152, bc3.ComputedImageSize);

        var tiny = Compute(0x48, 8, 8, 1, 1, TextureType.TCubeArray, 0, stored: 3072);
        Assert.True(tiny.SizeMatches, tiny.Note);
        Assert.Equal(512, tiny.FaceStride);
        Assert.Equal(0, tiny.FaceAlignment);

        // A 4096-byte base mip is exactly one page, so the 4096-byte face is aligned in place.
        var page = Compute(0x48, 64, 64, 1, 1, TextureType.TCubeArray, 0, stored: 24576);
        Assert.True(page.SizeMatches, page.Note);
        Assert.Equal(4096, page.FaceStride);
        Assert.Equal(4096, page.FaceAlignment);
        Assert.Equal(24576, page.ComputedImageSize);
    }

    [Fact]
    public void WidePitch_IsNotAppliedWhenEveryMipIsSmall()
    {
        // The record-level trigger fires (ASTC 5x5, blockHeightLog2 3, base row 62 * 16 = 992 is not
        // 1024-aligned) but the only mip is "small": heightBlocks 64 <= 1 << (3 + 3), so the widened
        // pitch is never selected. WidePitchApplied must report the applied pitch, not the trigger.
        var result = Compute(0x7b, 310, 320, 1, 1, TextureType.T2D, 0x403, stored: 65536);

        Assert.True(result.Applicable);
        Assert.True(result.SizeMatches, result.Note);
        Assert.False(result.WidePitchApplied);
        Assert.Equal(64, result.Mips[0].HeightBlocks);
        Assert.Equal(1024, result.Mips[0].Pitch); // align(992, 64); the widened pitch is also 1024 here
        Assert.Equal(65536, result.ComputedImageSize);
    }

    [Fact]
    public void BackSolvedPitch_DoesNotReportWidePitchApplied()
    {
        // ASTC 6x6 1026x1026, single mip, layout 0x403 (blockHeightLog2 3). The widened-pitch model
        // computes 589824; feeding the plain-pitch size (2752 * 192) instead forces the stored-size
        // back-solve, which must not report WidePitchApplied because it never selects the widened
        // pitch.
        var result = Compute(0x8b, 1026, 1026, 1, 1, TextureType.T2D, 0x403, stored: 528384);

        Assert.True(result.Applicable);
        Assert.True(result.PitchRecoveredFromStoredSize, result.Note);
        Assert.False(result.WidePitchApplied);
        Assert.True(result.SizeMatches, result.Note);
        Assert.Equal(2752, result.Mips[0].Pitch);
        Assert.Equal(528384, result.ComputedImageSize);
    }

    [Theory]
    [InlineData(1UL << 31)]
    [InlineData(1UL << 33)]
    public void HugeStoredImageSize_OnATinyRecord_IsRejectedWithoutANegativeSurface(ulong stored)
    {
        // A crafted imageSize must not back-solve a pitch whose per-mip surface product overflows a
        // signed int: the recovery gate refuses it and the layout keeps a sane, non-negative surface.
        var result = Compute(0x8b, 1, 1, 1, 1, TextureType.T2D, 0x403, stored: stored);

        Assert.True(result.Applicable);
        Assert.False(result.PitchRecoveredFromStoredSize);
        Assert.False(result.SizeMatches);
        Assert.All(result.Mips, m => Assert.True(m.SliceSurfaceSize >= 0));
        // Block height 8 (header 0x403) reduces to 1 for a one-block mip, so the surface is
        // 64-byte pitch * 8 padded rows.
        Assert.Equal(512, result.Mips[0].SliceSurfaceSize);
    }

    /// <summary>
    /// Custom geometries must use the driver's per-mip block-height reduction, not the constant-
    /// threshold "shift" heuristic that only coincided with the driver on the sizes Bayonetta 3
    /// ships. RGBA8 4x130 has block rows 130,65,32,16,8,4,2,1 and a driver sequence of
    /// 16,16,4,2,1,1,1,1 (the old heuristic gave 16,16,8,4,2,1,1,1). Such a texture round-tripped
    /// in the tool but deswizzled its lower mips wrong in the game.
    /// </summary>
    [Fact]
    public void CustomGeometry_UsesTheDriverMipBlockHeightReduction()
    {
        var result = SurfaceLayout.Compute(4, 130, 1, 8, TextureType.T2D, 0x4,
            FormatTable.Get(Xt1Format.Rgba8Unorm), storedImageSize: 0);

        Assert.True(result.Applicable);
        Assert.Equal(new[] { 16, 16, 4, 2, 1, 1, 1, 1 },
            result.Mips.Select(m => m.BlockHeight).ToArray());
    }

    /// <summary>
    /// A brand-new surface must advertise the driver-derived mip-0 block height in the header's low
    /// three bits, so the game builds a descriptor that matches the swizzled payload. An explicit
    /// word (e.g. a tile_width_spacing = 4 record) is still preserved.
    /// </summary>
    [Fact]
    public void CreateNew_DerivesTheCanonicalBlockHeightLog2()
    {
        Assert.Equal(2, EditableTexture.CreateNew(Xt1Format.Astc8x8Unorm, 256, 256).Header.BlockHeightLog2);
        Assert.Equal(1, EditableTexture.CreateNew(Xt1Format.Bc3Unorm, 64, 64).Header.BlockHeightLog2);
        Assert.Equal(0, EditableTexture.CreateNew(Xt1Format.AfL8Unorm, 8, 8).Header.BlockHeightLog2);
        Assert.Equal(3, EditableTexture.CreateNew(Xt1Format.Astc6x6Unorm, 1024, 1024, layout: 0x403)
            .Header.BlockHeightLog2);
    }
}
