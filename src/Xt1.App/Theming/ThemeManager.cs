using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Xt1.Core.Settings;

namespace Xt1.App.Theming;

/// <summary>Semantic colour a control wants, independent of the active theme.</summary>
public enum ThemeRole
{
    /// <summary>Default role for the control's type.</summary>
    None,

    /// <summary>De-emphasised text.</summary>
    Muted,

    /// <summary>Title / heading text.</summary>
    Heading,
}

/// <summary>
/// A control that themes its own subtree. The generic pass calls <see cref="ApplyTheme"/> and does not
/// recurse into the control's children, so the control is responsible for them (normally by calling
/// <see cref="ThemeManager.ApplyTo"/> on its root panel).
/// </summary>
public interface IThemedControl
{
    void ApplyTheme(ThemePalette palette);
}

/// <summary>
/// Owns the active theme: resolves the OS preference, paints any control tree with the current
/// palette, and repaints every open form when the choice changes. Kept deliberately free of app
/// state so <c>--panelshot</c> and the dialogs get the same treatment as the main window.
/// </summary>
public static class ThemeManager
{
    private static readonly ConditionalWeakTable<Control, RoleBox> Roles = new();
    private static readonly ConditionalWeakTable<Control, object> WindowThemeHooked = new();
    private static readonly ConditionalWeakTable<Control, object> TitleBarHooked = new();

    private sealed class RoleBox
    {
        public ThemeRole Role;
    }

    public static AppTheme Preference { get; private set; } = AppTheme.System;

    public static bool IsDark { get; private set; }

    public static ThemePalette Palette { get; private set; } = ThemePalette.Light;

    /// <summary>Raised after a theme change has been applied to every open form.</summary>
    public static event Action? Changed;

    /// <summary>Record which semantic colour a control uses; read by the generic theming pass.</summary>
    public static void SetRole(Control control, ThemeRole role)
    {
        if (Roles.TryGetValue(control, out var box)) box.Role = role;
        else Roles.Add(control, new RoleBox { Role = role });
    }

    /// <summary>Resolve and store the theme choice, then repaint every open form.</summary>
    public static void Initialize(AppTheme preference)
    {
        Preference = preference;
        IsDark = Resolve(preference);
        Palette = IsDark ? ThemePalette.Dark : ThemePalette.Light;
        ApplyToOpenForms();
    }

    /// <summary>Change the theme at runtime (as the View menu does) and repaint open forms.</summary>
    public static void SetPreference(AppTheme preference)
    {
        if (Preference == preference) return;
        Initialize(preference);
        Changed?.Invoke();
    }

