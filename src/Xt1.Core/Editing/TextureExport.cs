using Xt1.Core.Codecs;
using Xt1.Core.Containers;
using Xt1.Core.Imaging;

namespace Xt1.Core.Editing;

/// <summary>The file kinds a texture can be written as.</summary>
public enum TextureExportFormat
{
    /// <summary>Decoded image, written through <see cref="ImageIo"/>.</summary>
    Png,

    /// <summary>Stored surfaces re-wrapped as a DDS (no re-encode), all mips.</summary>
    Dds,

    /// <summary>Raw <c>XT1\0</c> header + payload.</summary>
    Xt1,
}

/// <summary>
/// Writes one texture out as PNG, DDS or raw XT1, so the shell's per-texture, per-bank and
/// folder-wide exports (and the matching CLI op) all share one implementation and one capability
/// test.
/// </summary>
public static class TextureExport
{
    /// <summary>Parse the CLI/shell spelling <c>png</c>/<c>dds</c>/<c>xt1</c>.</summary>
    public static bool TryParse(string? text, out TextureExportFormat format)
    {
        switch (text?.ToLowerInvariant())
        {
            case "png": format = TextureExportFormat.Png; return true;
            case "dds": format = TextureExportFormat.Dds; return true;
            case "xt1": format = TextureExportFormat.Xt1; return true;
            default: format = TextureExportFormat.Png; return false;
        }
    }

    /// <summary>The canonical lower-case extension, including the dot.</summary>
    public static string Extension(TextureExportFormat format) => format switch
    {
        TextureExportFormat.Png => ".png",
        TextureExportFormat.Dds => ".dds",
        TextureExportFormat.Xt1 => ".xt1",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>
    /// Whether the texture can be written in this format: PNG needs a decoder, DDS needs a DXGI
    /// mapping for the format, and raw XT1 always applies (it copies the stored header/payload).
    /// </summary>
    public static bool CanExport(EditableTexture texture, TextureExportFormat format) => format switch
    {
        TextureExportFormat.Png => TextureCodec.CanDecode(texture.FormatInfo),
        TextureExportFormat.Dds => texture.FormatInfo is { } info && DdsIo.CanExport(info),
        TextureExportFormat.Xt1 => true,
        _ => false,
    };

    /// <summary>
    /// Write one texture to <paramref name="path"/>. The PNG mip/face/slice select which surface is
    /// decoded (defaults to the base 2D level); DDS and XT1 always carry the whole texture.
    /// </summary>
    public static void Write(EditableTexture texture, TextureExportFormat format, string path,
        int mip = 0, int face = 0, int slice = 0)
    {
        switch (format)
        {
            case TextureExportFormat.Png:
            {
                var image = TextureCodec.Decode(texture, mip, face, slice, out var error)
                            ?? throw new InvalidOperationException(error ?? "decode failed");
                ImageIo.Save(image, path);
                break;
            }
            case TextureExportFormat.Dds:
                File.WriteAllBytes(path, DdsExport.Build(texture));
                break;
            case TextureExportFormat.Xt1:
                // trackOriginal would deep-clone the whole payload for dirty tracking that is never
                // read before the blob is written and discarded.
                File.WriteAllBytes(path,
                    new RawXt1(texture.Header.Clone(), texture.Payload, trackOriginal: false).ToBytes());
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    /// <summary>
    /// <see cref="Write"/> without throwing: false plus a reason when the texture does not support
    /// the format or the write fails. Lets batch loops report one bad texture and continue.
    /// </summary>
    public static bool TryWrite(EditableTexture texture, TextureExportFormat format, string path,
        out string? error, int mip = 0, int face = 0, int slice = 0)
    {
        error = null;
        if (!CanExport(texture, format))
        {
            error = CapabilityError(texture, format);
            return false;
        }

        try
        {
            Write(texture, format, path, mip, face, slice);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// The same checks as <see cref="TryWrite"/> but without touching disk, for dry runs: PNG is
    /// decoded (catching a bad mip/face/slice or an unresolvable layout) and DDS needs an applicable
    /// payload layout. Returns false plus a reason when the real write would be skipped.
    /// </summary>
    public static bool TryValidate(EditableTexture texture, TextureExportFormat format, out string? error,
        int mip = 0, int face = 0, int slice = 0)
    {
        error = null;
        if (!CanExport(texture, format))
        {
            error = CapabilityError(texture, format);
            return false;
        }

        switch (format)
        {
            case TextureExportFormat.Png:
                return TextureCodec.Decode(texture, mip, face, slice, out error) is not null;
            case TextureExportFormat.Dds when !texture.Layout.Applicable:
                error = texture.Layout.Note ?? "payload layout is unavailable";
                return false;
            default:
                return true;
        }
    }

    private static string CapabilityError(EditableTexture texture, TextureExportFormat format) =>
        format == TextureExportFormat.Dds
            ? $"no DDS mapping for {texture.FormatInfo?.Name ?? "unknown"}"
            : $"no decoder for {texture.FormatInfo?.Name ?? "unknown"}";
}
