using Xt1.Core.Format;

namespace Xt1.Core.Encoding;

/// <summary>
/// Owns the available encoders and resolves the one to use for a target format. NVTT is preferred;
/// when it cannot handle a format (or is not installed) the managed encoder is the fallback.
/// </summary>
public sealed class EncoderPicker
{
    private readonly ManagedEncoder _managed = new();
    private readonly NvttEncoder? _nvtt;
    private readonly CliEncoder? _cli;

    public EncoderPicker(NvttEncoder? nvtt, CliEncoder? cli)
    {
        _nvtt = nvtt;
        _cli = cli;
    }

    public ManagedEncoder Managed => _managed;

    public NvttEncoder? Nvtt => _nvtt;

    public CliEncoder? Cli => _cli;

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

        if (_nvtt is not null && _nvtt.Supports(format))
        {
            warn?.Invoke($"{backend} cannot encode {format.Name}; used NVTT");
            return _nvtt;
        }
        if (_managed.Supports(format))
        {
            warn?.Invoke($"{backend} cannot encode {format.Name}; used the managed encoder");
            return _managed;
        }
        if (_cli is not null && _cli.Supports(format))
        {
            warn?.Invoke($"{backend} cannot encode {format.Name}; used the nvtt_export CLI");
            return _cli;
        }

        warn?.Invoke($"no encoder can handle {format.Name}; the managed encoder will report the failure");
        return _managed;
    }
}
