using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using Xt1.App.Wpf.Theming;
using Xt1.Core.Editing;
using Xt1.Core.Encoding;

namespace Xt1.App.Wpf.Dialogs;

/// <summary>What the caller's runner should recompress: the loaded document, or a set of files.</summary>
public sealed record RecompressRequest(RecompressOptions Options, bool CurrentDocument, IReadOnlyList<string> Files);

/// <summary>
/// Batch recompresses ASTC textures to BC, either in the currently loaded document or across every
/// matching bank in a folder. Existing mips are recompressed; none are regenerated. Port of the
/// WinForms dialog; all work is done by the caller's runner so this window stays UI-only.
/// </summary>
public partial class BatchRecompressDialog : ThemedDialog
{
    private readonly Func<RecompressRequest, IProgress<string>, CancellationToken, Task> _runner;
    private CancellationTokenSource? _cancellation;
    private Storyboard? _busy;

    public BatchRecompressDialog(
        Func<RecompressRequest, IProgress<string>, CancellationToken, Task> runner,
        bool hasDocument)
    {
        _runner = runner;
        InitializeComponent();

        // Bind the enum values themselves rather than relying on item order matching the enum, so
        // reordering RecompressFormatRule can never silently select the wrong BC target.
        RuleCombo.ItemsSource = new[]
        {
            new RuleOption(RecompressFormatRule.AlwaysBc1, "Always BC1"),
            new RuleOption(RecompressFormatRule.Bc1OrBc3, "BC1 when opaque, BC3 when it has alpha"),
            new RuleOption(RecompressFormatRule.Bc1OrBc7, "BC1 when opaque, BC7 when it has alpha"),
            new RuleOption(RecompressFormatRule.AlwaysBc7, "Always BC7"),
        };
        RuleCombo.SelectedIndex = 1;

        var qualities = new[]
        {
            EncodeQuality.Fastest, EncodeQuality.Normal, EncodeQuality.Production, EncodeQuality.Highest,
        };
        OpaqueQualityCombo.ItemsSource = qualities;
        AlphaQualityCombo.ItemsSource = qualities;
        OpaqueQualityCombo.SelectedItem = EncodeQuality.Normal;
        AlphaQualityCombo.SelectedItem = EncodeQuality.Normal;

        CurrentRadio.IsEnabled = hasDocument;
        CurrentRadio.IsChecked = hasDocument;
        FolderRadio.IsChecked = !hasDocument;

        KeepNoMipsCheck.IsChecked = true;
        SyncFlagsCheck.IsChecked = true;

        UpdateSourceControls();
        UpdateQualityControls();
    }

    /// <summary>Resolved options from the current control state (exposed for tests/automation).</summary>
    public RecompressOptions Options => new(
        (RuleCombo.SelectedItem as RuleOption)?.Rule ?? RecompressFormatRule.Bc1OrBc3,
        DownscaleCheck.IsChecked == true,
        KeepNoMipsCheck.IsChecked == true,
        // NVTT when available; EncoderPicker substitutes the built-in managed encoder otherwise.
        EncoderBackend.Nvtt,
        (OpaqueQualityCombo.SelectedItem as EncodeQuality?) ?? EncodeQuality.Normal,
        (AlphaQualityCombo.SelectedItem as EncodeQuality?) ?? EncodeQuality.Normal,
        SyncFlagsCheck.IsChecked == true);

    private void OnSourceChanged(object sender, RoutedEventArgs e) => UpdateSourceControls();

    private void OnDownscaleChanged(object sender, RoutedEventArgs e) =>
        // "Do not downscale without mipmaps" only means anything while downscaling.
        KeepNoMipsCheck.IsEnabled = DownscaleCheck.IsChecked == true;

    private void OnRuleChanged(object sender, SelectionChangedEventArgs e) => UpdateQualityControls();

    private void UpdateSourceControls()
    {
        bool folder = FolderRadio.IsChecked == true;
        FolderBox.IsEnabled = folder;
        BrowseButton.IsEnabled = folder;
        RecursiveCheck.IsEnabled = folder;
        KeepNoMipsCheck.IsEnabled = DownscaleCheck.IsChecked == true;
    }

