using Xt1.Core.Encoding;
using Xt1.Core.Format;
using Xt1.Core.Imaging;

namespace Xt1.App.Forms;

/// <summary>Options for importing/replacing a texture from an image or DDS.</summary>
public sealed class TextureImportDialog : Form
{
    private readonly ComboBox _format = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly ComboBox _backend = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly ComboBox _quality = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly CheckBox _mips = new() { Text = "Generate full mip chain", AutoSize = true };
    private readonly CheckBox _cube = new() { Text = "Treat as cubemap (6 faces / DDS cubemap)", AutoSize = true };
    private readonly List<FormatInfo> _formats;

    public FormatInfo SelectedFormat => _formats[_format.SelectedIndex];

    public EncoderBackend Backend => (EncoderBackend)(_backend.SelectedItem ?? EncoderBackend.Managed);

    public EncodeQuality Quality => (EncodeQuality)(_quality.SelectedItem ?? EncodeQuality.Normal);

    public bool GenerateMips => _mips.Checked;

    public bool IsCube => _cube.Checked;

    public TextureImportDialog(string sourceDescription, RgbaImage preview, FormatInfo currentFormat,
        EncoderBackend backend, EncodeQuality quality, bool generateMips, bool canBeCube, bool defaultCube = false)
    {
        Text = "Import texture";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(520, 340);

        _formats = FormatTable.All
            .Where(f => f.Codec is CodecKind.Bc or CodecKind.Astc or CodecKind.Raw)
            .OrderBy(f => f.Value)
            .ToList();

        foreach (var format in _formats)
            _format.Items.Add($"{format.Name} ({format.Value})");
        int index = _formats.FindIndex(f => f.Format == currentFormat.Format);
        _format.SelectedIndex = index >= 0 ? index : _formats.FindIndex(f => f.Format == Xt1Format.Bc7Unorm);

        _backend.Items.AddRange(new object[] { EncoderBackend.Managed, EncoderBackend.Nvtt, EncoderBackend.Cli });
        _backend.SelectedItem = backend;
        _quality.Items.AddRange(new object[] { EncodeQuality.Fastest, EncodeQuality.Normal, EncodeQuality.Production, EncodeQuality.Highest });
        _quality.SelectedItem = quality;
        _mips.Checked = generateMips;
        _cube.Enabled = canBeCube;
        _cube.Checked = canBeCube && defaultCube;

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(12),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        int row = 0;
        Add(table, ref row, "Source", new Label { Text = sourceDescription, AutoSize = true });
        Add(table, ref row, "Decoded size", new Label { Text = $"{preview.Width} x {preview.Height} x {preview.Depth}", AutoSize = true });
        Add(table, ref row, "Target format", _format);
        Add(table, ref row, "Encoder backend", _backend);
        Add(table, ref row, "Quality", _quality);
        Add(table, ref row, string.Empty, _mips);
        Add(table, ref row, string.Empty, _cube);

        var ok = new Button { Text = "Import", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
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
    }

    private static void Add(TableLayoutPanel table, ref int row, string label, Control control)
    {
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, 0, row);
        table.Controls.Add(control, 1, row);
        row++;
    }
}
