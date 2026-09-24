using Xt1.Core.Codecs;
using Xt1.Core.Containers;
using Xt1.Core.Encoding;
using Xt1.Core.Format;
using Xt1.Core.Imaging;
using Xt1.Core.Xt1;

namespace Xt1.Core.Editing;

public sealed record RecompressOptions(
    RecompressTarget OpaqueFormat,
    RecompressTarget AlphaFormat,
    bool Downscale,
    bool SkipDownscaleWithoutMips,
    EncoderBackend Backend,
    EncodeQuality OpaqueQuality,
    EncodeQuality AlphaQuality,
    bool SyncFlags)
{
    /// <summary>
    /// Textures with transparency use <see cref="AlphaFormat"/> at <see cref="AlphaQuality"/>; opaque
    /// ones use <see cref="OpaqueFormat"/> at <see cref="OpaqueQuality"/>. Each target then resolves
    /// to its sRGB or UNORM variant from the source, so a single choice fits a mixed bank.
    /// </summary>
    public EncodeQuality QualityFor(bool hasAlpha) => hasAlpha ? AlphaQuality : OpaqueQuality;
}

public sealed record RecompressStats(int Files, int Converted, int Skipped);

/// <summary>
/// Recompresses ASTC textures to BC in place: the existing mip levels are decoded and re-encoded
/// as-is (no new mips are generated). "Downscale" halves the texture by dropping mip 0 when a mip
/// chain exists, or by box-filtering a single-mip image down when it does not.
/// </summary>
public sealed class BatchRecompressor
{
    private readonly EncoderPicker _encoders;

    public BatchRecompressor(EncoderPicker encoders) => _encoders = encoders;

    /// <summary>Recompress every eligible texture of an in-memory document (the caller saves it).</summary>
    public RecompressStats RunOnDocument(BankDocument document, RecompressOptions options,
        IProgress<string> log, CancellationToken token)
    {
        int converted = ConvertDocument(document, options, log, token, out int skipped);
        return new RecompressStats(1, converted, skipped);
    }

    /// <summary>Recompress whole files in place. `.dtt` payloads are reached through their `.dat`.</summary>
    public RecompressStats RunOnFiles(IReadOnlyList<string> files, RecompressOptions options,
        IProgress<string> log, CancellationToken token)
    {
        int converted = 0, skipped = 0;
        for (int i = 0; i < files.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var file = files[i];
            log.Report($"[{i + 1}/{files.Count}] {Path.GetFileName(file)}");
            try
            {
                var document = BankDocument.Load(file);
                int before = converted;
                int done = ConvertDocument(document, options, log, token, out int notConverted);
                converted += done;
                skipped += notConverted;

                if (done == 0)
                {
                    log.Report("    no ASTC textures converted; file left untouched");
                    continue;
                }

                var result = BankWriter.Write(document, new WriteOptions());
                result.SaveAll();
                log.Report($"    converted {converted - before} texture(s)");
            }
            catch (Exception ex)
            {
                log.Report("    failed: " + ex.Message);
            }
        }
        return new RecompressStats(files.Count, converted, skipped);
    }

    /// <summary>Convert every eligible texture of a document, including a raw `.xt1` wrapper.</summary>
    private int ConvertDocument(BankDocument document, RecompressOptions options, IProgress<string> log,
        CancellationToken token, out int skipped)
    {
        int converted = 0;
        skipped = 0;
        foreach (var section in document.Sections)
        {
            foreach (var texture in section.Bank.Textures)
            {
                token.ThrowIfCancellationRequested();
                if (Convert(texture, options, log)) converted++;
                else skipped++;
            }
        }

        if (document.RawXt1 is { } raw)
        {
            var wrapper = EditableTexture.FromRaw(raw);
            if (Convert(wrapper, options, log))
            {
                raw.Payload = wrapper.Payload;
                converted++;
            }
            else
            {
                skipped++;
            }
        }
        return converted;
    }

    /// <summary>Recompress one texture, returning true when its payload was replaced.</summary>
    public bool Convert(EditableTexture texture, RecompressOptions options, IProgress<string> log)
    {
        var source = texture.FormatInfo;
        if (source?.Codec != CodecKind.Astc) return false;
        if (texture.Header.Type.Is3D() || texture.Header.Type.IsCube()) return false;

        int mips = Math.Max(1, texture.Header.MipCount);
        var levels = new List<RgbaImage>(mips);
        for (int m = 0; m < mips; m++)
        {
            // Decode still returns a zero-filled image when the payload is truncated or missing and
            // reports that only through the warning. Treat any warning as "does not decode cleanly"
            // and skip, otherwise a broken source would be overwritten with an encode of zeros.
            var image = TextureCodec.Decode(texture, m, 0, 0, out var warning);
            if (image is null || warning is not null) return false;
            levels.Add(image);
        }

        bool hasAlpha = levels[0].HasTransparency();
        var target = (hasAlpha ? options.AlphaFormat : options.OpaqueFormat)
            .Resolve(source.IsSrgb);
        var encoder = _encoders.Pick(options.Backend, target);

        var working = levels;
        if (options.Downscale)
        {
            if (levels.Count > 1)
            {
                working = levels.GetRange(1, levels.Count - 1); // drop mip 0 (use the existing chain)
            }
            else if (!options.SkipDownscaleWithoutMips)
            {
                int w = Math.Max(1, levels[0].Width / 2);
                int h = Math.Max(1, levels[0].Height / 2);
                working = new List<RgbaImage> { MipChain.Downsample(levels[0], w, h, source.IsSrgb) };
            }
        }

        var originalFormat = texture.Header.Format;
        byte[] originalPayload = texture.Payload;
        uint originalSize = texture.PayloadSize;
        ulong originalImageSize = texture.Header.ImageSize;
        int originalMips = texture.Header.MipCount;
        uint originalW = texture.Header.Width;
        uint originalH = texture.Header.Height;
        texture.Header.Format = target.Format;

        var quality = options.QualityFor(hasAlpha);
        var result = TextureEncoder.EncodeLevels(texture, working, encoder, quality);
        if (!result.Success)
        {
            texture.Header.Format = originalFormat;
            texture.Payload = originalPayload;
            texture.PayloadSize = originalSize;
            texture.Header.ImageSize = originalImageSize;
            texture.Header.MipCount = originalMips;
            texture.Header.Width = originalW;
            texture.Header.Height = originalH;
            log.Report($"    texture {texture.Index}: {result.Error}");
            return false;
        }

        if (options.SyncFlags) TextureOperations.SyncFlagsFromFormat(texture);

        log.Report($"    #{texture.Index} {source.Name} -> {target.Name}, {quality} quality, {mips} mip(s)");
        return true;
    }
}
