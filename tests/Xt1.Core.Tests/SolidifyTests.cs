using Xt1.Core.Imaging;
using Xunit;

namespace Xt1.Core.Tests;

/// <summary>Alpha bleeding ("solidify" / edge pad) used by the image import path.</summary>
public class SolidifyTests
{
    private static RgbaImage Solid(byte r, byte g, byte b, int w = 16, int h = 16, byte a = 255)
    {
        var image = new RgbaImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                image.SetPixel(x, y, r, g, b, a);
        return image;
    }

    [Fact]
    public void Apply_FillsTransparentHoleWithNearestOpaqueColour()
    {
        var image = Solid(200, 30, 40);
        for (int y = 6; y < 10; y++)
            for (int x = 6; x < 10; x++)
                image.SetPixel(x, y, 0, 0, 0, 0);

        var result = Solidify.Apply(image);

        // The hole gets the surrounding colour but keeps its (transparent) alpha.
        for (int y = 6; y < 10; y++)
        {
            for (int x = 6; x < 10; x++)
            {
                var (r, g, b, a) = result.GetPixel(x, y);
                Assert.Equal(0, a);
                Assert.True(Math.Abs(r - 200) <= 6, $"r was {r}");
                Assert.True(Math.Abs(g - 30) <= 6, $"g was {g}");
                Assert.True(Math.Abs(b - 40) <= 6, $"b was {b}");
            }
        }

        // Opaque pixels are untouched, and the source is not modified in place.
        Assert.Equal((byte)255, result.GetPixel(0, 0).A);
        Assert.Equal((byte)0, image.GetPixel(7, 7).A);
    }

    [Fact]
    public void Apply_PullsFromTheNearestRegion()
    {
        var image = Solid(10, 220, 10);           // green
        for (int y = 0; y < 16; y++)
            for (int x = 8; x < 16; x++)
                image.SetPixel(x, y, 10, 10, 220, 255); // blue right half
        for (int y = 0; y < 16; y++)
            for (int x = 7; x < 9; x++)
                image.SetPixel(x, y, 0, 0, 0, 0);      // transparent seam

        var result = Solidify.Apply(image);

        var left = result.GetPixel(7, 8);
        var right = result.GetPixel(8, 8);
        Assert.Equal(0, left.A);
        Assert.Equal(0, right.A);
        Assert.True(left.G > left.B, "the left seam should take the green side's colour");
        Assert.True(right.B > right.G, "the right seam should take the blue side's colour");
    }

    [Fact]
    public void Apply_LeavesFullyTransparentAndFullyOpaqueImagesAlone()
    {
        var empty = Solid(12, 34, 56, a: 0);
        var emptyResult = Solidify.Apply(empty);
        Assert.Equal((byte)0, emptyResult.GetPixel(3, 3).A);
        Assert.Equal((byte)12, emptyResult.GetPixel(3, 3).R);

        var opaque = Solid(12, 34, 56);
        var opaqueResult = Solidify.Apply(opaque);
        Assert.Equal(opaque.Pixels, opaqueResult.Pixels);
    }

    [Fact]
    public void Process_SolidifiesBeforeMakingOpaque()
    {
        var image = Solid(200, 30, 40);
        for (int y = 6; y < 10; y++)
            for (int x = 6; x < 10; x++)
                image.SetPixel(x, y, 0, 0, 0, 0);

        var both = Solidify.Process(image, solidify: true, makeOpaque: true);
        for (int y = 6; y < 10; y++)
        {
            for (int x = 6; x < 10; x++)
            {
                var (r, _, _, a) = both.GetPixel(x, y);
                Assert.Equal(255, a);
                Assert.True(r > 100, $"the former hole must keep the filled colour, r={r}");
            }
        }

        // The opposite order (opaque first) would fill from an all-opaque image and leave it black,
        // which is exactly the regression this ordering guard prevents.
        var opaqueFirst = Solidify.Process(image, solidify: false, makeOpaque: true);
        Assert.Equal(255, opaqueFirst.GetPixel(7, 7).A);
        Assert.Equal(0, opaqueFirst.GetPixel(7, 7).R);
    }

    [Fact]
    public void MakeOpaque_SetsEveryAlphaTo255AndKeepsColour()
    {
        var image = Solid(90, 120, 150, a: 128);
        image.SetPixel(0, 0, 1, 2, 3, 0);

        var result = Solidify.MakeOpaque(image);

        for (int y = 0; y < result.Height; y++)
            for (int x = 0; x < result.Width; x++)
                Assert.Equal(255, result.GetPixel(x, y).A);
        Assert.Equal((byte)1, result.GetPixel(0, 0).R);
        Assert.Equal((byte)0, image.GetPixel(0, 0).A); // source untouched
    }
}
