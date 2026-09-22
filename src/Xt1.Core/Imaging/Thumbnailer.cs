using Xt1.Core.Codecs;
using Xt1.Core.Containers;
using Xt1.Core.Encoding;

namespace Xt1.Core.Imaging;

/// <summary>What to render for a thumbnail. A null <see cref="Mip"/> means "pick the cheapest level".</summary>
public readonly record struct ThumbnailOptions(int Size = Thumbnailer.DefaultSize, int? Mip = null,
    int Face = 0, int Slice = 0);

/// <summary>What the thumbnailer actually produced, for logs and tests.</summary>
public readonly record struct ThumbnailReport(
    int Mip, int SourceWidth, int SourceHeight, int OutputWidth, int OutputHeight, bool ToneMapped)
{
    public override string ToString() =>
        $"mip {Mip}: {SourceWidth}x{SourceHeight} -> {OutputWidth}x{OutputHeight}" +
        (ToneMapped ? ", tonemapped" : string.Empty);
}

/// <summary>
/// Renders a texture straight to a thumbnail-sized image. The mip level that still covers the
/// requested size is decoded instead of the base level, so a 4096-wide texture costs a 256-wide
/// decode - roughly 1/256th of the pixels - rather than a full decode followed by a downscale.
/// Nothing is ever upscaled: a texture smaller than the request comes back at its own size.
/// </summary>
public static class Thumbnailer
{
    /// <summary>Explorer's large-icon (256px) size, and a sensible default for any other caller.</summary>
    public const int DefaultSize = 256;

    /// <summary>Cap on the requested size; XT1 dimensions never exceed this in the game's data.</summary>
    public const int MaxSize = 4096;

    /// <summary>
    /// The highest mip level whose larger dimension still covers <paramref name="targetSize"/>: the
    /// cheapest level that reaches the target by downscaling. Base level when no mip covers it (that
    /// is the only level to use then) or when the chain has a single level.
    /// </summary>
    public static int PickMip(EditableTexture texture, int targetSize)
    {
        var mips = texture.Layout.Mips;
        int target = Math.Clamp(targetSize, 1, MaxSize);
        int chosen = 0;
        for (int i = 1; i < mips.Count; i++)
        {
            if (Math.Max(mips[i].Width, mips[i].Height) >= target)
                chosen = i;
        }
        return chosen;
    }

    /// <summary>
    /// Decode and scale one texture. Returns null with <paramref name="error"/> set when the format
    /// has no decoder or the requested mip is out of range; a non-null image can still carry a
    /// warning (a short payload decodes zero-filled).
    /// </summary>
    public static RgbaImage? Render(EditableTexture texture, ThumbnailOptions options,
        out ThumbnailReport report, out string? error)
    {
        report = default;
        error = null;

        var format = texture.FormatInfo;
        if (format is null)
        {
            error = "unknown format";
            return null;
        }

        var mips = texture.Layout.Mips;
        if (!texture.Layout.Applicable || mips.Count == 0)
        {
            error = "layout unavailable";
            return null;
        }

        int size = Math.Clamp(options.Size <= 0 ? DefaultSize : options.Size, 1, MaxSize);
        int mip = options.Mip ?? PickMip(texture, size);
        if (mip < 0 || mip >= mips.Count)
        {
            error = $"mip {mip} is out of range (texture has {mips.Count})";
            return null;
        }

        bool toneMapped = TextureCodec.IsHdr(format);
        RgbaImage? source;
        if (toneMapped)
        {
            var hdr = TextureCodec.DecodeHdr(texture, mip, options.Face, options.Slice, out error);
            source = hdr is null ? null : ToneMapHdr(hdr);
        }
        else
        {
            source = TextureCodec.Decode(texture, mip, options.Face, options.Slice, out error);
        }

        if (source is null) return null;

        var output = FitToSize(source, size);
        report = new ThumbnailReport(mip, source.Width, source.Height, output.Width, output.Height, toneMapped);
        return output;
    }

    /// <summary>
    /// Scale an image down so its larger dimension is at most <paramref name="maxSize"/>, keeping the
    /// aspect ratio. An image that already fits is returned as-is, so callers can use this to enforce
    /// a ceiling without ever inventing pixels.
    /// </summary>
    public static RgbaImage FitToSize(RgbaImage image, int maxSize)
    {
        int longest = Math.Max(image.Width, image.Height);
        if (maxSize <= 0 || longest <= maxSize) return image;

        double scale = (double)maxSize / longest;
        int width = Math.Max(1, (int)Math.Round(image.Width * scale));
        int height = Math.Max(1, (int)Math.Round(image.Height * scale));
        if (width == image.Width && height == image.Height) return image;

        // The same filter the mip chain uses, so a thumbnail's downscale matches a generated mip.
        return MipChain.Downsample(image, width, height, image.IsSrgb);
    }

    /// <summary>
    /// Reinhard tone map at exposure 1 (the viewport's default) followed by the sRGB encode, so an
    /// HDR (BC6H) thumbnail reads the same as the GL preview does.
    /// </summary>
    public static RgbaImage ToneMapHdr(FloatImage image)
    {
        var result = new RgbaImage(image.Width, image.Height) { IsSrgb = true };
        int texels = image.Width * image.Height;
        for (int i = 0; i < texels; i++)
        {
            for (int c = 0; c < 3; c++)
            {
                float value = MathF.Max(image.Pixels[i * 4 + c], 0f);
                float mapped = value / (1f + value);
                result.Pixels[i * 4 + c] = Srgb.FromLinear((int)MathF.Round(mapped * 65535f));
            }

            float alpha = Math.Clamp(image.Pixels[i * 4 + 3], 0f, 1f);
            result.Pixels[i * 4 + 3] = (byte)MathF.Round(alpha * 255f);
        }
        return result;
    }
}
