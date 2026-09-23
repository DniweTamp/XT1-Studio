using System.Diagnostics;
using Xt1.Core.Containers;
using Xt1.Core.Imaging;

namespace Xt1.App.Wpf.Cli;

/// <summary>
/// <c>XT1 Studio.exe --thumbnail &lt;file.xt1&gt; &lt;out.png&gt; [--size N] [--mip M] [--face F] [--slice S]</c>:
/// renders one loose XT1 texture to a thumbnail-sized image.
/// <para>
/// This is the command-line half of the Explorer-thumbnail story. Explorer itself only thumbnails a
/// file type through an in-process COM handler registered per extension, which for a .NET 8 app means
/// hosting the runtime inside <c>explorer.exe</c>; a shell thumbnail host that can run an external
/// tool (Media Preview's custom mode, CBX Shell, ...) instead calls this and caches the image, which
/// keeps that fragile part out of the app. Output is written with the same encoder as
/// <c>--export-decode</c>, so any format ImageSharp covers (.png/.bmp/.jpg) works.
/// </para>
/// </summary>
internal static class ThumbnailCli
{
    private static readonly string[] OutputExtensions = { ".png", ".bmp", ".jpg", ".jpeg" };

    internal static int Run(string? file, string? output, string[] args, Action<string> log)
    {
        if (file is null || output is null)
        {
            log("usage: --thumbnail <file.xt1> <out.png> [--size N] [--mip M] [--face F] [--slice S]");
            return 2;
        }

        if (!File.Exists(file))
        {
            log($"[error] file not found: {file}");
            return 2;
        }

        var extension = Path.GetExtension(output).ToLowerInvariant();
        if (!OutputExtensions.Contains(extension))
        {
            log($"[error] unsupported output extension '{extension}' (use .png, .bmp or .jpg)");
            return 2;
        }

        var options = new ThumbnailOptions(
            CliOptions.GetInt(args, "--size") ?? Thumbnailer.DefaultSize,
            CliOptions.GetInt(args, "--mip"),
            CliOptions.GetInt(args, "--face") ?? 0,
            CliOptions.GetInt(args, "--slice") ?? 0);

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(file);
        }
        catch (Exception ex)
        {
            log($"[error] {ex.Message}");
            return 1;
        }

        if (!RawXt1.HasMagic(bytes))
        {
            log($"[error] {Path.GetFileName(file)} is not a loose XT1 texture (no \"XT1\\0\" magic)");
            log("        banks (.dat/.wta/.wtb) and their .dtt/.wtp payloads are not thumbnailed;");
            log("        export the texture as .xt1 first (Export -> .xt1 in the texture menu)");
            return 2;
        }

        var watch = Stopwatch.StartNew();
        var texture = EditableTexture.FromRaw(RawXt1.Parse(bytes));
        var image = Thumbnailer.Render(texture, options, out var report, out var warning);
        if (image is null)
        {
            log($"[error] decode failed: {warning}");
            return 1;
        }

        try
        {
            ImageIo.Save(image, output);
        }
        catch (Exception ex)
        {
            log($"[error] {ex.Message}");
            return 1;
        }

        watch.Stop();
        log($"  + {Path.GetFileName(file)} -> {output}  {texture.FormatInfo?.Name}  {report}  " +
            $"{watch.ElapsedMilliseconds} ms");
        if (warning is not null)
            log($"      warning: {warning}");
        return 0;
    }
}

