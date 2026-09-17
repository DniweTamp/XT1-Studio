using System.Drawing.Imaging;

namespace Xt1.App;

/// <summary>
/// <c>Xt1.App.exe --panelshot &lt;file&gt; &lt;out.png&gt; [--index N] [--advanced] [--tree]</c>:
/// render the right-hand inspector (and optionally the tree) to a PNG so the layout can be reviewed
/// without a human at the keyboard, the same way <c>--export-decode</c> makes decode correctness
/// reviewable. Also prints the inspector state and the right-click menu for the selection.
/// </summary>
internal static class PanelShot
{
    internal static int Run(string? file, string? output, string[] args, Action<string> log)
    {
        if (file is null || output is null || !File.Exists(file))
        {
            log("usage: --panelshot <file> <out.png> [--index N] [--advanced] [--tree] [--height N]");
            return 2;
        }

        int? index = GetIntOption(args, "--index");
        int? height = GetIntOption(args, "--height");
        bool advanced = args.Contains("--advanced");
        bool tree = args.Contains("--tree");

        using var form = new Forms.MainForm(file);
        form.Show();
        Application.DoEvents();
        if (height is not null)
        {
            form.ClientSize = new Size(1400, height.Value);
            Pump(20);
        }

        if (index is not null)
        {
            form.UiTestSelectTexture(0, index.Value);
            Application.DoEvents();
        }

        Pump(20);
        form.UiTestSelectRightTab(advanced ? 1 : 0);
        Pump(20);

        log($"file: {Path.GetFileName(file)}");
        log($"inspector: {form.UiTestInspectorState}");
        log($"validation: {form.UiTestInspectorValidation}");
        log($"status: {form.UiTestStatus}");
        log("tree menu (text|state):");
        foreach (var item in form.UiTestTreeMenuItems()) log("  " + item);

        using var inspector = Capture(form.UiTestInspectorControl);
        if (inspector is null)
        {
            log("[error] could not capture the inspector");
            return 1;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        Bitmap bitmap = inspector;
        if (tree)
        {
            using var treeShot = Capture(form.UiTestTreeControl);
            if (treeShot is not null)
            {
                bitmap = new Bitmap(treeShot.Width + inspector.Width, Math.Max(treeShot.Height, inspector.Height));
                using var graphics = Graphics.FromImage(bitmap);
                graphics.Clear(Color.FromArgb(246, 246, 249));
                graphics.DrawImage(treeShot, 0, 0);
                graphics.DrawImage(inspector, treeShot.Width, 0);
            }
        }

        bitmap.Save(output, ImageFormat.Png);
        log($"wrote {output} ({bitmap.Width}x{bitmap.Height})");
        bitmap.Dispose();
        form.Close();
        return 0;
    }

    private static Bitmap? Capture(Control control)
    {
        if (control.Width <= 0 || control.Height <= 0) return null;
        var bitmap = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(bitmap, new Rectangle(0, 0, control.Width, control.Height));
        return bitmap;
    }

    private static void Pump(int iterations)
    {
        for (int i = 0; i < iterations; i++)
        {
            Application.DoEvents();
            Thread.Sleep(2);
        }
    }

    private static int? GetIntOption(string[] args, string name)
    {
        int at = Array.IndexOf(args, name);
        if (at < 0 || at + 1 >= args.Length) return null;
        return int.TryParse(args[at + 1], out int parsed) ? parsed : null;
    }
}
