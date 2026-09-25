using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using Xt1.App.Wpf.Theming;
using Xt1.Core.Containers;
using Xt1.Core.Editing;

namespace Xt1.App.Wpf.Dialogs;

/// <summary>What the caller's runner should export and where.</summary>
public sealed record ExportTexturesRequest(
    TextureExportFormat Format,
    IReadOnlyList<string> Files,
    string SourceRoot,
    string OutputFolder,
    bool SubfolderPerBank);

/// <summary>
/// Exports every texture in a folder of banks to PNG, DDS or raw XT1. Port of the batch recompress
/// window's shape: all work is done by the caller's runner, so this window stays UI-only.
/// </summary>
public partial class ExportTexturesDialog : ThemedDialog
{
    private static readonly ExportFormatOption[] Formats =
    {
        new(TextureExportFormat.Png, "PNG image (.png)",
            "Decodes mip 0 of each texture to a .png. Textures without a decoder are skipped."),
        new(TextureExportFormat.Dds, "DDS texture (.dds)",
            "Wraps the stored surfaces, all mips, in a .dds without re-encoding. " +
            "Formats without a DDS mapping are skipped."),
        new(TextureExportFormat.Xt1, "Raw XT1 (.xt1)",
            "Writes each texture's raw header and payload as a loose .xt1 file."),
    };

    private readonly Func<ExportTexturesRequest, IProgress<string>, CancellationToken, Task> _runner;
    private CancellationTokenSource? _cancellation;
    private Storyboard? _busy;

    public ExportTexturesDialog(
        Func<ExportTexturesRequest, IProgress<string>, CancellationToken, Task> runner)
    {
        _runner = runner;
        InitializeComponent();

        FormatCombo.ItemsSource = Formats;
        FormatCombo.SelectedItem = Formats[0];
        FlatRadio.IsChecked = true;

        // Seed from the last folder the shell browsed, so a repeated export needs no retyping.
        if (Directory.Exists(App.Settings.LastDirectory)) SourceBox.Text = App.Settings.LastDirectory;

        UpdateFormatHint();
    }

    private static ExportFormatOption SelectedFormat(ComboBox combo) =>
        combo.SelectedItem as ExportFormatOption ?? Formats[0];

    private void OnFormatChanged(object sender, SelectionChangedEventArgs e) => UpdateFormatHint();

    private void UpdateFormatHint() => FormatHint.Text = SelectedFormat(FormatCombo).Hint;

    /// <summary>
    /// A one-line summary for the headless UI smoke; constructing the window is what validates its
    /// XAML and style references, so the caller only reads this.
    /// </summary>
    internal string UiTestState()
    {
        UpdateFormatHint();
        return $"formats={FormatCombo.Items.Count} selected={SelectedFormat(FormatCombo).Label} " +
               $"hint={!string.IsNullOrEmpty(FormatHint.Text)}";
    }

    private void OnBrowseSource(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the source folder" };
        if (Directory.Exists(SourceBox.Text)) dialog.InitialDirectory = SourceBox.Text;
        if (dialog.ShowDialog(this) == true) SourceBox.Text = dialog.FolderName;
    }

    private void OnBrowseOutput(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the output folder" };
        if (Directory.Exists(OutputBox.Text)) dialog.InitialDirectory = OutputBox.Text;
        if (dialog.ShowDialog(this) == true) OutputBox.Text = dialog.FolderName;
    }

    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = false;

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        if (_cancellation is not null)
        {
            _cancellation.Cancel();
            return;
        }

        if (!Directory.Exists(SourceBox.Text))
        {
            RunStatus.Text = "Choose an existing source folder.";
            return;
        }

        string output = OutputBox.Text?.Trim() ?? string.Empty;
        if (output.Length == 0)
        {
            RunStatus.Text = "Choose an output folder.";
            return;
        }

        // Read the controls on the UI thread, then scan off it: a recursive enumeration over a large
        // extracted romfs must not stall the dialog before any progress is shown.
        string sourceRoot = SourceBox.Text;
        bool recursive = RecursiveCheck.IsChecked == true;
        RunStatus.Text = "Scanning for banks...";
        List<string> files;
        try
        {
            files = await Task.Run(() =>
            {
                var search = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                return Directory.EnumerateFiles(sourceRoot, "*.*", search)
                    .Where(BankFileScan.IsBankFile)
                    .ToList();
            });
        }
        catch (Exception ex)
        {
            RunStatus.Text = "Could not scan the source folder: " + ex.Message;
            return;
        }

        if (files.Count == 0)
        {
            RunStatus.Text = "No .dat/.wta/.wtb/.xt1 files found.";
            return;
        }

        try
        {
            Directory.CreateDirectory(output);
        }
        catch (Exception ex)
        {
            RunStatus.Text = "Could not create the output folder: " + ex.Message;
            return;
        }

        // Batch export writes deterministic names, so re-running into a populated folder replaces
        // files; say so before anything is touched rather than silently clobbering.
        if (Directory.EnumerateFileSystemEntries(output).Any() &&
            !ConfirmDialog.Confirm(this, "Overwrite output?",
                $"\"{output}\" already contains files. Same-named exports will be replaced." +
                $"{Environment.NewLine}{Environment.NewLine}Continue?", "Export"))
        {
            RunStatus.Text = "Export canceled.";
            return;
        }

        var request = new ExportTexturesRequest(
            SelectedFormat(FormatCombo).Format,
            files,
            sourceRoot,
            output,
            SubfolderPerBank: PerBankRadio.IsChecked == true);

        LogBox.Clear();
        RunStatus.Text = $"Exporting {files.Count} file(s)...";
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
            await _runner(request, progress, _cancellation.Token);
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
            RunButton.Content = "Export";
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

    private sealed record ExportFormatOption(TextureExportFormat Format, string Label, string Hint)
    {
        public override string ToString() => Label;
    }
}
