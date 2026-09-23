using Xt1.Core.Codecs;
using Xt1.Core.Containers;

namespace Xt1.App.Wpf.Cli;

/// <summary>
/// <c>XT1 Studio.exe --dump-tree &lt;file|folder&gt;</c>: prints the container â†’ sub-file â†’ texture
/// hierarchy the UI tree shows, and decodes every supported base mip to prove the payload lines
/// up. Given a folder it scans every bank file and reports an aggregate.
/// </summary>
internal static class TreeDump
{
    internal static int Run(string path, Action<string> log)
    {
        try
        {
            if (Directory.Exists(path))
                return RunFolder(path, log);

            if (!File.Exists(path))
            {
                log($"[error] file or folder not found: {path}");
                return 2;
            }

            var single = Inspect(path, log, printTree: true);
            return single.Failed == 0 && single.Error is null ? 0 : 1;
        }
        catch (Exception ex)
        {
            log($"[error] {ex.Message}");
            return 1;
        }
    }

    private sealed record Inspection(
        string Path, BankKind Kind, int Banks, int Textures, int Supported, int Decoded, int Failed,
        string? Error, List<string>? UnsupportedFormats = null,
        int LayoutMismatches = 0, int PitchRecovered = 0, int WidePitch = 0,
        List<string>? MismatchExamples = null,
        long MagentaPixels = 0, List<string>? MagentaExamples = null);

    private static Inspection Inspect(string path, Action<string>? log, bool printTree)
    {
        string name = Path.GetFileName(path);
        try
        {
            var document = BankDocument.Load(path);
            var rawTexture = document.RawXt1 is { } raw ? EditableTexture.FromRaw(raw) : null;
            var outline = BankOutline.Build(document, rawTexture);

            if (printTree)
            {
                log!($"{document.DisplayName}  [{document.Kind}]  " +
                     $"{document.Sections.Count} bank(s), {document.AllTextures.Count()} texture(s)");
                foreach (var warning in document.Warnings)
                    log!($"  ! {warning}");
                log!(outline.Label);
                Print(outline, 1, log!);
                var selector = BankOutline.SelectorLabels(document);
                if (selector.Count > 0)
                    log!($"  (bank selector: {string.Join(" | ", selector)})");
            }
            else
            {
                foreach (var warning in document.Warnings)
                    log?.Invoke($"  ! {name}: {warning}");
            }

            int total = 0, supported = 0, decoded = 0, failed = 0;
            int layoutMismatches = 0, pitchRecovered = 0, widePitch = 0;
            long magenta = 0;
            var unsupportedFormats = new List<string>();
            var mismatchExamples = new List<string>();
            var magentaExamples = new List<string>();
            foreach (var texture in document.AllTextures)
            {
                total++;
                var layout = texture.Layout;
                if (layout.Applicable)
                {
                    if (layout.WidePitchApplied) widePitch++;
                    if (layout.PitchRecoveredFromStoredSize) pitchRecovered++;
                    else if (!layout.SizeMatches)
                    {
                        layoutMismatches++;
                        if (printTree)
                            log!($"  ! #{texture.Index} {texture.FormatInfo!.Name}: {layout.Note}");
                        else if (mismatchExamples.Count < 200)
                            mismatchExamples.Add($"{name} #{texture.Index} {texture.FormatInfo?.Name}: {layout.Note}");
                    }
                }

                if (!TextureCodec.CanDecode(texture.FormatInfo))
                {
                    unsupportedFormats.Add(
                        texture.FormatInfo is null
                            ? $"unknown 0x{texture.Header.FormatValue:X2}"
                            : texture.FormatInfo.DisplayName);
                    continue;
                }
                supported++;
                var image = TextureCodec.Decode(texture, 0, 0, 0, out var error);
                if (image is null)
                {
                    failed++;
                    log?.Invoke($"  x {name} #{texture.Index} {texture.FormatInfo!.Name}: {error}");
                }
                else
                {
                    decoded++;

                    // Only a record whose geometry disagreed with the formula can leak padding, so
                    // magenta is only counted there (elsewhere it is ordinary texture content).
                    if (layout.PitchRecoveredFromStoredSize || layout.WidePitchApplied || !layout.SizeMatches)
                    {
                        long count = 0;
                        var pixels = image.Pixels;
                        for (int i = 0; i + 3 < pixels.Length; i += 4)
                            if (pixels[i] > 200 && pixels[i + 1] < 80 && pixels[i + 2] > 200)
                                count++;
                        if (count > 2000)
                        {
                            magenta += count;
                            if (magentaExamples.Count < 12)
                                magentaExamples.Add($"{name} #{texture.Index} {texture.FormatInfo?.Name}: {count} magenta px");
                        }
                    }
                }
            }

            if (printTree)
                log!($"  decode: {supported}/{total} supported, {failed} failed" +
                     (widePitch > 0 ? $", {widePitch} wide-pitch record(s)" : string.Empty) +
                     (pitchRecovered > 0 ? $", {pitchRecovered} pitch(es) recovered from imageSize" : string.Empty) +
                     (layoutMismatches > 0 ? $", {layoutMismatches} layout mismatch(es)" : string.Empty));

            return new Inspection(path, document.Kind, document.Sections.Count, total, supported, decoded,
                failed, null, unsupportedFormats, layoutMismatches, pitchRecovered, widePitch, mismatchExamples,
                magenta, magentaExamples);
        }
        catch (Exception ex)
        {
            log?.Invoke($"  x {name}: {ex.Message}");
            // A load failure is a file-level failure; it is not a texture that failed to decode.
            return new Inspection(path, default, 0, 0, 0, 0, 0, ex.Message);
        }
    }

