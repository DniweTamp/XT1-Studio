using System.Windows;

namespace Xt1.App.Wpf.Dialogs;

/// <summary>Small modal single-line text prompt, used for the batch-force helpers and the CRC32 name.</summary>
public partial class TextPromptDialog : ThemedDialog
{
    /// <summary>The text as entered; read after the dialog is accepted.</summary>
    public string Value => InputBox.Text;

    public TextPromptDialog(string title, string prompt, string initial)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        InputBox.Text = initial;

        // Focus the field with its contents selected so the user can type over the default.
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
