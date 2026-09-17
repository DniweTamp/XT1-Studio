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