    /// <summary>
    /// A `.dat` that holds no `"wta"`/`"wtp"` sub-file is not a texture bank (the effect folder is
    /// full of `epb` containers). Report it as skipped rather than failed.
    /// </summary>
    private static bool IsNonTextureContainer(string path, out string tags)
    {
        tags = string.Empty;
        if (!Path.GetExtension(path).Equals(".dat", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var dat = DatFile.Parse(File.ReadAllBytes(path));
            if (dat.FindByTag("wta") is not null || dat.FindByTag("wtp") is not null) return false;
            tags = string.Join(", ", dat.SubFiles.Select(s => s.Tag).Distinct());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int RunFolder(string directory, Action<string> log)
    {
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(IsBankFile)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        log($"scanning {files.Count} bank file(s) under {directory}");
        log("");

        int ok = 0, failedFiles = 0, skipped = 0, textures = 0, decoded = 0, failedTextures = 0;
        int layoutMismatches = 0, pitchRecovered = 0, widePitch = 0;
        long magenta = 0;
        var byKind = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var unsupported = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var mismatchExamples = new List<string>();
        var magentaExamples = new List<string>();

        foreach (var file in files)
        {
            if (IsNonTextureContainer(file, out var tags))
            {
                skipped++;
                log($"  - {Path.GetFileName(file)}: not a texture bank (tags: {tags})");
                continue;
            }

            var result = Inspect(file, log, printTree: false);
            textures += result.Textures;
            decoded += result.Decoded;
            failedTextures += result.Failed;
            layoutMismatches += result.LayoutMismatches;
            pitchRecovered += result.PitchRecovered;
            widePitch += result.WidePitch;
            if (result.MismatchExamples is not null) mismatchExamples.AddRange(result.MismatchExamples);
            magenta += result.MagentaPixels;
            if (result.MagentaExamples is not null) magentaExamples.AddRange(result.MagentaExamples);

            if (result.Error is not null || result.Failed > 0) failedFiles++;
            else ok++;

            string key = result.Error is null ? result.Kind.ToString() : "load-failed";
            byKind[key] = byKind.GetValueOrDefault(key) + 1;

            if (result.UnsupportedFormats is not null)
            {
                foreach (var format in result.UnsupportedFormats)
                    unsupported[format] = unsupported.GetValueOrDefault(format) + 1;
            }
        }

        log("");
        log($"files: {files.Count}  ok {ok}  failed {failedFiles}  skipped {skipped} (not texture banks)");
        log($"textures decoded: {decoded}/{textures}  ({failedTextures} failed)");
        log($"layouts: {widePitch} wide-pitch record(s), {pitchRecovered} pitch(es) recovered from imageSize, {layoutMismatches} unexplained mismatch(es)");
        log($"padding magenta leaked by anomalous/recovered records: {magenta} pixel(s) in {magentaExamples.Count} record(s)");
        foreach (var entry in byKind)
            log($"  {entry.Key}: {entry.Value}");
        if (unsupported.Count > 0)
        {
            log("textures with no decoder (metadata only):");
            foreach (var entry in unsupported)
                log($"  {entry.Key}: {entry.Value}");
        }
        if (mismatchExamples.Count > 0)
        {
            log("unexplained layout mismatches (showing up to 10):");
            foreach (var example in mismatchExamples.Take(10))
                log($"  {example}");
        }
        if (magentaExamples.Count > 0)
        {
            log("records with leaked padding (showing up to 12):");
            foreach (var example in magentaExamples.Take(12))
                log($"  {example}");
        }

        return failedFiles == 0 && failedTextures == 0 ? 0 : 1;
    }

    private static bool IsBankFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".wta", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".wtb", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".dat", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xt1", StringComparison.OrdinalIgnoreCase);
    }

    private static void Print(BankOutlineNode node, int depth, Action<string> log)
    {
        var indent = new string(' ', depth * 2);
        foreach (var child in node.Children)
        {
            log($"{indent}{child.Label}");
            Print(child, depth + 1, log);
        }
    }
}

