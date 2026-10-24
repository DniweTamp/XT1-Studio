using System.Globalization;
using System.Text.RegularExpressions;
using Xt1.Core.Codecs;
using Xt1.Core.Containers;
using Xt1.Core.Encoding;
using Xt1.Core.Imaging;
using Xt1.Core.Xt1;

namespace Xt1.Core.Editing;

/// <summary>What a matched file will be imported as.</summary>
public enum BatchReplaceKind
{
    Image,
    Dds,
    Xt1,
}

/// <summary>The outcome for one texture in a batch-replace plan.</summary>
public enum BatchReplaceStatus
{
    /// <summary>A file matched this texture and will replace it.</summary>
    Matched,

    /// <summary>No file in the folder matched this texture.</summary>
    Missing,

    /// <summary>More than one equally-preferred file matched this texture.</summary>
    Ambiguous,

    /// <summary>This texture cannot be replaced (not 2D, or an unknown format).</summary>
    Unsupported,
}

/// <summary>One texture's result within a <see cref="BatchReplacePlan"/>.</summary>
public sealed record BatchReplaceEntry(
    EditableTexture Texture,
    BatchReplaceStatus Status,
    string? File,
    BatchReplaceKind Kind,
    string? Reason = null);

/// <summary>What a scan of a folder would replace, before anything is changed.</summary>
public sealed record BatchReplacePlan(IReadOnlyList<BatchReplaceEntry> Entries, IReadOnlyList<string> UnusedFiles)
{
    public int Matched => Entries.Count(e => e.Status == BatchReplaceStatus.Matched);
    public int Missing => Entries.Count(e => e.Status == BatchReplaceStatus.Missing);
    public int Ambiguous => Entries.Count(e => e.Status == BatchReplaceStatus.Ambiguous);
    public int Unsupported => Entries.Count(e => e.Status == BatchReplaceStatus.Unsupported);
    public int Unused => UnusedFiles.Count;

    /// <summary>True when at least one match needs the encoder, i.e. is not a raw .xt1.</summary>
    public bool HasNonXt1 =>
        Entries.Any(e => e.Status == BatchReplaceStatus.Matched && e.Kind != BatchReplaceKind.Xt1);
}

/// <summary>The choices applied to every re-encoded texture in a batch replace.</summary>
public sealed record BatchReplaceOptions(EncodeQuality Quality, bool Solidify, bool SyncFlags);

public sealed record BatchReplaceStats(int Matched, int Replaced, int Skipped, int Unused);

/// <summary>
/// The commands a run produced (one per replaced texture) plus what happened. <see cref="Commands"/>
/// is ordered as the replacements were applied, so wrapping it in a <see cref="CompositeCommand"/>
/// makes the whole run a single undo.
/// </summary>
public sealed record BatchReplaceRunResult(IReadOnlyList<IEditCommand> Commands, BatchReplaceStats Stats, bool Canceled);

/// <summary>
/// Batch replace: scans a single folder for files exported as <c>tex&lt;index&gt;_0x&lt;hash&gt;.ext</c>,
/// matches each to a texture in a bank by index and hash, and re-encodes into the texture's existing
/// format. Image/DDS sources are decoded and re-encoded; a raw .xt1 replaces header and payload
/// verbatim. The scan is pure, so the caller can show what would change before applying.
/// </summary>
public sealed class TextureBatchReplacer
{
    // Exported names are "tex<index>_0x<hash>" (bank export) or "<stem>_tex<index>_0x<hash>"
    // (container export); searching for the pattern anywhere handles both.
    private static readonly Regex NamePattern = new(@"tex(\d+)_0x([0-9A-Fa-f]{8})", RegexOptions.Compiled);

    // Preference when several files match one texture: raw .xt1 beats a DDS beats an image.
    private static readonly Dictionary<string, int> Rank = new(StringComparer.OrdinalIgnoreCase)
    {
        [".xt1"] = 60,
        [".dds"] = 50,
        [".png"] = 40,
        [".bmp"] = 30,
        [".jpg"] = 20,
        [".jpeg"] = 20,
        [".tga"] = 10,
    };

