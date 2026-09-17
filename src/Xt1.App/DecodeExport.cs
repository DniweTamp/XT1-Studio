using Xt1.Core.Codecs;
using Xt1.Core.Containers;
using Xt1.Core.Format;
using Xt1.Core.Imaging;
using Xt1.Core.Settings;
using Xt1.Core.Xt1;
using Xt1.Core.Util;

namespace Xt1.App;

/// <summary>
/// <c>Xt1.App.exe --export-decode &lt;file&gt; &lt;out.png|outDir&gt; [options]</c>: decode a texture and
/// write it as a PNG so the layout can be inspected visually. Layout overrides exist to compare
/// candidate geometries for the records whose stored <c>imageSize</c> disagrees with the formula.
/// </summary>
internal static class DecodeExport
{
    internal static int Run(string? file, string? output, string[] args, Action<string> log)
    {
        if (file is null || output is null)
        {
            log("usage: --export-decode <file> <out.png|dir> [--index N] [--hash 0xH] [--bank B] [--mip M]");
            log("                            [--pitch P] [--blockheight H] [--no-deswizzle] [--all]");
            return 2;
        }

        if (!File.Exists(file))
        {
            log($"[error] file not found: {file}");
            return 2;
        }

        int? index = GetIntOption(args, "--index");
        uint? hash = GetUIntOption(args, "--hash");
        int? bankIndex = GetIntOption(args, "--bank");
        int mip = GetIntOption(args, "--mip") ?? 0;
        int slice = GetIntOption(args, "--slice") ?? 0;
        int? pitchOverride = GetIntOption(args, "--pitch");
        int? blockHeightOverride = GetIntOption(args, "--blockheight");
        bool noDeswizzle = args.Contains("--no-deswizzle");
        bool all = args.Contains("--all");
        bool scan = args.Contains("--scan-layouts");

        var settings = AppSettings.Load();
        var document = BankDocument.Load(file, settings.Resolve().Oodle);

        var candidates = new List<(BankSection Section, EditableTexture Texture)>();
        foreach (var section in document.Sections)
        {
            if (bankIndex is not null && document.Sections.IndexOf(section) != bankIndex) continue;
            foreach (var texture in section.Bank.Textures)
            {
                if (index is not null && texture.Index != index) continue;
                if (hash is not null && texture.EffectiveHash() != hash) continue;
                candidates.Add((section, texture));
            }
        }

        if (candidates.Count == 0)
        {
            log("[error] no texture matched");
            return 2;
        }

        if (scan)
        {
            foreach (var (_, texture) in candidates)
                ScanLayouts(texture, mip, log);
            return 0;
        }

        bool toDirectory = all || candidates.Count > 1 || Directory.Exists(output) ||
                           !Path.HasExtension(output);

        int written = 0;
        foreach (var (section, texture) in candidates)
        {
            var image = Decode(texture, mip, slice, pitchOverride, blockHeightOverride, noDeswizzle, out var geometry, out var error);
            if (image is null)
            {
                log($"  x #{texture.Index} {texture.FormatInfo?.Name}: {error}");
                continue;
            }

            var path = toDirectory
                ? Path.Combine(output, $"#{texture.Index}_{texture.EffectiveHash():X8}.png")
                : output;
            if (toDirectory) Directory.CreateDirectory(output);
            ImageIo.Save(image, path);

            var layout = texture.Layout;
            var mipInfo = mip < layout.Mips.Count ? layout.Mips[mip] : null;
            log($"  + #{texture.Index} 0x{texture.EffectiveHash():X8} {texture.FormatInfo?.Name} " +
                $"{image.Width}x{image.Height} -> {path}");
            log($"      pixels: {ChannelStats(image)}");
            log($"      stored={texture.Header.ImageSize} computed={layout.ComputedImageSize} " +
                $"match={layout.SizeMatches} recovered={layout.PitchRecoveredFromStoredSize}");
            if (mipInfo is not null)
                log($"      layout: pitch={mipInfo.Pitch} blockHeight={mipInfo.BlockHeight} " +
                    $"blocks={mipInfo.WidthBlocks}x{mipInfo.HeightBlocks} lines={mipInfo.Lines} " +
                    $"blockDepth={mipInfo.BlockDepth}" + (geometry is null ? string.Empty : $"  override: {geometry}"));
            written++;
        }

        log($"wrote {written} image(s)");
        return written > 0 ? 0 : 1;
    }

