using System.Drawing.Imaging;
using Xt1.App.Rendering;
using Xt1.Core.Codecs;
using Xt1.Core.Containers;
using Xt1.Core.Imaging;
using Xt1.Core.Xt1;
using Xt1.Core.Settings;

namespace Xt1.App;

/// <summary>
/// <c>Xt1.App.exe --gltest &lt;file&gt; [options]</c>: assemble a 3D texture in the real OpenGL
/// viewport, render it offscreen and check the result numerically, so the 3D view can be verified
/// without a human at the keyboard. It also writes the frames as PNGs for visual inspection.
///
/// Checks: the assembled volume draws pixels; those pixels sit where the orbit camera should put
/// them; orbiting changes the image; per-channel isolation changes the image; and the bounds box
/// draws. Options: <c>--index</c>, <c>--hash</c>, <c>--bank</c>, <c>--mip</c>, <c>--out &lt;dir&gt;</c>,
/// <c>--mode rgb|alpha|mip</c>, <c>--channel rgba|r|g|b|a</c>, <c>--yaw</c>, <c>--pitch</c>,
/// <c>--distance</c>, <c>--size</c>, <c>--density</c>.
/// </summary>
internal static class GlTest
{
    private const byte BgR = 23, BgG = 23, BgB = 28; // GL clear color 0.09/0.09/0.11

    internal static int Run(string? file, string[] args, Action<string> log)
    {
        if (file is null || !File.Exists(file))
        {
            log("usage: --gltest <file> [--index N] [--hash 0xH] [--bank B] [--mip M] [--out dir]");
            log("                    [--mode rgb|alpha|mip] [--channel rgba|r|g|b|a] [--size N]");
            return 2;
        }

        int? index = CliOptions.GetInt(args, "--index");
        uint? hash = CliOptions.GetUInt(args, "--hash");
        int? bankIndex = CliOptions.GetInt(args, "--bank");
        int mip = CliOptions.GetInt(args, "--mip") ?? 0;
        int size = CliOptions.GetInt(args, "--size") ?? 320;
        string outputDirectory = CliOptions.Get(args, "--out") ?? Path.Combine(Path.GetTempPath(), "kilo", "gl");
        var mode = ParseMode(CliOptions.Get(args, "--mode"));
        var channel = ParseChannel(CliOptions.Get(args, "--channel"));
        float? yaw = CliOptions.GetFloat(args, "--yaw");
        float? pitch = CliOptions.GetFloat(args, "--pitch");
        float? distance = CliOptions.GetFloat(args, "--distance");
        float? density = CliOptions.GetFloat(args, "--density");

        var settings = AppSettings.Load();
        var document = BankDocument.Load(file, settings.Resolve().Oodle);

        var candidates = new List<(int Bank, EditableTexture Texture)>();
        foreach (var section in document.Sections)
        {
            if (bankIndex is not null && document.Sections.IndexOf(section) != bankIndex) continue;
            foreach (var texture in section.Bank.Textures)
            {
                if (index is not null && texture.Index != index) continue;
                if (hash is not null && texture.EffectiveHash() != hash) continue;
                if (texture.Header.Type.Is3D() || texture.Header.Type.IsCube())
                    candidates.Add((document.Sections.IndexOf(section), texture));
            }
        }

        if (candidates.Count == 0)
        {
            log("[error] no 3D or cube texture matched (use --index/--hash to pick one)");
            return 2;
        }

        int failures = 0;
        foreach (var (bank, texture) in candidates)
        {
            if (!RunOne(texture, bank, mip, size, mode, channel, yaw, pitch, distance, density,
                    outputDirectory, log))
                failures++;
        }

        log(failures == 0 ? "GL-TEST PASSED" : $"GL-TEST FAILED ({failures} texture(s))");
        return failures == 0 ? 0 : 1;
    }

