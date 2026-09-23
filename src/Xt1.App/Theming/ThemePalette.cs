using System.Drawing;

namespace Xt1.App.Theming;

/// <summary>
/// The complete set of colours the UI is painted with, for one theme. Controls never hardcode a
/// colour; they read a role from the active <see cref="ThemePalette"/> so both themes stay in step.
/// </summary>
public sealed class ThemePalette
{
    /// <summary>Window / outermost panel background.</summary>
    public required Color Window { get; init; }

    /// <summary>Raised card / inspector surface sitting on <see cref="Window"/>.</summary>
    public required Color Surface { get; init; }

    /// <summary>Editable input background (text boxes, lists, trees).</summary>
    public required Color Input { get; init; }

    /// <summary>Subtle dividers and hairline borders.</summary>
    public required Color Border { get; init; }

    /// <summary>Primary body text.</summary>
    public required Color Text { get; init; }

    /// <summary>Secondary / de-emphasised text.</summary>
    public required Color MutedText { get; init; }

    /// <summary>Titles and card captions.</summary>
    public required Color HeadingText { get; init; }

    /// <summary>Selection / focus accent.</summary>
    public required Color Accent { get; init; }

    /// <summary>Buttons, toolbars and other raised controls.</summary>
    public required Color Control { get; init; }

    public required Color ControlHover { get; init; }

    public required Color ControlPressed { get; init; }

    /// <summary>Menu / drop-down background.</summary>
    public required Color MenuBack { get; init; }

    /// <summary>Highlighted menu or toolbar item.</summary>
    public required Color MenuHover { get; init; }

    /// <summary>Status bar background.</summary>
    public required Color StatusBack { get; init; }

    /// <summary>Image / 3D viewport canvas. Deliberately dark in both themes.</summary>
    public required Color Viewer { get; init; }

    public required Color OkText { get; init; }

    public required Color OkBack { get; init; }

    public required Color WarnText { get; init; }

    public required Color WarnBack { get; init; }

    public required Color PendingBack { get; init; }

    public static ThemePalette Light { get; } = new()
    {
        Window = Color.FromArgb(244, 245, 247),
        Surface = Color.White,
        Input = Color.White,
        Border = Color.FromArgb(222, 225, 230),
        Text = Color.FromArgb(31, 35, 41),
        MutedText = Color.FromArgb(107, 114, 128),
        HeadingText = Color.FromArgb(17, 24, 39),
        Accent = Color.FromArgb(59, 130, 246),
        Control = Color.FromArgb(236, 237, 240),
        ControlHover = Color.FromArgb(225, 227, 232),
        ControlPressed = Color.FromArgb(211, 214, 220),
        MenuBack = Color.White,
        MenuHover = Color.FromArgb(233, 238, 247),
        StatusBack = Color.FromArgb(236, 237, 240),
        Viewer = Color.FromArgb(32, 32, 36),
        OkText = Color.FromArgb(47, 125, 70),
        OkBack = Color.FromArgb(234, 246, 238),
        WarnText = Color.FromArgb(180, 35, 24),
        WarnBack = Color.FromArgb(253, 236, 236),
        PendingBack = Color.FromArgb(238, 242, 247),
    };

    public static ThemePalette Dark { get; } = new()
    {
        Window = Color.FromArgb(27, 28, 31),
        Surface = Color.FromArgb(36, 38, 42),
        Input = Color.FromArgb(43, 45, 50),
        Border = Color.FromArgb(58, 61, 67),
        Text = Color.FromArgb(230, 232, 236),
        MutedText = Color.FromArgb(154, 160, 170),
        HeadingText = Color.FromArgb(242, 244, 248),
        Accent = Color.FromArgb(79, 141, 247),
        Control = Color.FromArgb(42, 44, 49),
        ControlHover = Color.FromArgb(52, 55, 61),
        ControlPressed = Color.FromArgb(61, 65, 72),
        MenuBack = Color.FromArgb(36, 38, 42),
        MenuHover = Color.FromArgb(52, 55, 61),
        StatusBack = Color.FromArgb(33, 35, 39),
        Viewer = Color.FromArgb(20, 21, 25),
        OkText = Color.FromArgb(123, 216, 143),
        OkBack = Color.FromArgb(30, 51, 38),
        WarnText = Color.FromArgb(241, 154, 154),
        WarnBack = Color.FromArgb(58, 34, 36),
        PendingBack = Color.FromArgb(42, 45, 52),
    };
}
