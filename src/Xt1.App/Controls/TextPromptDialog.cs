namespace Xt1.App.Controls;

/// <summary>Small modal single-line text prompt, used for the batch-force helpers and the CRC32 name.</summary>
public sealed class TextPromptDialog : Form
{
    private readonly TextBox _text;

    public string Value => _text.Text;

    public TextPromptDialog(string title, string prompt, string initial)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(340, 120);

        _text = new TextBox { Text = initial, Location = new Point(12, 40), Width = 300 };
        Controls.Add(new Label { Text = prompt, AutoSize = true, Location = new Point(12, 16) });
        Controls.Add(_text);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(150, 78), AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(240, 78), AutoSize = true };
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