    private static bool RunOne(
        EditableTexture texture, int bank, int mip, int size, VolumeRenderMode mode, ImageChannelMode channel,
        float? yaw, float? pitch, float? distance, float? density, string outputDirectory, Action<string> log)
    {
        var layout = texture.Layout;
        if (!layout.Applicable || mip >= layout.Mips.Count)
        {
            log($"  x #{texture.Index}: no layout for mip {mip}");
            return false;
        }

        if (texture.Header.Type.IsCube()) return RunCube(texture, mip, size, yaw, pitch, outputDirectory, log);

        var slices = TextureCodec.DecodeVolumeSlices(texture, mip, 0, out var error);
        if (slices.Count == 0)
        {
            log($"  x #{texture.Index}: {error ?? "volume decode failed"}");
            return false;
        }

        var volume = new RgbaImage(slices[0].Width, slices[0].Height, slices.Count) { IsSrgb = slices[0].IsSrgb };
        for (int z = 0; z < slices.Count; z++) volume.SetSlice(z, slices[z]);

        var channels = ChannelMeans(volume);
        bool gray = Math.Abs(channels.R - channels.G) < 2 && Math.Abs(channels.G - channels.B) < 2;
        log($"#{texture.Index} 0x{texture.EffectiveHash():X8} {texture.FormatInfo?.Name} " +
            $"{volume.Width}x{volume.Height}x{volume.Depth} mip {mip}  alphaUniform={volume.IsAlphaUniform()}");
        log($"  channel means: R {channels.R:F1}  G {channels.G:F1}  B {channels.B:F1}  A {channels.A:F1}" +
            (gray ? "  (gray: R~G~B)" : string.Empty));

        using var form = new Form
        {
            ClientSize = new Size(64, 64),
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
        };
        using var control = new GlPreviewControl { Dock = DockStyle.Fill };
        form.Controls.Add(control);
        form.Show();
        Application.DoEvents();

        if (!control.Available)
        {
            log($"  x GL viewport unavailable: {control.LastError}");
            return false;
        }

        control.VolumeResolutionCap = 4096;
        if (density is not null) control.Density = density.Value;
        control.Channel = channel;
        control.RenderMode = mode;
        if (yaw is not null) control.Camera.Yaw = yaw.Value;
        if (pitch is not null) control.Camera.Pitch = pitch.Value;
        if (distance is not null) control.Camera.Distance = distance.Value;
        control.SetVolume(volume);
        Application.DoEvents();

        Directory.CreateDirectory(outputDirectory);
        string stem = $"#{texture.Index}_{texture.EffectiveHash():X8}_m{mip}";

        bool ok = true;
        double minimum = 0.005 * size * size;

        // 1. The assembled volume (no planes, no bounds) must draw something. Each opacity source is
        // rendered so the report says which one is visible for this volume, because a volume whose RGB
        // is black is invisible in the luminance-derived RGB mode but fine in Alpha (and vice versa).
        control.ShowSlices = false;
        control.ShowBounds = false;
        var perMode = new Dictionary<VolumeRenderMode, Bitmap>();
        foreach (var candidate in new[] { VolumeRenderMode.Rgb, VolumeRenderMode.Alpha, VolumeRenderMode.Mip })
        {
            control.RenderMode = candidate;
            var bitmap = Render(control, size);
            if (bitmap is null)
            {
                log("  x offscreen render failed");
                return false;
            }
            Save(bitmap, Path.Combine(outputDirectory, $"{stem}_{candidate.ToString().ToLowerInvariant()}.png"));
            perMode[candidate] = bitmap;
            var stats = Analyze(bitmap);
            log($"  [{(stats.Drawn > minimum ? "ok" : "--")}] {candidate} assembled: {stats.Drawn} px " +
                $"({stats.Drawn / (double)(size * size):P2}), mean luminance {stats.MeanLuminance:F1}");
        }
        control.RenderMode = mode;

        var volumeOnly = perMode[mode];
        var drawn = Analyze(volumeOnly);
        var best = perMode.OrderByDescending(pair => Analyze(pair.Value).Drawn).First();
        ok &= Check(log, "assembled volume draws pixels in some opacity source",
            Analyze(best.Value).Drawn > minimum,
            $"{best.Key} draws the most ({Analyze(best.Value).Drawn} px)");
        ok &= Check(log, $"object is inside the frame and centered ({mode})", drawn.Centered, drawn.Bounds);

        // 2. Orbiting must change the image (the camera moves around the object, not inside it).
        float baseYaw = control.Camera.Yaw;
        control.Camera.Yaw = baseYaw + 0.8f;
        var orbited = Render(control, size);
        control.Camera.Yaw = baseYaw;
        if (orbited is not null)
        {
            Save(orbited, Path.Combine(outputDirectory, stem + "_orbited.png"));
            double difference = MeanAbsDiff(volumeOnly, orbited);
            ok &= Check(log, "orbiting changes the image", difference > 2.0, $"mean |diff| {difference:F2}");
        }

        // 3. Channel isolation must reach the shader. A gray volume (R~G~B) cannot show a difference,
        // so that case is reported as not applicable rather than failed.
        if (channel == ImageChannelMode.Rgba)
        {
            if (gray)
            {
                log("  [--] channel isolation: n/a (R~G~B)");
            }
            else
            {
                control.Channel = ImageChannelMode.Red;
                var red = Render(control, size);
                control.Channel = ImageChannelMode.Rgba;
                if (red is not null)
                {
                    Save(red, Path.Combine(outputDirectory, stem + "_red.png"));
                    double difference = MeanAbsDiff(volumeOnly, red);
                    ok &= Check(log, "channel isolation changes the 3D image", difference > 0.5,
                        $"mean |diff| {difference:F2}");
                }
            }
        }

        // 4. The opacity source must reach the shader (RGB luminance vs stored alpha vs MIP).
        int distinctModes = perMode.Values.Select(Analyze).Select(s => s.Drawn).Distinct().Count();
        ok &= Check(log, "the render modes do not all produce the same image", distinctModes > 1,
            $"{distinctModes} distinct coverage(s)");

        // 5. Bounds and slice planes must draw on top of the volume. The planes are constrained to the
        // volume's box, so the region they light up must stay inside the box's projected outline:
        // that catches a plane that is placed or oriented differently from the volume.
        control.ShowBounds = true;
        control.ShowSlices = false;
        var boundsOnly = Render(control, size);
        Save(boundsOnly, Path.Combine(outputDirectory, stem + "_bounds.png"));
        if (boundsOnly is not null)
        {
            var bounds = Analyze(boundsOnly);
            ok &= Check(log, "bounds box draws", bounds.Drawn > drawn.Drawn,
                $"{bounds.Drawn} px vs {drawn.Drawn} px without bounds");

            control.ShowSlices = true;
            var boundsAndSlices = Render(control, size);
            Save(boundsAndSlices, Path.Combine(outputDirectory, stem + "_bounds_slices.png"));
            if (boundsAndSlices is not null)
            {
                var withPlanes = Analyze(boundsAndSlices);
                bool contained = Contained(withPlanes, bounds, 4);
                ok &= Check(log, "slice planes stay inside the volume box", contained,
                    $"planes [{withPlanes.Bounds}] vs box [{bounds.Bounds}]");
            }
        }

        control.ShowBounds = false;
        control.ShowSlices = true;
        var withSlices = Render(control, size);
        Save(withSlices, Path.Combine(outputDirectory, stem + "_slices.png"));
        if (withSlices is not null)
        {
            double difference = MeanAbsDiff(volumeOnly, withSlices);
            ok &= Check(log, "slice planes draw on top of the volume", difference > 0.5,
                $"mean |diff| {difference:F2}");
        }

        log(ok
            ? $"  ok #{texture.Index} ({volume.Width}x{volume.Height}x{volume.Depth}) -> {outputDirectory}"
            : $"  FAILED #{texture.Index}");
        return ok;
    }

