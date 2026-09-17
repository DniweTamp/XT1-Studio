using Xt1.Core.Codecs;
using Xt1.Core.Format;
using Xt1.Core.Imaging;
using Xt1.Core.Nvtt;
using Xt1.Core.Xt1;
using Xunit;

namespace Xt1.Core.Tests;

public class NvttTests
{
    [Fact]
    public void AstcFormatMapping_IsOneToOneInDocumentedOrder()
    {
        for (int i = 0; i < 14; i++)
        {
            var unorm = FormatTable.Get((uint)(0x79 + i))!;
            var srgb = FormatTable.Get((uint)(0x87 + i))!;
            Assert.Equal((NvttFormat)((int)NvttFormat.AstcLdr4x4 + i), NvttFormatMap.ToNvtt(unorm.Format));
            Assert.Equal((NvttFormat)((int)NvttFormat.AstcLdr4x4 + i), NvttFormatMap.ToNvtt(srgb.Format));
        }
    }

    [Fact]
    public void BcFormatMapping_MatchesNvttEnum()
    {
        Assert.Equal(NvttFormat.Bc1, NvttFormatMap.ToNvtt(Xt1Format.Bc1Unorm));
        Assert.Equal(NvttFormat.Bc2, NvttFormatMap.ToNvtt(Xt1Format.Bc2Unorm));
        Assert.Equal(NvttFormat.Bc3, NvttFormatMap.ToNvtt(Xt1Format.Bc3Unorm));
        Assert.Equal(NvttFormat.Bc4, NvttFormatMap.ToNvtt(Xt1Format.Bc4Unorm));
        Assert.Equal(NvttFormat.Bc4, NvttFormatMap.ToNvtt(Xt1Format.Bc4UnormAlt));
        Assert.Equal(NvttFormat.Bc5, NvttFormatMap.ToNvtt(Xt1Format.Bc5Unorm));
        Assert.Equal(NvttFormat.Bc5, NvttFormatMap.ToNvtt(Xt1Format.Bc5UnormAlt));
        Assert.Equal(NvttFormat.Bc6U, NvttFormatMap.ToNvtt(Xt1Format.Bc6hUf16));
        Assert.Equal(NvttFormat.Bc7, NvttFormatMap.ToNvtt(Xt1Format.Bc7Unorm));
        Assert.Null(NvttFormatMap.ToNvtt(Xt1Format.Rgba8Unorm));
        Assert.Null(NvttFormatMap.ToNvtt(Xt1Format.AfL8Unorm));
    }

    private static RgbaImage MakeTestImage(int size)
    {
        var image = new RgbaImage(size, size);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                image.SetPixel(x, y, (byte)(x * 255 / (size - 1)), (byte)(y * 255 / (size - 1)),
                    (byte)((x ^ y) & 0xFF), 255);
        return image;
    }

    [Fact]
    public void Nvtt_EncodesBcAndAstc_WithCorrectBlockGeometry()
    {
        using var nvtt = NvttNative.TryCreate(out _);
        if (nvtt is null) return; // optional dependency

        var image = MakeTestImage(64);

        var bc1 = nvtt.EncodeSurface(image, NvttFormat.Bc1, NvttQuality.Normal, srgb: false);
        Assert.Equal(16 * 16 * 8, bc1.Length); // 64/4 blocks squared * 8 bytes

        var bc3 = nvtt.EncodeSurface(image, NvttFormat.Bc3, NvttQuality.Normal, srgb: false);
        Assert.Equal(16 * 16 * 16, bc3.Length);

        var bc7 = nvtt.EncodeSurface(image, NvttFormat.Bc7, NvttQuality.Normal, srgb: false);
        Assert.Equal(16 * 16 * 16, bc7.Length);

        var astc4 = nvtt.EncodeSurface(image, NvttFormat.AstcLdr4x4, NvttQuality.Normal, srgb: false);
        Assert.Equal(16 * 16 * 16, astc4.Length);

        var astc8 = nvtt.EncodeSurface(image, NvttFormat.AstcLdr8x8, NvttQuality.Normal, srgb: false);
        Assert.Equal(8 * 8 * 16, astc8.Length);
    }

    [Fact]
    public void Nvtt_EncodedBlocks_DecodeWithManagedDecoders()
    {
        using var nvtt = NvttNative.TryCreate(out _);
        if (nvtt is null) return;

        var image = MakeTestImage(64);

        var bc3 = nvtt.EncodeSurface(image, NvttFormat.Bc3, NvttQuality.Highest, srgb: false);
        var decodedBc3 = TextureCodec.DecodeLinear(bc3, 64, 64, FormatTable.Get(Xt1Format.Bc3Unorm)!, out var error);
        Assert.NotNull(decodedBc3);
        Assert.Null(error);

        var astc = nvtt.EncodeSurface(image, NvttFormat.AstcLdr4x4, NvttQuality.Normal, srgb: false);
        var decodedAstc = TextureCodec.DecodeLinear(astc, 64, 64, FormatTable.Get(Xt1Format.Astc4x4Unorm)!, out var astcError);
        Assert.NotNull(decodedAstc);
        Assert.Null(astcError);

        Assert.True(MeanAbsoluteError(image.Pixels, decodedBc3!.Pixels) < 24, "BC3 round-trip error too high");
        Assert.True(MeanAbsoluteError(image.Pixels, decodedAstc!.Pixels) < 24, "ASTC round-trip error too high");
    }

    [Fact]
    public void Nvtt_AllFourteenAstcFootprints_DecodeViaManagedDecoder()
    {
        using var nvtt = NvttNative.TryCreate(out _);
        if (nvtt is null) return;

        var image = MakeTestImage(96);
        for (int i = 0; i < FormatTable.AstcFootprintNames.Length; i++)
        {
            var info = FormatTable.Get((uint)(0x79 + i))!;
            var nvttFormat = (NvttFormat)((int)NvttFormat.AstcLdr4x4 + i);

            var encoded = nvtt.EncodeSurface(image, nvttFormat, NvttQuality.Normal, srgb: false);
            int expectedBlocks = info.WidthBlocks(96) * info.HeightBlocks(96);
            Assert.Equal(expectedBlocks * 16, encoded.Length);

            var decoded = TextureCodec.DecodeLinear(encoded, 96, 96, info, out var error);
            Assert.NotNull(decoded);
            Assert.Null(error);
            Assert.Equal(96, decoded!.Width);
            Assert.Equal(96, decoded.Height);
            Assert.True(MeanAbsoluteError(image.Pixels, decoded.Pixels) < 32,
                $"footprint {FormatTable.AstcFootprintNames[i]} round-trip error too high");
        }
    }

    private static double MeanAbsoluteError(byte[] a, byte[] b)
    {
        long total = 0;
        int count = Math.Min(a.Length, b.Length);
        for (int i = 0; i < count; i++) total += Math.Abs(a[i] - b[i]);
        return count == 0 ? double.MaxValue : (double)total / count;
    }
}
