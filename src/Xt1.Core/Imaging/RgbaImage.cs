namespace Xt1.Core.Imaging;

/// <summary>
/// A decoded RGBA8 image. Row-major and tightly packed; for volumes, slice-major (z outer).
/// All decoders normalize to this representation so the UI/renderers only deal with RGBA8
/// (HDR values are tonemapped or clamped by the caller).
/// </summary>
public sealed class RgbaImage
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>Number of Z slices; 1 for 2D/cube faces.</summary>
    public int Depth { get; }

    /// <summary>Whether the source format was sRGB-tagged (display hint only).</summary>
    public bool IsSrgb { get; set; }

    /// <summary>RGBA8 pixels, width*height*depth*4 bytes.</summary>
    public byte[] Pixels { get; }

    public RgbaImage(int width, int height, int depth = 1)
    {
        if (width <= 0 || height <= 0 || depth <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "image dimensions must be positive");
        Width = width;
        Height = height;
        Depth = depth;
        Pixels = new byte[checked(width * height * depth * 4)];
    }

    public RgbaImage(int width, int height, byte[] pixels, int depth = 1)
    {
        if (width <= 0 || height <= 0 || depth <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "image dimensions must be positive");
        int expected = checked(width * height * depth * 4);
        if (pixels.Length != expected)
            throw new ArgumentException($"expected {expected} bytes, got {pixels.Length}", nameof(pixels));
        Width = width;
        Height = height;
        Depth = depth;
        Pixels = pixels;
    }

    public int SliceStride => Width * Height * 4;

    public long ByteCount => (long)Width * Height * Depth * 4;

    public Span<byte> Slice(int z) => Pixels.AsSpan(z * SliceStride, SliceStride);

    public RgbaImage GetSlice(int z)
    {
        var slice = new RgbaImage(Width, Height) { IsSrgb = IsSrgb };
        Slice(z).CopyTo(slice.Pixels);
        return slice;
    }

    public void SetSlice(int z, RgbaImage slice)
    {
        if (slice.Width != Width || slice.Height != Height)
            throw new ArgumentException("slice dimensions do not match", nameof(slice));
        slice.Pixels.CopyTo(Pixels, z * SliceStride);
    }

    public RgbaImage Clone()
    {
        var copy = new RgbaImage(Width, Height, Depth) { IsSrgb = IsSrgb };
        Pixels.CopyTo(copy.Pixels, 0);
        return copy;
    }

    public void Fill(byte r, byte g, byte b, byte a)
    {
        for (int i = 0; i < Pixels.Length; i += 4)
        {
            Pixels[i] = r;
            Pixels[i + 1] = g;
            Pixels[i + 2] = b;
            Pixels[i + 3] = a;
        }
    }

    public (byte R, byte G, byte B, byte A) GetPixel(int x, int y, int z = 0)
    {
        int i = z * SliceStride + (y * Width + x) * 4;
        return (Pixels[i], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]);
    }

    public void SetPixel(int x, int y, byte r, byte g, byte b, byte a, int z = 0)
    {
        int i = z * SliceStride + (y * Width + x) * 4;
        Pixels[i] = r;
        Pixels[i + 1] = g;
        Pixels[i + 2] = b;
        Pixels[i + 3] = a;
    }

    /// <summary>Nearest-neighbour resize. Used for GPU preview downscaling only.</summary>
    public RgbaImage Resize(int newWidth, int newHeight)
    {
        if (newWidth <= 0 || newHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(newWidth));
        var result = new RgbaImage(newWidth, newHeight, Depth) { IsSrgb = IsSrgb };
        for (int z = 0; z < Depth; z++)
        {
            for (int y = 0; y < newHeight; y++)
            {
                int sy = (int)((long)y * Height / newHeight);
                for (int x = 0; x < newWidth; x++)
                {
                    int sx = (int)((long)x * Width / newWidth);
                    var (r, g, b, a) = GetPixel(sx, sy, z);
                    result.SetPixel(x, y, r, g, b, a, z);
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Whether any pixel has a non-opaque alpha. Shared by every encoder so the BC1-vs-BC1a
    /// decision is identical across backends.
    /// </summary>
    public bool HasTransparency()
    {
        for (int i = 3; i < Pixels.Length; i += 4)
            if (Pixels[i] != 255)
                return true;
        return false;
    }

    /// <summary>Extract a single channel as an 8-bit grayscale image (copy).</summary>
    public RgbaImage ExtractChannel(int channel)
    {
        var result = new RgbaImage(Width, Height, Depth) { IsSrgb = false };
        for (int i = 0; i < Pixels.Length; i += 4)
        {
            byte v = Pixels[i + channel];
            result.Pixels[i] = v;
            result.Pixels[i + 1] = v;
            result.Pixels[i + 2] = v;
            result.Pixels[i + 3] = 255;
        }
        return result;
    }

    /// <summary>Return a copy with alpha forced opaque (for viewing formats with meaningless alpha).</summary>
    public RgbaImage WithOpaqueAlpha()
    {
        var copy = Clone();
        for (int i = 3; i < copy.Pixels.Length; i += 4)
            copy.Pixels[i] = 255;
        return copy;
    }

    /// <summary>
    /// Whether every texel carries the same alpha. Volumes whose alpha is uniform (all 0 or all 255)
    /// have no density information in the alpha channel, so alpha-blended previews of them show
    /// nothing useful and should fall back to a luminance-derived opacity.
    /// </summary>
    public bool IsAlphaUniform()
    {
        if (Pixels.Length == 0) return true;
        byte first = Pixels[3];
        for (int i = 7; i < Pixels.Length; i += 4)
            if (Pixels[i] != first)
                return false;
        return true;
    }
}