    private static Bitmap? Render(GlPreviewControl control, int size) => control.RenderToBitmap(size, size);

    /// <summary>
    /// The cubemap path: an inside-out skybox. There is no object to orbit, so only the basics are
    /// checked - that the six faces load, that turning the camera changes what is drawn, and that the
    /// wheel zoom (which narrows the field of view here, since the camera cannot move) does too.
    /// </summary>
    private static bool RunCube(
        EditableTexture texture, int mip, int size, float? yaw, float? pitch, string outputDirectory,
        Action<string> log)
    {
        var faces = TextureCodec.DecodeCubeFaces(texture, mip, out var error);
        if (faces.Count != 6)
        {
            log($"  x #{texture.Index}: {error ?? "cube decode failed"}");
            return false;
        }

        log($"#{texture.Index} 0x{texture.EffectiveHash():X8} {texture.FormatInfo?.Name} " +
            $"cube {faces[0].Width}x{faces[0].Height} mip {mip} (6 faces)");

        using var form = new Form
        {
            ClientSize = new Size(64, 64),
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
        };
        using var control = new GlPreviewControl { Dock = DockStyle.Fill };
        form.Controls.Add(control);
        form.Show();
        Application.DoEvents();

        if (!control.Available)
        {
            log($"  x GL viewport unavailable: {control.LastError}");
            return false;
        }

        if (yaw is not null) control.Camera.Yaw = yaw.Value;
        if (pitch is not null) control.Camera.Pitch = pitch.Value;
        control.SetCubeFaces(faces);
        Application.DoEvents();

        Directory.CreateDirectory(outputDirectory);
        string stem = $"#{texture.Index}_{texture.EffectiveHash():X8}_m{mip}_cube";

        bool ok = true;
        var first = Render(control, size);
        if (first is null)
        {
            log("  x offscreen render failed");
            return false;
        }
        Save(first, Path.Combine(outputDirectory, stem + ".png"));
        var drawn = Analyze(first);
        ok &= Check(log, "skybox draws pixels", drawn.Drawn > 0.5 * size * size,
            $"{drawn.Drawn} px ({drawn.Drawn / (double)(size * size):P2})");

        float baseYaw = control.Camera.Yaw;
        control.Camera.Yaw = baseYaw + 0.8f;
        var turned = Render(control, size);
        control.Camera.Yaw = baseYaw;
        if (turned is not null)
        {
            Save(turned, Path.Combine(outputDirectory, stem + "_turned.png"));
            double difference = MeanAbsDiff(first, turned);
            if (difference > 2.0)
            {
                ok &= Check(log, "turning the skybox changes the image", true, $"mean |diff| {difference:F2}");
            }
            else if (drawn.LuminanceSpread <= 8)
            {
                // The skybox is a single flat color (possibly black), so no rotation can change a
                // single pixel. Judged from the render, because a near-black face is functionally flat
                // however much its low byte values wiggle.
                log($"  [--] turning: n/a (flat skybox, luminance spread {drawn.LuminanceSpread})");
            }
            else
            {
                ok &= Check(log, "turning the skybox changes the image", false, $"mean |diff| {difference:F2}");
            }
        }

        float baseFov = control.Camera.Fov;
        control.Camera.ZoomFov(3f);
        var zoomed = Render(control, size);
        control.Camera.Fov = baseFov;
        if (zoomed is not null)
        {
            Save(zoomed, Path.Combine(outputDirectory, stem + "_zoomed.png"));
            double difference = MeanAbsDiff(first, zoomed);
            if (difference > 1.0)
            {
                ok &= Check(log, "wheel zoom changes the skybox", true, $"mean |diff| {difference:F2}");
            }
            else if (drawn.LuminanceSpread <= 8)
            {
                log($"  [--] wheel zoom: n/a (flat skybox, luminance spread {drawn.LuminanceSpread})");
            }
            else
            {
                ok &= Check(log, "wheel zoom changes the skybox", false, $"mean |diff| {difference:F2}");
            }
        }

        log(ok ? $"  ok #{texture.Index} cube -> {outputDirectory}" : $"  FAILED #{texture.Index}");
        return ok;
    }

