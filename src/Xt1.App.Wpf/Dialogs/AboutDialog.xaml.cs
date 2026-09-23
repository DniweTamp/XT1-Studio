namespace Xt1.App.Wpf.Dialogs;

/// <summary>Version and format-reference information, mirroring the WinForms Help > About text.</summary>
public partial class AboutDialog : ThemedDialog
{
    public AboutDialog()
    {
        InitializeComponent();
        Version? version = typeof(AboutDialog).Assembly.GetName().Version;
        VersionText.Text = version is null ? string.Empty : $"Version {version}";
    }
}
