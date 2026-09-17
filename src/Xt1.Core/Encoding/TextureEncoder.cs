using Xt1.Core.Containers;
using Xt1.Core.Format;
using Xt1.Core.Imaging;
using Xt1.Core.Util;
using Xt1.Core.Xt1;

namespace Xt1.Core.Encoding;

/// <summary>
/// High-level re-encode: takes a decoded image, encodes each mip (and cube face) with the chosen
/// backend, swizzles to block-linear, packs the XT1 payload (512 per-mip / 4096 per-face
/// alignment) and updates the texture's header and table entries.
/// </summary>
public static class TextureEncoder
{
    /// <summary>Re-encode a 2D texture (does not change format; set <c>Header.Format</c> first).</summary>
    public static EncodeResult Encode2D(EditableTexture texture, RgbaImage baseImage,
        ITextureEncoder encoder, bool generateMips, EncodeQuality quality)
    {
        var format = texture.FormatInfo;
        if (format is null) return EncodeResult.Fail("unknown format");
        if (!encoder.Supports(format))
            return EncodeResult.Fail($"{encoder.Name} cannot encode {format.Name}");
        if (texture.Header.Type.Is3D())
            return EncodeResult.Fail("3D textures are view-only; re-encoding a volume is not supported");

        int mipCount = generateMips ? MipChain.FullMipCount(baseImage.Width, baseImage.Height) : 1;
        var levels = MipChain.Build(baseImage, mipCount, format.IsSrgb);

        var layout = ComputeLayout(texture.Header, baseImage.Width, baseImage.Height, 1, mipCount, texture.Header.Type);
        if (layout is null) return EncodeResult.Fail("cannot compute payload layout for the new dimensions");

        var payload = PackChain(levels, layout, format, encoder, quality, out var error);
        if (payload is null) return EncodeResult.Fail(error ?? "encode failed");

        Apply(texture, payload, baseImage.Width, baseImage.Height, 1, mipCount);
        return EncodeResult.Ok(payload);
    }

    /// <summary>Re-encode a cubemap from six faces (order must match the XT1 face order).</summary>
    public static EncodeResult EncodeCube(EditableTexture texture, IReadOnlyList<RgbaImage> faces,
        ITextureEncoder encoder, bool generateMips, EncodeQuality quality)
    {
        var format = texture.FormatInfo;
        if (format is null) return EncodeResult.Fail("unknown format");
        if (faces.Count != 6) return EncodeResult.Fail("a cubemap needs exactly 6 faces");
        if (!encoder.Supports(format))
            return EncodeResult.Fail($"{encoder.Name} cannot encode {format.Name}");

        int width = faces[0].Width;
        int height = faces[0].Height;
        foreach (var face in faces)
            if (face.Width != width || face.Height != height)
                return EncodeResult.Fail("all cube faces must share dimensions");

        int mipCount = generateMips ? MipChain.FullMipCount(width, height) : 1;
        var layout = ComputeLayout(texture.Header, width, height, 1, mipCount, TextureType.TCubeArray);
        if (layout is null) return EncodeResult.Fail("cannot compute payload layout for cube faces");

        // Reuse the validated face stride from SurfaceLayout rather than recomputing a 4096-byte
        // alignment: a face chain whose base mip is under one page is stored densely (see
        // SurfaceLayout), and re-encoding must reproduce the same payload size the game expects.
        long faceStride = layout.FaceStride > 0 ? layout.FaceStride : layout.FaceSize;
        long total = faceStride * 6;
        if (total > int.MaxValue) return EncodeResult.Fail("cubemap payload is too large");

        var payload = new byte[total];
        for (int face = 0; face < 6; face++)
        {
            var levels = MipChain.Build(faces[face], mipCount, format.IsSrgb);
            var faceBytes = PackChain(levels, layout, format, encoder, quality, out var error);
            if (faceBytes is null) return EncodeResult.Fail($"face {face}: {error}");
            faceBytes.CopyTo(payload, face * faceStride);
        }

        Apply(texture, payload, width, height, 1, mipCount, cube: true);
        return EncodeResult.Ok(payload);
    }

    private static LayoutResult? ComputeLayout(Xt1Header header, int width, int height, int depth,
        int mipCount, TextureType type)
    {
        var temp = header.Clone();
        temp.Width = (uint)width;
        temp.Height = (uint)height;
        temp.Depth = (uint)depth;
        temp.MipCount = mipCount;
        temp.Type = type;
        var layout = SurfaceLayout.Compute(temp);
        return layout.Applicable ? layout : null;
    }

    private static byte[]? PackChain(IReadOnlyList<RgbaImage> levels, LayoutResult layout, FormatInfo format,
        ITextureEncoder encoder, EncodeQuality quality, out string? error)
    {
        error = null;
        long size = 0;
        var chunks = new List<(long Offset, byte[] Surface)>(levels.Count);

        for (int m = 0; m < levels.Count; m++)
        {
            if (m >= layout.Mips.Count)
            {
                error = "mip count exceeds the layout";
                return null;
            }

            var info = layout.Mips[m];
            var result = encoder.Encode(levels[m], format, format.IsSrgb, quality);
            if (!result.Success)
            {
                error = result.Error;
                return null;
            }

            int expected = info.WidthBlocks * info.HeightBlocks * format.BytesPerBlock;
            if (result.Data.Length < expected)
            {
                error = $"encoder returned {result.Data.Length} bytes, expected {expected}";
                return null;
            }

            var surface = new byte[info.SliceSurfaceSize];
            GobSwizzle.Swizzle(result.Data, surface, info.WidthBlocks, info.HeightBlocks,
                format.BytesPerBlock, info.BlockHeight, info.Pitch);

            long offset = Mem.Align(size, 512);
            chunks.Add((offset, surface));
            size = offset + surface.Length;
        }

        if (size > int.MaxValue)
        {
            error = "payload is too large";
            return null;
        }

        var payload = new byte[size];
        foreach (var (offset, surface) in chunks)
            surface.CopyTo(payload, (int)offset);
        return payload;
    }

    private static void Apply(EditableTexture texture, byte[] payload, int width, int height, int depth,
        int mipCount, bool cube = false)
    {
        var header = texture.Header;
        header.Width = (uint)width;
        header.Height = (uint)height;
        header.Depth = (uint)depth;
        header.MipCount = mipCount;
        header.ImageSize = (ulong)payload.Length;
        header.Type = cube ? TextureType.TCubeArray : header.Type;
        header.HeaderSize = Xt1Header.Size;

        // The 0x14 high word must agree with the format: "af" (0x6661) for format 0x01 and zero for
        // everything else, or the record fails its own validation (XT1_FORMAT.md sections 5 and 13).
        header.CodecTag = header.FormatValue == (uint)Xt1Format.AfL8Unorm ? Xt1Header.AfCodecTag : (ushort)0;

        texture.Payload = payload;
        texture.PayloadSize = (uint)payload.Length;
    }
}
