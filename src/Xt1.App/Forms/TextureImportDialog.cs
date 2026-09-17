using Xt1.Core.Encoding;
using Xt1.Core.Format;
using Xt1.Core.Imaging;

namespace Xt1.App.Forms;

/// <summary>Options for importing/replacing a texture from an image file.</summary>
public sealed class TextureImportDialog : Form
{
    private readonly ComboBox _format = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly ComboBox _backend = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly ComboBox _quality = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly CheckBox _mips = new() { Text = "Generate full mip chain", AutoSize = true };
    private readonly CheckBox _solidify = new()
    {
        Text = "Solidify (edge-pad transparent RGB from the nearest opaque colour)",
        AutoSize = true,
    };
    private readonly CheckBox _opaque = new()
    {
        Text = "Force opaque (set alpha to 255)",
        AutoSize = true,
    };
    private readonly List<FormatInfo> _formats;

    public FormatInfo SelectedFormat => _formats[_format.SelectedIndex];

    public EncoderBackend Backend => (EncoderBackend)(_backend.SelectedItem ?? EncoderBackend.Nvtt);

    public EncodeQuality Quality => (EncodeQuality)(_quality.SelectedItem ?? EncodeQuality.Normal);

    public bool GenerateMips => _mips.Checked;

    /// <summary>Edge-pad the RGB of transparent texels before encoding (avoids dark halos).</summary>
    public bool Solidify => _solidify.Checked;

    /// <summary>Set the encoded texture's alpha to 255 everywhere.</summary>
    public bool MakeOpaque => _opaque.Checked;

    /// <summary>Test hook: the target-format labels as shown in the dropdown.</summary>
    internal IReadOnlyList<string> UiTestFormatItems() =>
        _format.Items.Cast<object>().Select(item => item.ToString() ?? string.Empty).ToList();

    /// <summary>Test hook: the option labels with their default state.</summary>
    internal IReadOnlyList<string> UiTestOptionLabels() => new[]
    {
        $"{_mips.Text}|{_mips.Checked}",
        $"{_solidify.Text}|{_solidify.Checked}",
        $"{_opaque.Text}|{_opaque.Checked}",
    };

    public TextureImportDialog(string sourceDescription, RgbaImage preview, FormatInfo currentFormat,
        EncoderBackend backend, EncodeQuality quality, bool generateMips, bool solidify = false,
        bool makeOpaque = false)
    {
        Text = "Import texture";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(620, 360);

        // Aliases (community BC7 at 0x4D/0x4E) are offered alongside the canonical id so a mod that
        // uses them can be authored or updated; the id in the label tells the two apart.
        _formats = FormatTable.All
            .Where(f => f.Codec is CodecKind.Bc or CodecKind.Astc or CodecKind.Raw)
            .OrderBy(f => f.Value)
            .ToList();

        foreach (var format in _formats)
            _format.Items.Add(format.DisplayName);
        int index = _formats.FindIndex(f => f.Format == currentFormat.Format);
        _format.SelectedIndex = index >= 0 ? index : _formats.FindIndex(f => f.Format == Xt1Format.Bc7Unorm);

        _backend.Items.AddRange(new object[] { EncoderBackend.Nvtt, EncoderBackend.Managed, EncoderBackend.Cli });
        _backend.SelectedItem = backend;
        _quality.Items.AddRange(new object[] { EncodeQuality.Fastest, EncodeQuality.Normal, EncodeQuality.Production, EncodeQuality.Highest });
        _quality.SelectedItem = quality;
        _mips.Checked = generateMips;
        _solidify.Checked = solidify;
        _opaque.Checked = makeOpaque;

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
        Add(table, ref row, "Transparent RGB", _solidify);
        Add(table, ref row, "Alpha", _opaque);

        var tips = new ToolTip();
        tips.SetToolTip(_solidify,
            "Fill the colour of fully transparent pixels from the nearest opaque pixels before " +
            "encoding, so filtering and block compression do not leave a dark fringe. Alpha is kept.");
        tips.SetToolTip(_opaque,
            "Replace the alpha channel with 255, making the imported texture fully opaque.");

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
