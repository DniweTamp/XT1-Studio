using System.IO;
using System.Windows;

namespace Xt1.App.Wpf.Cli;

/// <summary>
/// The headless command-line surface, dispatched before the shell opens:
/// <c>--help</c>, <c>--selfcheck</c>, <c>--uitest</c>, <c>--export-decode</c>, <c>--dump-tree</c>,
/// <c>--thumbnail</c>, <c>--panelshot</c>, <c>--gltest</c>, <c>--file</c>, <c>--register-shell</c>,
/// <c>--unregister-shell</c>.
///
/// <c>--file</c> exposes the shell's file operations (new/save, export, import, add, delete, raw
/// replace, batch recompress) headlessly; the rest are diagnostics (decode/encode self-check, tree
/// dump, thumbnail generation, offscreen GL checks, UI layout checks) and the shell registration
/// helpers. Anything not matching a switch opens the shell with the first existing file argument.
/// </summary>
internal static class HeadlessCli
{

    /// <summary>Run the requested diagnostic; returns the process exit code.</summary>
    internal static int Run(string[] args, Action<string> log)
    {
        if (args.Any(a => a is "--help" or "-h" or "--?" or "/?"))
        {
            Usage(log);
            return 0;
        }

        if (args.Contains("--selfcheck", StringComparer.OrdinalIgnoreCase))
            return SelfCheck.Run(log);

        var uiTestIndex = Array.FindIndex(args, a => string.Equals(a, "--uitest", StringComparison.OrdinalIgnoreCase));
        if (uiTestIndex >= 0)
        {
            var uiTarget = uiTestIndex + 1 < args.Length && !args[uiTestIndex + 1].StartsWith('-')
                ? args[uiTestIndex + 1]
                : null;
            return UiSmoke.Run(uiTarget, log);
        }

        var exportIndex = Array.FindIndex(args, a => string.Equals(a, "--export-decode", StringComparison.OrdinalIgnoreCase));
        if (exportIndex >= 0)
        {
            var rest = args[(exportIndex + 1)..];
            var exportFile = rest.Length > 0 && !rest[0].StartsWith("--") ? rest[0] : null;
            var exportOut = rest.Length > 1 && !rest[1].StartsWith("--") ? rest[1] : null;
            return DecodeExport.Run(exportFile, exportOut, rest, log);
        }

        if (args.Contains("--register-shell", StringComparer.OrdinalIgnoreCase))
            return ShellCli.Run(register: true, args, log);

        if (args.Contains("--unregister-shell", StringComparer.OrdinalIgnoreCase))
            return ShellCli.Run(register: false, args, log);

        var thumbIndex = Array.FindIndex(args, a => string.Equals(a, "--thumbnail", StringComparison.OrdinalIgnoreCase));
        if (thumbIndex >= 0)
        {
            var rest = args[(thumbIndex + 1)..];
            var thumbFile = rest.Length > 0 && !rest[0].StartsWith('-') ? rest[0] : null;
            var thumbOut = rest.Length > 1 && !rest[1].StartsWith('-') ? rest[1] : null;
            return ThumbnailCli.Run(thumbFile, thumbOut, rest, log);
        }

        var shotIndex = Array.FindIndex(args, a => string.Equals(a, "--panelshot", StringComparison.OrdinalIgnoreCase));
        if (shotIndex >= 0)
        {
            var rest = args[(shotIndex + 1)..];
            var shotFile = rest.Length > 0 && !rest[0].StartsWith("--") ? rest[0] : null;
            var shotOut = rest.Length > 1 && !rest[1].StartsWith("--") ? rest[1] : null;
            return PanelShot.Run(shotFile, shotOut, rest, log);
        }

        var glIndex = Array.FindIndex(args, a => string.Equals(a, "--gltest", StringComparison.OrdinalIgnoreCase));
        if (glIndex >= 0)
        {
            var rest = args[(glIndex + 1)..];
            var glFile = rest.Length > 0 && !rest[0].StartsWith("--") ? rest[0] : null;
            return GlTest.Run(glFile, rest, log);
        }

        var dumpIndex = Array.FindIndex(args, a => string.Equals(a, "--dump-tree", StringComparison.OrdinalIgnoreCase));
        if (dumpIndex >= 0)
        {
            var target = dumpIndex + 1 < args.Length ? args[dumpIndex + 1] : null;
            if (target is null)
            {
                log("usage: XT1 Studio.exe --dump-tree <file>");
                return 2;
            }

            return TreeDump.Run(target, log);
        }

        if (args.Contains("--file", StringComparer.OrdinalIgnoreCase))
            return FileCli.Run(args, log);

        log("usage: XT1 Studio.exe [file] [--help]");
        log("       [--selfcheck] [--uitest <file>] [--export-decode <file> <out.png>] [--dump-tree <file>]");
        log("       [--thumbnail <file> <out.png>] [--panelshot <file> <out.png>] [--gltest <file>]");
        log("       [--file <op> ...] [--register-shell] [--unregister-shell]");
        log("run --help for the full command list");
        return 2;
    }

    /// <summary>The full command list, printed for <c>--help</c> and on a failed dispatch.</summary>
    internal static void Usage(Action<string> log)
    {
        log("XT1 Studio - texture bank viewer and editor");
        log("");
        log("usage: XT1 Studio.exe [file]                  open the shell (optionally on a file)");
        log("       XT1 Studio.exe --help                  show this help");
        log("       XT1 Studio.exe <command> [options]");
        log("");
        log("commands:");
        log("  --selfcheck                              decode/encode self-check");
        log("  --uitest [file]                          build the shell headlessly and check it");
        log("  --dump-tree <file|folder>                print the bank hierarchy and decode every texture");
        log("  --export-decode <file> <out> [options]   decode a texture to PNG (layout overrides)");
        log("  --thumbnail <file.xt1> <out> [options]   render a loose XT1 to a thumbnail image");
        log("  --panelshot <file> <out>                 render the inspector to a PNG");
        log("  --gltest <file> [--out dir]              offscreen GL preview checks");
        log("  --file <op> ...                          file operations (below)");
        log("  --register-shell | --unregister-shell    register the Explorer thumbnail handler");
        log("");
        FileCli.Usage(log);
    }

    private static readonly string[] Switches =
    {
        "--selfcheck", "--uitest", "--export-decode", "--dump-tree", "--thumbnail", "--panelshot",
        "--gltest", "--file", "--help", "-h", "--?", "/?", "--register-shell", "--unregister-shell",
    };

    /// <summary>True when the arguments ask for a headless run, so the shell must not open.</summary>
    internal static bool IsHeadless(string[] args) =>
        args.Any(a => Switches.Contains(a, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Run headless work on the UI thread. The diagnostics are dispatched from the application's
    /// startup, which is already the dispatcher thread, so this only centralises error reporting.
    /// </summary>
    internal static int RunOnUiThread(Action<string> log, Func<int> work)
    {
        try
        {
            return work();
        }
        catch (Exception ex)
        {
            log($"[error] {ex.Message}");
            return 1;
        }
    }
}

