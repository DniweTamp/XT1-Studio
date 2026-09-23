using System.Windows;

namespace Xt1.App.Wpf.Dialogs;

/// <summary>Picks the container shape for a new empty bank: a `.wta`+`.wtp` pair or a single `.wtb`.</summary>
public partial class NewBankDialog : ThemedDialog
{
    /// <summary>True when the standalone `.wtb` shape was chosen; otherwise the split pair.</summary>
    public bool Standalone => StandaloneRadio.IsChecked == true;

    public NewBankDialog()
    {
        InitializeComponent();
    }

    private void OnCreate(object sender, RoutedEventArgs e) => DialogResult = true;
}