    /// <summary>Decode with optional layout overrides (bypasses the recorded geometry).</summary>
    private static RgbaImage? Decode(
        EditableTexture texture, int mip, int slice, int? pitchOverride, int? blockHeightOverride,
        bool noDeswizzle, out string? geometry, out string? error)
    {
        geometry = null;
        error = null;

        var format = texture.FormatInfo;
        if (format is null)
        {
            error = "unknown format";
            return null;
        }

        var layout = texture.Layout;
        if (!layout.Applicable || mip < 0 || mip >= layout.Mips.Count)
        {
            error = "layout unavailable";
            return null;
        }

        var info = layout.Mips[mip];

        if (pitchOverride is null && blockHeightOverride is null && !noDeswizzle)
        {
            // The normal path.
            return TextureCodec.Decode(texture, mip, 0, slice, out error);
        }

        int bpb = format.BytesPerBlock;
        int wb = info.WidthBlocks;
        int hb = info.HeightBlocks;
        int blockHeight = blockHeightOverride ?? info.BlockHeight;
        int pitch = pitchOverride ?? info.Pitch;
        int widthInGobs = (pitch + 63) / 64;
        int rows = Mem.Align(hb, 8 * blockHeight);
        geometry = $"pitch={pitch} ({widthInGobs} GOBs) blockHeight={blockHeight} rows={rows} " +
                   $"surface={pitch * rows} blockDepth={info.BlockDepth}";

        var linear = new byte[checked(wb * hb * bpb)];
        long offset = info.Offset;
        int available = (int)Math.Min(payloadLength(texture) - offset, int.MaxValue);
        if (available <= 0)
        {
            error = "payload ends before this mip";
            return null;
        }

        if (noDeswizzle)
        {
            var raw = new byte[linear.Length];
            texture.Payload.AsSpan((int)offset, Math.Min(available, raw.Length)).CopyTo(raw);
            geometry += "  (no deswizzle)";
            return TextureCodec.DecodeLinear(raw, info.Width, info.Height, format, out error);
        }

        // Custom deswizzle: the standard algorithm but with a forced pitch, which the row stride
        // (image_width_in_gobs) feeds into.
        int surfaceSize = pitch * rows * Math.Max(1, info.SliceCount);
        var surface = new byte[Math.Max(surfaceSize, info.SliceCount * pitch * rows)];
        texture.Payload.AsSpan((int)offset, Math.Min(available, surface.Length)).CopyTo(surface);
        DeswizzleWithPitch(surface, linear, wb, hb, bpb, blockHeight, widthInGobs);
        return TextureCodec.DecodeLinear(linear, info.Width, info.Height, format, out error);
    }

    /// <summary>
    /// Sweep candidate geometries and score them. Magenta (255,0,255) is the atlas's padding fill,
    /// so a layout that pulls padding into the image shows up as magenta pixels; a wrong stride
    /// also shows up as high neighbour variation. The correct geometry scores zero magenta.
    /// </summary>
    private static void ScanLayouts(EditableTexture texture, int mip, Action<string> log)
    {
        var layout = texture.Layout;
        if (!layout.Applicable || mip >= layout.Mips.Count) return;
        var info = layout.Mips[mip];
        int bpb = texture.FormatInfo!.BytesPerBlock;
        int needed = info.WidthBlocks * bpb;
        long payloadLength = texture.Payload.LongLength - info.Offset;

        log($"#{texture.Index} 0x{texture.EffectiveHash():X8} {texture.FormatInfo.Name} " +
            $"{info.Width}x{info.Height} blocks={info.WidthBlocks}x{info.HeightBlocks} " +
            $"stored={texture.Header.ImageSize} payload={payloadLength}");
        log("  pitch  bh  rows  surface  magenta      mean|dx|   verdict");

        for (int blockHeight = 1; blockHeight <= 16; blockHeight *= 2)
        {
            for (int pitch = Mem.Align(needed, 64); pitch <= needed + 4096; pitch += 64)
            {
                int rows = Mem.Align(info.HeightBlocks, 8 * blockHeight);
                long surface = (long)pitch * rows * Math.Max(1, info.SliceCount);
                if (surface > payloadLength || surface > int.MaxValue) continue;

                var linear = new byte[info.WidthBlocks * info.HeightBlocks * bpb];
                var buffer = new byte[(int)surface];
                texture.Payload.AsSpan((int)info.Offset, (int)surface).CopyTo(buffer);
                DeswizzleWithPitch(buffer, linear, info.WidthBlocks, info.HeightBlocks, bpb,
                    blockHeight, pitch / 64);

                var image = TextureCodec.DecodeLinear(linear, info.Width, info.Height, texture.FormatInfo, out var decodeError);
                if (image is null)
                {
                    log($"  {pitch,5}  {blockHeight,2}  {rows,4}  {surface,7}   decode failed: {decodeError}");
                    continue;
                }

                long magenta = 0;
                int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
                for (int i = 0; i + 3 < image.Pixels.Length; i += 4)
                {
                    if (image.Pixels[i] > 200 && image.Pixels[i + 1] < 80 && image.Pixels[i + 2] > 200)
                    {
                        magenta++;
                        int p = i / 4;
                        int px = p % image.Width;
                        int py = p / image.Width;
                        if (px < minX) minX = px;
                        if (px > maxX) maxX = px;
                        if (py < minY) minY = py;
                        if (py > maxY) maxY = py;
                    }
                }

                double variation = MeanHorizontalDelta(image);
                string bbox = magenta == 0 ? "-" : $"[{minX},{minY}..{maxX},{maxY}]";
                string verdict = magenta == 0 ? "CLEAN" : string.Empty;
                log($"  {pitch,5}  {blockHeight,2}  {rows,4}  {surface,7}  {magenta,8}  {variation,10:F2}   {bbox} {verdict}");
            }
        }
    }

