using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Xt1.Core.Encoding;
using Xt1.Core.Format;
using Xt1.Core.Interop;
using Xt1.Core.Settings;

namespace Xt1.App.Wpf.Dialogs;

/// <summary>
/// Encoder/import defaults, preview defaults and the Explorer thumbnail registration. Edits a copy of
/// the live settings; the caller reads <see cref="Result"/> after OK. Registration itself changes
/// machine state immediately, like the WinForms dialog, and is not part of <see cref="Result"/>.
/// </summary>
public partial class SettingsDialog : ThemedDialog
{
    private const int VolumeCapMin = 64;
    private const int VolumeCapMax = 2048;
    private const int VolumeCapStep = 64;
    private const string VolumeHintText = "Larger 3D volumes are downscaled for the GPU preview only.";

    private readonly List<FormatInfo> _formats;

    /// <summary>A copy of the settings passed in, updated in place when the user accepts.</summary>
    public AppSettings Result { get; }

    public SettingsDialog(AppSettings current, string encoderDescription)
    {
        Result = CopyOf(current);
        InitializeComponent();

        // The Encoder line is the shell's own verdict, so it reflects what the app actually loads
        // rather than whether a file happens to exist. The hint below only reports which path was
        // found: NVTT is optional, an explicit path wins, and a path that does not exist falls back to
        // the app folder, the NVIDIA install directories, then PATH.
        EncoderText.Text = encoderDescription;
        NvttDllBox.Text = Result.NvttDllPath ?? string.Empty;
        UpdateNvttHint();

        _formats = FormatTable.All
            .Where(f => f.Codec is CodecKind.Bc or CodecKind.Astc or CodecKind.Raw)
            .OrderBy(f => f.Value)
            .ToList();
        FormatCombo.ItemsSource = _formats.Select(f => f.DisplayName).ToList();
        int index = _formats.FindIndex(f => f.Format == Result.DefaultImportFormat);
        FormatCombo.SelectedIndex = index >= 0 ? index : 0;

        SyncCheck.IsChecked = Result.SyncFlagsFromFormat;
        OpaqueAlphaCheck.IsChecked = Result.OpaqueAlphaByDefault;
        ShowAlphaCheck.IsChecked = Result.ShowAlphaChannel;
        VolumeCapBox.Text = Math.Clamp(Result.VolumeResolutionCap, VolumeCapMin, VolumeCapMax)
            .ToString(CultureInfo.InvariantCulture);

        RefreshShell();
        ShellMachineCheck.Checked += (_, _) => RefreshShell();
        ShellMachineCheck.Unchecked += (_, _) => RefreshShell();
    }

    /// <summary>Reflect the current registration state in the button label, enabled state and status.</summary>
    /// <summary>
    /// Say which path was found for the box's current content. Deliberately worded as presence ("used
    /// if it loads"), because only the shell can say whether the library loads — the Encoder line
    /// above carries that verdict.
    /// </summary>
    private void UpdateNvttHint()
    {
        var typed = NullIfEmpty(NvttDllBox.Text);
        bool typedExists = typed is not null && File.Exists(typed);
        var resolved = ToolLocator.Find(ToolLocator.NvttDllFileName, typed);

        EncoderHint.Text = typed is not null
            ? typedExists
                ? $"Using {resolved}. OK applies it; NVTT is used if it loads, otherwise the built-in encoder."
                : resolved is not null
                    ? $"Not found at {typed}; auto-detection is used instead ({resolved})."
                    : $"Not found at {typed}, and auto-detection found nothing; the built-in encoder is used."
            : resolved is not null
                ? $"Auto-detected {resolved}. NVTT is used if it loads, otherwise the built-in encoder."
                : "Not found automatically: the built-in encoder is used, which cannot encode BC6H.";
    }

    private void OnNvttDllChanged(object sender, TextChangedEventArgs e) => UpdateNvttHint();