    /// <summary>The OS light/dark preference, or light when it cannot be read.</summary>
    public static bool SystemPrefersDark()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int light) return light == 0;
        }
        catch
        {
            // An unreadable or absent value means "light", which is also the Windows default.
        }
        return false;
    }

    private static bool Resolve(AppTheme preference) => preference switch
    {
        AppTheme.Dark => true,
        AppTheme.Light => false,
        _ => SystemPrefersDark(),
    };

    private static void ApplyToOpenForms()
    {
        foreach (Form form in Application.OpenForms)
        {
            if (form.IsDisposed) continue;
            Apply(form);
        }
    }

    /// <summary>Theme a form and its whole control tree, including its title bar.</summary>
    public static void Apply(Form form)
    {
        ApplyTo(form);
        ApplyTitleBar(form);
    }

    /// <summary>Recursively paint a control tree with the active palette.</summary>
    public static void ApplyTo(Control? control)
    {
        if (control is null || control.IsDisposed) return;

        // A themed control owns its subtree; it reapplies children itself.
        if (control is IThemedControl themed)
        {
            themed.ApplyTheme(Palette);
            return;
        }

        switch (control)
        {
            case TreeView tree:
                tree.BackColor = Palette.Input;
                tree.ForeColor = Palette.Text;
                tree.LineColor = Palette.Border;
                ApplyNativeWindowTheme(tree);
                break;

            case ListView list:
                list.BackColor = Palette.Input;
                list.ForeColor = Palette.Text;
                ApplyNativeWindowTheme(list);
                break;

            case TextBoxBase text:
                text.BackColor = RoleBack(control, Palette.Input);
                text.ForeColor = RoleFore(control);
                break;

            case NumericUpDown numeric:
                numeric.BackColor = Palette.Input;
                numeric.ForeColor = Palette.Text;
                break;

            case ComboBox combo:
                combo.FlatStyle = FlatStyle.Flat;
                combo.BackColor = Palette.Input;
                combo.ForeColor = Palette.Text;
                break;

            case CheckBox check:
                check.FlatStyle = FlatStyle.Flat;
                check.ForeColor = RoleFore(control);
                check.BackColor = Color.Transparent;
                break;

            case RadioButton radio:
                radio.FlatStyle = FlatStyle.Flat;
                radio.ForeColor = RoleFore(control);
                radio.BackColor = Color.Transparent;
                break;

            case Button button:
                ThemeButton(button);
                break;

            case TabControl tabs:
                tabs.BackColor = Palette.Window;
                tabs.ForeColor = Palette.Text;
                break;

            case TabPage page:
                page.BackColor = Palette.Window;
                page.ForeColor = Palette.Text;
                break;

            case SplitContainer split:
                split.BackColor = Palette.Window;
                break;

            case ToolStrip strip:
                ThemeToolStrip(strip);
                break;

            case Form form:
                form.BackColor = Palette.Window;
                form.ForeColor = Palette.Text;
                break;

            case Label or LinkLabel:
                control.ForeColor = RoleFore(control);
                control.BackColor = Color.Transparent;
                break;

            case ProgressBar:
                break;

            default:
                control.BackColor = RoleBack(control, Palette.Window);
                control.ForeColor = Palette.Text;
                break;
        }

        foreach (Control child in control.Controls) ApplyTo(child);
    }

    private static void ThemeButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = Palette.Control;
        button.ForeColor = Palette.Text;
        button.FlatAppearance.BorderColor = Palette.Border;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = Palette.ControlHover;
        button.FlatAppearance.MouseDownBackColor = Palette.ControlPressed;
    }

    private static void ThemeToolStrip(ToolStrip strip)
    {
        // Assigning Renderer puts the strip into custom render mode; do not also set RenderMode
        // (setting it first leaves the strip briefly with no renderer, which wedges painting).
        strip.Renderer = new ThemedToolStripRenderer(Palette);
        strip.BackColor = strip switch
        {
            StatusStrip => Palette.StatusBack,
            MenuStrip => Palette.MenuBack,
            _ => Palette.Control,
        };
        strip.ForeColor = Palette.Text;

        // Embedded combos are real ComboBoxes once the strip owns them; theme those too.
        foreach (ToolStripItem item in strip.Items)
        {
            if (item is ToolStripComboBox combo)
            {
                combo.BackColor = Palette.Input;
                combo.ForeColor = Palette.Text;
                combo.ComboBox.FlatStyle = FlatStyle.Flat;
                combo.ComboBox.BackColor = Palette.Input;
                combo.ComboBox.ForeColor = Palette.Text;
            }
            else if (item is ToolStripDropDownItem drop)
            {
                ThemeToolStrip(drop.DropDown);
            }
        }
    }

    private static Color RoleFore(Control control)
    {
        return Role(control) switch
        {
            ThemeRole.Muted => Palette.MutedText,
            ThemeRole.Heading => Palette.HeadingText,
            _ => Palette.Text,
        };
    }

    private static Color RoleBack(Control control, Color fallback) =>
        control.BackColor == Color.Transparent ? Color.Transparent : fallback;

    private static ThemeRole Role(Control control) =>
        Roles.TryGetValue(control, out var box) ? box.Role : ThemeRole.None;

    // --- native theming -----------------------------------------------------------------

    private static void ApplyNativeWindowTheme(Control control)
    {
        void Apply()
        {
            if (!OperatingSystem.IsWindows() || !control.IsHandleCreated) return;
            try
            {
                SetWindowTheme(control.Handle, IsDark ? "DarkMode_Explorer" : "Explorer", null);
            }
            catch
            {
                // uxtheme is best-effort; the control still paints with the palette colours.
            }
        }

        Apply();
        if (WindowThemeHooked.TryGetValue(control, out _)) return;
        WindowThemeHooked.Add(control, new object());
        control.HandleCreated += (_, _) => Apply();
    }

    /// <summary>Paint the non-client title bar dark to match (Windows 10 1809+).</summary>
    public static void ApplyTitleBar(Form form)
    {
        void Apply()
        {
            if (!OperatingSystem.IsWindows() || !form.IsHandleCreated) return;
            int dark = IsDark ? 1 : 0;
            try
            {
                // 20 is the documented attribute; 19 is the pre-20H1 name for the same flag.
                if (DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int)) != 0)
                    DwmSetWindowAttribute(form.Handle, 19, ref dark, sizeof(int));
            }
            catch
            {
                // Older Windows or a non-DWM session: the title bar simply keeps its system colour.
            }
        }

        Apply();
        if (TitleBarHooked.TryGetValue(form, out _)) return;
        TitleBarHooked.Add(form, new object());
        form.HandleCreated += (_, _) => Apply();
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string? subAppName, string? subIdList);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);
}