    private static string ChannelStats(RgbaImage image)
    {
        int min = 255, max = 0;
        long sum = 0;
        var pixels = image.Pixels;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int v = pixels[i];
            if (v < min) min = v;
            if (v > max) max = v;
            sum += v;
        }
        long count = pixels.Length / 4;
        return $"R min {min} max {max} mean {(count == 0 ? 0 : (double)sum / count):F1}";
    }

    private static double MeanHorizontalDelta(RgbaImage image)
    {
        long total = 0;
        long count = 0;
        for (int y = 0; y < image.Height; y += 4)
        {
            for (int x = 0; x + 4 < image.Width; x += 4)
            {
                int a = (y * image.Width + x) * 4;
                int b = a + 4;
                total += Math.Abs(image.Pixels[a] - image.Pixels[b])
                       + Math.Abs(image.Pixels[a + 1] - image.Pixels[b + 1])
                       + Math.Abs(image.Pixels[a + 2] - image.Pixels[b + 2]);
                count += 3;
            }
        }
        return count == 0 ? 0 : (double)total / count;
    }

    private static int payloadLength(EditableTexture texture) => texture.Payload.Length;

    /// <summary>2D deswizzle with an explicit GOB pitch (everything else identical to the standard).</summary>
    private static void DeswizzleWithPitch(
        byte[] source, byte[] destination, int widthBlocks, int heightBlocks,
        int bytesPerBlock, int blockHeight, int widthInGobs)
    {
        int blockSizeInBytes = GobSwizzle.GobSizeInBytes * blockHeight;
        int blockHeightInBytes = GobSwizzle.GobHeightInBytes * blockHeight;
        int rowBytes = widthBlocks * bytesPerBlock;
        for (int y = 0; y < heightBlocks; y++)
        {
            int offsetY = (y / blockHeightInBytes) * blockSizeInBytes * widthInGobs
                        + (y % blockHeightInBytes) / GobSwizzle.GobHeightInBytes * GobSwizzle.GobSizeInBytes;
            int destRow = y * rowBytes;
            for (int x = 0; x < rowBytes; x++)
            {
                int swizzled = offsetY
                             + (x / GobSwizzle.GobWidthInBytes) * blockSizeInBytes
                             + GobOffset(x % GobSwizzle.GobWidthInBytes, y % GobSwizzle.GobHeightInBytes);
                if (swizzled < source.Length)
                    destination[destRow + x] = source[swizzled];
            }
        }
    }

    private static int GobOffset(int x, int y) =>
        (x % 64) / 32 * 256 + (y % 8) / 2 * 64 + (x % 32) / 16 * 32 + (y % 2) * 16 + (x % 16);

    private static string? GetOption(string[] args, string name)
    {
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private static int? GetIntOption(string[] args, string name)
    {
        var value = GetOption(args, name);
        if (value is null) return null;
        return int.TryParse(value, out int parsed) ? parsed
            : value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
              int.TryParse(value[2..], System.Globalization.NumberStyles.HexNumber, null, out parsed)
                ? parsed
                : null;
    }

    private static uint? GetUIntOption(string[] args, string name)
    {
        var value = GetOption(args, name);
        if (value is null) return null;
        var text = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        return uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out uint parsed)
            ? parsed
            : uint.TryParse(value, out parsed) ? parsed : null;
    }
}
