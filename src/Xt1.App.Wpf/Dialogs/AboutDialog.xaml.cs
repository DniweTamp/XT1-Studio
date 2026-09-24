namespace Xt1.App.Wpf.Dialogs;

/// <summary>Help > About: what the tool does, with the version in the window title.</summary>
public partial class AboutDialog : ThemedDialog
{
    public AboutDialog()
    {
        InitializeComponent();
        Version? version = typeof(AboutDialog).Assembly.GetName().Version;
        Title = version is null ? "About XT1 Studio" : $"About XT1 Studio — {version}";
    }
}
