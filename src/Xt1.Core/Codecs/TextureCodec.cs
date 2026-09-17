using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using AstcSharp;
using AstcSharp.Core;
using Xt1.Core.Containers;
using Xt1.Core.Format;
using Xt1.Core.Imaging;
using Xt1.Core.Xt1;

namespace Xt1.Core.Codecs;

/// <summary>
/// Decodes a texture's stored (block-linear) payload into RGBA8. BC1-BC7 go through
/// BCnEncoder.Net, ASTC through AstcSharp (managed, all 14 footprints), uncompressed formats
/// are copied directly. Formats without a decoder surface metadata only.
/// </summary>
public static class TextureCodec
{
    private static readonly BcDecoder BcDecoder = new();

    public static bool CanDecode(FormatInfo? format) =>
        format is not null && ManagedFormatMap.SupportsDecode(format);

    public static bool CanEncode(FormatInfo? format) =>
        format is not null && ManagedFormatMap.SupportsEncode(format);

    /// <summary>
    /// Extract and deswizzle one subresource (mip, cube face, 3D slice) into a linear block
    /// array. The result is always <c>widthBlocks * heightBlocks * bytesPerBlock</c> long;
    /// missing payload bytes are zero-filled.
    /// </summary>
    public static byte[] ExtractLinearBlocks(
        EditableTexture texture, int mip, int face, int slice, out MipInfo? mipInfo, out string? warning)
    {
        warning = null;
        mipInfo = null;
        var format = texture.FormatInfo;
        if (!CanDecode(format))
        {
            warning = "format has no decoder";
            return Array.Empty<byte>();
        }

        var layout = texture.Layout;
        if (!layout.Applicable || mip < 0 || mip >= layout.Mips.Count)
        {
            warning = "layout unavailable or mip out of range";
            return Array.Empty<byte>();
        }

        var info = layout.Mips[mip];
        mipInfo = info;

        // 3D surfaces interleave Z inside each block, so the whole volume has to be untiled at
        // once; taking one slice at a time produces the classic block-tile pattern.
        if (layout.Type.Is3D() && info.BlockDepth > 1)
        {
            var volume = ExtractLinearVolume(texture, mip, out var volumeInfo, out warning);
            if (volumeInfo is null) return Array.Empty<byte>();
            int sliceBlocks = checked(volumeInfo.WidthBlocks * volumeInfo.HeightBlocks * format!.BytesPerBlock);
            int index = Math.Clamp(slice, 0, volumeInfo.SliceCount - 1);
            var sliceBytes = new byte[sliceBlocks];
            int at = index * sliceBlocks;
            if (at + sliceBlocks <= volume.Length)
                volume.AsSpan(at, sliceBlocks).CopyTo(sliceBytes);
            return sliceBytes;
        }

        return ExtractSliceBlocks(texture, layout, info, face, slice, format!, out warning);
    }

    /// <summary>
    /// Deswizzle a whole 3D mip into linear order. The result is slice-major with
    /// <c>widthBlocks * heightBlocks * bytesPerBlock</c> bytes per slice, so slices are contiguous.
    /// </summary>
    public static byte[] ExtractLinearVolume(
        EditableTexture texture, int mip, out MipInfo? mipInfo, out string? warning)
    {
        warning = null;
        mipInfo = null;
        var format = texture.FormatInfo;
        if (!CanDecode(format))
        {
            warning = "format has no decoder";
            return Array.Empty<byte>();
        }

        var layout = texture.Layout;
        if (!layout.Applicable || mip < 0 || mip >= layout.Mips.Count)
        {
            warning = "layout unavailable or mip out of range";
            return Array.Empty<byte>();
        }

        var info = layout.Mips[mip];
        mipInfo = info;
        int bpb = format!.BytesPerBlock;
        var linear = new byte[checked(info.WidthBlocks * info.HeightBlocks * info.SliceCount * bpb)];

        long offset = info.Offset;
        var payload = texture.Payload;
        if (offset < 0 || offset >= payload.LongLength)
        {
            warning = "payload ends before this mip; missing bytes read as zero";
            return linear;
        }

        // The tiled volume pads depth up to a multiple of the block depth, which can exceed the
        // slice count the layout records.
        int sourceSize = Math.Max((int)info.TotalSize, GobSwizzle.VolumeSize(
            info.WidthBlocks, info.HeightBlocks, info.SliceCount, bpb, info.BlockHeight, info.BlockDepth,
            info.Pitch));

        int available = (int)Math.Min(sourceSize, payload.LongLength - offset);
        if (available < info.TotalSize)
            warning = "payload is shorter than the computed layout; missing bytes read as zero";

        var surface = new byte[sourceSize];
        payload.AsSpan((int)offset, available).CopyTo(surface);

        GobSwizzle.DeswizzleVolume(
            surface, linear,
            info.WidthBlocks, info.HeightBlocks, info.SliceCount,
            bpb, info.BlockHeight, info.BlockDepth, info.Pitch);

        return linear;
    }

