using System.Diagnostics;
using Xt1.Core.Format;
using Xt1.Core.Imaging;
using Xt1.Core.Interop;
using Xt1.Core.Nvtt;

namespace Xt1.Core.Encoding;

/// <summary>
/// Command-line fallback using <c>nvtt_export.exe</c>. The real flags are probed from
/// <c>--help</c> once; the encoder then writes a temporary PNG, converts it to DDS and reads
/// the compressed surface back.
/// </summary>
public sealed class CliEncoder : ITextureEncoder
{
    private readonly string _exePath;
    private readonly string _workingDirectory;

    public string Name => "nvtt_export";

    public string ExecutablePath => _exePath;

    /// <summary>Result of the one-time <c>--help</c> probe (null when not yet run).</summary>
    public string? HelpText { get; private set; }

    public bool HelpProbed { get; private set; }

    public bool HelpSupportsRequiredFlags { get; private set; }

    private CliEncoder(string exePath)
    {
        _exePath = exePath;
        _workingDirectory = Path.Combine(Path.GetTempPath(), "Xt1Studio", "cli");
        Directory.CreateDirectory(_workingDirectory);
    }

    public static CliEncoder? TryCreate(out string? error, string? explicitPath = null)
    {
        error = null;
        var path = ToolLocator.Find(ToolLocator.NvttCliFileName, explicitPath);
        if (path is null)
        {
            error = "nvtt_export.exe not found";
            return null;
        }
        return new CliEncoder(path);
    }

    public bool Supports(FormatInfo format) => NvttFormatMap.CliSupports(format.Format);

    /// <summary>Run <c>--help</c> once and verify the flags we rely on are present.</summary>
    public void ProbeHelp()
    {
        if (HelpProbed) return;
        HelpProbed = true;
        try
        {
            HelpText = RunProcess("--help", TimeSpan.FromSeconds(30), out var stderr, out _);
            if (string.IsNullOrWhiteSpace(HelpText)) HelpText = stderr;
            HelpSupportsRequiredFlags =
                HelpText is not null &&
                HelpText.Contains("-o", StringComparison.Ordinal) &&
                HelpText.Contains("-f", StringComparison.Ordinal) &&
                HelpText.Contains("-q", StringComparison.Ordinal) &&
                HelpText.Contains("no-mips", StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            HelpText = "help probe failed: " + ex.Message;
            HelpSupportsRequiredFlags = false;
        }
    }

    public EncodeResult Encode(RgbaImage image, FormatInfo format, bool srgb, EncodeQuality quality)
    {
        var formatName = NvttFormatMap.ToCliFormatName(format.Format);
        if (formatName is null)
            return EncodeResult.Fail($"format {format.Name} is not supported by the nvtt_export CLI");
        if (!NvttFormatMap.TryGetCliFormatId(format.Format, image.HasTransparency(), out int formatId))
            return EncodeResult.Fail($"no CLI format id known for '{formatName}'");

        ProbeHelp();
        if (!HelpSupportsRequiredFlags)
            return EncodeResult.Fail($"nvtt_export --help did not advertise the expected flags: {HelpText}");

        var token = Guid.NewGuid().ToString("N");
        var inputPath = Path.Combine(_workingDirectory, token + ".png");
        var outputPath = Path.Combine(_workingDirectory, token + ".dds");

        try
        {
            ImageIo.Save(image, inputPath);

            int q = quality switch
            {
                EncodeQuality.Fastest => 0,
                EncodeQuality.Production => 2,
                EncodeQuality.Highest => 3,
                _ => 1,
            };

            string arguments =
                $"-f {formatId} -o \"{outputPath}\" -q {q} --no-mips " +
                $"--export-transfer-function {(srgb ? "srgb" : "linear")} --dx10 \"{inputPath}\"";

            var stdout = RunProcess(arguments, TimeSpan.FromMinutes(5), out var stderr, out int exitCode);
            if (exitCode != 0 || !File.Exists(outputPath))
                return EncodeResult.Fail($"nvtt_export failed (exit {exitCode}): {stderr} {stdout}");

            var dds = DdsIo.Read(File.ReadAllBytes(outputPath));
            var surface = dds.Data[0][0];
            if (surface.Length == 0)
                return EncodeResult.Fail("nvtt_export produced an empty surface");

            return EncodeResult.Ok(surface);
        }
        catch (Exception ex)
        {
            return EncodeResult.Fail(ex.Message);
        }
        finally
        {
            TryDelete(inputPath);
            TryDelete(outputPath);
        }
    }

    private string RunProcess(string arguments, TimeSpan timeout, out string stderr, out int exitCode)
    {
        var info = new ProcessStartInfo(_exePath, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _workingDirectory,
        };

        using var process = Process.Start(info)!;
        string stdout = process.StandardOutput.ReadToEnd();
        stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            exitCode = -1;
            return stdout;
        }
        exitCode = process.ExitCode;
        return stdout;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
