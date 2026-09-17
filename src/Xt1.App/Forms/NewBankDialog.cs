namespace Xt1.App.Forms;

/// <summary>Picks the container shape for a new empty bank: a `.wta`+`.wtp` pair or a single `.wtb`.</summary>
public sealed class NewBankDialog : Form
{
    private readonly RadioButton _wta = new()
    {
        Text = "Effect bank (.wta + .wtp)\r\nA header file plus a separate payload stream.",
        AutoSize = true,
        Checked = true,
    };

    private readonly RadioButton _wtb = new()
    {
        Text = "Standalone bank (.wtb)\r\nA single file with the payload inline.",
        AutoSize = true,
    };

    public bool Standalone => _wtb.Checked;

    public NewBankDialog()
    {
        Text = "New empty bank";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(430, 170);

        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(14),
            AutoSize = true,
        };
        panel.Controls.Add(_wta);
        panel.Controls.Add(_wtb);

        var ok = new Button { Text = "Create", DialogResult = DialogResult.OK, AutoSize = true };
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

        Controls.Add(panel);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
