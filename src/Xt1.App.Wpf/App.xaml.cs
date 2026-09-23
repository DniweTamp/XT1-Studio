using System.Windows;
using Xt1.App.Wpf.Theming;
using Xt1.Core.Settings;

namespace Xt1.App.Wpf;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        Settings = AppSettings.Load();
        // Insert the palette before the shell is constructed so the first frame is already themed.
        ThemeManager.Initialize(Settings.Theme);
        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}