    private static void Save(Bitmap? bitmap, string path)
    {
        if (bitmap is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        bitmap.Save(path, ImageFormat.Png);
    }

    private static bool Check(Action<string> log, string what, bool passed, string detail)
    {
        log($"  [{(passed ? "ok" : "XX")}] {what}: {detail}");
        return passed;
    }

    private readonly record struct Stats(
        int Drawn, double MeanLuminance, int LuminanceSpread, string Bounds, bool Centered,
        int MinX, int MinY, int MaxX, int MaxY);

    /// <summary>Whether the region <paramref name="inner"/> lights up is inside <paramref name="outer"/>.</summary>
    private static bool Contained(Stats inner, Stats outer, int tolerance) =>
        inner.MaxX < 0 ||
        (inner.MinX >= outer.MinX - tolerance && inner.MaxX <= outer.MaxX + tolerance &&
         inner.MinY >= outer.MinY - tolerance && inner.MaxY <= outer.MaxY + tolerance);

    private readonly record struct ChannelStats(double R, double G, double B, double A);

    private static ChannelStats ChannelMeans(RgbaImage image)
    {
        var pixels = image.Pixels;
        long r = 0, g = 0, b = 0, a = 0;
        long count = pixels.Length / 4;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            r += pixels[i];
            g += pixels[i + 1];
            b += pixels[i + 2];
            a += pixels[i + 3];
        }
        if (count == 0) return default;
        return new ChannelStats((double)r / count, (double)g / count, (double)b / count, (double)a / count);
    }

