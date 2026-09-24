using Xt1.Core.Format;

namespace Xt1.Core.Editing;

/// <summary>
/// A logical re-encode target family. Families that have an sRGB variant follow the source texture's
/// colour space, so one choice is correct across a bank that mixes sRGB and UNORM payloads.
/// </summary>
public sealed record RecompressTarget(string Label, FormatInfo Unorm, FormatInfo? Srgb)
{
    /// <summary>The concrete format to encode with, given whether the source is sRGB.</summary>
    public FormatInfo Resolve(bool sourceIsSrgb) =>
        sourceIsSrgb && Srgb is not null ? Srgb : Unorm;

    public override string ToString() => Label;
}

/// <summary>
/// Every encodable target family the app supports, derived from <see cref="FormatTable"/> so a new
/// format cannot be added to the table and forgotten in the recompress pickers.
/// </summary>
public static class RecompressTargets
{
    public static IReadOnlyList<RecompressTarget> All { get; } = Build();

    private static List<RecompressTarget> Build()
    {
        // Aliases (0x49/0x4B) share their name with the canonical ids, so they must be dropped before
        // grouping by name. Unknown 0x6D is Raw but is never re-encoded (its channel order is
        // unconfirmed), and RGBA16F is dropped by the codec filter.
        var candidates = FormatTable.All
            .Where(f => !f.IsAlias)
            .Where(f => f.Codec is CodecKind.Bc or CodecKind.Astc or CodecKind.Raw)
            .Where(f => f.Format != Xt1Format.Unknown6d)
            .ToList();

        var targets = new List<RecompressTarget>();
        foreach (var group in candidates.GroupBy(FamilyKey))
        {
            var members = group.ToList();
            var unorm = members.FirstOrDefault(m => !m.IsSrgb) ?? members[0];
            var srgb = members.FirstOrDefault(m => m.IsSrgb);
            targets.Add(new RecompressTarget(LabelFor(unorm), unorm, srgb));
        }

        return targets
            .OrderBy(t => CodecRank(t.Unorm.Codec))
            .ThenBy(t => t.Unorm.Value)
            .ToList();
    }

    /// <summary>Name with the trailing colour-space suffix removed, e.g. BC1_UNORM/BC1_SRGB -> BC1.</summary>
    private static string FamilyKey(FormatInfo info)
    {
        var name = info.Name;
        if (name.EndsWith("_UNORM", StringComparison.Ordinal)) return name[..^"_UNORM".Length];
        if (name.EndsWith("_SRGB", StringComparison.Ordinal)) return name[..^"_SRGB".Length];
        return name;
    }

    /// <summary>Short picker label, e.g. BC1_UNORM -> BC1, BC6H_UF16 -> BC6H, ASTC_LDR_8x8_* -> ASTC 8x8.</summary>
    private static string LabelFor(FormatInfo unorm)
    {
        var name = unorm.Name;
        if (name.EndsWith("_UNORM", StringComparison.Ordinal)) name = name[..^"_UNORM".Length];
        else if (name.EndsWith("_UF16", StringComparison.Ordinal)) name = name[..^"_UF16".Length];
        return name.Replace("ASTC_LDR_", "ASTC ");
    }

    private static int CodecRank(CodecKind codec) => codec switch
    {
        CodecKind.Bc => 0,
        CodecKind.Astc => 1,
        _ => 2,
    };
}
