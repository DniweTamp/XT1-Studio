using Xt1.Core.Imaging;

namespace Xt1.Core.Encoding;

/// <summary>Box-filter mip chain generation (sRGB-aware downsampling is done in linear space).</summary>
public static class MipChain
{
    public static int FullMipCount(int width, int height)
    {
        int max = Math.Max(1, Math.Max(width, height));
        int count = 1;
        while (max > 1)
        {
            max >>= 1;
            count++;
        }
        return count;
    }

    public static int MipDimension(int baseSize, int mip) => Math.Max(1, baseSize >> mip);

    /// <summary>Build down to <paramref name="mipCount"/> levels; index 0 is a clone of the base.</summary>
    public static List<RgbaImage> Build(RgbaImage baseImage, int mipCount, bool srgb)
    {
        mipCount = Math.Max(1, mipCount);
        var levels = new List<RgbaImage>(mipCount) { baseImage.Clone() };
        for (int m = 1; m < mipCount; m++)
        {
            int w = MipDimension(baseImage.Width, m);
            int h = MipDimension(baseImage.Height, m);
            levels.Add(Downsample(levels[m - 1], w, h, srgb));
        }
        return levels;
    }

    public static RgbaImage Downsample(RgbaImage source, int newWidth, int newHeight, bool srgb)
    {
        var result = new RgbaImage(newWidth, newHeight, source.Depth) { IsSrgb = source.IsSrgb };

        for (int z = 0; z < source.Depth; z++)
        {
            for (int y = 0; y < newHeight; y++)
            {
                long y0 = (long)y * source.Height / newHeight;
                long y1 = Math.Max(y0 + 1, (long)(y + 1) * source.Height / newHeight);
                for (int x = 0; x < newWidth; x++)
                {
                    long x0 = (long)x * source.Width / newWidth;
                    long x1 = Math.Max(x0 + 1, (long)(x + 1) * source.Width / newWidth);

                    long r = 0, g = 0, b = 0, a = 0, n = 0;
                    for (long sy = y0; sy < y1; sy++)
                    {
                        for (long sx = x0; sx < x1; sx++)
                        {
                            var (pr, pg, pb, pa) = source.GetPixel((int)sx, (int)sy, z);
                            if (srgb)
                            {
                                r += Srgb.ToLinear(pr);
                                g += Srgb.ToLinear(pg);
                                b += Srgb.ToLinear(pb);
                            }
                            else
                            {
                                r += pr;
                                g += pg;
                                b += pb;
                            }
                            a += pa;
                            n++;
                        }
                    }

                    if (n == 0) n = 1;
                    if (srgb)
                    {
                        result.SetPixel(x, y,
                            Srgb.FromLinear((int)(r / n)), Srgb.FromLinear((int)(g / n)), Srgb.FromLinear((int)(b / n)),
                            (byte)(a / n), z);
                    }
                    else
                    {
                        result.SetPixel(x, y, (byte)(r / n), (byte)(g / n), (byte)(b / n), (byte)(a / n), z);
                    }
                }
            }
        }
        return result;
    }
}
