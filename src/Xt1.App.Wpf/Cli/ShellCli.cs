using Xt1.Shell;

namespace Xt1.App.Wpf.Cli;

/// <summary>
/// <c>XT1 Studio.exe --register-shell [--machine] [--shell-path &lt;...comhost.dll&gt;]</c> and
/// <c>--unregister-shell [--machine]</c>: add or remove the per-user registry entries that make
/// Explorer thumbnail loose <c>.xt1</c> files through <see cref="Xt1ThumbnailProvider"/>.
/// <para>
/// Registration is per-user by default, so it needs no administrator; <c>--machine</c> writes to
/// HKLM instead. Explorer keys off the extension's <c>ShellEx</c> entry and the CLSID block, so
/// neither being the default app for <c>.xt1</c> nor a running instance of this app is involved.
/// </para>
/// </summary>
internal static class ShellCli
{
    internal static int Run(bool register, string[] args, Action<string> log)
    {
        bool machine = args.Contains("--machine");
        var explicitPath = CliOptions.Get(args, "--shell-path");
        var scope = machine ? "machine-wide" : "current user";

        try
        {
            if (!register)
            {
                ShellIntegration.Disable(machine);
                log($"[ok] removed the .xt1 thumbnail handler ({scope})");
                return 0;
            }

            var comHost = ShellIntegration.ComHost(explicitPath);
            if (comHost is null)
            {
                log("[error] Xt1.Shell.comhost.dll was not found next to the app.");
                log("        build or publish the app first (the shell host is copied into a `shell`");
                log("        folder beside it), or point at it with --shell-path <...\\Xt1.Shell.comhost.dll>");
                return 2;
            }

            ShellIntegration.Enable(machine, comHost);
            log($"[ok] registered the .xt1 thumbnail handler ({scope})");
            log($"     clsid     {ShellRegistration.Clsid}");
            log($"     com host  {comHost}");
            log("     Explorer caches thumbnails per file: restart it (or clear the thumbnail cache) to");
            log("     pick up files it has already listed.");
            return 0;
        }
        catch (Exception ex)
        {
            log($"[error] {ex.Message}");
            if (machine) log("        --machine writes to HKLM and needs an elevated shell");
            return 1;
        }
    }
}

