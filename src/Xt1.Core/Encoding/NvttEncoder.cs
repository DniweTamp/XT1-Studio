using Xt1.Core.Format;
using Xt1.Core.Imaging;
using Xt1.Core.Nvtt;

namespace Xt1.Core.Encoding;

/// <summary>NVTT in-process encoder. Preferred for BC6H, ASTC and optionally BC1-BC7.</summary>
public sealed class NvttEncoder : ITextureEncoder, IDisposable
{
    private readonly NvttNative _native;

    public NvttEncoder(NvttNative native) => _native = native;

    public string Name => $"NVTT {_native.Version}";

    public NvttNative Native => _native;

    public bool Supports(FormatInfo format) => NvttFormatMap.ToNvtt(format.Format) is not null;

    public EncodeResult Encode(RgbaImage image, FormatInfo format, bool srgb, EncodeQuality quality)
    {
        var nvttFormat = NvttFormatMap.ToNvtt(format.Format);
        if (nvttFormat is null)
            return EncodeResult.Fail($"format {format.Name} is not supported by NVTT");

        try
        {
            var bytes = _native.EncodeSurface(image, nvttFormat.Value, MapQuality(quality), srgb,
                hasAlpha: image.HasTransparency());
            return EncodeResult.Ok(bytes);
        }
        catch (Exception ex)
        {
            return EncodeResult.Fail(ex.Message);
        }
    }

    private static NvttQuality MapQuality(EncodeQuality quality) => quality switch
    {
        EncodeQuality.Fastest => NvttQuality.Fastest,
        EncodeQuality.Production => NvttQuality.Production,
        EncodeQuality.Highest => NvttQuality.Highest,
        _ => NvttQuality.Normal,
    };

    public void Dispose() => _native.Dispose();
}
