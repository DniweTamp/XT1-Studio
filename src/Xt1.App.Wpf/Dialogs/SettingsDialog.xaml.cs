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
/// Encoder defaults, the import dialog's format filter, preview defaults and the Explorer thumbnail
/// registration. Edits a copy of the live settings; the caller reads <see cref="Result"/> after OK.
/// Registration itself changes machine state immediately, like the WinForms dialog, and is not part
/// of <see cref="Result"/>.
/// </summary>
public partial class SettingsDialog : ThemedDialog
{
    private const int VolumeCapMin = 64;
    private const int VolumeCapMax = 2048;
    private const int VolumeCapStep = 64;
    private const string VolumeHintText = "Larger 3D volumes are downscaled for the GPU preview only.";

    private readonly List<FormatInfo> _formats = new();
    private readonly Dictionary<Xt1Format, CheckBox> _formatChecks = new();

    /// <summary>A copy of the settings passed in, updated in place when the user accepts.</summary>
    public AppSettings Result { get; }

    public SettingsDialog(AppSettings current, string encoderDescription, bool nvttActive)
    {
        Result = CopyOf(current);
        InitializeComponent();

        // The Encoder line is the shell's own verdict, so it reflects what the app actually loads
        // rather than whether a file happens to exist. The hint below only reports which path was
        // found: NVTT is optional, an explicit path wins, and a path that does not exist falls back to
        // the app folder, the NVIDIA install directories, then PATH.
        EncoderText.Text = encoderDescription;
        ApplyEncoderStatus(nvttActive);
        NvttDllBox.Text = Result.NvttDllPath ?? string.Empty;
        UpdateNvttHint();

        BuildFormatList();

        SyncCheck.IsChecked = Result.SyncFlagsFromFormat;
        OpaqueAlphaCheck.IsChecked = Result.OpaqueAlphaByDefault;
        ShowAlphaCheck.IsChecked = Result.ShowAlphaChannel;
        VolumeCapBox.Text = Math.Clamp(Result.VolumeResolutionCap, VolumeCapMin, VolumeCapMax)
            .ToString(CultureInfo.InvariantCulture);

        RefreshShell();
        ShellMachineCheck.Checked += (_, _) => RefreshShell();
        ShellMachineCheck.Unchecked += (_, _) => RefreshShell();
    }

    /// <summary>
    /// Build the "Import formats" checklist from the encodable formats, grouped by codec. A null
    /// filter means everything is on; otherwise only the recorded formats are ticked. Built in code
    /// because the list is long and its contents come from the format table, not from XAML.
    /// </summary>
    private void BuildFormatList()
    {
        bool all = Result.ImportFormats is null;
        var enabled = all ? null : new HashSet<Xt1Format>(Result.ImportFormats!);
        // A filter whose entries are all unknown to this build (a settings file from a newer version)
        // must not open with nothing ticked: fall back to offering everything.
        if (!all && !FormatTable.Encodable.Any(f => enabled!.Contains(f.Format)))
        {
            all = true;
            enabled = null;
        }

        foreach (var group in FormatTable.Encodable
                     .GroupBy(f => f.Codec)
                     .OrderBy(g => g.Key switch { CodecKind.Bc => 0, CodecKind.Astc => 1, _ => 2 }))
        {
            FormatList.Children.Add(new TextBlock
            {
                Text = group.Key switch
                {
                    CodecKind.Bc => "Block compression (BC)",
                    CodecKind.Astc => "Adaptive scalable texture compression (ASTC)",
                    _ => "Uncompressed",
                },
                Style = (Style)FindResource("SectionHeader"),
                Margin = new Thickness(0, FormatList.Children.Count == 0 ? 0 : 14, 0, 8),
            });

            var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var info in group.OrderBy(f => f.Value))
            {
                var check = new CheckBox
                {
                    Content = info.DisplayName,
                    Style = (Style)FindResource("PillCheckBox"),
                    Margin = new Thickness(0, 0, 8, 8),
                    IsChecked = all || enabled!.Contains(info.Format),
                };
                check.Checked += OnFormatCheckChanged;
                check.Unchecked += OnFormatCheckChanged;
                _formatChecks[info.Format] = check;
                wrap.Children.Add(check);
            }
            FormatList.Children.Add(wrap);
        }

