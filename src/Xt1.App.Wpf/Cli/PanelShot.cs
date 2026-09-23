using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xt1.App.Wpf.Preview;
using Xt1.Core.Containers;
using Xt1.Core.Util;

namespace Xt1.App.Wpf.Cli;

/// <summary>
/// <c>XT1 Studio.exe --panelshot &lt;file&gt; &lt;out.png&gt; [--index N] [--advanced] [--tree]</c>:
/// render the right-hand inspector (and optionally the asset tree) to a PNG so the layout can be
/// reviewed without a human at the keyboard, the same way <c>--export-decode</c> makes decode
/// correctness reviewable. Also prints the inspector state and the right-click menu for the selection.
///
/// The WPF version renders the live visual tree with <see cref="RenderTargetBitmap"/>, so no screen
/// capture or window placement is involved.
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

        int? index = CliOptions.GetInt(args, "--index");
        int? height = CliOptions.GetInt(args, "--height");
        bool advanced = args.Contains("--advanced");
        bool tree = args.Contains("--tree");

        return HeadlessCli.RunOnUiThread(log, () =>
        {
            var window = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000,
                Top = 0,
                ShowInTaskbar = false,
            };

            try
            {
                window.Show();
                window.UiTestLoadFile(file);
                if (height is not null) window.Height = height.Value;
                if (index is not null) window.UiTestSelectTexture(0, index.Value);
                window.UiTestSelectRightTab(advanced ? 1 : 0);

                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);

                log($"file: {Path.GetFileName(file)}");
                log($"inspector: {window.UiTestInspectorState}");
                log($"validation: {window.UiTestInspectorValidation}");
                log($"status: {window.UiTestStatus}");
                log("tree menu (text|state):");
                foreach (var item in window.UiTestTreeMenuItems()) log("  " + item);

                var inspector = Capture(window.UiTestInspectorElement);
                if (inspector is null)
                {
                    log("[error] could not render the inspector");
                    return 1;
                }

                BitmapSource result = inspector;
                if (tree && Capture(window.UiTestTreeElement) is { } treeShot)
                {
                    var composed = new DrawingVisual();
                    using (var context = composed.RenderOpen())
                    {
                        context.DrawRectangle(ThemeBrush("Brush.Window"), null,
                            new Rect(0, 0, treeShot.Width + inspector.Width,
                                Math.Max(treeShot.Height, inspector.Height)));
                        context.DrawImage(treeShot, new Rect(0, 0, treeShot.Width, treeShot.Height));
                        context.DrawImage(inspector,
                            new Rect(treeShot.Width, 0, inspector.Width, inspector.Height));
                    }

                    var composedBitmap = new RenderTargetBitmap(
                        treeShot.PixelWidth + inspector.PixelWidth,
                        Math.Max(treeShot.PixelHeight, inspector.PixelHeight), 96, 96, PixelFormats.Pbgra32);
                    composedBitmap.Render(composed);
                    result = composedBitmap;
                }

                var directory = Path.GetDirectoryName(Path.GetFullPath(output));
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(result));
                using (var stream = File.Create(output)) encoder.Save(stream);

                log($"wrote {output} ({result.PixelWidth}x{result.PixelHeight})");
                return 0;
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>Render one element at its laid-out size, or null when it has no size yet.</summary>
    private static BitmapSource? Capture(FrameworkElement element)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return null;

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        return bitmap;
    }

    private static Brush ThemeBrush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Black;
}