    /// <summary>Deswizzle a single 2D surface (one slice of a block-depth-1 mip, or a cube face).</summary>
    private static byte[] ExtractSliceBlocks(
        EditableTexture texture, LayoutResult layout, MipInfo info, int face, int slice,
        FormatInfo format, out string? warning)
    {
        warning = null;
        int bpb = format.BytesPerBlock;
        var linear = new byte[checked(info.WidthBlocks * info.HeightBlocks * bpb)];

        long offset = info.Offset;
        if (layout.Type.IsCube())
            offset += layout.FaceOffset(face);
        if (layout.Type.Is3D())
        {
            if (slice < 0 || slice >= info.SliceCount) slice = 0;
            offset += (long)slice * info.SliceSurfaceSize;
        }

        var payload = texture.Payload;
        if (offset < 0 || offset >= payload.LongLength)
        {
            warning = offset == payload.LongLength
                ? "payload ends exactly at this subresource (empty)"
                : "payload is shorter than the computed layout; missing bytes read as zero";
            return linear;
        }

        int available = (int)Math.Min(info.SliceSurfaceSize, payload.LongLength - offset);
        if (available < info.SliceSurfaceSize)
            warning = "payload is shorter than the computed layout; missing bytes read as zero";

        // Deswizzle reads the whole surface, so a truncated payload is staged into a zero-filled
        // buffer first: missing bytes read as zero instead of throwing.
        var surface = new byte[info.SliceSurfaceSize];
        payload.AsSpan((int)offset, available).CopyTo(surface);

        GobSwizzle.Deswizzle(
            surface, linear,
            info.WidthBlocks, info.HeightBlocks, bpb, info.BlockHeight, info.Pitch);

        return linear;
    }

