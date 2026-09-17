using BCnEncoder.Shared;
using Xt1.Core.Format;

namespace Xt1.Core.Codecs;

/// <summary>
/// The one authoritative XT1 format to BCnEncoder <see cref="CompressionFormat"/> mapping, plus
/// the managed encode/decode capability flags. Both the decoder and the managed encoder derive
/// from this so a new format cannot be added to one path and forgotten in the other.
/// </summary>
public static class ManagedFormatMap
{
    /// <summary>True when the managed decoder can decode the format.</summary>
    public static bool SupportsDecode(FormatInfo format) =>
        format.Codec is CodecKind.Raw or CodecKind.Astc ||
        (format.Codec == CodecKind.Bc && TryGetBcFormat(format.Format, forEncoding: false, out _));

    /// <summary>True when the managed encoder can encode the format.</summary>
    public static bool SupportsEncode(FormatInfo format)
    {
        // 0x6D's channel order is unconfirmed, so it is viewable but never re-encoded.
        if (format.Format == Xt1Format.Unknown6d) return false;

        return format.Codec is CodecKind.Raw or CodecKind.Astc ||
               (format.Codec == CodecKind.Bc && TryGetBcFormat(format.Format, forEncoding: true, out _));
    }

    /// <summary>The BCnEncoder format used for decoding.</summary>
    public static bool TryGetDecodeFormat(FormatInfo format, out CompressionFormat compressionFormat) =>
        TryGetBcFormat(format.Format, forEncoding: false, out compressionFormat);

    /// <summary>The BCnEncoder format used for encoding (BC1 alpha is chosen by the caller).</summary>
    public static bool TryGetEncodeFormat(FormatInfo format, out CompressionFormat compressionFormat) =>
        TryGetBcFormat(format.Format, forEncoding: true, out compressionFormat);

    private static bool TryGetBcFormat(Xt1Format format, bool forEncoding, out CompressionFormat compression)
    {
        switch (format)
        {
            case Xt1Format.Bc1Unorm:
            case Xt1Format.Bc1Srgb:
                compression = CompressionFormat.Bc1;
                return true;
            case Xt1Format.Bc2Unorm:
            case Xt1Format.Bc2Srgb:
                compression = CompressionFormat.Bc2;
                return true;
            case Xt1Format.Bc3Unorm:
            case Xt1Format.Bc3Srgb:
                compression = CompressionFormat.Bc3;
                return true;
            case Xt1Format.Bc4Unorm:
            case Xt1Format.Bc4Snorm:
                compression = CompressionFormat.Bc4;
                return true;
            case Xt1Format.Bc5Unorm:
            case Xt1Format.Bc5Snorm:
                compression = CompressionFormat.Bc5;
                return true;
            case Xt1Format.Bc7Unorm:
            case Xt1Format.Bc7Srgb:
                compression = CompressionFormat.Bc7;
                return true;
            case Xt1Format.Bc6hUf16:
                // BC6H is decode-only in the managed path; encode it with NVTT.
                compression = CompressionFormat.Bc6U;
                return !forEncoding;
            default:
                compression = CompressionFormat.Unknown;
                return false;
        }
    }
}