    private void UpdateQualityControls()
    {
        var rule = (RuleCombo.SelectedItem as RuleOption)?.Rule ?? RecompressFormatRule.Bc1OrBc3;
        bool mixed = RecompressOptions.UsesSeparateAlphaQuality(rule);
        OpaqueQualityLabel.Text = mixed ? "Opaque quality" : "Quality";
        AlphaQualityLabel.Visibility = mixed ? Visibility.Visible : Visibility.Collapsed;
        AlphaQualityCombo.Visibility = mixed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the source folder" };
        if (!string.IsNullOrWhiteSpace(FolderBox.Text)) dialog.InitialDirectory = FolderBox.Text;
        if (dialog.ShowDialog(this) == true) FolderBox.Text = dialog.FolderName;
    }

    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = false;

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        if (_cancellation is not null)
        {
            _cancellation.Cancel();
            return;
        }

        bool current = CurrentRadio.IsChecked == true;
        IReadOnlyList<string> files;
        if (current)
        {
            files = Array.Empty<string>();
        }
        else
        {
            if (!Directory.Exists(FolderBox.Text))
            {
                RunStatus.Text = "Choose an existing source folder.";
                return;
            }

            var search = RecursiveCheck.IsChecked == true ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            files = Directory.EnumerateFiles(FolderBox.Text, "*.*", search)
                .Where(f => f.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".wta", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".wtb", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".xt1", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (files.Count == 0)
            {
                RunStatus.Text = "No .dat/.wta/.wtb/.xt1 files found.";
                return;
            }

            // Show the resolved list so the scope of an in-place rewrite is visible before it happens.
            const int shown = 10;
            var listing = string.Join(Environment.NewLine, files.Take(shown).Select(Path.GetFileName));
            if (files.Count > shown) listing += Environment.NewLine + $"... and {files.Count - shown} more";
            bool proceed = ConfirmDialog.Confirm(this, "Overwrite originals?",
                $"This rewrites {files.Count} file(s) in place, replacing every ASTC texture that converts " +
                $"with a BC texture:{Environment.NewLine}{Environment.NewLine}{listing}{Environment.NewLine}{Environment.NewLine}" +
                "There is no backup. Continue?", "Overwrite");
            if (!proceed)
            {
                RunStatus.Text = "Canceled.";
                return;
            }
        }

        LogBox.Clear();
        RunStatus.Text = "Running...";
        RunButton.Content = "Cancel";
        StartBusy();

        _cancellation = new CancellationTokenSource();
        var progress = new Progress<string>(text =>
        {
            LogBox.AppendText(text + Environment.NewLine);
            LogBox.ScrollToEnd();
        });

        try
        {
            await _runner(new RecompressRequest(Options, current, files), progress, _cancellation.Token);
            LogBox.AppendText("Done." + Environment.NewLine);
            RunStatus.Text = "Done.";
        }
        catch (OperationCanceledException)
        {
            LogBox.AppendText("Canceled." + Environment.NewLine);
            RunStatus.Text = "Canceled.";
        }
        catch (Exception ex)
        {
            LogBox.AppendText("Failed: " + ex.Message + Environment.NewLine);
            RunStatus.Text = "Failed: " + ex.Message;
        }
        finally
        {
            StopBusy();
            RunButton.Content = "Run";
            _cancellation.Dispose();
            _cancellation = null;
        }
    }

    /// <summary>Sweep the accent block inside the clipped strip while work is in flight.</summary>
    private void StartBusy()
    {
        BusyBar.Visibility = Visibility.Visible;
        var animation = new DoubleAnimation
        {
            From = -140,
            To = Math.Max(240, BusyBar.ActualWidth),
            Duration = TimeSpan.FromMilliseconds(1100),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(animation, BusyBlock);
        Storyboard.SetTargetProperty(animation, new PropertyPath("RenderTransform.X"));
        _busy = new Storyboard();
        _busy.Children.Add(animation);
        _busy.Begin();
    }

    private void StopBusy()
    {
        _busy?.Stop();
        _busy = null;
        BusyShift.X = -140;
        BusyBar.Visibility = Visibility.Collapsed;
    }

    /// <summary>Combo item binding an enum value to a display label.</summary>
    private sealed record RuleOption(RecompressFormatRule Rule, string Label)
    {
        public override string ToString() => Label;
    }
}

