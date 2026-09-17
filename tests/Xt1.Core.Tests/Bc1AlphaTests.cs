using Xt1.Core.Codecs;
using Xt1.Core.Encoding;
using Xt1.Core.Format;
using Xt1.Core.Imaging;
using Xunit;

namespace Xt1.Core.Tests;

/// <summary>
/// BC1 is stored as DXT1A by the game when a texture has cutout alpha. Decoding it as the 4-colour
/// BC1 variant silently drops that alpha, so these lock the 1-bit-alpha interpretation.
/// </summary>
public class Bc1AlphaTests
{
    [Fact]
    public void Bc1_CutoutAlpha_SurvivesEncodeDecode()
    {
        var info = FormatTable.Get(Xt1Format.Bc1Unorm)!;
        var image = new RgbaImage(32, 32);
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                bool transparent = x < 16;
                image.SetPixel(x, y, 200, 40, 40, transparent ? (byte)0 : (byte)255);
            }
        }

        var encoded = new ManagedEncoder().Encode(image, info, srgb: false, EncodeQuality.Normal);
        Assert.True(encoded.Success, encoded.Error);

        var decoded = TextureCodec.DecodeLinear(encoded.Data, 32, 32, info, out var error);
        Assert.NotNull(decoded);
        Assert.Null(error);

        // The transparent half must decode to alpha 0, the opaque half to 255.
        Assert.Equal(0, decoded!.GetPixel(4, 4).A);
        Assert.Equal(255, decoded.GetPixel(28, 4).A);
    }

    [Fact]
    public void Bc1_OpaqueData_StaysOpaque()
    {
        var info = FormatTable.Get(Xt1Format.Bc1Unorm)!;
        var image = new RgbaImage(32, 32);
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
                image.SetPixel(x, y, (byte)(x * 8), (byte)(y * 8), 64, 255);

        var encoded = new ManagedEncoder().Encode(image, info, srgb: false, EncodeQuality.Normal);
        Assert.True(encoded.Success, encoded.Error);

        var decoded = TextureCodec.DecodeLinear(encoded.Data, 32, 32, info, out _);
        Assert.NotNull(decoded);
        for (int y = 0; y < 32; y += 7)
            for (int x = 0; x < 32; x += 7)
                Assert.Equal(255, decoded!.GetPixel(x, y).A);
    }
}
