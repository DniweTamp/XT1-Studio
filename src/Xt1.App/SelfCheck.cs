using Xt1.Core.Codecs;
using Xt1.Core.Containers;
using Xt1.Core.Editing;
using Xt1.Core.Encoding;
using Xt1.Core.Format;
using Xt1.Core.Imaging;
using Xt1.Core.Interop;
using Xt1.Core.Nvtt;
using Xt1.Core.Settings;
using Xt1.Core.Xt1;
using Xt1.App.Services;

namespace Xt1.App;

/// <summary>
/// Headless diagnostic used by <c>Xt1.App.exe --selfcheck</c>. Exercises the same services the
/// UI uses (settings, native tools, bank loading, codecs, encoders, writers) without a window,
/// so the app assembly can be verified on a machine without a usable display.
/// </summary>
internal static class SelfCheck
{
    internal static int Run(Action<string> log)
    {
        int failures = 0;
        void Check(string name, Func<string?> action)
        {
            try
            {
                var detail = action();
                log($"[ ok ] {name}{(detail is null ? string.Empty : " - " + detail)}");
            }
            catch (Exception ex)
            {
                failures++;
                log($"[FAIL] {name}: {ex.Message}");
            }
        }

        var settings = AppSettings.Load();
        var tools = settings.Resolve();
        log($"settings: {AppSettings.SettingsPath}");
        log($"tools: nvttDll={tools.NvttDll ?? "-"} nvttCli={tools.NvttCli ?? "-"}");

        NvttNative? nvtt = null;
        try
        {
            Check("nvtt load", () =>
            {
                nvtt = NvttNative.TryCreate(out var error, tools.NvttDll);
                if (nvtt is null) throw new InvalidOperationException(error);
                return $"version {nvtt.Version}";
            });

            var managed = new ManagedEncoder();
            NvttEncoder? nvttEncoder = nvtt is null ? null : new NvttEncoder(nvtt);
            var cli = CliEncoder.TryCreate(out _, tools.NvttCli);
            var encoders = new EncoderPicker(nvttEncoder, cli);

            var image = new RgbaImage(32, 32);
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++)
                    image.SetPixel(x, y, (byte)(x * 8), (byte)(y * 8), 64, 255);

            Check("managed encode/decode (BC3)", () =>
            {
                var info = FormatTable.Get(Xt1Format.Bc3Unorm)!;
                var encoded = managed.Encode(image, info, false, EncodeQuality.Normal);
                if (!encoded.Success) throw new InvalidOperationException(encoded.Error);
                var decoded = TextureCodec.DecodeLinear(encoded.Data, 32, 32, info, out var error);
                if (decoded is null) throw new InvalidOperationException(error);
                return $"{encoded.Data.Length} bytes";
            });

            Check("managed encode/decode (ASTC 8x8)", () =>
            {
                var info = FormatTable.Get(Xt1Format.Astc8x8Unorm)!;
                var encoder = encoders.Pick(EncoderBackend.Managed, info);
                var encoded = encoder.Encode(image, info, false, EncodeQuality.Normal);
                if (!encoded.Success) throw new InvalidOperationException(encoded.Error);
                var decoded = TextureCodec.DecodeLinear(encoded.Data, 32, 32, info, out var error);
                if (decoded is null) throw new InvalidOperationException(error);
                return $"{encoded.Data.Length} bytes via {encoder.Name}";
            });

            if (nvttEncoder is not null)
            {
                Check("nvtt encode (BC7)", () =>
                {
                    var info = FormatTable.Get(Xt1Format.Bc7Unorm)!;
                    var encoded = nvttEncoder.Encode(image, info, false, EncodeQuality.Normal);
                    if (!encoded.Success) throw new InvalidOperationException(encoded.Error);
                    return $"{encoded.Data.Length} bytes";
                });
            }

            var golden = new[]
            {
                @"D:\Bayonetta3\romfs\pl\pl0080.dat",
                @"D:\Bayonetta3\romfs\em\em0000.dat",
            }.FirstOrDefault(File.Exists);

            if (golden is null)
            {
                log("[skip] no golden sample found");
            }
            else
            {
                Check("load golden bank", () =>
                {
                    var document = BankDocument.Load(golden);
                    return $"{Path.GetFileName(golden)}: {document.Sections.Count} section(s), " +
                           $"{document.AllTextures.Count()} textures";
                });

                Check("round-trip writer (no edits)", () =>
                {
                    var document = BankDocument.Load(golden);
                    var result = BankWriter.Write(document, new WriteOptions());
                    var header = result.Files.First(f =>
                        f.Path.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) ||
                        f.Path.EndsWith(".wta", StringComparison.OrdinalIgnoreCase));
                    var original = File.ReadAllBytes(golden);
                    bool identical = header.Bytes.AsSpan().SequenceEqual(original);
                    if (!identical) throw new InvalidOperationException("no-edit save is not byte-identical");
                    return $"{result.Files.Count} output file(s)";
                });

                Check("decode first texture", () =>
                {
                    var document = BankDocument.Load(golden);
                    var texture = document.AllTextures.First(t => TextureCodec.CanDecode(t.FormatInfo));
                    var image = TextureCodec.Decode(texture, 0, 0, 0, out var error);
                    if (image is null) throw new InvalidOperationException(error);
                    return $"{image.Width}x{image.Height} {texture.FormatInfo!.Name}";
                });

                Check("re-encode texture", () =>
                {
                    var document = BankDocument.Load(golden);
                    var texture = document.AllTextures.First(t => TextureCodec.CanDecode(t.FormatInfo));
                    var image = TextureCodec.Decode(texture, 0, 0, 0, out _)!;
                    var target = texture.FormatInfo!;
                    var encoder = encoders.Pick(settings.PreferredBackend, target);
                    var result = TextureOperations.Replace2D(texture, image, encoder, true, EncodeQuality.Normal);
                    if (!result.Success) throw new InvalidOperationException(result.Error);
                    if (!texture.Layout.SizeMatches)
                        throw new InvalidOperationException("layout mismatch after re-encode: " + texture.Layout.Note);
                    return $"{encoder.Name}, {texture.Payload.Length} bytes, mips {texture.Header.MipCount}";
                });

                Check("save edited bank, reload, verify payload offsets", () =>
                {
                    var document = BankDocument.Load(golden);
                    var texture = document.AllTextures.First(t =>
                        TextureCodec.CanDecode(t.FormatInfo) && t.Header.Type == TextureType.T2D);
                    var image = TextureCodec.Decode(texture, 0, 0, 0, out _)!;
                    var encoder = encoders.Pick(settings.PreferredBackend, texture.FormatInfo!);

                    // Resize so the payload length changes and every later offset has to shift.
                    var resized = new RgbaImage(96, 64);
                    for (int y = 0; y < resized.Height; y++)
                        for (int x = 0; x < resized.Width; x++)
                            resized.SetPixel(x, y, (byte)(x * 2), (byte)(y * 4), 128, 255);

                    var replaced = TextureOperations.Replace2D(texture, resized, encoder, true, EncodeQuality.Normal);
                    if (!replaced.Success) throw new InvalidOperationException(replaced.Error);

                    var directory = Path.Combine(Path.GetTempPath(), "Xt1Studio", "selfcheck",
                        Guid.NewGuid().ToString("N"));
                    try
                    {
                        var output = BankWriter.Write(document, new WriteOptions { OutputDirectory = directory });
                        output.SaveAll();

                        var saved = output.Files.First(f =>
                            f.Path.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) ||
                            f.Path.EndsWith(".wta", StringComparison.OrdinalIgnoreCase)).Path;
                        var reloaded = BankDocument.Load(saved);

                        long expected = 0;
                        foreach (var reloadedTexture in reloaded.Sections[0].Bank.Textures)
                        {
                            if (reloadedTexture.PayloadOffset != (uint)expected)
                                throw new InvalidOperationException(
                                    $"table0 mismatch at texture {reloadedTexture.Index}: " +
                                    $"0x{reloadedTexture.PayloadOffset:X} != 0x{expected:X}");
                            expected = Xt1.Core.Util.Mem.Align(expected + reloadedTexture.PayloadSize, 4096);
                        }

                        return $"{reloaded.AllTextures.Count()} textures, payload offsets consistent";
                    }
                    finally
                    {
                        try { Directory.Delete(directory, recursive: true); } catch { }
                    }
                });
            }
        }
        finally
        {
            nvtt?.Dispose();
        }

        log(failures == 0 ? "SELF-CHECK PASSED" : $"SELF-CHECK FAILED ({failures})");
        return failures == 0 ? 0 : 1;
    }
}
