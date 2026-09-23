using System.Drawing;

namespace Xt1.Rendering.WinForms.Theming;

/// <summary>
/// Flat, palette-driven renderer for menu, toolbar, context-menu and status strips. The stock
/// professional renderer hardcodes light gradients, so dark mode needs its own colour table.
/// </summary>
public sealed class ThemedToolStripRenderer : ToolStripProfessionalRenderer
{
    private readonly ThemePalette _palette;

    public ThemedToolStripRenderer(ThemePalette palette)
        : base(new ThemedColorTable(palette))
    {
        _palette = palette;
        RoundedEdges = false;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        if (!e.Item.Enabled) e.TextColor = _palette.MutedText;
        else e.TextColor = _palette.Text;
        base.OnRenderItemText(e);
    }
}

internal sealed class ThemedColorTable : ProfessionalColorTable
{
    private readonly ThemePalette _p;

    public ThemedColorTable(ThemePalette palette)
    {
        _p = palette;
        UseSystemColors = false;
    }

    public override Color MenuStripGradientBegin => _p.MenuBack;
    public override Color MenuStripGradientEnd => _p.MenuBack;
    public override Color MenuItemSelected => _p.MenuHover;
    public override Color MenuItemSelectedGradientBegin => _p.MenuHover;
    public override Color MenuItemSelectedGradientEnd => _p.MenuHover;
    public override Color MenuItemPressedGradientBegin => _p.MenuBack;
    public override Color MenuItemPressedGradientMiddle => _p.MenuBack;
    public override Color MenuItemPressedGradientEnd => _p.MenuBack;
    public override Color MenuItemBorder => _p.Border;
    public override Color MenuBorder => _p.Border;

    public override Color ToolStripGradientBegin => _p.Control;
    public override Color ToolStripGradientMiddle => _p.Control;
    public override Color ToolStripGradientEnd => _p.Control;
    public override Color ToolStripBorder => _p.Border;
    public override Color ToolStripDropDownBackground => _p.MenuBack;

    public override Color ImageMarginGradientBegin => _p.MenuBack;
    public override Color ImageMarginGradientMiddle => _p.MenuBack;
    public override Color ImageMarginGradientEnd => _p.MenuBack;
    public override Color ImageMarginRevealedGradientBegin => _p.MenuHover;
    public override Color ImageMarginRevealedGradientMiddle => _p.MenuHover;
    public override Color ImageMarginRevealedGradientEnd => _p.MenuHover;

    public override Color ButtonSelectedHighlight => _p.MenuHover;
    public override Color ButtonSelectedHighlightBorder => _p.Border;
    public override Color ButtonSelectedGradientBegin => _p.MenuHover;
    public override Color ButtonSelectedGradientMiddle => _p.MenuHover;
    public override Color ButtonSelectedGradientEnd => _p.MenuHover;
    public override Color ButtonSelectedBorder => _p.Border;

    public override Color ButtonPressedHighlight => _p.ControlPressed;
    public override Color ButtonPressedHighlightBorder => _p.Border;
    public override Color ButtonPressedGradientBegin => _p.ControlPressed;
    public override Color ButtonPressedGradientMiddle => _p.ControlPressed;
    public override Color ButtonPressedGradientEnd => _p.ControlPressed;
    public override Color ButtonPressedBorder => _p.Border;

    public override Color ButtonCheckedHighlight => _p.ControlPressed;
    public override Color ButtonCheckedHighlightBorder => _p.Border;
    public override Color ButtonCheckedGradientBegin => _p.ControlPressed;
    public override Color ButtonCheckedGradientMiddle => _p.ControlPressed;
    public override Color ButtonCheckedGradientEnd => _p.ControlPressed;

    public override Color CheckBackground => _p.Accent;
    public override Color CheckSelectedBackground => _p.Accent;
    public override Color CheckPressedBackground => _p.Accent;

    public override Color SeparatorDark => _p.Border;
    public override Color SeparatorLight => _p.Border;

    public override Color StatusStripGradientBegin => _p.StatusBack;
    public override Color StatusStripGradientEnd => _p.StatusBack;

    public override Color GripDark => _p.Border;
    public override Color GripLight => _p.Border;

    public override Color OverflowButtonGradientBegin => _p.Control;
    public override Color OverflowButtonGradientMiddle => _p.Control;
    public override Color OverflowButtonGradientEnd => _p.Control;

    public override Color RaftingContainerGradientBegin => _p.Control;
    public override Color RaftingContainerGradientEnd => _p.Control;
}