    private readonly EncoderPicker _encoders;

    public TextureBatchReplacer(EncoderPicker encoders) => _encoders = encoders;

    /// <summary>
    /// Match the top-level files of <paramref name="folder"/> to <paramref name="textures"/> by
    /// <c>tex&lt;index&gt;_0x&lt;hash&gt;</c>. No file is read and no texture is changed.
    /// </summary>
    public static BatchReplacePlan BuildPlan(IReadOnlyList<EditableTexture> textures, string folder,
        CancellationToken token = default)
    {
        var candidates = new Dictionary<(int Index, uint Hash), List<(string Path, int Rank)>>();
        var files = new List<string>();

        if (Directory.Exists(folder))
        {
            // One folder only: subfolders are deliberately not walked, so the scan can never pull in
            // another bank's exports.
            foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
            {
                token.ThrowIfCancellationRequested();
                string extension = Path.GetExtension(path);
                if (!Rank.TryGetValue(extension, out int rank)) continue;

                files.Add(path);
                var match = NamePattern.Match(Path.GetFileNameWithoutExtension(path));
                if (!match.Success) continue;
                if (!int.TryParse(match.Groups[1].Value, out int index)) continue;
                if (!uint.TryParse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                        out uint hash)) continue;

                var key = (index, hash);
                if (!candidates.TryGetValue(key, out var list)) candidates[key] = list = new List<(string, int)>();
                list.Add((path, rank));
            }
        }

        var entries = new List<BatchReplaceEntry>(textures.Count);

        foreach (var texture in textures)
        {
            token.ThrowIfCancellationRequested();
            var type = texture.Header.Type;
            if (type.Is3D() || type.IsCube())
            {
                entries.Add(new BatchReplaceEntry(texture, BatchReplaceStatus.Unsupported, null,
                    BatchReplaceKind.Image, "not a 2D texture"));
                continue;
            }

            if (texture.FormatInfo is null)
            {
                entries.Add(new BatchReplaceEntry(texture, BatchReplaceStatus.Unsupported, null,
                    BatchReplaceKind.Image, "unknown format"));
                continue;
            }

            if (!candidates.TryGetValue((texture.Index, texture.EffectiveHash()), out var list) || list.Count == 0)
            {
                entries.Add(new BatchReplaceEntry(texture, BatchReplaceStatus.Missing, null, BatchReplaceKind.Image));
                continue;
            }

            int best = list.Max(c => c.Rank);
            var preferred = list.Where(c => c.Rank == best).ToList();
            if (preferred.Count > 1)
            {
                // Equally-preferred files (same extension) cannot be resolved honestly, so skip.
                entries.Add(new BatchReplaceEntry(texture, BatchReplaceStatus.Ambiguous, null,
                    BatchReplaceKind.Image, string.Join(", ", preferred.Select(c => Path.GetFileName(c.Path)))));
                continue;
            }

            var chosen = preferred[0];
            entries.Add(new BatchReplaceEntry(texture, BatchReplaceStatus.Matched, chosen.Path, KindOf(chosen.Path)));
        }

