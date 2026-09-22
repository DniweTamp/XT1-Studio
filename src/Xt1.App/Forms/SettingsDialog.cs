using Xt1.Core.Encoding;
using Xt1.Core.Format;
using Xt1.Core.Settings;

namespace Xt1.App.Forms;

/// <summary>Encoder preference, import defaults, preview defaults and optional tool paths.</summary>
public sealed class SettingsDialog : Form
{
    private const int LabelColumn = 170;

    private readonly TextBox _nvttDll = new() { Dock = DockStyle.Fill };
    private readonly TextBox _nvttCli = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _backend = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox _defaultFormat = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly CheckBox _syncFlags = new()
    {
        Text = "Sync WTB sRGB / cubemap flags from the texture format on add or replace",
        AutoSize = true,
    };
    private readonly CheckBox _srgbPreview = new() { Text = "Apply sRGB display gamma by default", AutoSize = true };
    private readonly CheckBox _opaqueAlpha = new() { Text = "Start with opaque alpha (ignore stored alpha)", AutoSize = true };
    private readonly CheckBox _showAlpha = new() { Text = "Offer the alpha-only option in the channel selector", AutoSize = true };
    private readonly NumericUpDown _volumeCap = new()
    {
        Minimum = 64, Maximum = 2048, Increment = 64, Width = 90, TextAlign = HorizontalAlignment.Left,
    };
    private readonly Button _shellButton = new() { AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
    private readonly CheckBox _shellMachine = new()
    {
        Text = "For all users on this machine (needs an administrator)",
        AutoSize = true,
    };
    private Label _shellStatus = null!;

    public AppSettings Result { get; }

    private TableLayoutPanel _grid = null!;

    /// <summary>
    /// Test hook: the Explorer thumbnail row - the button's label, the status line and whether the
    /// button can act (the shell host has to be deployed beside the app for it to do anything).
    /// </summary>
    internal (string Button, string Status, bool Enabled) UiTestShellRow() =>
        (_shellButton.Text, _shellStatus.Text, _shellButton.Enabled);

    /// <summary>
    /// Test hook: per row, the label's right edge / vertical center and the editor's left edge /
    /// vertical center, so a regression in the alignment can be caught headlessly.
    /// </summary>
    internal IReadOnlyList<(string Label, int LabelRight, int LabelCenterY, int EditorLeft, int EditorCenterY)>
        UiTestRowAlignment()
    {
        var rows = new List<(string, int, int, int, int)>();
        foreach (Control control in _grid.Controls)
        {
            if (control is not Label label || label.AutoSize || _grid.GetColumn(label) != 0) continue;
            int labelRow = _grid.GetRow(label);
            foreach (Control editor in _grid.Controls)
            {
                if (editor == label || _grid.GetRow(editor) != labelRow || _grid.GetColumn(editor) != 1) continue;
                rows.Add((label.Text,
                    label.Right,
                    label.Top + label.Height / 2,
                    editor.Left,
                    editor.Top + editor.Height / 2));
                break;
            }
        }
        return rows;
    }

    public SettingsDialog(AppSettings settings)
    {
        Result = settings;

        Text = "Settings";
        Icon = AppIcon.Shared;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(680, 580);

        _backend.Items.AddRange(new object[] { EncoderBackend.Nvtt, EncoderBackend.Managed, EncoderBackend.Cli });
        _backend.SelectedItem = settings.PreferredBackend;

        var formats = FormatTable.All
            .Where(f => f.Codec is CodecKind.Bc or CodecKind.Astc or CodecKind.Raw)
            .OrderBy(f => f.Value).ToList();
        foreach (var format in formats)
            _defaultFormat.Items.Add(format.DisplayName);
        int selected = formats.FindIndex(f => f.Format == settings.DefaultImportFormat);
        _defaultFormat.SelectedIndex = selected >= 0 ? selected : 0;

        _syncFlags.Checked = settings.SyncFlagsFromFormat;
        _srgbPreview.Checked = settings.ApplySrgbPreview;
        _opaqueAlpha.Checked = settings.OpaqueAlphaByDefault;
        _showAlpha.Checked = settings.ShowAlphaChannel;
        _volumeCap.Value = Math.Clamp(settings.VolumeResolutionCap, 64, 2048);
        _nvttDll.Text = settings.NvttDllPath ?? string.Empty;
        _nvttCli.Text = settings.NvttCliPath ?? string.Empty;

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            Padding = new Padding(14, 10, 14, 10),
            AutoScroll = true,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LabelColumn));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _grid = grid;

        int row = 0;
        AddSection(grid, ref row, "Encoder");
        AddRow(grid, ref row, "Preferred backend", _backend, null);
        AddHint(grid, ref row, "NVTT is used when available; the managed encoder is the fallback.");
        AddRow(grid, ref row, "Default import format", _defaultFormat, null);
        AddRow(grid, ref row, string.Empty, _syncFlags, null, span: 2);

        AddSection(grid, ref row, "Preview");
        AddRow(grid, ref row, string.Empty, _srgbPreview, null, span: 2);
        AddRow(grid, ref row, string.Empty, _opaqueAlpha, null, span: 2);
        AddRow(grid, ref row, string.Empty, _showAlpha, null, span: 2);
        AddRow(grid, ref row, "Volume preview cap", _volumeCap, null);
        AddHint(grid, ref row, "Larger 3D volumes are downscaled for the GPU preview only.");

