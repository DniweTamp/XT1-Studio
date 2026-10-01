using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using Xt1.Core.Editing;
using Xt1.Core.Encoding;

namespace Xt1.App.Wpf.Dialogs;

/// <summary>A plan plus the one-time options chosen for it.</summary>
public sealed record BatchReplaceRequest(BatchReplacePlan Plan, BatchReplaceOptions Options);

/// <summary>
/// Batch replace from a single folder. Scanning and applying are done by the caller's delegates, so
/// this window stays UI-only: it shows what would change, collects the one quality choice, runs the
/// batch and reports the result. Closing while a run is in flight cancels it and closes once it stops.
/// </summary>
public partial class BatchReplaceDialog : ThemedDialog
{
    private readonly Func<string, CancellationToken, Task<BatchReplacePlan>> _scan;
    private readonly Func<BatchReplaceRequest, IProgress<string>, CancellationToken, Task<BatchReplaceRunResult>> _run;
    private readonly Action<BatchReplaceRunResult> _onApplied;

    private BatchReplacePlan? _plan;
    private CancellationTokenSource? _cancellation;
    private Storyboard? _busy;
    private bool _applied;
    private bool _closeAfterRun;

    public BatchReplaceDialog(
        string bankLabel,
        string initialFolder,
        Func<string, CancellationToken, Task<BatchReplacePlan>> scan,
        Func<BatchReplaceRequest, IProgress<string>, CancellationToken, Task<BatchReplaceRunResult>> run,
        Action<BatchReplaceRunResult> onApplied)
    {
        _scan = scan;
        _run = run;
        _onApplied = onApplied;

        InitializeComponent();

        BankText.Text = bankLabel;
        FolderBox.Text = initialFolder;
        QualityCombo.ItemsSource = Enum.GetValues<EncodeQuality>();
        QualityCombo.SelectedItem = EncodeQuality.Normal;
        OptionsCard.IsEnabled = false;

        Loaded += async (_, _) => await ScanAsync();
    }

    /// <summary>
    /// A one-line summary for the headless UI smoke; constructing the window is what validates its
    /// XAML and style references, so the caller only reads this.
    /// </summary>
    internal string UiTestState() =>
        $"qualities={QualityCombo.Items.Count} options={OptionsCard.IsEnabled} run={RunButton.IsEnabled}";

    private async void OnBrowse(object sender, RoutedEventArgs e)
    {
        // A scan or run in flight owns the folder; don't queue another (FolderBrowse is disabled then,
        // but guard anyway so a stray click cannot start a second scan).
        if (_applied || _cancellation is not null) return;

        var dialog = new OpenFolderDialog { Title = "Choose the folder of replacement files" };
        if (Directory.Exists(FolderBox.Text)) dialog.InitialDirectory = FolderBox.Text;
        if (dialog.ShowDialog(this) != true) return;

        FolderBox.Text = dialog.FolderName;
        await ScanAsync();
    }

    /// <summary>Scan the chosen folder and show what would change, before anything is touched.</summary>
    private async Task ScanAsync()
    {
        string folder = FolderBox.Text?.Trim() ?? string.Empty;
        if (!Directory.Exists(folder))
        {
            RunStatus.Text = "Choose an existing folder.";
            RunButton.IsEnabled = false;
            OptionsCard.IsEnabled = false;
            return;
        }

        RunButton.IsEnabled = false;
        OptionsCard.IsEnabled = false;
        FolderBrowse.IsEnabled = false;
        RunStatus.Text = "Scanning...";
        StartBusy();

        // The CTS is captured locally so a superseded scan can never dispose or null the live one.
        var cts = new CancellationTokenSource();
        _cancellation = cts;
        try
        {
            _plan = await _scan(folder, cts.Token);
            ShowPlan(_plan);
        }
        catch (OperationCanceledException)
        {
            RunStatus.Text = "Scan canceled.";
        }
        catch (Exception ex)
        {
            RunStatus.Text = "Could not scan the folder: " + ex.Message;
        }
        finally
        {
            StopBusy();
            if (ReferenceEquals(_cancellation, cts)) _cancellation = null;
            cts.Dispose();
            if (!_applied) FolderBrowse.IsEnabled = true;
            if (_closeAfterRun) Close();
        }
    }

