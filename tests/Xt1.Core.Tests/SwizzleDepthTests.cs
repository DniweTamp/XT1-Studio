using Xt1.Core.Codecs;
using Xt1.Core.Containers;
using Xt1.Core.Format;
using Xt1.Core.Imaging;
using Xt1.Core.Xt1;
using Xunit;

namespace Xt1.Core.Tests;

/// <summary>
/// Block-linear addressing details that a naive slice-by-slice implementation gets wrong:
/// Z is interleaved into blocks for 3D surfaces, and the ASTC large-dimension records store a
/// row pitch wider than the standard formula predicts.
/// </summary>
public class SwizzleDepthTests
{
    /// <summary>Mean absolute difference between neighbours, either in-plane or across slices.</summary>
    private static double MeanAbsDelta(byte[] volume, MipInfo info, int sliceBytes, bool alongZ)
    {
        int bpp = sliceBytes / (info.WidthBlocks * info.HeightBlocks);
        int rowBytes = info.WidthBlocks * bpp;
        long total = 0;
        long count = 0;

        if (alongZ)
        {
            for (int z = 0; z + 1 < info.SliceCount; z++)
            {
                int a = z * sliceBytes;
                int b = (z + 1) * sliceBytes;
                for (int i = 0; i < sliceBytes; i += bpp)
                {
                    total += Math.Abs(volume[a + i] - volume[b + i]);
                    count++;
                }
            }
        }
        else
        {
            for (int z = 0; z < info.SliceCount; z++)
            {
                int slice = z * sliceBytes;
                for (int y = 0; y < info.HeightBlocks; y++)
                {
                    int row = slice + y * rowBytes;
                    for (int x = 0; x + bpp < rowBytes; x += bpp)
                    {
                        total += Math.Abs(volume[row + x] - volume[row + x + bpp]);
                        count++;
                    }
                }
            }
        }

        return count == 0 ? 0 : (double)total / count;
    }

    [Fact]
    public void ThreeDimensionalSurfaces_InterleaveDepth()
    {
        if (!TestPaths.Exists(TestPaths.CoreDat)) return;

        var document = BankDocument.Load(TestPaths.CoreDat);
        var texture = document.AllTextures.FirstOrDefault(t =>
            t.Header.Type == TextureType.T3D && t.Header.Depth >= 64);
        Assert.NotNull(texture);

        var mip0 = texture!.Layout.Mips[0];
        Assert.True(mip0.BlockDepth > 1,
            $"a 3D surface must interleave Z; got blockDepth {mip0.BlockDepth}");

        var volume = TextureCodec.ExtractLinearVolume(texture, 0, out var info, out var error);
        Assert.Null(error);
        Assert.NotNull(info);

        int sliceBytes = info!.WidthBlocks * info.HeightBlocks * texture.FormatInfo!.BytesPerBlock;
        Assert.Equal((long)sliceBytes * info.SliceCount, volume.LongLength);

        // A volume untiled as independent slices scrambles Z, so adjacent slices disagree
        // strongly. Measured with the interleave: ~10.5 in-plane and ~11.8 slice-to-slice;
        // without it the slice-to-slice figure is ~36.8.
        double inPlane = MeanAbsDelta(volume, info, sliceBytes, alongZ: false);
        double acrossSlices = MeanAbsDelta(volume, info, sliceBytes, alongZ: true);

        Assert.True(inPlane < 12.5, $"in-plane variation {inPlane:F2} too high (expected < 12.5)");
        Assert.True(acrossSlices < 20.0,
            $"slice-to-slice variation {acrossSlices:F2} too high (expected < 20; a depth-agnostic " +
            "deswizzle gives ~36.8)");
    }