        // "Unused" means an accepted file that matches no texture at all. Files tied to a texture that
        // was skipped (ambiguous/unsupported) stay accounted for rather than being misreported.
        var textureKeys = textures.Select(t => (t.Index, t.EffectiveHash())).ToHashSet();
        var accounted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, list) in candidates)
            if (textureKeys.Contains(key))
                foreach (var candidate in list) accounted.Add(candidate.Path);

        var unused = files.Where(f => !accounted.Contains(f)).ToList();
        return new BatchReplacePlan(entries, unused);
    }

    /// <summary>
    /// Apply a plan: every matched texture is replaced in place (so the caller can turn the returned
    /// commands into one undo). Per-texture failures are logged and skipped; cancellation stops after
    /// the current texture and keeps what was already applied.
    /// </summary>
    public BatchReplaceRunResult Apply(BatchReplacePlan plan, BatchReplaceOptions options,
        IProgress<string> log, CancellationToken token)
    {
        var commands = new List<IEditCommand>();
        int replaced = 0, skipped = 0;
        bool canceled = false;

        foreach (var entry in plan.Entries)
        {
            if (token.IsCancellationRequested)
            {
                canceled = true;
                break;
            }

            if (entry.Status != BatchReplaceStatus.Matched || entry.File is null) continue;

            var texture = entry.Texture;
            log.Report($"#{texture.Index} 0x{texture.EffectiveHash():X8} <- {Path.GetFileName(entry.File)}");
            try
            {
                var result = entry.Kind == BatchReplaceKind.Xt1
                    ? TextureOperations.ReplaceWithXt1(texture, File.ReadAllBytes(entry.File))
                    : ReplaceFromImage(texture, entry.File, entry.Kind, options);

                if (!result.Success)
                {
                    skipped++;
                    log.Report("    skipped: " + result.Error);
                    continue;
                }

                // Replace2D/ReplaceWithXt1 stash their undo command in a thread-static slot; harvest it
                // here, on the same thread, so the batch can combine them.
                var command = TextureOperations.TakePendingCommand() ?? result.Command;
                if (command is not null) commands.Add(command);
                replaced++;

                var info = texture.FormatInfo;
                log.Report($"    -> {info?.Name}, {texture.Header.Width}x{texture.Header.Height}, " +
                           $"{texture.Header.MipCount} mip(s)");
            }
            catch (Exception ex)
            {
                skipped++;
                log.Report("    skipped: " + ex.Message);
            }
        }

        var stats = new BatchReplaceStats(plan.Matched, replaced, skipped, plan.Unused);
        return new BatchReplaceRunResult(commands, stats, canceled);
    }

    /// <summary>Decode a source file and re-encode it into the texture's existing format.</summary>
    private OperationResult ReplaceFromImage(EditableTexture texture, string file, BatchReplaceKind kind,
        BatchReplaceOptions options)
    {
        var format = texture.FormatInfo;
        if (format is null) return OperationResult.Fail("unknown format");

        RgbaImage? image = kind == BatchReplaceKind.Dds ? LoadDdsBase(file) : ImageIo.Load(file);
        if (image is null) return OperationResult.Fail("could not decode " + Path.GetFileName(file));

        var prepared = Solidify.Process(image, options.Solidify, makeOpaque: false);

        // The texture adopts the image's size 1:1; keep its mip count but never above what the new
        // base level can hold.
        int mips = Math.Clamp(Math.Max(1, texture.Header.MipCount), 1,
            MipChain.FullMipCount(prepared.Width, prepared.Height));

        // Same target format as the bank already stores, via the normal NVTT -> managed fallback.
        var encoder = _encoders.Pick(EncoderBackend.Nvtt, format);
        return TextureOperations.Replace2D(texture, prepared, encoder, mips, options.Quality,
            syncFlags: options.SyncFlags);
    }

    /// <summary>Decode mip 0 of a .dds so it can be re-encoded into the bank's format.</summary>
    private static RgbaImage? LoadDdsBase(string file)
    {
        var dds = DdsIo.Read(File.ReadAllBytes(file));
        if (dds.Format is null) return null;

        var temp = EditableTexture.CreateNew(dds.Format.Format, (uint)dds.Width, (uint)dds.Height);
        if (!TextureEncoder.ImportDds(temp, dds).Success) return null;
        return TextureCodec.Decode(temp, 0, 0, 0, out _);
    }

    private static BatchReplaceKind KindOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".xt1" => BatchReplaceKind.Xt1,
        ".dds" => BatchReplaceKind.Dds,
        _ => BatchReplaceKind.Image,
    };
}
