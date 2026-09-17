using Xt1.Core.Codecs;
using Xt1.Core.Containers;
using Xt1.Core.Editing;
using Xt1.Core.Encoding;
using Xt1.Core.Format;
using Xt1.Core.Settings;
using Xt1.Core.Xt1;

namespace Xt1.App.Services;

public sealed record BatchOptions(
    FormatInfo TargetFormat,
    EncoderBackend Backend,
    EncodeQuality Quality,
    bool GenerateMips,
    bool OverwriteInPlace,
    string? OutputDirectory);

/// <summary>
/// Converts every 2D texture in a set of banks to a target format. Cubemaps and 3D textures are
/// skipped (3D editing is out of scope; cube editing is available per-texture in the UI).
/// </summary>
public sealed class BatchConverter
{
    private readonly AppSettings _settings;
    private readonly ManagedEncoder _managed = new();
    private readonly NvttEncoder? _nvtt;
    private readonly CliEncoder? _cli;

    public BatchConverter(AppSettings settings, NvttEncoder? nvtt, CliEncoder? cli)
    {
        _settings = settings;
        _nvtt = nvtt;
        _cli = cli;
    }

    /// <summary>
    /// Choose an encoder for a format. Falls back when the requested backend cannot handle the
    /// format and reports the substitution through <paramref name="warn"/>.
    /// </summary>
    public ITextureEncoder Pick(EncoderBackend backend, FormatInfo format, Action<string>? warn = null)
    {
        switch (backend)
        {
            case EncoderBackend.Nvtt when _nvtt is not null && _nvtt.Supports(format):
                return _nvtt;
            case EncoderBackend.Cli when _cli is not null && _cli.Supports(format):
                return _cli;
            case EncoderBackend.Managed when _managed.Supports(format):
                return _managed;
        }

        if (_managed.Supports(format))
        {
            warn?.Invoke($"{backend} cannot encode {format.Name}; used the managed encoder");
            return _managed;
        }
        if (_nvtt is not null && _nvtt.Supports(format))
        {
            warn?.Invoke($"{backend} cannot encode {format.Name}; used NVTT");
            return _nvtt;
        }
        if (_cli is not null && _cli.Supports(format))
        {
            warn?.Invoke($"{backend} cannot encode {format.Name}; used the nvtt_export CLI");
            return _cli;
        }

        warn?.Invoke($"no encoder can handle {format.Name}; the managed encoder will report the failure");
        return _managed;
    }

    public void Run(IReadOnlyList<string> files, BatchOptions options,
        IProgress<string> log, CancellationToken token)
    {
        int fileIndex = 0;
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            fileIndex++;
            log.Report($"[{fileIndex}/{files.Count}] {Path.GetFileName(file)}");
            try
            {
                var document = BankDocument.Load(file, _settings.Resolve().Oodle);
                int converted = 0;
                int skipped = 0;

                foreach (var section in document.Sections)
                {
                    foreach (var texture in section.Bank.Textures)
                    {
                        token.ThrowIfCancellationRequested();
                        if (texture.Header.Type.Is3D() || texture.Header.Type.IsCube())
                        {
                            skipped++;
                            continue;
                        }
                        if (!TextureCodec.CanDecode(texture.FormatInfo))
                        {
                            skipped++;
                            continue;
                        }

                        var image = TextureCodec.Decode(texture, 0, 0, 0, out _);
                        if (image is null)
                        {
                            skipped++;
                            continue;
                        }

                        var encoder = Pick(options.Backend, options.TargetFormat,
                            warning => log.Report("    " + warning));

                        // Only commit the format change once the encode succeeded, otherwise a
                        // failed encode would be written out with a header that lies about the
                        // payload format.
                        var originalFormat = texture.Header.Format;
                        texture.Header.Format = options.TargetFormat.Format;
                        var result = TextureOperations.Replace2D(texture, image, encoder,
                            options.GenerateMips, options.Quality);
                        if (!result.Success)
                        {
                            texture.Header.Format = originalFormat;
                            log.Report($"    texture {texture.Index}: {result.Error}");
                            skipped++;
                        }
                        else
                        {
                            converted++;
                        }
                    }
                }

                if (converted == 0)
                {
                    log.Report("    nothing converted; file not written");
                    continue;
                }

                var writeOptions = new WriteOptions
                {
                    OutputDirectory = options.OverwriteInPlace ? null : options.OutputDirectory,
                    RecompressOodle = _settings.RecompressOodleOnSave,
                    OodlePath = _settings.Resolve().Oodle,
                };
                var output = BankWriter.Write(document, writeOptions);
                output.SaveAll();
                log.Report($"    converted {converted}, skipped {skipped}");
            }
            catch (Exception ex)
            {
                log.Report($"    failed: {ex.Message}");
            }
        }
    }
}
