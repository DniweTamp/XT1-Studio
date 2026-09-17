using Xt1.Core.Codecs;
using Xt1.Core.Containers;
using Xt1.Core.Xt1;

namespace Xt1.Core.Imaging;

/// <summary>
/// Builds a DDS from a texture's stored surfaces without re-encoding. Handles 2D, cubemaps and 3D
/// volumes: a volume level is every depth slice (Z-interleaved), not just the first slice.
/// </summary>
public static class DdsExport
{
    public static bool CanExport(EditableTexture texture) =>
        texture.FormatInfo is { } format && DdsIo.CanExport(format);

    public static byte[] Build(EditableTexture texture)
    {
        var info = texture.FormatInfo ?? throw new NotSupportedException("unknown format");
        var layout = texture.Layout;
        if (!layout.Applicable)
            throw new NotSupportedException(layout.Note ?? "payload layout is unavailable");

        int mipCount = Math.Min(Math.Max(1, texture.Header.MipCount), layout.Mips.Count);
        int faceCount = layout.FaceCount;
        bool is3D = texture.Header.Type.Is3D();

        var data = new byte[faceCount][][];
        for (int face = 0; face < faceCount; face++)
        {
            data[face] = new byte[mipCount][];
            for (int mip = 0; mip < mipCount; mip++)
            {
                var surface = layout.Mips[mip];
                // A 3D level is every depth slice (Z-interleaved); one slice would leave the file
                // short of what the depth field promises and readers reject it.
                int expected = surface.WidthBlocks * surface.HeightBlocks * surface.SliceCount
                               * info.BytesPerBlock;
                var level = is3D
                    ? TextureCodec.ExtractLinearVolume(texture, mip, out _, out _)
                    : TextureCodec.ExtractLinearBlocks(texture, mip, face, 0, out _, out _);

                // A short level would be written as a truncated surface with no error; refuse instead.
                if (level.Length < expected)
                    throw new NotSupportedException(
                        $"{info.Name}: level {mip} produced {level.Length} of {expected} bytes");

                data[face][mip] = level.Length == expected ? level : level.AsSpan(0, expected).ToArray();
            }
        }

        return DdsIo.Write(info, (int)texture.Header.Width, (int)texture.Header.Height,
            (int)texture.Header.Depth, mipCount, faceCount, texture.Header.Type.IsCube(), data);
    }
}
