namespace Xt1.Core.Format;

/// <summary>How a texture's payload is stored / can be decoded.</summary>
public enum CodecKind
{
    /// <summary>Format is known by name but has no decoder/encoder (metadata only).</summary>
    Unsupported = 0,

    /// <summary>Uncompressed, block geometry 1x1.</summary>
    Raw,

    /// <summary>BC1..BC7 (S3TC/DXTC family).</summary>
    Bc,

    /// <summary>ASTC LDR, 16 bytes per block, 14 footprints.</summary>
    Astc,
}
