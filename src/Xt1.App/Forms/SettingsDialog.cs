using Xt1.Core.Encoding;
using Xt1.Core.Format;
using Xt1.Core.Settings;

namespace Xt1.App.Forms;

/// <summary>Tool paths, encoder preference and preview defaults.</summary>
public sealed class SettingsDialog : Form
{
    private readonly TextBox _nvttDll = new() { Width = 420 };
    private readonly TextBox _nvttCli = new() { Width = 420 };
    private readonly TextBox _oodle = new() { Width = 420 };
    private readonly ComboBox _backend = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    private readonly ComboBox _defaultFormat = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    private readonly CheckBox _generateMips = new() { Text = "Generate full mip chains on import", AutoSize = true };
    private readonly CheckBox _srgbPreview = new() { Text = "Apply sRGB display gamma by default", AutoSize = true };
    private readonly CheckBox _recompressOodle = new() { Text = "Recompress payloads with Oodle on save (when supported)", AutoSize = true };
    private readonly NumericUpDown _volumeCap = new() { Minimum = 64, Maximum = 2048, Increment = 64, Width = 90 };

    public AppSettings Result { get; }

    public SettingsDialog(AppSettings settings)
    {
        Result = settings;

        Text = "Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(620, 430);

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            Padding = new Padding(12),
            AutoScroll = true,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));

        _backend.Items.AddRange(new object[] { EncoderBackend.Managed, EncoderBackend.Nvtt, EncoderBackend.Cli });
        _backend.SelectedItem = settings.PreferredBackend;

        var formats = FormatTable.All.Where(f => f.Codec is CodecKind.Bc or CodecKind.Astc or CodecKind.Raw)
            .OrderBy(f => f.Value).ToList();
        foreach (var format in formats)
            _defaultFormat.Items.Add($"{format.Name} ({format.Value})");
        int selected = formats.FindIndex(f => f.Format == settings.DefaultImportFormat);
        _defaultFormat.SelectedIndex = selected >= 0 ? selected : 0;

        _generateMips.Checked = settings.GenerateMips;
        _srgbPreview.Checked = settings.ApplySrgbPreview;
        _recompressOodle.Checked = settings.RecompressOodleOnSave;
        _volumeCap.Value = Math.Clamp(settings.VolumeResolutionCap, 64, 2048);
        _nvttDll.Text = settings.NvttDllPath ?? string.Empty;
        _nvttCli.Text = settings.NvttCliPath ?? string.Empty;
        _oodle.Text = settings.OodlePath ?? string.Empty;

        int row = 0;
        AddPathRow(table, ref row, "nvtt30205.dll", _nvttDll);
        AddPathRow(table, ref row, "nvtt_export.exe", _nvttCli);
        AddPathRow(table, ref row, "oo2core DLL", _oodle);

        AddRow(table, ref row, "Preferred backend", _backend);
        AddRow(table, ref row, "Default import format", _defaultFormat);
        AddRow(table, ref row, "Volume preview cap", _volumeCap);
        AddRow(table, ref row, string.Empty, _generateMips);
        AddRow(table, ref row, string.Empty, _srgbPreview);
        AddRow(table, ref row, string.Empty, _recompressOodle);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        ok.Click += (_, _) => Commit(formats);
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 40,
            Padding = new Padding(8),
        };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        Controls.Add(table);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;

        void Commit(List<Xt1.Core.Format.FormatInfo> formatList)
        {
            Result.PreferredBackend = (EncoderBackend)(_backend.SelectedItem ?? EncoderBackend.Managed);
            if (_defaultFormat.SelectedIndex >= 0 && _defaultFormat.SelectedIndex < formatList.Count)
                Result.DefaultImportFormat = formatList[_defaultFormat.SelectedIndex].Format;
            Result.GenerateMips = _generateMips.Checked;
            Result.ApplySrgbPreview = _srgbPreview.Checked;
            Result.RecompressOodleOnSave = _recompressOodle.Checked;
            Result.VolumeResolutionCap = (int)_volumeCap.Value;
            Result.NvttDllPath = NullIfEmpty(_nvttDll.Text);
            Result.NvttCliPath = NullIfEmpty(_nvttCli.Text);
            Result.OodlePath = NullIfEmpty(_oodle.Text);
        }
    }

    private static string? NullIfEmpty(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static void AddRow(TableLayoutPanel table, ref int row, string label, Control control)
    {
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, 0, row);
        table.Controls.Add(control, 1, row);
        table.SetColumnSpan(control, 2);
        row++;
    }

    private static void AddPathRow(TableLayoutPanel table, ref int row, string label, TextBox box)
    {
        var browse = new Button { Text = "…", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "DLL / EXE (*.dll;*.exe)|*.dll;*.exe|All files (*.*)|*.*" };
            if (dialog.ShowDialog() == DialogResult.OK) box.Text = dialog.FileName;
        };
        AddRow(table, ref row, label, box);
        table.Controls.Add(browse, 2, row - 1);
    }
}
