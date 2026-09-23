using System.Windows;

namespace Xt1.App.Wpf.Dialogs;

/// <summary>A themed yes/no confirmation, so destructive actions never fall back to a stock MessageBox.</summary>
public partial class ConfirmDialog : ThemedDialog
{
    public ConfirmDialog(string title, string message, string confirmText)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
    }

    /// <summary>Show the confirmation; true only when the user chooses the confirm action.</summary>
    public static bool Confirm(Window? owner, string title, string message, string confirmText)
    {
        var dialog = new ConfirmDialog(title, message, confirmText);
        if (owner is not null) dialog.Owner = owner;
        return dialog.ShowDialog() == true;
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
}