        OnFormatSelectionChanged();
    }

    /// <summary>
    /// Rebuild the "Default import format" combo from the ticked formats so it can never offer — or
    /// save — a format the import dialog would hide. The previous selection is kept when it is still
    /// enabled, otherwise the recorded default is used, otherwise the first enabled format.
    /// </summary>
    private void RefreshFormatCombo()
    {
        var previous = FormatCombo.SelectedIndex >= 0 && FormatCombo.SelectedIndex < _formats.Count
            ? _formats[FormatCombo.SelectedIndex].Format
            : Result.DefaultImportFormat;

        _formats.Clear();
        _formats.AddRange(FormatTable.Encodable.Where(f =>
            _formatChecks.TryGetValue(f.Format, out var check) && check.IsChecked == true));

        FormatCombo.ItemsSource = _formats.Select(f => f.DisplayName).ToList();
        int index = _formats.FindIndex(f => f.Format == previous);
        if (index < 0) index = _formats.FindIndex(f => f.Format == Result.DefaultImportFormat);
        FormatCombo.SelectedIndex = index >= 0 ? index : (_formats.Count > 0 ? 0 : -1);
    }

    /// <summary>Keep the default-format combo and the count hint in step with the checklist.</summary>
    private void OnFormatSelectionChanged()
    {
        RefreshFormatCombo();
        UpdateFormatHint();
    }

    /// <summary>
    /// A one-line summary for the headless UI smoke; constructing the dialog is what validates its XAML
    /// and style references (including the code-built import-format checklist), so the caller only
    /// reads this.
    /// </summary>
    internal string UiTestState()
    {
        int on = _formatChecks.Count(pair => pair.Value.IsChecked == true);
        return $"formats={_formatChecks.Count} enabled={on} hint={!string.IsNullOrEmpty(FormatHint.Text)}";
    }

    private void OnFormatCheckChanged(object sender, RoutedEventArgs e) => OnFormatSelectionChanged();

    private void OnFormatSelectAll(object sender, RoutedEventArgs e) => SetAllFormatChecks(true);

    private void OnFormatSelectNone(object sender, RoutedEventArgs e) => SetAllFormatChecks(false);

    private void SetAllFormatChecks(bool value)
    {
        foreach (var check in _formatChecks.Values) check.IsChecked = value;
        OnFormatSelectionChanged();
    }

    /// <summary>Live count of ticked formats, warning when the selection would empty the dialog.</summary>
    private void UpdateFormatHint()
    {
        if (FormatHint is null) return;
        int on = _formatChecks.Count(pair => pair.Value.IsChecked == true);
        int total = _formatChecks.Count;
        if (on == 0)
        {
            FormatHint.Text = "At least one format must stay enabled.";
            FormatHint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.WarningText");
        }
        else
        {
            FormatHint.Text = on == total
                ? $"All {total} formats are offered in the import dialog."
                : $"{on} of {total} formats are offered in the import dialog.";
            FormatHint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Muted");
        }
    }

    /// <summary>
    /// Tint the encoder verdict box when NVTT is actually in use, and say when it was picked up by
    /// auto-detection rather than from a path the user configured.
    /// </summary>
    private void ApplyEncoderStatus(bool nvttActive)
    {
        if (!nvttActive) return;

        if (string.IsNullOrWhiteSpace(Result.NvttDllPath))
            EncoderText.Text += " - auto detected";

        EncoderStatusBox.SetResourceReference(Border.BackgroundProperty, "Brush.Success");
        EncoderStatusBox.SetResourceReference(Border.BorderBrushProperty, "Brush.Success");
        EncoderText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.SuccessText");
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
        ImportFormats = s.ImportFormats is null ? null : new List<Xt1Format>(s.ImportFormats),
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

        var chosenFormats = FormatTable.Encodable
            .Where(f => _formatChecks.TryGetValue(f.Format, out var check) && check.IsChecked == true)
            .Select(f => f.Format)
            .ToList();
        if (chosenFormats.Count == 0)
        {
            UpdateFormatHint();
            return;
        }

        if (FormatCombo.SelectedIndex >= 0 && FormatCombo.SelectedIndex < _formats.Count)
            Result.DefaultImportFormat = _formats[FormatCombo.SelectedIndex].Format;
        // A full selection is stored as null ("no filter") so the settings file stays minimal and a
        // format added by a later version is visible without the user having to revisit this tab.
        Result.ImportFormats = chosenFormats.Count == _formatChecks.Count ? null : chosenFormats;
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

