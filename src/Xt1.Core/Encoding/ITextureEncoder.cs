using Xt1.Core.Format;
using Xt1.Core.Imaging;

namespace Xt1.Core.Encoding;

public enum EncoderBackend
{
    /// <summary>BCnEncoder.Net + AstcSharp + raw copy. Zero external dependencies.</summary>
    Managed,

    /// <summary>NVIDIA Texture Tools 3.2.5 via the in-process C API.</summary>
    Nvtt,

    /// <summary>nvtt_export.exe / astcenc.exe / texconv.exe command line.</summary>
    Cli,
}

public enum EncodeQuality
{
    Fastest,
    Normal,
    Production,
    Highest,
}

public sealed record EncodeResult(bool Success, byte[] Data, string? Error)
{
    public static EncodeResult Ok(byte[] data) => new(true, data, null);
    public static EncodeResult Fail(string error) => new(false, Array.Empty<byte>(), error);
}

/// <summary>Encodes a single RGBA8 surface into the target format's linear block layout.</summary>
public interface ITextureEncoder
{
    string Name { get; }

    bool Supports(FormatInfo format);

    EncodeResult Encode(RgbaImage image, FormatInfo format, bool srgb, EncodeQuality quality);
}
