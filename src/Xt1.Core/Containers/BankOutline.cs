using Xt1.Core.Xt1;

namespace Xt1.Core.Containers;

public enum OutlineKind
{
    /// <summary>The container itself (`.dat`, `.wta`, `.wtb`, `.xt1`).</summary>
    Document,

    /// <summary>A bank inside the container (a `"wta"` sub-file, or the single WTB).</summary>
    SubFile,

    /// <summary>A texture record.</summary>
    Texture,
}

/// <summary>One node of the container hierarchy shown in the UI tree.</summary>
public sealed class BankOutlineNode
{
    public required string Label { get; init; }
    public required OutlineKind Kind { get; init; }
    public BankSection? Section { get; init; }
    public EditableTexture? Texture { get; init; }
    public List<BankOutlineNode> Children { get; } = new();

    public override string ToString() => Label;
}

/// <summary>
/// Builds the collapsible container → sub-file → texture hierarchy. A `.dat` that holds several
/// `"wta"` banks produces one branch per bank:
/// <code>
/// ui_core.dat  (DAT)
///   wta[7]  (10 textures)
///     #0  ASTC_LDR_6x6_SRGB  1024x1024  m1
///   wta[8]  (4 textures)
///     #0  ...
/// </code>
/// Kept in the core so the grouping/labeling rules are unit-testable without a UI.
/// </summary>
public static class BankOutline
{
    /// <summary>The root label for a document, e.g. <c>ui_core.dat  (DAT)</c>.</summary>
    public static string DocumentLabel(BankDocument document)
    {
        var kind = document.Kind switch
        {
            BankKind.ModelDat => "DAT",
            BankKind.EffectWta => "WTA",
            BankKind.StandaloneWtb => "WTB",
            BankKind.RawXt1 => "XT1",
            _ => "bank",
        };
        return $"{document.DisplayName}  ({kind})";
    }

    /// <summary>The label for a bank branch, e.g. <c>wta[7]  (10 textures)</c>.</summary>
    public static string SectionLabel(BankSection section)
    {
        int count = section.Bank.Count;
        return $"{section.Label}  ({count} {(count == 1 ? "texture" : "textures")})";
    }

    /// <summary>
    /// The label for a texture leaf: <c>#0  0x421E5ED4  BC1_UNORM  512x512  m10</c>. The table3
    /// hash is shown after the index when the bank carries a hash table (materials bind textures
    /// by it); effect banks omit table3 entirely, so the segment is dropped there.
    /// </summary>
    public static string TextureLabel(EditableTexture texture, bool includeHash = false)
    {
        var info = texture.FormatInfo;
        var label = $"#{texture.Index}  ";
        if (includeHash) label += $"0x{texture.EffectiveHash():X8}  ";
        label += $"{info?.Name ?? "?"}  {texture.Header.Width}x{texture.Header.Height}";
        if (texture.Header.Type.Is3D()) label += $"x{texture.Header.Depth}";
        label += $"  m{texture.Header.MipCount}";
        if (texture.Header.Type.IsCube()) label += "  cube";
        return label;
    }

    /// <summary>
    /// Build the hierarchy. <paramref name="rawTexture"/> is the optional preview wrapper for a
    /// raw `.xt1` document (which has no WTB bank of its own).
    /// </summary>
    public static BankOutlineNode Build(BankDocument document, EditableTexture? rawTexture = null)
    {
        var root = new BankOutlineNode
        {
            Label = DocumentLabel(document),
            Kind = OutlineKind.Document,
        };

        if (document.RawXt1 is not null)
        {
            var container = new BankOutlineNode
            {
                Label = "xt1  (raw payload)",
                Kind = OutlineKind.SubFile,
            };
            if (rawTexture is not null)
            {
                container.Children.Add(new BankOutlineNode
                {
                    Label = TextureLabel(rawTexture),
                    Kind = OutlineKind.Texture,
                    Texture = rawTexture,
                });
            }
            root.Children.Add(container);
            return root;
        }

        foreach (var section in document.Sections)
        {
            var node = new BankOutlineNode
            {
                Label = SectionLabel(section),
                Kind = OutlineKind.SubFile,
                Section = section,
            };
            foreach (var texture in section.Bank.Textures)
            {
                node.Children.Add(new BankOutlineNode
                {
                    Label = TextureLabel(texture, section.Bank.IncludeTable3),
                    Kind = OutlineKind.Texture,
                    Texture = texture,
                    Section = section,
                });
            }
            root.Children.Add(node);
        }

        return root;
    }

    /// <summary>Labels for the bank selector; empty when the container has a single bank.</summary>
    public static IReadOnlyList<string> SelectorLabels(BankDocument document)
    {
        if (document.Sections.Count <= 1)
            return Array.Empty<string>();

        var labels = new List<string>(document.Sections.Count);
        foreach (var section in document.Sections)
            labels.Add($"{section.Label}  ({section.Bank.Count})");
        return labels;
    }
}
