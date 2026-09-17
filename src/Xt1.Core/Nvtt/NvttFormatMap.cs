using Xt1.Core.Format;

namespace Xt1.Core.Nvtt;

/// <summary><c>NvttFormat</c> from nvtt_wrapper.h 3.2.5.</summary>
public enum NvttFormat
{
    Rgba = 0,
    Bc1 = 1,
    Bc1a = 2,
    Bc2 = 3,
    Bc3 = 4,
    Bc3n = 5,
    Bc4 = 6,
    Bc4S = 7,
    Ati2 = 8,
    Bc5 = 9,
    Bc5S = 10,
    Dxt1n = 11,
    Ctx1 = 12,
    Bc6U = 13,
    Bc6S = 14,
    Bc7 = 15,
    Bc3Rgbm = 16,
    AstcLdr4x4 = 17,
    AstcLdr5x4 = 18,
    AstcLdr5x5 = 19,
    AstcLdr6x5 = 20,
    AstcLdr6x6 = 21,
    AstcLdr8x5 = 22,
    AstcLdr8x6 = 23,
    AstcLdr8x8 = 24,
    AstcLdr10x5 = 25,
    AstcLdr10x6 = 26,
    AstcLdr10x8 = 27,
    AstcLdr10x10 = 28,
    AstcLdr12x10 = 29,
    AstcLdr12x12 = 30,
}

public enum NvttQuality
{
    Fastest = 0,
    Normal = 1,
    Production = 2,
    Highest = 3,
}

public enum NvttInputFormat
{
    Bgra8Ub = 0,
    Bgra8Sb = 1,
    Rgba16F = 2,
    Rgba32F = 3,
    R32F = 4,
}

public enum NvttContainer
{
    Dds = 0,
    Dds10 = 1,
}

/// <summary>Per-format NVTT capability for both the in-process library and the CLI.</summary>
public sealed record NvttFormatSupport(Xt1Format Format, NvttFormat? InProcess, string? CliName, int CliFormatId);

/// <summary>
/// The single source of truth for "can NVTT encode this XT1 format" for both backends. Keeping
/// the in-process format and the CLI name/id in one record means the two backends cannot report
/// conflicting support for the same format.
/// </summary>
public static class NvttFormatMap
{
    private static readonly Dictionary<Xt1Format, NvttFormatSupport> Support;

    // A static constructor guarantees CliFormatIds (declared below) is initialized before
    // BuildSupport reads it, instead of relying on field declaration order.
    static NvttFormatMap()
    {
        Support = BuildSupport();
    }

    /// <summary>The in-process NVTT format, or null when NVTT cannot encode it.</summary>
    public static NvttFormat? ToNvtt(Xt1Format format) =>
        Support.TryGetValue(format, out var support) ? support.InProcess : null;

    /// <summary>The nvtt_export CLI format name, or null when the CLI cannot encode it.</summary>
    public static string? ToCliFormatName(Xt1Format format) =>
        Support.TryGetValue(format, out var support) ? support.CliName : null;

    /// <summary>The nvtt_export <c>-f</c> id for a format, if the CLI supports it.</summary>
    public static bool TryGetCliFormatId(Xt1Format format, out int id)
    {
        if (Support.TryGetValue(format, out var support) && support.CliName is not null)
        {
            id = support.CliFormatId;
            return true;
        }
        id = 0;
        return false;
    }

    public static bool InProcessSupports(Xt1Format format) => ToNvtt(format) is not null;

    public static bool CliSupports(Xt1Format format) => ToCliFormatName(format) is not null;

    /// <summary>The ASTC block size name used by the nvtt_export CLI (e.g. "astc-ldr-8x8").</summary>
    public static string? ToAstcCliName(Xt1Format format)
    {
        var info = FormatTable.Get(format);
        if (info is null || info.Codec != CodecKind.Astc) return null;
        return "astc-ldr-" + FormatTable.AstcFootprintNames[info.AstcFootprintIndex];
    }

    /// <summary>
    /// The nvtt_export 3.2.5 <c>-f</c> ids, parsed from the installed <c>--help</c>.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, int> CliFormatIds =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["astc-ldr-10x10"] = 1,
            ["astc-ldr-10x5"] = 2,
            ["astc-ldr-10x6"] = 3,
            ["astc-ldr-10x8"] = 4,
            ["astc-ldr-12x10"] = 5,
            ["astc-ldr-12x12"] = 6,
            ["astc-ldr-4x4"] = 7,
            ["astc-ldr-5x4"] = 8,
            ["astc-ldr-5x5"] = 9,
            ["astc-ldr-6x5"] = 10,
            ["astc-ldr-6x6"] = 11,
            ["astc-ldr-8x5"] = 12,
            ["astc-ldr-8x6"] = 13,
            ["astc-ldr-8x8"] = 14,
            ["bc1"] = 15,
            ["bc1a"] = 16,
            ["bc2"] = 17,
            ["bc3"] = 18,
            ["bc3n"] = 19,
            ["bc4"] = 20,
            ["bc5"] = 21,
            ["bc6"] = 22,
            ["bc7"] = 23,
            ["bc6s"] = 37,
            ["bc6u"] = 38,
        };

    private static Dictionary<Xt1Format, NvttFormatSupport> BuildSupport()
    {
        var map = new Dictionary<Xt1Format, NvttFormatSupport>();

        void Add(Xt1Format format, NvttFormat? inProcess, string? cliName)
        {
            int id = cliName is not null && CliFormatIds.TryGetValue(cliName, out int value) ? value : -1;
            map[format] = new NvttFormatSupport(format, inProcess, id >= 0 ? cliName : null, id);
        }

        Add(Xt1Format.Bc1Unorm, NvttFormat.Bc1, "bc1");
        Add(Xt1Format.Bc1Srgb, NvttFormat.Bc1, "bc1");
        Add(Xt1Format.Bc2Unorm, NvttFormat.Bc2, "bc2");
        Add(Xt1Format.Bc2Srgb, NvttFormat.Bc2, "bc2");
        Add(Xt1Format.Bc3Unorm, NvttFormat.Bc3, "bc3");
        Add(Xt1Format.Bc3Srgb, NvttFormat.Bc3, "bc3");
        Add(Xt1Format.Bc4Unorm, NvttFormat.Bc4, "bc4");
        Add(Xt1Format.Bc5Unorm, NvttFormat.Bc5, "bc5");
        Add(Xt1Format.Bc6hUf16, NvttFormat.Bc6U, "bc6u");
        Add(Xt1Format.Bc7Unorm, NvttFormat.Bc7, "bc7");
        Add(Xt1Format.Bc7Srgb, NvttFormat.Bc7, "bc7");

        for (int i = 0; i < FormatTable.AstcFootprintNames.Length; i++)
        {
            var unorm = (Xt1Format)(0x79 + i);
            var srgb = (Xt1Format)(0x87 + i);
            var nvtt = (NvttFormat)((int)NvttFormat.AstcLdr4x4 + i);
            string cliName = "astc-ldr-" + FormatTable.AstcFootprintNames[i];
            Add(unorm, nvtt, cliName);
            Add(srgb, nvtt, cliName);
        }

        // Aliases inherit their canonical format's support; FormatTable is the only place an alias
        // is declared, so this cannot fall out of step with the managed and DDS tables.
        foreach (var alias in FormatTable.Aliases)
        {
            if (map.TryGetValue(alias.AliasOf!.Value, out var support))
                map[alias.Format] = support with { Format = alias.Format };
        }

        return map;
    }
}