    [Fact]
    public void ThreeDimensionalBlocks_AreByteExactAfterSwizzleRoundTrip()
    {
        // The volume swizzle must be a true inverse, including the partial GOBs on odd sizes.
        foreach (var (wb, hb, db, bpp, bh, bd) in new[]
                 {
                     (16, 16, 16, 4, 1, 16),
                     (17, 13, 7, 4, 4, 4),
                     (5, 9, 3, 16, 2, 2),
                     (1, 1, 1, 4, 1, 1),
                     (33, 7, 20, 1, 8, 16),
                 })
        {
            var linear = new byte[wb * hb * db * bpp];
            for (int i = 0; i < linear.Length; i++) linear[i] = (byte)(i * 31 + (i >> 5));

            int surfaceSize = GobSwizzle.VolumeSize(wb, hb, db, bpp, bh, bd);
            var surface = new byte[surfaceSize];
            GobSwizzle.SwizzleVolume(linear, surface, wb, hb, db, bpp, bh, bd);

            var back = new byte[linear.Length];
            GobSwizzle.DeswizzleVolume(surface, back, wb, hb, db, bpp, bh, bd);

            Assert.Equal(linear, back);
        }
    }

    /// <summary>
    /// ui_core.dat's "core.wta" #1 (1026x1026 ASTC 6x6, hash 0x482960F9) uses Tegra
    /// tile_width_spacing = 4: the formula's 43-GOB row is widened to 48 GOBs (3072 bytes).
    /// </summary>
    [Fact]
    public void AstcLargeOddDimension_UsesTheWidePitch()
    {
        if (!TestPaths.Exists(TestPaths.UiCoreDat)) return;

        var document = BankDocument.Load(TestPaths.UiCoreDat);
        var texture = document.AllTextures.FirstOrDefault(t => t.Header.Width == 1026 && t.Header.Height == 1026);
        Assert.NotNull(texture);

        var layout = texture!.Layout;
        Assert.True(layout.WidePitchApplied);
        Assert.False(layout.PitchRecoveredFromStoredSize);
        Assert.True(layout.SizeMatches, layout.Note);
        Assert.Equal(589824UL, texture.Header.ImageSize);
        Assert.Equal(589824L, layout.ComputedImageSize);
        Assert.Equal(3072, layout.Mips[0].Pitch);   // 48 GOBs, not the formula's 2752 (43)
        Assert.Equal(171, layout.Mips[0].WidthBlocks);

        var image = TextureCodec.Decode(texture, 0, 0, 0, out var error);
        Assert.NotNull(image);
        Assert.Null(error);
        Assert.Equal(1026, image!.Width);
        Assert.Equal(1026, image.Height);
        Assert.True(MeanHorizontalDelta(image!) > 1.5, "decoded image looks blank");
    }

    /// <summary>
    /// The wide pitch must actually be used by the swizzle, not just by the size maths. The
    /// surface's padding is filled with magenta (255,0,255) in these atlases, so a wrong stride
    /// drags padding into the image: decoding with the 2752-byte formula pitch leaks ~79k magenta
    /// pixels, with the wide 3072-byte pitch ~0.
    /// </summary>
    [Fact]
    public void WidePitch_IsUsedByTheSwizzle_SoPaddingDoesNotLeak()
    {
        if (!TestPaths.Exists(TestPaths.UiCoreDat)) return;

        var document = BankDocument.Load(TestPaths.UiCoreDat);
        var texture = document.AllTextures.FirstOrDefault(t => t.Header.Width == 1026 && t.Header.Height == 1026);
        Assert.NotNull(texture);
        Assert.True(texture!.Layout.WidePitchApplied);

        var image = TextureCodec.Decode(texture!, 0, 0, 0, out var error);
        Assert.NotNull(image);
        Assert.Null(error);

        long magenta = 0;
        var pixels = image!.Pixels;
        for (int i = 0; i + 3 < pixels.Length; i += 4)
            if (pixels[i] > 200 && pixels[i + 1] < 80 && pixels[i + 2] > 200)
                magenta++;

        Assert.True(magenta < 500,
            $"{magenta} magenta padding pixels leaked into the decode; the swizzle ignored the " +
            "wide pitch (expected fewer than 500)");

        // ... and the image must not be blank, which would also satisfy the check above.
        Assert.True(MeanHorizontalDelta(image) > 1.5, "decoded image looks blank");
    }