    /// <summary>Decode a linear block array (already deswizzled) into RGBA8.</summary>
    public static RgbaImage? DecodeLinear(byte[] linear, int width, int height, FormatInfo format, out string? error)
    {
        error = null;
        if (width <= 0 || height <= 0)
        {
            error = "invalid dimensions";
            return null;
        }

        try
        {
            switch (format.Codec)
            {
                case CodecKind.Raw:
                    return DecodeRaw(linear, width, height, format);
                case CodecKind.Bc:
                    return DecodeBc(linear, width, height, format);
                case CodecKind.Astc:
                    return DecodeAstc(linear, width, height, format);
                default:
                    error = $"format {format.Name} has no decoder";
                    return null;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Decode one subresource directly from a texture.</summary>
    public static RgbaImage? Decode(EditableTexture texture, int mip, int face, int slice, out string? error)
    {
        var format = texture.FormatInfo;
        if (format is null)
        {
            error = "unknown format";
            return null;
        }

        var linear = ExtractLinearBlocks(texture, mip, face, slice, out var info, out var warning);
        if (info is null)
        {
            error = warning;
            return null;
        }

        var image = DecodeLinear(linear, info.Width, info.Height, format, out error);
        if (image is not null)
        {
            image.IsSrgb = format.IsSrgb;
            if (warning is not null)
                error = warning;
        }
        return image;
    }

    /// <summary>True when the format decodes to linear float data that should be tone mapped.</summary>
    public static bool IsHdr(FormatInfo? format) =>
        format is not null &&
        ManagedFormatMap.TryGetDecodeFormat(format, out var compression) &&
        compression == CompressionFormat.Bc6U;

    /// <summary>
    /// Decode a linear block array to unclamped linear float (HDR). Only formats with an HDR
    /// decoder (BC6H) are supported; other formats return null so the caller uses the 8-bit path.
    /// </summary>
    public static FloatImage? DecodeLinearHdr(byte[] linear, int width, int height, FormatInfo format,
        out string? error)
    {
        error = null;
        if (width <= 0 || height <= 0)
        {
            error = "invalid dimensions";
            return null;
        }

        if (!ManagedFormatMap.TryGetDecodeFormat(format, out var compression) ||
            compression != CompressionFormat.Bc6U)
        {
            error = "format has no HDR decoder";
            return null;
        }

        try
        {
            var hdr = BcDecoder.DecodeRawHdr(linear, width, height, compression);
            var image = new FloatImage(width, height);
            for (int i = 0; i < width * height && i < hdr.Length; i++)
            {
                image.Pixels[i * 4 + 0] = hdr[i].r;
                image.Pixels[i * 4 + 1] = hdr[i].g;
                image.Pixels[i * 4 + 2] = hdr[i].b;
                image.Pixels[i * 4 + 3] = 1f;
            }
            return image;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Decode one subresource as linear float, or null when the format is not HDR.</summary>
    public static FloatImage? DecodeHdr(EditableTexture texture, int mip, int face, int slice, out string? error)
    {
        var format = texture.FormatInfo;
        if (format is null)
        {
            error = "unknown format";
            return null;
        }

        var linear = ExtractLinearBlocks(texture, mip, face, slice, out var info, out var warning);
        if (info is null)
        {
            error = warning;
            return null;
        }

        var image = DecodeLinearHdr(linear, info.Width, info.Height, format, out error);
        if (image is not null) error = warning;
        return image;
    }

    public static IReadOnlyList<FloatImage> DecodeCubeFacesHdr(EditableTexture texture, int mip, out string? error)
    {
        var faces = new List<FloatImage>(6);
        error = null;
        if (!IsHdr(texture.FormatInfo))
        {
            error = "format is not HDR";
            return faces;
        }

        for (int face = 0; face < 6; face++)
        {
            var image = DecodeHdr(texture, mip, face, 0, out var faceError);
            if (image is null)
            {
                error = $"face {face}: {faceError}";
                return faces;
            }
            faces.Add(image);
        }
        return faces;
    }

    public static IReadOnlyList<RgbaImage> DecodeCubeFaces(EditableTexture texture, int mip, out string? error)
    {
        var faces = new List<RgbaImage>(6);
        error = null;
        for (int face = 0; face < 6; face++)
        {
            var image = Decode(texture, mip, face, 0, out var faceError);
            if (image is null)
            {
                error = $"face {face}: {faceError}";
                return faces;
            }
            faces.Add(image);
        }
        return faces;
    }

    public static IReadOnlyList<RgbaImage> DecodeVolumeSlices(EditableTexture texture, int mip, int maxSlices, out string? error)
    {
        error = null;
        var result = new List<RgbaImage>();
        var format = texture.FormatInfo;
        if (format is null)
        {
            error = "unknown format";
            return result;
        }

        var layout = texture.Layout;
        if (!layout.Applicable || mip < 0 || mip >= layout.Mips.Count)
        {
            error = "layout unavailable";
            return result;
        }

        var info = layout.Mips[mip];
        int count = info.SliceCount;
        if (maxSlices > 0) count = Math.Min(count, maxSlices);

        // Interleaved Z: until the whole volume once and cut the slices out of it. Decoding slice
        // by slice would re-deswizzle the volume for every slice.
        if (info.BlockDepth > 1)
        {
            var volume = ExtractLinearVolume(texture, mip, out var volumeInfo, out var warning);
            if (volumeInfo is null)
            {
                error = warning;
                return result;
            }

            int sliceBytes = checked(volumeInfo.WidthBlocks * volumeInfo.HeightBlocks * format.BytesPerBlock);
            for (int z = 0; z < count; z++)
            {
                int at = z * sliceBytes;
                if (at < 0 || at + sliceBytes > volume.Length) break;

                var image = DecodeLinear(volume.AsSpan(at, sliceBytes).ToArray(),
                    info.Width, info.Height, format, out var sliceError);
                if (image is null)
                {
                    error = $"slice {z}: {sliceError}";
                    return result;
                }
                image.IsSrgb = format.IsSrgb;
                result.Add(image);
            }

            if (warning is not null) error = warning;
            return result;
        }

        for (int slice = 0; slice < count; slice++)
        {
            var image = Decode(texture, mip, 0, slice, out var sliceError);
            if (image is null)
            {
                error = $"slice {slice}: {sliceError}";
                return result;
            }
            result.Add(image);
        }
        return result;
    }

    private static RgbaImage DecodeRaw(byte[] linear, int width, int height, FormatInfo format)
    {
        var image = new RgbaImage(width, height);
        if (format.BytesPerBlock == 1 && format.BlockWidth == 1)
        {
            // Format 0x01 ("af"): one 8-bit channel per pixel, shown as grayscale. The channel's
            // semantic (intensity vs alpha) is unconfirmed, so the value is replicated to RGB so
            // it is visible either way.
            int pixels = Math.Min(width * height, linear.Length);
            for (int i = 0; i < pixels; i++)
            {
                byte v = linear[i];
                image.Pixels[i * 4 + 0] = v;
                image.Pixels[i * 4 + 1] = v;
                image.Pixels[i * 4 + 2] = v;
                image.Pixels[i * 4 + 3] = 255;
            }
        }
        else if (format.BytesPerBlock == 4 && format.BlockWidth == 1)
        {
            int needed = width * height * 4;
            int copy = Math.Min(needed, linear.Length);
            Array.Copy(linear, image.Pixels, copy);
            if (format.Format == Xt1Format.Bgra8Unorm)
                SwapRedBlue(image.Pixels);
        }
        else if (format.BytesPerBlock == 8)
        {
            // RGBA16F preview: convert half floats to 8-bit.
            for (int i = 0; i < width * height; i++)
            {
                int src = i * 8;
                if (src + 8 > linear.Length) break;
                image.Pixels[i * 4 + 0] = HalfToByte(linear, src + 0);
                image.Pixels[i * 4 + 1] = HalfToByte(linear, src + 2);
                image.Pixels[i * 4 + 2] = HalfToByte(linear, src + 4);
                image.Pixels[i * 4 + 3] = HalfToByte(linear, src + 6);
            }
        }
        return image;
    }

    private static RgbaImage DecodeBc(byte[] linear, int width, int height, FormatInfo format)
    {
        if (!ManagedFormatMap.TryGetDecodeFormat(format, out var compression))
            throw new NotSupportedException($"no managed decoder for {format.Name}");

        var image = new RgbaImage(width, height);
        if (compression == CompressionFormat.Bc6U)
        {
            var hdr = BcDecoder.DecodeRawHdr(linear, width, height, compression);
            for (int i = 0; i < width * height && i < hdr.Length; i++)
            {
                image.Pixels[i * 4 + 0] = Tonemap(hdr[i].r);
                image.Pixels[i * 4 + 1] = Tonemap(hdr[i].g);
                image.Pixels[i * 4 + 2] = Tonemap(hdr[i].b);
                image.Pixels[i * 4 + 3] = 255;
            }
            return image;
        }

        var pixels = BcDecoder.DecodeRaw(linear, width, height, compression);
        for (int i = 0; i < width * height && i < pixels.Length; i++)
        {
            image.Pixels[i * 4 + 0] = pixels[i].r;
            image.Pixels[i * 4 + 1] = pixels[i].g;
            image.Pixels[i * 4 + 2] = pixels[i].b;
            image.Pixels[i * 4 + 3] = pixels[i].a;
        }
        return image;
    }

    private static RgbaImage DecodeAstc(byte[] linear, int width, int height, FormatInfo format)
    {
        int footprintIndex = format.AstcFootprintIndex;
        if (footprintIndex < 0 || footprintIndex >= FormatTable.AstcFootprintNames.Length)
            throw new InvalidDataException("invalid ASTC footprint");

        var footprintType = (FootprintType)footprintIndex;
        var footprint = Footprint.FromFootprintType(footprintType);

        using var source = new MemoryStream(linear, writable: false);
        using var destination = new MemoryStream();
        AstcDecoder.DecompressImage(source, destination, width, height, footprint, LdrDecodeMode.Linear);
        var bytes = destination.ToArray();

        var image = new RgbaImage(width, height);
        Array.Copy(bytes, image.Pixels, Math.Min(bytes.Length, image.Pixels.Length));
        return image;
    }

    private static void SwapRedBlue(byte[] pixels)
    {
        for (int i = 0; i + 3 < pixels.Length; i += 4)
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
    }

    private static byte HalfToByte(byte[] data, int offset)
    {
        ushort h = (ushort)(data[offset] | (data[offset + 1] << 8));
        return (byte)Math.Clamp((int)MathF.Round(HalfToFloat(h) * 255f), 0, 255);
    }

    private static float HalfToFloat(ushort value)
    {
        int sign = (value >> 15) & 1;
        int exponent = (value >> 10) & 0x1F;
        int mantissa = value & 0x3FF;

        double result;
        if (exponent == 0)
            result = mantissa * Math.Pow(2, -24);
        else if (exponent == 31)
            result = mantissa == 0 ? double.PositiveInfinity : double.NaN;
        else
            result = (1 + mantissa / 1024.0) * Math.Pow(2, exponent - 15);

        return (float)(sign == 1 ? -result : result);
    }

    private static byte Tonemap(float value)
    {
        if (float.IsNaN(value) || value <= 0) return 0;
        float mapped = value / (1f + value);
        return (byte)Math.Clamp((int)MathF.Round(mapped * 255f), 0, 255);
    }
}
