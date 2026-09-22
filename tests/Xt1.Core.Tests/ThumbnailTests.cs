using Xt1.Core.Containers;
using Xt1.Core.Editing;
using Xt1.Core.Encoding;
using Xt1.Core.Format;
using Xt1.Core.Imaging;
using Xt1.Core.Xt1;
using Xunit;

namespace Xt1.Core.Tests;

/// <summary>
/// A thumbnail must decode the cheapest mip that still covers the requested size (a 4096-wide
/// texture should cost a 256-wide decode, not a full one plus a downscale), never upscale, and keep
/// the aspect ratio. These lock that choice and the scaling around it.
/// </summary>
public class ThumbnailTests
{
    /// <summary>A BC3 texture with a generated full mip chain, so the levels really exist.</summary>
    private static EditableTexture BuildTexture(int width, int height)
    {
        var info = FormatTable.Get(Xt1Format.Bc3Unorm)!;
        var texture = EditableTexture.CreateNew(info.Format, (uint)width, (uint)height);
        var image = new RgbaImage(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image.SetPixel(x, y,
                    (byte)(width == 1 ? 0 : x * 255 / (width - 1)),
                    (byte)(height == 1 ? 0 : y * 255 / (height - 1)),
                    128, 255);
            }
        }

        var result = TextureOperations.Replace2D(texture, image, new ManagedEncoder(),
            generateMips: true, EncodeQuality.Normal);
        Assert.True(result.Success, result.Error);
        Assert.True(texture.Layout.Mips.Count > 1, "the mip chain was not generated");
        return texture;
    }

    [Fact]
    public void PickMip_ChoosesTheCheapestLevelThatStillCoversTheTarget()
    {
        var texture = BuildTexture(256, 256);
        int last = texture.Layout.Mips.Count - 1;

        // 256..1 is 9 levels, so mip 2 is 64x64 and mip 8 is 1x1.
        Assert.Equal(0, Thumbnailer.PickMip(texture, 256));
        Assert.Equal(1, Thumbnailer.PickMip(texture, 128));
        Assert.Equal(2, Thumbnailer.PickMip(texture, 64));

        // A target between two levels takes the larger one and is downscaled from there.
        Assert.Equal(2, Thumbnailer.PickMip(texture, 48));
        Assert.Equal(3, Thumbnailer.PickMip(texture, 32));

        // The smallest target bottoms out at the last level rather than overflowing the chain.
        Assert.Equal(last, Thumbnailer.PickMip(texture, 1));
    }

    [Fact]
    public void Render_DecodesTheCheapMip_NotTheBaseLevel()
    {
        var texture = BuildTexture(512, 512);
        var image = Thumbnailer.Render(texture, new ThumbnailOptions(256), out var report, out var error);

        Assert.NotNull(image);
        Assert.Null(error);
        Assert.Equal(1, report.Mip);           // 256x256 decoded instead of the 512x512 base level
        Assert.Equal(256, report.SourceWidth);
        Assert.Equal(256, image!.Width);
        Assert.Equal(256, image.Height);
        Assert.False(report.ToneMapped);
    }

    [Fact]
    public void Render_DownscalesWhenTheChosenMipIsStillLarger()
    {
        var texture = BuildTexture(256, 256);
        var image = Thumbnailer.Render(texture, new ThumbnailOptions(48), out var report, out var error);

        Assert.NotNull(image);
        Assert.Null(error);
        Assert.Equal(2, report.Mip);           // 64x64, the cheapest level covering 48
        Assert.Equal(64, report.SourceWidth);
        Assert.Equal(48, image!.Width);
        Assert.Equal(48, image.Height);
    }

    [Fact]
    public void Render_NeverUpscales_ASmallerTexture()
    {
        var texture = BuildTexture(32, 32);
        var image = Thumbnailer.Render(texture, new ThumbnailOptions(256), out var report, out var error);

        Assert.NotNull(image);
        Assert.Null(error);
        Assert.Equal(0, report.Mip);
        Assert.Equal(32, image!.Width);
        Assert.Equal(32, image.Height);
    }

    [Fact]
    public void Render_HonoursAForcedMip()
    {
        var texture = BuildTexture(256, 256);
        var image = Thumbnailer.Render(texture, new ThumbnailOptions(64, Mip: 0), out var report, out var error);

        Assert.NotNull(image);
        Assert.Null(error);
        Assert.Equal(0, report.Mip);
        Assert.Equal(256, report.SourceWidth);
        Assert.Equal(64, image!.Width);
    }

    [Fact]
    public void Render_KeepsTheAspectRatio()
    {
        var texture = BuildTexture(256, 128);
        var image = Thumbnailer.Render(texture, new ThumbnailOptions(64), out var report, out var error);

        Assert.NotNull(image);
        Assert.Null(error);
        Assert.Equal(2, report.Mip);           // 64x32
        Assert.Equal(64, image!.Width);
        Assert.Equal(32, image.Height);
    }

    [Fact]
    public void Render_RejectsAnOutOfRangeMip()
    {
        var texture = BuildTexture(64, 64);
        var image = Thumbnailer.Render(texture, new ThumbnailOptions(64, Mip: 99), out _, out var error);

        Assert.Null(image);
        Assert.Contains("out of range", error);
    }

    [Fact]
    public void Render_RejectsAFormatWithNoLayout()
    {
        var texture = EditableTexture.CreateNew((Xt1Format)0x1234, 64, 64);
        var image = Thumbnailer.Render(texture, new ThumbnailOptions(64), out _, out var error);

        Assert.Null(image);
        Assert.Equal("unknown format", error);
    }

    [Fact]
    public void FitToSize_ReturnsTheSameImageWhenItAlreadyFits()
    {
        var image = new RgbaImage(200, 100);
        Assert.Same(image, Thumbnailer.FitToSize(image, 256));

        var scaled = Thumbnailer.FitToSize(image, 64);
        Assert.Equal(64, scaled.Width);
        Assert.Equal(32, scaled.Height);
    }

    [Fact]
    public void FitToSize_AveragesAlphaInsteadOfDroppingIt()
    {
        var image = new RgbaImage(2, 2);
        image.SetPixel(0, 0, 255, 0, 0, 255);
        image.SetPixel(1, 0, 0, 0, 255, 0);
        image.SetPixel(0, 1, 255, 0, 0, 255);
        image.SetPixel(1, 1, 0, 0, 255, 0);

        var scaled = Thumbnailer.FitToSize(image, 1);

        Assert.Equal(1, scaled.Width);
        Assert.InRange(scaled.GetPixel(0, 0).A, 120, 136);
    }

    [Fact]
    public void ToneMapHdr_MapsHighlightsAndEncodesSrgb()
    {
        var hdr = new FloatImage(3, 1);
        hdr.SetPixel(0, 0, 0f, 0f, 0f);
        hdr.SetPixel(1, 0, 1f, 1f, 1f);
        hdr.SetPixel(2, 0, 16f, 16f, 16f);

        var mapped = Thumbnailer.ToneMapHdr(hdr);

        // Reinhard: 0 -> 0, 1 -> 0.5, 16 -> 0.94; then the sRGB encode (0.5 -> 188).
        Assert.Equal(0, mapped.GetPixel(0, 0).R);
        Assert.Equal(188, mapped.GetPixel(1, 0).R);
        Assert.True(mapped.GetPixel(2, 0).R > mapped.GetPixel(1, 0).R);
        Assert.Equal(255, mapped.GetPixel(2, 0).A);
        Assert.True(mapped.IsSrgb);
    }
}
