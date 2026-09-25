using Xt1.Core.Containers;

namespace Xt1.Core.Editing;

/// <summary>What a folder-wide export should write and how the output is laid out.</summary>
/// <param name="Format">PNG, DDS or raw XT1.</param>
/// <param name="SourceRoot">Folder the file list was taken from; subfolder mode mirrors paths under it.</param>
/// <param name="OutputFolder">Destination root folder.</param>
/// <param name="SubfolderPerBank">
/// True: one subfolder per bank file (mirroring the source tree). False: every texture in the one
/// output folder, prefixed with the bank's name.
/// </param>
public sealed record FolderExportOptions(
    TextureExportFormat Format,
    string SourceRoot,
    string OutputFolder,
    bool SubfolderPerBank);

public sealed record FolderExportStats(int Files, int Textures, int Written, int Skipped);

/// <summary>
/// Exports every texture of a set of bank files (`.dat`/`.wta`/`.wtb`/`.xt1`) to PNG, DDS or raw
/// XT1. The shape mirrors <see cref="BatchRecompressor.RunOnFiles"/>: load, walk, report, skip what
/// cannot be written, and never let one bad file abort the batch.
/// </summary>
public sealed class TextureFolderExporter
{
    public FolderExportStats Run(IReadOnlyList<string> files, FolderExportOptions options,
        IProgress<string> log, CancellationToken token)
    {
        int written = 0, skipped = 0, textures = 0;
        string extension = TextureExport.Extension(options.Format);
        var usedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < files.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var file = files[i];
            log.Report($"[{i + 1}/{files.Count}] {Path.GetFileName(file)}");

            BankDocument document;
            try
            {
                document = BankDocument.Load(file);
            }
            catch (Exception ex)
            {
                log.Report("    failed to load: " + ex.Message);
                continue;
            }

            var queue = new List<EditableTexture>();
            foreach (var section in document.Sections)
                queue.AddRange(section.Bank.Textures);
            // A loose .xt1 has no WTB bank of its own; its one texture still belongs in the batch.
            if (document.RawXt1 is { } raw)
                queue.Add(EditableTexture.FromRawReadOnly(raw));

            if (queue.Count == 0)
            {
                log.Report("    no textures");
                continue;
            }

            string bankStem = Path.GetFileNameWithoutExtension(file);
            string folder = options.SubfolderPerBank
                ? ReserveDirectory(options, file, bankStem, usedDirectories)
                : options.OutputFolder;
            Directory.CreateDirectory(folder);

            int fileWritten = 0, fileSkipped = 0;
            foreach (var texture in queue)
            {
                token.ThrowIfCancellationRequested();
                textures++;

                string name = options.SubfolderPerBank
                    ? $"tex{texture.Index}_0x{texture.EffectiveHash():X8}{extension}"
                    : $"{bankStem}_tex{texture.Index}_0x{texture.EffectiveHash():X8}{extension}";

                string path = ReserveFile(Path.Combine(folder, name), usedFiles);

                if (TryWriteAtomic(texture, options.Format, path, out var error))
                {
                    written++;
                    fileWritten++;
                }
                else
                {
                    skipped++;
                    fileSkipped++;
                    log.Report($"    skipped #{texture.Index} {texture.FormatInfo?.Name ?? "unknown"}: {error}");
                }
            }

            log.Report($"    exported {fileWritten}, skipped {fileSkipped}");
        }

        return new FolderExportStats(files.Count, textures, written, skipped);
    }

    /// <summary>
    /// One output subfolder per bank file, under the source tree's relative path so recursive scans
    /// with same-named banks (e.g. two `core.dat`) stay separate. A stem used twice in one directory
    /// gets a numeric suffix rather than merging.
    /// </summary>
    private static string ReserveDirectory(FolderExportOptions options, string file, string bankStem,
        HashSet<string> used)
    {
        string relative = Path.GetRelativePath(options.SourceRoot, Path.GetDirectoryName(file) ?? options.SourceRoot);
        if (relative == ".") relative = string.Empty;

        string basePath = Path.Combine(options.OutputFolder, relative, bankStem);
        string path = basePath;
        for (int n = 2; !used.Add(path); n++)
            path = $"{basePath}_{n}";
        return path;
    }

    /// <summary>
    /// Write through a sibling temp file (keeping the real extension, so PNG picks its encoder) and
    /// move it into place only after a successful write. A failed or cancelled write therefore
    /// cannot truncate or replace an existing output, and leaves no partial file behind.
    /// </summary>
    private static bool TryWriteAtomic(EditableTexture texture, TextureExportFormat format, string path,
        out string? error)
    {
        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        string temp = Path.Combine(directory, $"{stem}.tmp{extension}");

        if (!TextureExport.TryWrite(texture, format, temp, out error))
        {
            TryDelete(temp);
            return false;
        }

        try
        {
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            TryDelete(temp);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best effort: a leftover temp file must not mask the real write error.
        }
    }

    /// <summary>Append a numeric suffix when a file name is already taken in the same output folder.</summary>
    private static string ReserveFile(string path, HashSet<string> used)
    {
        if (used.Add(path)) return path;

        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        for (int n = 2; ; n++)
        {
            string candidate = Path.Combine(directory, $"{stem}_{n}{extension}");
            if (used.Add(candidate)) return candidate;
        }
    }
}