    private void OnBrowseNvtt(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose nvtt30205.dll",
            Filter = "NVTT library (nvtt30205.dll)|nvtt30205.dll|DLL (*.dll)|*.dll|All files (*.*)|*.*",
            FileName = NvttDllBox.Text,
        };
        if (dialog.ShowDialog(this) == true) NvttDllBox.Text = dialog.FileName;
    }

    private static string? NullIfEmpty(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private void RefreshShell()
    {
        bool machine = ShellMachineCheck.IsChecked == true;
        bool registered = ShellIntegration.IsRegistered(machine);
        var comHost = ShellIntegration.ComHost();
        ShellButton.Content = registered ? "Remove" : "Register";
        // There is nothing to register without the COM host (a dev build without Xt1.Shell output).
        ShellButton.IsEnabled = registered || comHost is not null;
        ShellStatus.Text = ShellIntegration.Describe(machine, registered, comHost);
    }

    private void OnToggleShell(object sender, RoutedEventArgs e)
    {
        bool machine = ShellMachineCheck.IsChecked == true;
        try
        {
            if (ShellIntegration.IsRegistered(machine)) ShellIntegration.Disable(machine);
            else ShellIntegration.Enable(machine);
        }
        catch (Exception ex)
        {
            // Registration failures (no host, not elevated, policy) belong on the status line.
            ShellStatus.Text = ex.Message;
            return;
        }

        RefreshShell();
    }

    private static AppSettings CopyOf(AppSettings s) => new()
    {
        Theme = s.Theme,
        DefaultImportFormat = s.DefaultImportFormat,
        OpaqueAlphaByDefault = s.OpaqueAlphaByDefault,
        ShowAlphaChannel = s.ShowAlphaChannel,
        SyncFlagsFromFormat = s.SyncFlagsFromFormat,
        VolumeResolutionCap = s.VolumeResolutionCap,
        NvttDllPath = s.NvttDllPath,
        NvttCliPath = s.NvttCliPath,
        NvttDefaultApplied = s.NvttDefaultApplied,
        LastDirectory = s.LastDirectory,
    };


    /// <summary>Live feedback for the volume cap, which WPF has no NumericUpDown for.</summary>
    private void OnVolumeCapChanged(object sender, TextChangedEventArgs e) => UpdateVolumeHint();

    private void UpdateVolumeHint()
    {
        if (VolumeCapHint is null) return;
        if (TryParseVolumeCap(VolumeCapBox.Text, out _))
        {
            VolumeCapHint.Text = VolumeHintText;
            VolumeCapHint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Muted");
        }
        else
        {
            VolumeCapHint.Text =
                $"Enter a whole number between {VolumeCapMin} and {VolumeCapMax}; it is rounded to the nearest multiple of {VolumeCapStep}.";
            VolumeCapHint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.WarningText");
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!TryParseVolumeCap(VolumeCapBox.Text, out int cap))
        {
            UpdateVolumeHint();
            VolumeCapBox.Focus();
            return;
        }

        if (FormatCombo.SelectedIndex >= 0 && FormatCombo.SelectedIndex < _formats.Count)
            Result.DefaultImportFormat = _formats[FormatCombo.SelectedIndex].Format;
        Result.NvttDllPath = NullIfEmpty(NvttDllBox.Text);
        Result.SyncFlagsFromFormat = SyncCheck.IsChecked == true;
        Result.OpaqueAlphaByDefault = OpaqueAlphaCheck.IsChecked == true;
        Result.ShowAlphaChannel = ShowAlphaCheck.IsChecked == true;
        Result.VolumeResolutionCap = cap;
        DialogResult = true;
    }

    private static bool TryParseVolumeCap(string text, out int value)
    {
        value = 0;
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            return false;
        if (parsed < VolumeCapMin || parsed > VolumeCapMax) return false;
        int snapped = (int)Math.Round(parsed / (double)VolumeCapStep, MidpointRounding.AwayFromZero) * VolumeCapStep;
        value = Math.Clamp(snapped, VolumeCapMin, VolumeCapMax);
        return true;
    }


}

