namespace Xt1.Core.Imaging;

/// <summary>
/// Alpha bleeding ("solidify" / edge pad). Fully transparent texels carry no meaningful colour, so
/// bilinear filtering and block compression pull the neighbouring colour out of them and leave a
/// dark fringe. Filling their RGB from the nearest opaque texels first removes that fringe; alpha is
/// left untouched. Equivalent to the GPU/Exporter "Edge Pad (Solidify)" pass, implemented here so it
/// also applies to the managed encoder (the bundled NVTT CLI has no such switch).
/// </summary>
public static class Solidify
{
    /// <summary>
    /// Return a copy of <paramref name="source"/> whose fully transparent RGB has been edge-padded
    /// from the nearest opaque pixels. Alpha is preserved, so the texture keeps its cutout.
    /// </summary>
    /// <param name="opaqueAbove">Alpha values greater than this count as a colour source (0 = any
    /// non-zero alpha).</param>
    public static RgbaImage Apply(RgbaImage source, int opaqueAbove = 0)
    {
        var result = source.Clone();
        for (int z = 0; z < result.Depth; z++)
            FillSlice(result, z, opaqueAbove);
        return result;
    }

    /// <summary>
    /// The import-time composition. Order matters and is fixed here: alpha-bleed first (while the
    /// cutout still exists, so the fill knows which texels are transparent), then the opaque
    /// conversion. Doing it the other way would only ever fill an already-opaque image and leave the
    /// former transparent texels black.
    /// </summary>
    public static RgbaImage Process(RgbaImage source, bool solidify, bool makeOpaque)
    {
        var result = solidify ? Apply(source) : source;
        return makeOpaque ? MakeOpaque(result) : result;
    }

    /// <summary>Set every texel's alpha to 255, turning the texture opaque.</summary>
    public static RgbaImage MakeOpaque(RgbaImage source)
    {
        var result = source.Clone();
        for (int i = 3; i < result.Pixels.Length; i += 4)
            result.Pixels[i] = 255;
        return result;
    }

    private static void FillSlice(RgbaImage image, int z, int opaqueAbove)
    {
        int w = image.Width;
        int h = image.Height;
        int count = w * h;
        int offset = z * image.SliceStride;
        var pixels = image.Pixels;

        // Multi-source BFS out of every opaque texel: each transparent texel is filled with the
        // average of the already-known neighbours that reached it first, which is the nearest
        // opaque colour (with no directional bias and no fixed iteration limit).
        var known = new bool[count];
        var queue = new Queue<int>();
        for (int p = 0; p < count; p++)
        {
            if (pixels[offset + p * 4 + 3] <= opaqueAbove) continue;
            known[p] = true;
            queue.Enqueue(p);
        }
        if (queue.Count == 0 || queue.Count == count) return; // nothing to fill, or nothing to fill from

        while (queue.Count > 0)
        {
            int p = queue.Dequeue();
            int px = p % w;
            int py = p / w;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = px + dx;
                    int ny = py + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int n = ny * w + nx;
                    if (known[n]) continue;

                    int r = 0, g = 0, b = 0, samples = 0;
                    for (int ey = -1; ey <= 1; ey++)
                    {
                        for (int ex = -1; ex <= 1; ex++)
                        {
                            if (ex == 0 && ey == 0) continue;
                            int ax = nx + ex;
                            int ay = ny + ey;
                            if (ax < 0 || ay < 0 || ax >= w || ay >= h) continue;
                            int q = ay * w + ax;
                            if (!known[q]) continue;
                            int o = offset + q * 4;
                            r += pixels[o];
                            g += pixels[o + 1];
                            b += pixels[o + 2];
                            samples++;
                        }
                    }
                    if (samples == 0) continue;

                    int target = offset + n * 4;
                    pixels[target] = (byte)(r / samples);
                    pixels[target + 1] = (byte)(g / samples);
                    pixels[target + 2] = (byte)(b / samples);
                    known[n] = true;
                    queue.Enqueue(n);
                }
            }
        }
    }
}
