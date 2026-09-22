namespace Xt1.Core.Format;

/// <summary>Global registry of XT1 formats. Built once; immutable afterwards.</summary>
public static class FormatTable
{
    /// <summary>ASTC footprint names in enum order (4x4 first). Index matches <see cref="FormatInfo.AstcFootprintIndex"/>.</summary>
    public static readonly string[] AstcFootprintNames =
    {
        "4x4", "5x4", "5x5", "6x5", "6x6", "8x5", "8x6",
        "8x8", "10x5", "10x6", "10x8", "10x10", "12x10", "12x12",
    };

    private static readonly Dictionary<uint, FormatInfo> Map = Build();
    private static readonly List<FormatInfo> Ordered = Map.Values.OrderBy(f => f.Value).ToList();
    private static readonly FormatInfo[] Astc = Ordered.Where(f => f.Codec == CodecKind.Astc).ToArray();

    public static IEnumerable<FormatInfo> All => Ordered;

    public static IReadOnlyList<FormatInfo> AstcFormats => Astc;

    public static bool TryGet(uint value, out FormatInfo info) => Map.TryGetValue(value, out info!);

    public static FormatInfo? Get(uint value) => Map.TryGetValue(value, out var info) ? info : null;

    public static FormatInfo? Get(Xt1Format format) => Get((uint)format);

    private static Dictionary<uint, FormatInfo> Build()
    {
        var list = new List<FormatInfo>
        {
            // Format 0x01 ("af"): a single 8-bit channel at 1 byte per pixel. The layout formula
            // applies normally with bytesPerBlock = 1 (verified against real data, see Xt1Format).
            new(Xt1Format.AfL8Unorm, "AF_L8_UNORM", 1, 1, 1, CodecKind.Raw, false, true),
            new(Xt1Format.Bgra8Unorm, "BGRA8_UNORM", 1, 1, 4, CodecKind.Raw, false, false),
            new(Xt1Format.Rgba8Unorm, "RGBA8_UNORM", 1, 1, 4, CodecKind.Raw, false, true),
            new(Xt1Format.Rgba8Srgb, "RGBA8_SRGB", 1, 1, 4, CodecKind.Raw, true, true),
            new(Xt1Format.Rgba16Float, "RGBA16F", 1, 1, 8, CodecKind.Unsupported, false, false),
            new(Xt1Format.Bc1Unorm, "BC1_UNORM", 4, 4, 8, CodecKind.Bc, false, true),
            new(Xt1Format.Bc2Unorm, "BC2_UNORM", 4, 4, 16, CodecKind.Bc, false, true),
            new(Xt1Format.Bc3Unorm, "BC3_UNORM", 4, 4, 16, CodecKind.Bc, false, true),
            new(Xt1Format.Bc4Unorm, "BC4_UNORM", 4, 4, 8, CodecKind.Bc, false, true),
            new(Xt1Format.Bc1Srgb, "BC1_SRGB", 4, 4, 8, CodecKind.Bc, true, true),
            new(Xt1Format.Bc2Srgb, "BC2_SRGB", 4, 4, 16, CodecKind.Bc, true, true),
            new(Xt1Format.Bc3Srgb, "BC3_SRGB", 4, 4, 16, CodecKind.Bc, true, true),
            // 0x49 is BC4_UNORM in practice (community tools label it SNORM, but the payload tests
            // unsigned). Alias so the id round-trips; decoded and encoded like 0x45.
            new(Xt1Format.Bc4UnormAlt, "BC4_UNORM", 4, 4, 8, CodecKind.Bc, false, true)
            {
                AliasOf = Xt1Format.Bc4Unorm,
            },
            new(Xt1Format.Bc5Unorm, "BC5_UNORM", 4, 4, 16, CodecKind.Bc, false, false),
            // 0x4B is BC5_UNORM in practice (labelled SNORM by other tools); alias of 0x4A.
            new(Xt1Format.Bc5UnormAlt, "BC5_UNORM", 4, 4, 16, CodecKind.Bc, false, true)
            {
                AliasOf = Xt1Format.Bc5Unorm,
            },
            new(Xt1Format.Bc6hUf16, "BC6H_UF16", 4, 4, 16, CodecKind.Bc, false, true),
            // BC7 as Bayonetta 3 mods store it (0x4D/0x4E). The blocks are standard BC7 and the tool
            // round-trips them. The 0x51/0x52 ids some tools list for BC7 are a different format and
            // are intentionally absent, so these are the only BC7 entries.
            new(Xt1Format.Bc7Unorm, "BC7_UNORM", 4, 4, 16, CodecKind.Bc, false, true),
            new(Xt1Format.Bc7Srgb, "BC7_SRGB", 4, 4, 16, CodecKind.Bc, true, true),
            new(Xt1Format.Unknown6d, "UNKNOWN_0x6D (32bpp)", 1, 1, 4, CodecKind.Raw, false, false),
        };

        for (int i = 0; i < AstcFootprintNames.Length; i++)
        {
            var (bw, bh) = ParseFootprint(AstcFootprintNames[i]);
            list.Add(new FormatInfo(
                (Xt1Format)(0x79 + i), $"ASTC_LDR_{AstcFootprintNames[i]}_UNORM", bw, bh, 16, CodecKind.Astc, false, true));
            list.Add(new FormatInfo(
                (Xt1Format)(0x87 + i), $"ASTC_LDR_{AstcFootprintNames[i]}_SRGB", bw, bh, 16, CodecKind.Astc, true, true));
        }

        var map = new Dictionary<uint, FormatInfo>(list.Count);
        foreach (var info in list)
            map[info.Value] = info;
        return map;
    }

    private static (int, int) ParseFootprint(string name)
    {
        var parts = name.Split('x');
        return (int.Parse(parts[0]), int.Parse(parts[1]));
    }

    /// <summary>
    /// Resolve an alias id to the canonical format it stands in for; returns the input unchanged for
    /// a canonical (or unknown) format. The codec/NVTT/DXGI tables all key off this so an alias is
    /// declared once, in the format table.
    /// </summary>
    public static Xt1Format Canonical(Xt1Format format) => Get(format)?.AliasOf ?? format;

    /// <summary>Every alias entry (an id whose <see cref="FormatInfo.AliasOf"/> points elsewhere).</summary>
    public static IEnumerable<FormatInfo> Aliases => All.Where(f => f.IsAlias);
}