    private static Stats Analyze(Bitmap bitmap)
    {
        var pixels = ReadPixels(bitmap, out int width, out int height);
        int drawn = 0;
        long luminance = 0;
        int minLuminance = 255, maxLuminance = 0;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                int sample = (r * 77 + g * 150 + b * 29) >> 8;
                luminance += sample;
                if (sample < minLuminance) minLuminance = sample;
                if (sample > maxLuminance) maxLuminance = sample;
                if (Math.Abs(r - BgR) > 6 || Math.Abs(g - BgG) > 6 || Math.Abs(b - BgB) > 6)
                {
                    drawn++;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }

        bool centered = false;
        string bounds = "none";
        if (maxX >= 0)
        {
            bounds = $"[{minX},{minY}..{maxX},{maxY}] of {width}x{height}";
            double cx = (minX + maxX) / 2.0 / width;
            double cy = (minY + maxY) / 2.0 / height;
            centered = cx > 0.3 && cx < 0.7 && cy > 0.3 && cy < 0.7;
        }

        return new Stats(drawn, (double)luminance / (width * height), maxLuminance - minLuminance,
            bounds, centered, minX, minY, maxX, maxY);
    }

    private static double MeanAbsDiff(Bitmap a, Bitmap b)
    {
        var pa = ReadPixels(a, out int wa, out int ha);
        var pb = ReadPixels(b, out int wb, out int hb);
        if (wa != wb || ha != hb) return double.MaxValue;
        long total = 0;
        for (int i = 0; i + 3 < pa.Length; i += 4)
        {
            total += Math.Abs(pa[i] - pb[i]) + Math.Abs(pa[i + 1] - pb[i + 1]) + Math.Abs(pa[i + 2] - pb[i + 2]);
        }
        return total / (double)(wa * ha * 3);
    }

    /// <summary>Copy a bitmap into a top-down BGRA byte array.</summary>
    private static byte[] ReadPixels(Bitmap bitmap, out int width, out int height)
    {
        width = bitmap.Width;
        height = bitmap.Height;
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var buffer = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
                System.Runtime.InteropServices.Marshal.Copy(
                    IntPtr.Add(data.Scan0, y * data.Stride), buffer, y * width * 4, width * 4);
            return buffer;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static VolumeRenderMode ParseMode(string? value) => value?.ToLowerInvariant() switch
    {
        "alpha" => VolumeRenderMode.Alpha,
        "mip" => VolumeRenderMode.Mip,
        _ => VolumeRenderMode.Rgb,
    };

    private static ImageChannelMode ParseChannel(string? value) => value?.ToLowerInvariant() switch
    {
        "r" => ImageChannelMode.Red,
        "g" => ImageChannelMode.Green,
        "b" => ImageChannelMode.Blue,
        "a" => ImageChannelMode.Alpha,
        _ => ImageChannelMode.Rgba,
    };

}
