using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using AstcSharp;
using AstcSharp.Core;
using Xt1.Core.Codecs;
using Xt1.Core.Format;
using Xt1.Core.Imaging;

namespace Xt1.Core.Encoding;

/// <summary>Zero-dependency encoder: BCnEncoder.Net for BC1-BC5/BC7, AstcSharp for ASTC, raw copy for RGBA8.</summary>
public sealed class ManagedEncoder : ITextureEncoder
{
    public string Name => "Managed";

    public bool Supports(FormatInfo format) => ManagedFormatMap.SupportsEncode(format);

    public EncodeResult Encode(RgbaImage image, FormatInfo format, bool srgb, EncodeQuality quality)
    {
        try
        {
            return format.Codec switch
            {
                CodecKind.Raw => EncodeResult.Ok(EncodeRaw(image, format)),
                CodecKind.Bc => EncodeResult.Ok(EncodeBc(image, format, quality)),
                CodecKind.Astc => EncodeResult.Ok(EncodeAstc(image, format)),
                _ => EncodeResult.Fail($"format {format.Name} is not supported by the managed encoder"),
            };
        }
        catch (Exception ex)
        {
            return EncodeResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Uncompressed targets. 32bpp formats keep the RGBA bytes; the 8-bit "af" format collapses to
    /// a single channel. Which channel a game's single-channel texture stores is not confirmed, so
    /// the red channel is used (import a grayscale image to get the expected result).
    /// </summary>
    private static byte[] EncodeRaw(RgbaImage image, FormatInfo format)
    {
        if (format.BytesPerBlock == 1 && format.BlockWidth == 1 && format.BlockHeight == 1)
        {
            var plane = new byte[checked(image.Width * image.Height * image.Depth)];
            var pixels = image.Pixels;
            for (int i = 0; i < plane.Length; i++)
                plane[i] = pixels[i * 4];
            return plane;
        }

        if (format.BytesPerBlock == 4 && format.BlockWidth == 1 && format.BlockHeight == 1)
        {
            var copy = (byte[])image.Pixels.Clone();
            if (format.Format == Xt1Format.Bgra8Unorm)
            {
                for (int i = 0; i + 3 < copy.Length; i += 4)
                    (copy[i], copy[i + 2]) = (copy[i + 2], copy[i]);
            }
            return copy;
        }

        throw new NotSupportedException(
            $"{format.Name} is {format.BytesPerBlock} bytes per block; the managed raw encoder handles 1 and 4");
    }

    private static byte[] EncodeBc(RgbaImage image, FormatInfo format, EncodeQuality quality)
    {
        if (!ManagedFormatMap.TryGetEncodeFormat(format, out var compression))
            throw new NotSupportedException($"managed BC encoder cannot handle {format.Name}");

        // BC1 has an alpha variant; every other format is unambiguous.
        if (compression == CompressionFormat.Bc1 && image.HasTransparency())
            compression = CompressionFormat.Bc1WithAlpha;

        var encoder = new BcEncoder(compression);
        encoder.OutputOptions.GenerateMipMaps = false;
        encoder.OutputOptions.Quality = quality switch
        {
            EncodeQuality.Fastest => CompressionQuality.Fast,
            EncodeQuality.Highest => CompressionQuality.BestQuality,
            _ => CompressionQuality.Balanced,
        };

        return encoder.EncodeToRawBytes(image.Pixels, image.Width, image.Height, PixelFormat.Rgba32,
            mipLevel: 0, out _, out _);
    }

    private static byte[] EncodeAstc(RgbaImage image, FormatInfo format)
    {
        int index = format.AstcFootprintIndex;
        if (index < 0) throw new InvalidOperationException("not an ASTC format");
        var footprint = Footprint.FromFootprintType((FootprintType)index);

        using var source = new MemoryStream(image.Pixels, writable: false);
        using var destination = new MemoryStream();
        AstcEncoder.CompressImage(source, destination, image.Width, image.Height, footprint);
        return destination.ToArray();
    }
}
