using Xt1.Core.Codecs;
using Xt1.Core.Containers;
using Xt1.Core.Encoding;
using Xt1.Core.Format;
using Xt1.Core.Imaging;
using Xt1.Core.Xt1;
using Xunit;

namespace Xt1.Core.Tests;

/// <summary>
/// Format 0x01, codec tag "af". Previously assumed to be an opaque second compression layer with
/// no applicable layout; it is in fact a single 8-bit channel, 1 byte per pixel, uncompressed and
/// block-linear like every other format.
/// </summary>
public class AfFormatTests
{
    private static readonly FormatInfo Format = FormatTable.Get(Xt1Format.AfL8Unorm)!;

    [Fact]
    public void Format_is_a_one_byte_raw_codec()
    {
        Assert.Equal(Xt1Format.AfL8Unorm, Format.Format);
        Assert.Equal(CodecKind.Raw, Format.Codec);
        Assert.Equal(1, Format.BytesPerBlock);
        Assert.Equal(1, Format.BlockWidth);
        Assert.Equal(1, Format.BlockHeight);
        Assert.True(Format.Verified);
        Assert.True(TextureCodec.CanDecode(Format));
        Assert.True(TextureCodec.CanEncode(Format));
    }

    [Fact]
    public void DecodesAsGrayscale()
    {
        // One byte per pixel, deswizzled, replicated to RGB with opaque alpha.
        var header = EditableTexture.CreateNew(Xt1Format.AfL8Unorm, 8, 8, layout: 1).Header;
        var size = SurfaceLayout.Compute(header).ComputedImageSize;
        var payload = new byte[size];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 3);

        var texture = EditableTexture.CreateNew(Xt1Format.AfL8Unorm, 8, 8, layout: 1);
        texture.Payload = payload;
        texture.PayloadSize = (uint)payload.Length;
        texture.Header.ImageSize = (ulong)payload.Length;

        var image = TextureCodec.Decode(texture, 0, 0, 0, out var error);
        Assert.NotNull(image);
        Assert.Null(error);
        Assert.Equal(8, image!.Width);
        Assert.Equal(8, image.Height);

        for (int i = 0; i < image.Pixels.Length; i += 4)
        {
            Assert.Equal(image.Pixels[i], image.Pixels[i + 1]);
            Assert.Equal(image.Pixels[i], image.Pixels[i + 2]);
            Assert.Equal(255, image.Pixels[i + 3]);
        }
    }

    [Fact]
    public void EncodeDecodeRoundTripsExactly()
    {
        var image = new RgbaImage(64, 32);
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                byte v = (byte)((x * 4) ^ (y * 8));
                image.SetPixel(x, y, v, v, v, 255);
            }
        }

        var encoded = new ManagedEncoder().Encode(image, Format, srgb: false, EncodeQuality.Normal);
        Assert.True(encoded.Success, encoded.Error);
        Assert.Equal(64 * 32, encoded.Data.Length);

        var decoded = TextureCodec.DecodeLinear(encoded.Data, 64, 32, Format, out var error);
        Assert.NotNull(decoded);
        Assert.Null(error);
        Assert.Equal(image.Pixels, decoded!.Pixels); // lossless
    }

    [Fact]
    public void RealUiCoreAfTexture_DecodesAndMatchesItsLayout()
    {
        if (!TestPaths.Exists(TestPaths.UiCoreDat)) return;

        var document = BankDocument.Load(TestPaths.UiCoreDat);
        var texture = document.AllTextures.FirstOrDefault(t => t.FormatInfo?.Format == Xt1Format.AfL8Unorm);
        Assert.NotNull(texture);

        // It is no longer reported as "no layout".
        Assert.True(texture!.Layout.Applicable);
        Assert.True(texture.Layout.SizeMatches, texture.Layout.Note);
        Assert.Equal(1048576L, texture.Layout.ComputedImageSize);
        Assert.Equal(1048576UL, texture.Header.ImageSize);

        var image = TextureCodec.Decode(texture, 0, 0, 0, out var error);
        Assert.NotNull(image);
        Assert.Null(error);
        Assert.Equal(1024, image!.Width);
        Assert.Equal(1024, image.Height);
    }

    [Fact]
    public void RealEffectAfTextures_Decode()
    {
        var path = Path.Combine(TestPaths.EffectTextureDirectory, "055.wta");
        if (!File.Exists(path)) return;

        var document = BankDocument.Load(path);
        var afTextures = document.AllTextures.Where(t => t.FormatInfo?.Format == Xt1Format.AfL8Unorm).ToList();
        Assert.Equal(3, afTextures.Count);

        foreach (var texture in afTextures)
        {
            Assert.True(texture.Layout.Applicable);
            Assert.True(texture.Layout.SizeMatches, $"#{texture.Index}: {texture.Layout.Note}");

            var image = TextureCodec.Decode(texture, 0, 0, 0, out var error);
            Assert.NotNull(image);
            Assert.Null(error);
            Assert.Equal((int)texture.Header.Width, image!.Width);
            Assert.Equal((int)texture.Header.Height, image.Height);
        }

        // The two 128x128 masks are stored as constant planes.
        var masks = afTextures.Where(t => t.Header.Width == 128).OrderBy(t => t.Index).ToList();
        Assert.Equal(25600UL, masks[0].Header.ImageSize);
        var first = TextureCodec.Decode(masks[0], 0, 0, 0, out _)!;
        Assert.All(Enumerable.Range(0, 16), i => Assert.Equal(255, first.Pixels[i * 4]));
    }
}