    [Fact]
    public void ExplicitPitch_ChangesTheMappingAndStillRoundTrips()
    {
        const int widthBlocks = 171, heightBlocks = 171, bpp = 16, blockHeight = 8;
        var linear = new byte[widthBlocks * heightBlocks * bpp];
        for (int i = 0; i < linear.Length; i++) linear[i] = (byte)(i * 7 + 3);

        // Two different strides must produce different surfaces.
        var narrow = new byte[GobSwizzle.SurfaceSize(widthBlocks, heightBlocks, bpp, blockHeight, 2752)];
        var wide = new byte[GobSwizzle.SurfaceSize(widthBlocks, heightBlocks, bpp, blockHeight, 3072)];
        GobSwizzle.Swizzle(linear, narrow, widthBlocks, heightBlocks, bpp, blockHeight, 2752);
        GobSwizzle.Swizzle(linear, wide, widthBlocks, heightBlocks, bpp, blockHeight, 3072);
        Assert.False(narrow.AsSpan().SequenceEqual(wide), "an explicit pitch must change the layout");

        // And each must be an exact inverse of its own deswizzle.
        foreach (var (surface, pitch) in new[] { (narrow, 2752), (wide, 3072) })
        {
            var back = new byte[linear.Length];
            GobSwizzle.Deswizzle(surface, back, widthBlocks, heightBlocks, bpp, blockHeight, pitch);
            Assert.Equal(linear, back);
        }
    }

    /// <summary>Mean absolute difference between horizontal neighbours.</summary>
    private static double MeanHorizontalDelta(RgbaImage image)
    {
        long total = 0;
        long count = 0;
        for (int y = 0; y < image.Height; y += 4)
        {
            for (int x = 0; x + 4 < image.Width; x += 4)
            {
                int a = (y * image.Width + x) * 4;
                int b = a + 4;
                total += Math.Abs(image.Pixels[a] - image.Pixels[b])
                       + Math.Abs(image.Pixels[a + 1] - image.Pixels[b + 1])
                       + Math.Abs(image.Pixels[a + 2] - image.Pixels[b + 2]);
                count += 3;
            }
        }
        return count == 0 ? 0 : (double)total / count;
    }

    [Fact]
    public void MultiMipAnomaly_IsExplainedByWidePitch()
    {
        // pl0080.dat: ASTC 6x6 1024x1024 / 10 mips. The earlier implementation could only
        // back-solve a single mip, so this multi-mip chain stayed flagged; tile_width_spacing = 4
        // now computes it exactly.
        var result = SurfaceLayout.Compute(
            1024, 1024, 1, 10, TextureType.T2D, 0x403,
            FormatTable.Get(Xt1Format.Astc6x6Unorm), storedImageSize: 915456);

        Assert.True(result.SizeMatches, result.Note);
        Assert.Equal(915456, result.ComputedImageSize);
        Assert.True(result.WidePitchApplied);
        Assert.False(result.PitchRecoveredFromStoredSize);
    }

    /// <summary>
    /// em305c.dat #2: ASTC 5x5 2050x2050, 11 mips, layout 0x403. This is the exact chain the
    /// wide-pitch rule must reproduce, including the later mips that stay "small".
    /// </summary>
    [Fact]
    public void WidePitch_ElevenMipChain_MatchesEm305c()
    {
        var result = SurfaceLayout.Compute(
            2050, 2050, 1, 11, TextureType.T2D, 0x403,
            FormatTable.Get(Xt1Format.Astc5x5Unorm), storedImageSize: 4596736);

        Assert.True(result.SizeMatches, result.Note);
        Assert.True(result.WidePitchApplied);
        Assert.Equal(new[] { 7168, 4096, 2048, 832, 448, 256, 128, 64, 64, 64, 64 },
            result.Mips.Select(m => m.Pitch).ToArray());
        Assert.Equal(new long[] { 3211264, 1048576, 262144, 53248, 14336, 4096, 1024, 512, 512, 512, 512 },
            result.Mips.Select(m => (long)m.SliceSurfaceSize).ToArray());
        Assert.Equal(new long[] { 0, 3211264, 4259840, 4521984, 4575232, 4589568, 4593664, 4594688, 4595200, 4595712, 4596224 },
            result.Mips.Select(m => m.Offset).ToArray());
        Assert.Equal(4596736L, result.ComputedImageSize);
    }
}
