namespace Xt1.Core.Imaging;

/// <summary>Minimal TGA reader/writer (truecolor 24/32-bit, uncompressed and RLE).</summary>
public static class TgaIo
{
    /// <summary>Same order as the DDS reader's limits, so both import paths reject the same files.</summary>
    private const int MaxDimension = 8192;
    private const long MaxPixelBytes = 256L * 1024 * 1024;


    public static RgbaImage Read(byte[] data)
    {
        if (data.Length < 18) throw new InvalidDataException("TGA too small");
        int idLength = data[0];
        int colorMapType = data[1];
        int imageType = data[2];
        int width = data[12] | (data[13] << 8);
        int height = data[14] | (data[15] << 8);
        int bpp = data[16];
        int descriptor = data[17];

        if (colorMapType != 0) throw new NotSupportedException("color-mapped TGA is not supported");
        if (imageType != 2 && imageType != 3 && imageType != 10 && imageType != 11)
            throw new NotSupportedException($"TGA image type {imageType} is not supported");
        if (bpp != 24 && bpp != 32 && bpp != 8)
            throw new NotSupportedException($"TGA {bpp}bpp is not supported");

        bool rle = imageType is 10 or 11;
        bool grayscale = imageType is 3 or 11;
        bool topLeft = (descriptor & 0x20) != 0;
        int bytesPerPixel = bpp / 8;

        // The dimensions are 16-bit and untrusted: reject what they claim instead of allocating it,
        // and size the buffer in long arithmetic so a large product cannot wrap.
        if (width < 1 || height < 1 || width > MaxDimension || height > MaxDimension)
            throw new InvalidDataException($"TGA dimensions {width}x{height} are out of range");
        long pixelBytes = (long)width * height * bytesPerPixel;
        if (pixelBytes > MaxPixelBytes)
            throw new InvalidDataException($"TGA of {width}x{height} is too large to decode");

        var image = new RgbaImage(width, height);
        int offset = 18 + idLength;
        var pixels = new byte[pixelBytes];
        int written = 0;

        if (!rle)
        {
            int copy = Math.Min(pixels.Length, data.Length - offset);
            Array.Copy(data, offset, pixels, 0, copy);
        }
        else
        {
            while (written < pixels.Length && offset < data.Length)
            {
                int header = data[offset++];
                int count = (header & 0x7F) + 1;
                if ((header & 0x80) != 0)
                {
                    if (offset + bytesPerPixel > data.Length) break;
                    for (int i = 0; i < count && written < pixels.Length; i++)
                    {
                        Array.Copy(data, offset, pixels, written, bytesPerPixel);
                        written += bytesPerPixel;
                    }
                    offset += bytesPerPixel;
                }
                else
                {
                    int run = count * bytesPerPixel;
                    int copy = Math.Min(run, Math.Min(pixels.Length - written, data.Length - offset));
                    Array.Copy(data, offset, pixels, written, copy);
                    written += copy;
                    offset += count * bytesPerPixel;
                }
            }
        }

        for (int y = 0; y < height; y++)
        {
            int srcY = topLeft ? y : height - 1 - y;
            for (int x = 0; x < width; x++)
            {
                int src = (srcY * width + x) * bytesPerPixel;
                if (src + bytesPerPixel > pixels.Length) continue;
                if (grayscale)
                {
                    byte v = pixels[src];
                    image.SetPixel(x, y, v, v, v, 255);
                }
                else
                {
                    image.SetPixel(x, y, pixels[src + 2], pixels[src + 1], pixels[src], bpp == 32 ? pixels[src + 3] : (byte)255);
                }
            }
        }
        return image;
    }

    public static byte[] Write(RgbaImage image)
    {
        var output = new byte[18 + image.Width * image.Height * 4];
        output[2] = 2; // uncompressed truecolor
        output[12] = (byte)(image.Width & 0xFF);
        output[13] = (byte)(image.Width >> 8);
        output[14] = (byte)(image.Height & 0xFF);
        output[15] = (byte)(image.Height >> 8);
        output[16] = 32;
        output[17] = 0x28; // 8 alpha bits | top-left origin

        int at = 18;
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                var (r, g, b, a) = image.GetPixel(x, y);
                output[at++] = b;
                output[at++] = g;
                output[at++] = r;
                output[at++] = a;
            }
        return output;
    }
}
