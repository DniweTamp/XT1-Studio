using System.Drawing;
using Xt1.App.Theming;

namespace Xt1.App.Controls;

/// <summary>
/// TabControl with palette-drawn headers, so the 2D / Cube-3D tabs are readable in dark mode instead
/// of keeping the stock light chrome. Page content is still painted by the child TabPages.
/// </summary>
public sealed class ThemedTabControl : TabControl
{
    public ThemedTabControl()
    {
        DrawMode = TabDrawMode.OwnerDrawFixed;
        SizeMode = TabSizeMode.Fixed;
        ItemSize = new Size(96, 26);
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
        ThemeManager.Changed += OnThemeChanged;
    }

    private void OnThemeChanged() => Invalidate();

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= TabPages.Count) return;

        var palette = ThemeManager.Palette;
        var graphics = e.Graphics;
        var bounds = GetTabRect(e.Index);
        bool selected = e.Index == SelectedIndex;

        using (var back = new SolidBrush(selected ? palette.Surface : palette.Window))
            graphics.FillRectangle(back, bounds);

        if (selected)
        {
            using var accent = new SolidBrush(palette.Accent);
            graphics.FillRectangle(accent, bounds.Left + 1, bounds.Bottom - 2, Math.Max(0, bounds.Width - 2), 2);
        }

        TextRenderer.DrawText(graphics, TabPages[e.Index].Text, Font, bounds,
            selected ? palette.Text : palette.MutedText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ThemeManager.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }
}
