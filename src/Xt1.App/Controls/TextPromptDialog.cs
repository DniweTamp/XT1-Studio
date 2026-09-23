using Xt1.App.Theming;

namespace Xt1.App.Controls;

/// <summary>Small modal single-line text prompt, used for the batch-force helpers and the CRC32 name.</summary>
public sealed class TextPromptDialog : ThemedForm
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
        ClientSize = new Size(380, 132);

        _text = new TextBox { Text = initial, Dock = DockStyle.Fill };

        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(14, 14, 14, 4),
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        top.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        top.Controls.Add(new Label { Text = prompt, AutoSize = true, Margin = new Padding(0, 0, 0, 6) }, 0, 0);
        top.Controls.Add(_text, 0, 1);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 44,
            Padding = new Padding(10, 8, 10, 8),
        };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        Controls.Add(top);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
