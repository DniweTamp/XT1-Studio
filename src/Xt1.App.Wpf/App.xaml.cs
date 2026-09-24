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
        // No theme has been chosen yet (fresh install, or a config that only carries the legacy "System"
        // default): start from the OS light/dark preference. This is an in-memory default rather than a
        // saved choice, so the app keeps matching the OS until the Theme button records an explicit
        // Light or Dark; after that the button only toggles between the two.
        if (Settings.Theme == AppTheme.System)
            Settings.Theme = ThemeManager.SystemPrefersDark() ? AppTheme.Dark : AppTheme.Light;

        // Insert the palette before the shell is constructed so the first frame is already themed.
        ThemeManager.Initialize(Settings.Theme);

        // The diagnostics and shell-registration helpers run without opening the shell, mirroring the
        // WinForms entry point's switch table.
        if (Cli.HeadlessCli.IsHeadless(e.Args))
        {
            Shutdown(Cli.HeadlessCli.Run(e.Args, Console.WriteLine));
            return;
        }

        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}

