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
/// Headless diagnostic used by <c>XT1 Studio.exe --selfcheck</c>. Exercises the same services the
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

                // A real texture exported as a loose .xt1 in its own temp folder, for the two checks
                // below. The caller removes the folder when it is done with the file.
                (string Source, EditableTexture Texture) WriteSampleXt1()
                {
                    var document = BankDocument.Load(golden!);
                    var texture = document.AllTextures.First(t => TextureCodec.CanDecode(t.FormatInfo));
                    var directory = Path.Combine(Path.GetTempPath(), "Xt1Studio", "selfcheck",
                        Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(directory);
                    var source = Path.Combine(directory, "sample.xt1");
                    File.WriteAllBytes(source, new RawXt1(texture.Header.Clone(), texture.Payload).ToBytes());
                    return (source, texture);
                }

                static void DeleteSample(string source)
                {
                    try { Directory.Delete(Path.GetDirectoryName(source)!, recursive: true); } catch { }
                }

                /// <summary>The size a thumbnail should come out at: the request, or the level under it.</summary>
                static int ExpectedSize(EditableTexture texture, int requested)
                {
                    var mip = texture.Layout.Mips[Thumbnailer.PickMip(texture, requested)];
                    return Math.Min(requested, Math.Max(mip.Width, mip.Height));
                }

                // The whole --thumbnail path on a real texture: export it as a loose .xt1, run the
                // command line over it, and check the image that comes back is the expected size.
                Check("thumbnail a loose .xt1", () =>
                {
                    var (source, texture) = WriteSampleXt1();
                    try
                    {
                        var target = Path.Combine(Path.GetDirectoryName(source)!, "thumb.png");
                        var rest = new[] { source, target, "--size", "256" };
                        int exit = ThumbnailCli.Run(source, target, rest, line => log("       " + line));
                        if (exit != 0) throw new InvalidOperationException($"--thumbnail exited {exit}");
                        if (!File.Exists(target)) throw new InvalidOperationException("no image was written");

                        var mips = texture.Layout.Mips;
                        int mip = Thumbnailer.PickMip(texture, 256);
                        var thumb = ImageIo.Load(target);
                        if (Math.Max(thumb.Width, thumb.Height) != ExpectedSize(texture, 256))
                            throw new InvalidOperationException(
                                $"thumbnail is {thumb.Width}x{thumb.Height}, " +
                                $"expected {ExpectedSize(texture, 256)}");
                        if (mips.Count > 1 && Math.Max(mips[0].Width, mips[0].Height) > 256 && mip == 0)
                            throw new InvalidOperationException("a level under the base was available but not used");
                        return $"mip {mip} of {mips.Count} -> {thumb.Width}x{thumb.Height}";
                    }
                    finally
                    {
                        DeleteSample(source);
                    }
                });

                // The shell handler itself, called the way Explorer calls it (minus the COM hop):
                // initialize from the file, ask for a 256px thumbnail, and check the DIB that comes
                // back. The COM host also has to be deployed beside the app for any of this to work.
                Check("shell thumbnail handler", () =>
                {
                    var comHost = Xt1.Shell.ShellRegistration.FindComHost();
                    if (comHost is null)
                        throw new InvalidOperationException(
                            "Xt1.Shell.comhost.dll is not beside the app (expected in a `shell` folder)");

                    var (source, texture) = WriteSampleXt1();
                    try
                    {
                        var provider = new Xt1.Shell.Xt1ThumbnailProvider();
                        int initialized = provider.Initialize(source, 0);
                        if (initialized != 0)
                            throw new InvalidOperationException($"Initialize failed (0x{initialized:X8})");

                        int hr = ((Xt1.Shell.IThumbnailProvider)provider)
                            .GetThumbnail(256, out var bitmap, out var alpha);
                        if (hr != 0) throw new InvalidOperationException($"GetThumbnail failed (0x{hr:X8})");

                        try
                        {
                            if (!Xt1.Shell.DibBitmap.Describe(bitmap, out int width, out int height, out int bpp))
                                throw new InvalidOperationException("the handler did not return a DIB");

                            int expected = ExpectedSize(texture, 256);
                            if (Math.Max(width, height) != expected)
                                throw new InvalidOperationException(
                                    $"{width}x{height} bitmap, expected {expected} on the long side");
                            if (bpp != 32)
                                throw new InvalidOperationException($"bitmap is {bpp}bpp, expected 32");
                            return $"{width}x{height} {bpp}bpp, alpha type {alpha}, host {Path.GetFileName(comHost)}";
                        }
                        finally
                        {
                            Xt1.Shell.DibBitmap.Delete(bitmap);
                        }
                    }
                    finally
                    {
                        DeleteSample(source);
                    }
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