    private void ShowPlan(BatchReplacePlan plan)
    {
        // Built as one string: a bank can hold far more textures than the folder has files, and a
        // TextBox append (with a scroll) per texture would stall the UI for no benefit.
        var text = new StringBuilder();
        text.AppendLine($"matched {plan.Matched}, missing {plan.Missing}, ambiguous {plan.Ambiguous}, " +
                        $"unsupported {plan.Unsupported}, unused {plan.Unused} file(s)");

        foreach (var entry in plan.Entries)
            if (entry.Status != BatchReplaceStatus.Missing) text.AppendLine(Describe(entry));

        foreach (var file in plan.UnusedFiles) text.AppendLine($"unused: {Path.GetFileName(file)}");

        LogBox.Text = text.ToString();
        LogBox.ScrollToEnd();

        // The quality/solidify choice only matters when something is re-encoded; raw .xt1 copies are
        // verbatim, so the options stay off when every match is a raw replacement.
        OptionsCard.IsEnabled = plan.HasNonXt1;
        OptionHint.Text = plan.HasNonXt1
            ? "Applied to every re-encoded texture. Each texture keeps its existing format."
            : "Every match is a raw .xt1; no re-encode options apply.";

        RunButton.IsEnabled = plan.Matched > 0 && !_applied;
        RunStatus.Text = plan.Matched == 0
            ? "No matching files."
            : $"{plan.Matched} texture(s) ready to replace.";
    }

    private static string Describe(BatchReplaceEntry entry) => entry.Status switch
    {
        BatchReplaceStatus.Matched =>
            $"#{entry.Texture.Index} 0x{entry.Texture.EffectiveHash():X8} <- {Path.GetFileName(entry.File!)}",
        BatchReplaceStatus.Missing =>
            $"#{entry.Texture.Index} 0x{entry.Texture.EffectiveHash():X8}: no matching file",
        BatchReplaceStatus.Ambiguous =>
            $"#{entry.Texture.Index}: ambiguous ({entry.Reason})",
        _ => $"#{entry.Texture.Index}: skipped ({entry.Reason})",
    };

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        // The same button doubles as Cancel while a run is in flight.
        if (_cancellation is not null)
        {
            _cancellation.Cancel();
            return;
        }

        if (_plan is null || _plan.Matched == 0 || _applied) return;

        var options = new BatchReplaceOptions(
            QualityCombo.SelectedItem is EncodeQuality quality ? quality : EncodeQuality.Normal,
            SolidifyCheck.IsChecked == true,
            App.Settings.SyncFlagsFromFormat);
        var request = new BatchReplaceRequest(_plan, options);

        RunButton.Content = "Cancel";
        FolderBrowse.IsEnabled = false;
        OptionsCard.IsEnabled = false;
        LogBox.Clear();
        StartBusy();

        var cts = new CancellationTokenSource();
        _cancellation = cts;
        var progress = new Progress<string>(Append);

        try
        {
            var result = await _run(request, progress, cts.Token);

            // Push even a partially-applied run: cancellation stops after the current texture, and
            // whatever was already replaced must stay undoable as one step.
            if (result.Commands.Count > 0)
            {
                _onApplied(result);
                _applied = true;
            }

            var stats = result.Stats;
            Append($"done: {stats.Replaced} replaced, {stats.Skipped} skipped, {stats.Unused} unused" +
                   (result.Canceled ? " (canceled)" : string.Empty));
            RunStatus.Text = $"Replaced {stats.Replaced} texture(s)" +
                             (stats.Skipped > 0 ? $", skipped {stats.Skipped}" : string.Empty) +
                             (result.Canceled ? " (canceled)" : string.Empty);
        }
        catch (OperationCanceledException)
        {
            RunStatus.Text = "Canceled.";
        }
        catch (Exception ex)
        {
            Append("failed: " + ex.Message);
            RunStatus.Text = "Failed: " + ex.Message;
        }
        finally
        {
            StopBusy();
            if (ReferenceEquals(_cancellation, cts)) _cancellation = null;
            cts.Dispose();
            RunButton.Content = "Replace";
            RunButton.IsEnabled = false;
            if (_closeAfterRun) Close();
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>Closing while a scan or run is in flight cancels it and closes once it stops.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_cancellation is not null)
        {
            e.Cancel = true;
            _closeAfterRun = true;
            _cancellation.Cancel();
            return;
        }

        base.OnClosing(e);
    }

    private void Append(string text)
    {
        LogBox.AppendText(text + Environment.NewLine);
        LogBox.ScrollToEnd();
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
}
