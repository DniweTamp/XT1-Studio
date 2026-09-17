using Xt1.Core.Encoding;
using Xt1.Core.Format;

namespace Xt1.App.Forms;

/// <summary>Runs a batch conversion over a folder of banks.</summary>
public sealed class BatchBuildDialog : Form
{
    private readonly TextBox _folder = new() { Width = 380 };
    private readonly TextBox _output = new() { Width = 380 };
    private readonly ComboBox _format = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    private readonly ComboBox _backend = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly ComboBox _quality = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly CheckBox _mips = new() { Text = "Generate full mip chain", AutoSize = true };
    private readonly CheckBox _inPlace = new() { Text = "Overwrite original files (destructive)", AutoSize = true, Checked = false };
    private readonly TextBox _log = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
        Font = new Font("Consolas", 9f),
    };
    private readonly Button _run = new() { Text = "Run", AutoSize = true };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill };
    private readonly List<FormatInfo> _formats;
    private CancellationTokenSource? _cancellation;

    public BatchBuildDialog(
        Func<IReadOnlyList<string>, BatchRequest, IProgress<string>, CancellationToken, Task> runner,
        EncoderBackend backend, EncodeQuality quality, bool generateMips, FormatInfo defaultFormat)
    {
        Text = "Batch build";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(640, 520);
        ClientSize = new Size(680, 560);

        _formats = FormatTable.All.Where(f => f.Codec is CodecKind.Bc or CodecKind.Astc or CodecKind.Raw)
            .OrderBy(f => f.Value).ToList();
        foreach (var format in _formats) _format.Items.Add($"{format.Name} ({format.Value})");
        int index = _formats.FindIndex(f => f.Format == defaultFormat.Format);
        _format.SelectedIndex = index >= 0 ? index : 0;

        _backend.Items.AddRange(new object[] { EncoderBackend.Managed, EncoderBackend.Nvtt, EncoderBackend.Cli });
        _backend.SelectedItem = backend;
        _quality.Items.AddRange(new object[] { EncodeQuality.Fastest, EncodeQuality.Normal, EncodeQuality.Production, EncodeQuality.Highest });
        _quality.SelectedItem = quality;
        _mips.Checked = generateMips;

        var chooseFolder = new Button { Text = "…", AutoSize = true };
        chooseFolder.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog();
            if (dialog.ShowDialog(this) == DialogResult.OK) _folder.Text = dialog.SelectedPath;
        };
        var chooseOutput = new Button { Text = "…", AutoSize = true };
        chooseOutput.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog();
            if (dialog.ShowDialog(this) == DialogResult.OK) _output.Text = dialog.SelectedPath;
        };

        _inPlace.CheckedChanged += (_, _) => _output.Enabled = !_inPlace.Checked;
        _output.Enabled = !_inPlace.Checked;

        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(10) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
        int row = 0;
        Add(top, ref row, "Source folder", _folder, chooseFolder);
        Add(top, ref row, "Output folder", _output, chooseOutput);
        Add(top, ref row, "Target format", _format, null);
        Add(top, ref row, "Backend", _backend, null);
        Add(top, ref row, "Quality", _quality, null);
        Add(top, ref row, string.Empty, _mips, null);
        Add(top, ref row, string.Empty, _inPlace, null);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 46, ColumnCount = 2, Padding = new Padding(10, 6, 10, 6) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(_progress, 0, 0);
        bottom.Controls.Add(_run, 1, 0);

        Controls.Add(_log);
        Controls.Add(bottom);
        Controls.Add(top);

        _run.Click += async (_, _) => await RunAsync(runner);
    }

    private async Task RunAsync(Func<IReadOnlyList<string>, BatchRequest, IProgress<string>, CancellationToken, Task> runner)
    {
        if (_cancellation is not null)
        {
            _cancellation.Cancel();
            return;
        }

        if (!Directory.Exists(_folder.Text))
        {
            MessageBox.Show(this, "Choose an existing source folder.", "Batch build",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var files = Directory.EnumerateFiles(_folder.Text, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".wta", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".wtb", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".dtt", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (files.Count == 0)
        {
            MessageBox.Show(this, "No .wta/.dat/.wtb files found.", "Batch build",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_inPlace.Checked)
        {
            var confirm = MessageBox.Show(this,
                $"This will overwrite {files.Count} original file(s) in place, including every " +
                ".dat/.dtt and .wta/.wtp pair it rewrites.\n\nThere is no backup. Continue?",
                "Overwrite originals?", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes) return;
        }
        else
        {
            if (!Directory.Exists(_output.Text))
            {
                MessageBox.Show(this, "Choose an existing output folder (or enable overwrite in place).",
                    "Batch build", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (string.Equals(Path.GetFullPath(_output.Text).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(_folder.Text).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this,
                    "The output folder is the same as the source folder; choose a different folder " +
                    "or enable overwrite in place.",
                    "Batch build", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        var request = new BatchRequest(
            _formats[_format.SelectedIndex],
            (EncoderBackend)(_backend.SelectedItem ?? EncoderBackend.Managed),
            (EncodeQuality)(_quality.SelectedItem ?? EncodeQuality.Normal),
            _mips.Checked,
            _inPlace.Checked,
            _inPlace.Checked ? null : _output.Text);

        _log.Clear();
        _progress.Maximum = files.Count;
        _progress.Value = 0;
        _run.Text = "Cancel";
        _cancellation = new CancellationTokenSource();

        var progress = new Progress<string>(text => _log.AppendText(text + Environment.NewLine));
        try
        {
            await runner(files, request, progress, _cancellation.Token);
            _log.AppendText("Done." + Environment.NewLine);
        }
        catch (OperationCanceledException)
        {
            _log.AppendText("Canceled." + Environment.NewLine);
        }
        catch (Exception ex)
        {
            _log.AppendText("Failed: " + ex.Message + Environment.NewLine);
        }
        finally
        {
            _progress.Value = _progress.Maximum;
            _run.Text = "Run";
            _cancellation.Dispose();
            _cancellation = null;
        }
    }

    private static void Add(TableLayoutPanel table, ref int row, string label, Control control, Control? extra)
    {
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 9, 3, 3) }, 0, row);
        table.Controls.Add(control, 1, row);
        if (extra is not null) table.Controls.Add(extra, 2, row);
        row++;
    }
}

public sealed record BatchRequest(
    FormatInfo TargetFormat,
    EncoderBackend Backend,
    EncodeQuality Quality,
    bool GenerateMips,
    bool OverwriteInPlace,
    string? OutputDirectory);
