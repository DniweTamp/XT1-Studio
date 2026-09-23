using System.Globalization;
using System.Windows;
using System.Windows.Controls;
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

    public SettingsDialog(AppSettings current)
    {
        Result = CopyOf(current);
        InitializeComponent();

        // The encoder is not a choice: report which one the app resolved. nvtt30205.dll is looked up
        // beside the executable; the built-in encoder takes over when it is not deployed.
        var nvttDll = ToolLocator.Find(ToolLocator.NvttDllFileName);
        EncoderText.Text = nvttDll is not null
            ? "NVTT (nvtt30205.dll)"
            : "Built-in encoder (nvtt30205.dll not found)";

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

