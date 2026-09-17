namespace Xt1.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--selfcheck", StringComparison.OrdinalIgnoreCase)))
            return SelfCheck.Run(line => Console.WriteLine(line));

        var uiTestIndex = Array.FindIndex(args, a => string.Equals(a, "--uitest", StringComparison.OrdinalIgnoreCase));
        if (uiTestIndex >= 0)
        {
            var uiTarget = uiTestIndex + 1 < args.Length && !args[uiTestIndex + 1].StartsWith('-')
                ? args[uiTestIndex + 1]
                : null;
            return UiSmoke.Run(uiTarget, line => Console.WriteLine(line));
        }

        var exportIndex = Array.FindIndex(args, a => string.Equals(a, "--export-decode", StringComparison.OrdinalIgnoreCase));
        if (exportIndex >= 0)
        {
            var rest = args[(exportIndex + 1)..];
            var exportFile = rest.Length > 0 && !rest[0].StartsWith("--") ? rest[0] : null;
            var exportOut = rest.Length > 1 && !rest[1].StartsWith("--") ? rest[1] : null;
            return DecodeExport.Run(exportFile, exportOut, rest, line => Console.WriteLine(line));
        }

        var shotIndex = Array.FindIndex(args, a => string.Equals(a, "--panelshot", StringComparison.OrdinalIgnoreCase));
        if (shotIndex >= 0)
        {
            var rest = args[(shotIndex + 1)..];
            var shotFile = rest.Length > 0 && !rest[0].StartsWith("--") ? rest[0] : null;
            var shotOut = rest.Length > 1 && !rest[1].StartsWith("--") ? rest[1] : null;
            return PanelShot.Run(shotFile, shotOut, rest, line => Console.WriteLine(line));
        }

        var glIndex = Array.FindIndex(args, a => string.Equals(a, "--gltest", StringComparison.OrdinalIgnoreCase));
        if (glIndex >= 0)
        {
            var rest = args[(glIndex + 1)..];
            var glFile = rest.Length > 0 && !rest[0].StartsWith("--") ? rest[0] : null;
            return GlTest.Run(glFile, rest, line => Console.WriteLine(line));
        }

        var dumpIndex = Array.FindIndex(args, a => string.Equals(a, "--dump-tree", StringComparison.OrdinalIgnoreCase));
        if (dumpIndex >= 0)
        {
            var target = dumpIndex + 1 < args.Length ? args[dumpIndex + 1] : null;
            if (target is null)
            {
                Console.WriteLine("usage: Xt1.App.exe --dump-tree <file>");
                return 2;
            }
            return TreeDump.Run(target, line => Console.WriteLine(line));
        }

        ApplicationConfiguration.Initialize();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            var initialFile = args.FirstOrDefault(a => !a.StartsWith('-') && File.Exists(a));
            Application.Run(new Forms.MainForm(initialFile));
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "XT1 Studio - fatal error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}

/// <summary>
/// Option parsing shared by the command-line diagnostics. One implementation means an option like
/// <c>--index</c> is read the same way by every subcommand; before this, <c>0x</c>-prefixed indices
/// were honoured by <c>--export-decode</c> but silently dropped by <c>--gltest</c>/<c>--panelshot</c>.
/// </summary>
internal static class CliOptions
{
    /// <summary>The value after <paramref name="name"/>, or null when the option is absent.</summary>
    public static string? Get(string[] args, string name)
    {
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    /// <summary>An integer option: decimal, or hexadecimal when prefixed with <c>0x</c>.</summary>
    public static int? GetInt(string[] args, string name)
    {
        var value = Get(args, name);
        if (value is null) return null;
        return int.TryParse(value, out int parsed) ? parsed
            : value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
              int.TryParse(value[2..], System.Globalization.NumberStyles.HexNumber, null, out parsed)
                ? parsed
                : null;
    }

    /// <summary>A hash option, read as hex with or without the <c>0x</c> prefix.</summary>
    public static uint? GetUInt(string[] args, string name)
    {
        var value = Get(args, name);
        if (value is null) return null;
        var text = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        return uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out uint parsed)
            ? parsed
            : uint.TryParse(value, out parsed) ? parsed : null;
    }

    public static float? GetFloat(string[] args, string name)
    {
        var value = Get(args, name);
        if (value is null) return null;
        return float.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float parsed)
            ? parsed
            : null;
    }
}
