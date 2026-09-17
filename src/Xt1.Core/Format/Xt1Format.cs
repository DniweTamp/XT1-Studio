namespace Xt1.Core.Format;

/// <summary>
/// XT1 format enum for Bayonetta 3. Names/values come from XT1_FORMAT.md section 8 (NVN-aligned).
/// Values marked "unverified" were never observed in BT3 but are included from the plan's codec matrix.
/// </summary>
public enum Xt1Format : uint
{
    /// <summary>
    /// Format 0x01, codec tag "af". Single 8-bit channel, 1 byte per pixel, uncompressed and
    /// block-linear like every other format. Verified three ways against real data: imageSize
    /// matches the section 6 layout formula with bytesPerBlock = 1 for 1024x1024/1 (1048576),
    /// 128x128/8 (25600) and 8x512/10 (66560); the deswizzled plane has 3.5x lower 2D total
    /// variation than the raw order; and blocking with blockHeightLog2 = 5 (layout 0x205) is the
    /// unique variation minimum, i.e. the layout low 3 bits behave exactly as for other formats.
    /// The earlier "second-layer compressed, imageSize is the compressed size" reading is wrong.
    /// </summary>
    AfL8Unorm = 0x01,

    /// <summary>Uncompressed 32bpp BGRA byte order in memory. Unverified in BT3.</summary>
    Bgra8Unorm = 0x3B,

    /// <summary>R8G8B8A8 uncompressed.</summary>
    Rgba8Unorm = 0x25,

    /// <summary>R8G8B8A8 uncompressed, sRGB-tagged.</summary>
    Rgba8Srgb = 0x38,

    /// <summary>RGBA16F uncompressed. Reported by other tools; unverified in BT3.</summary>
    Rgba16Float = 0x41,

    Bc1Unorm = 0x42,
    Bc2Unorm = 0x43,
    Bc3Unorm = 0x44,
    Bc4Unorm = 0x45,
    Bc1Srgb = 0x46,
    Bc2Srgb = 0x47,
    Bc3Srgb = 0x48,
    Bc4Snorm = 0x49,

    /// <summary>Unverified in BT3.</summary>
    Bc5Unorm = 0x4A,

    /// <summary>Unverified in BT3.</summary>
    Bc5Snorm = 0x4B,

    /// <summary>BC6H unsigned float (HDR). Used for cubemaps.</summary>
    Bc6hUf16 = 0x50,

    /// <summary>Unverified in BT3.</summary>
    Bc7Unorm = 0x51,

    /// <summary>Unverified in BT3.</summary>
    Bc7Srgb = 0x52,

    /// <summary>
    /// Format 0x6D, observed only on 3D textures (16x16x32). Uncompressed 32bpp: the section 6
    /// layout formula matches the stored imageSize with exactly 4 bytes per texel and 32 slices
    /// (1024 x 32 = 32768), and 4 is the only bytes-per-texel value that fits. The channel order
    /// (RGBA vs BGRA) is unconfirmed, so this is decoded for viewing but not offered for encoding.
    /// </summary>
    Unknown6d = 0x6D,

    // ASTC LDR UNORM: 0x79..0x86, footprints in enum order.
    Astc4x4Unorm = 0x79,
    Astc5x4Unorm = 0x7A,
    Astc5x5Unorm = 0x7B,
    Astc6x5Unorm = 0x7C,
    Astc6x6Unorm = 0x7D,
    Astc8x5Unorm = 0x7E,
    Astc8x6Unorm = 0x7F,
    Astc8x8Unorm = 0x80,
    Astc10x5Unorm = 0x81,
    Astc10x6Unorm = 0x82,
    Astc10x8Unorm = 0x83,
    Astc10x10Unorm = 0x84,
    Astc12x10Unorm = 0x85,
    Astc12x12Unorm = 0x86,

    // ASTC LDR sRGB: 0x87..0x94, exactly +0x0E from UNORM.
    Astc4x4Srgb = 0x87,
    Astc5x4Srgb = 0x88,
    Astc5x5Srgb = 0x89,
    Astc6x5Srgb = 0x8A,
    Astc6x6Srgb = 0x8B,
    Astc8x5Srgb = 0x8C,
    Astc8x6Srgb = 0x8D,
    Astc8x8Srgb = 0x8E,
    Astc10x5Srgb = 0x8F,
    Astc10x6Srgb = 0x90,
    Astc10x8Srgb = 0x91,
    Astc10x10Srgb = 0x92,
    Astc12x10Srgb = 0x93,
    Astc12x12Srgb = 0x94,
}
