using Xt1.App.Theming;
using Xt1.Core.Editing;
using Xt1.Core.Encoding;

namespace Xt1.App.Forms;

public sealed record RecompressRequest(RecompressOptions Options, bool CurrentDocument, IReadOnlyList<string> Files);

/// <summary>
/// Batch recompresses ASTC textures to BC, either in the currently loaded document or across every
/// matching bank in a folder. Existing mips are recompressed; none are regenerated.
/// </summary>
public sealed class BatchRecompressDialog : ThemedForm
{
    private readonly RadioButton _current = new() { Text = "Current document", AutoSize = true };
    private readonly RadioButton _folderMode = new() { Text = "Folder", AutoSize = true };
    private readonly TextBox _folder = new() { Width = 380 };
    private readonly Button _browse = new() { Text = "…", AutoSize = true };
    private readonly CheckBox _recursive = new() { Text = "Include subfolders (recursive)", AutoSize = true };
    private readonly ComboBox _rule = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly CheckBox _downscale = new() { Text = "Downscale by half (drop base mip / resize single-mip images)", AutoSize = true };
    private readonly CheckBox _keepNoMips = new() { Text = "Do not downscale images without mipmaps", AutoSize = true, Checked = true };
    private readonly CheckBox _syncFlags = new() { Text = "Sync WTB sRGB / cubemap flags from the new format", AutoSize = true, Checked = true };
    private readonly ComboBox _backend = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly Label _opaqueQualityLabel = new() { Text = "Quality", AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
    private readonly ComboBox _opaqueQuality = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly Label _alphaQualityLabel = new() { Text = "Alpha quality", AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
    private readonly ComboBox _alphaQuality = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
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
    private CancellationTokenSource? _cancellation;

    /// <summary>Test hook for verifying the conditional quality controls without running a batch.</summary>
    internal (string OpaqueLabel, bool AlphaVisible, IReadOnlyList<string> Qualities) UiTestQualityState() =>
        (_opaqueQualityLabel.Text, _alphaQuality.Visible && _alphaQualityLabel.Visible,
            _opaqueQuality.Items.Cast<object>().Select(item => item.ToString() ?? string.Empty).ToList());

    /// <summary>Test hook for switching format rules through the same selection event as the UI.</summary>
    internal void UiTestSelectRule(RecompressFormatRule rule)
    {
        for (int i = 0; i < _rule.Items.Count; i++)
        {
            if (_rule.Items[i] is RuleOption option && option.Rule == rule)
            {
                _rule.SelectedIndex = i;
                return;
            }
        }
    }

    public BatchRecompressDialog(
        Func<RecompressRequest, IProgress<string>, CancellationToken, Task> runner,
        EncoderBackend backend, bool hasDocument)
    {
        Text = "Batch recompress (ASTC to BC)";
        Icon = AppIcon.Shared;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(720, 560);
        ClientSize = new Size(760, 600);

        // Bind the enum values themselves rather than relying on the item order matching the enum;
        // reordering RecompressFormatRule can then never silently select the wrong BC target.
        _rule.Items.AddRange(new object[]
        {
            new RuleOption(RecompressFormatRule.AlwaysBc1, "Always BC1"),
            new RuleOption(RecompressFormatRule.Bc1OrBc3, "BC1 when opaque, BC3 when it has alpha"),
            new RuleOption(RecompressFormatRule.Bc1OrBc7, "BC1 when opaque, BC7 when it has alpha"),
            new RuleOption(RecompressFormatRule.AlwaysBc7, "Always BC7"),
        });
        _rule.SelectedIndex = 1;
        _rule.SelectedIndexChanged += (_, _) => UpdateQualityControls();

        _backend.Items.AddRange(new object[] { EncoderBackend.Nvtt, EncoderBackend.Managed, EncoderBackend.Cli });
        // Honor the caller's backend (including Cli) instead of coercing it; fall back only if unset.
        _backend.SelectedItem = backend;
        var qualities = new object[]
        {
            EncodeQuality.Fastest,
            EncodeQuality.Normal,
            EncodeQuality.Production,
            EncodeQuality.Highest,
        };
        _opaqueQuality.Items.AddRange(qualities);
        _alphaQuality.Items.AddRange(qualities);
        _opaqueQuality.SelectedItem = EncodeQuality.Normal;
        _alphaQuality.SelectedItem = EncodeQuality.Normal;

        _current.Enabled = hasDocument;
        _current.Checked = hasDocument;
        _folderMode.Checked = !hasDocument;

        _browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog();
            if (dialog.ShowDialog(this) == DialogResult.OK) _folder.Text = dialog.SelectedPath;
        };

        _downscale.CheckedChanged += (_, _) => _keepNoMips.Enabled = _downscale.Checked;
        _keepNoMips.Enabled = _downscale.Checked;
        _folderMode.CheckedChanged += (_, _) => UpdateFolderEnabled();
        _current.CheckedChanged += (_, _) => UpdateFolderEnabled();
        UpdateFolderEnabled();

        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3,
            Padding = new Padding(10),
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        int row = 0;
        var modeRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        modeRow.Controls.Add(_current);
        modeRow.Controls.Add(_folderMode);
        Add(top, ref row, "Source", modeRow, null);
        Add(top, ref row, "Folder", _folder, _browse);
        Add(top, ref row, string.Empty, _recursive, null);
        Add(top, ref row, "Target format", _rule, null);
        Add(top, ref row, string.Empty, _downscale, null);
        Add(top, ref row, string.Empty, _keepNoMips, null);
        Add(top, ref row, string.Empty, _syncFlags, null);
        Add(top, ref row, "Encoder backend", _backend, null);
        Add(top, ref row, _opaqueQualityLabel, _opaqueQuality, null);
        Add(top, ref row, _alphaQualityLabel, _alphaQuality, null);
        AddHint(top, ref row, "Folder mode rewrites the originals in place; there is no backup.");
        UpdateQualityControls();

        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            ColumnCount = 2,
            Padding = new Padding(10, 6, 10, 6),
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(_progress, 0, 0);
        bottom.Controls.Add(_run, 1, 0);

        Controls.Add(_log);
        Controls.Add(bottom);
        Controls.Add(top);

        _run.Click += async (_, _) => await RunAsync(runner);
    }

    private void UpdateFolderEnabled()
    {
        bool folder = _folderMode.Checked;
        _folder.Enabled = folder;
        _browse.Enabled = folder;
        _recursive.Enabled = folder;
    }

    private void UpdateQualityControls()
    {
        var rule = (_rule.SelectedItem as RuleOption)?.Rule ?? RecompressFormatRule.Bc1OrBc3;
        bool mixed = RecompressOptions.UsesSeparateAlphaQuality(rule);
        _opaqueQualityLabel.Text = mixed ? "Opaque quality" : "Quality";
        _alphaQualityLabel.Visible = mixed;
        _alphaQuality.Visible = mixed;
    }

    private static void Add(TableLayoutPanel table, ref int row, string label, Control control, Control? extra)
    {
        Add(table, ref row,
            new Label { Text = label, AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, control, extra);
    }

    private static void Add(TableLayoutPanel table, ref int row, Control label, Control control, Control? extra)
    {
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(label, 0, row);
        table.Controls.Add(control, 1, row);
        if (extra is not null) table.Controls.Add(extra, 2, row);
        row++;
    }

    private static void AddHint(TableLayoutPanel table, ref int row, string text)
    {
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = ThemeManager.Palette.MutedText,
            Margin = new Padding(3, 2, 0, 6),
        };
        ThemeManager.SetRole(label, ThemeRole.Muted);
        table.Controls.Add(label, 1, row);
        row++;
    }

    private async Task RunAsync(Func<RecompressRequest, IProgress<string>, CancellationToken, Task> runner)
    {
        if (_cancellation is not null)
        {
            _cancellation.Cancel();
            return;
        }

        var options = new RecompressOptions(
            (_rule.SelectedItem as RuleOption)?.Rule ?? RecompressFormatRule.Bc1OrBc3,
            _downscale.Checked,
            _keepNoMips.Checked,
            (EncoderBackend)(_backend.SelectedItem ?? EncoderBackend.Nvtt),
            (EncodeQuality)(_opaqueQuality.SelectedItem ?? EncodeQuality.Normal),
            (EncodeQuality)(_alphaQuality.SelectedItem ?? EncodeQuality.Normal),
            _syncFlags.Checked);

        IReadOnlyList<string> files;
        bool current = _current.Checked;
        if (current)
        {
            files = Array.Empty<string>();
        }
        else
        {
            if (!Directory.Exists(_folder.Text))
            {
                MessageBox.Show(this, "Choose an existing source folder.", "Batch recompress",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var search = _recursive.Checked ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            files = Directory.EnumerateFiles(_folder.Text, "*.*", search)
                .Where(f => f.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".wta", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".wtb", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".xt1", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (files.Count == 0)
            {
                MessageBox.Show(this, "No .dat/.wta/.wtb/.xt1 files found.", "Batch recompress",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Show the resolved list so the user can see the exact scope before an in-place rewrite.
            const int shown = 10;
            var listing = string.Join(Environment.NewLine, files.Take(shown).Select(Path.GetFileName));
            if (files.Count > shown) listing += Environment.NewLine + $"... and {files.Count - shown} more";
            var confirm = MessageBox.Show(this,
                $"This rewrites {files.Count} file(s) in place, replacing every ASTC texture that " +
                $"converts with a BC texture:\n\n{listing}\n\nThere is no backup. Continue?",
                "Overwrite originals?", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes) return;
        }

        _log.Clear();
        _progress.Style = ProgressBarStyle.Marquee;
        _run.Text = "Cancel";
        _cancellation = new CancellationTokenSource();
        var progress = new Progress<string>(text => _log.AppendText(text + Environment.NewLine));
        try
        {
            await runner(new RecompressRequest(options, current, files), progress, _cancellation.Token);
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
            _progress.Style = ProgressBarStyle.Continuous;
            _run.Text = "Run";
            _cancellation.Dispose();
            _cancellation = null;
        }
    }

    /// <summary>Combo item binding an enum value to a display label.</summary>
    private sealed record RuleOption(RecompressFormatRule Rule, string Label)
    {
        public override string ToString() => Label;
    }
}