        AddSection(grid, ref row, "Native tools");
        AddRow(grid, ref row, "nvtt30205.dll", _nvttDll, MakeBrowse(_nvttDll));
        AddRow(grid, ref row, "nvtt_export.exe", _nvttCli, MakeBrowse(_nvttCli));
        AddHint(grid, ref row, "Leave blank to auto-detect, or point at a custom install.");
        // The settings file lives next to the executable; no need to spend a section on it.

        AddSection(grid, ref row, "Explorer");
        AddRow(grid, ref row, "Thumbnails for .xt1", _shellButton, null);
        AddRow(grid, ref row, string.Empty, _shellMachine, null, span: 2);
        _shellStatus = MakeHint(string.Empty);
        AddControl(grid, ref row, _shellStatus);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        ok.Click += (_, _) => Commit(formats);
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 44,
            Padding = new Padding(10, 8, 10, 8),
        };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        Controls.Add(grid);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;

        RefreshShell();
        _shellMachine.CheckedChanged += (_, _) => RefreshShell();
        _shellButton.Click += (_, _) => ToggleShell();

        // The button acts on the checked scope and immediately (it is a registration, not a setting
        // that waits for OK), so its label and the status line always describe the current state.
        void RefreshShell()
        {
            bool machine = _shellMachine.Checked;
            bool registered = ShellIntegration.IsRegistered(machine);
            var comHost = ShellIntegration.ComHost();
            _shellButton.Text = registered ? "Remove" : "Register";
            _shellButton.Enabled = registered || comHost is not null;
            _shellStatus.Text = ShellIntegration.Describe(machine, registered, comHost);
        }

        void ToggleShell()
        {
            bool machine = _shellMachine.Checked;
            try
            {
                if (ShellIntegration.IsRegistered(machine))
                    ShellIntegration.Disable(machine);
                else
                    ShellIntegration.Enable(machine);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Explorer thumbnails", MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            RefreshShell();
        }

        void Commit(List<FormatInfo> formatList)
        {
            Result.PreferredBackend = (EncoderBackend)(_backend.SelectedItem ?? EncoderBackend.Nvtt);
            if (_defaultFormat.SelectedIndex >= 0 && _defaultFormat.SelectedIndex < formatList.Count)
                Result.DefaultImportFormat = formatList[_defaultFormat.SelectedIndex].Format;
            Result.SyncFlagsFromFormat = _syncFlags.Checked;
            Result.ApplySrgbPreview = _srgbPreview.Checked;
            Result.OpaqueAlphaByDefault = _opaqueAlpha.Checked;
            Result.ShowAlphaChannel = _showAlpha.Checked;
            Result.VolumeResolutionCap = (int)_volumeCap.Value;
            Result.NvttDllPath = NullIfEmpty(_nvttDll.Text);
            Result.NvttCliPath = NullIfEmpty(_nvttCli.Text);
        }
    }

    private static string? NullIfEmpty(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static Button MakeBrowse(TextBox box)
    {
        var browse = new Button { Text = "Browse...", AutoSize = true, Margin = new Padding(6, 2, 0, 2) };
        browse.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog
            {
                Filter = "DLL / EXE (*.dll;*.exe)|*.dll;*.exe|All files (*.*)|*.*",
                FileName = box.Text,
            };
            if (dialog.ShowDialog() == DialogResult.OK) box.Text = dialog.FileName;
        };
        return browse;
    }

    private static void AddSection(TableLayoutPanel grid, ref int row, string title)
    {
        var label = new Label
        {
            Text = title,
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Margin = new Padding(0, row == 0 ? 0 : 14, 0, 4),
        };
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(label, 0, row);
        grid.SetColumnSpan(label, 3);
        row++;
    }

    private static void AddHint(TableLayoutPanel grid, ref int row, string text) =>
        AddControl(grid, ref row, MakeHint(text));

    /// <summary>
    /// A full-width grey line. MaximumSize caps the width so a long status wraps instead of being
    /// clipped at the dialog edge.
    /// </summary>
    private static Label MakeHint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Color.FromArgb(112, 112, 124),
        Margin = new Padding(0, 1, 0, 4),
        MaximumSize = new Size(636, 0),
    };

    private static void AddControl(TableLayoutPanel grid, ref int row, Control control)
    {
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(control, 0, row);
        grid.SetColumnSpan(control, 3);
        row++;
    }

    /// <summary>
    /// One settings row. The label is stretched and vertically centered so every row's label and
    /// editor share the same baseline, which is what the earlier nested-panel version got wrong.
    /// </summary>
    private static void AddRow(TableLayoutPanel grid, ref int row, string label, Control control,
        Control? extra, int span = 1)
    {
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var caption = new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 3, 10, 3),
        };
        grid.Controls.Add(caption, 0, row);

        control.Margin = new Padding(0, 2, 0, 2);
        if (control is NumericUpDown) control.Dock = DockStyle.None;
        grid.Controls.Add(control, 1, row);
        if (span == 2) grid.SetColumnSpan(control, 2);

        if (extra is not null)
        {
            extra.Margin = new Padding(6, 2, 0, 2);
            grid.Controls.Add(extra, 2, row);
        }
        row++;
    }
}
