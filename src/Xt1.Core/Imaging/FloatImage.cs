namespace Xt1.Core.Imaging;

/// <summary>
/// A linear float RGBA image, used to keep an HDR texture's full range (values can exceed 1) so the
/// GL preview can apply exposure before tone mapping instead of tonemapping it away at decode time.
/// Named FloatImage rather than HdrImage to avoid colliding with BCnEncoder's own HdrImage type.
/// </summary>
public sealed class FloatImage
{
    public FloatImage(int width, int height)
    {
        Width = width;
        Height = height;
        Pixels = new float[width * height * 4];
    }

    public int Width { get; }
    public int Height { get; }
    public float[] Pixels { get; }

    public void SetPixel(int x, int y, float r, float g, float b, float a = 1f)
    {
        int i = (y * Width + x) * 4;
        Pixels[i + 0] = r;
        Pixels[i + 1] = g;
        Pixels[i + 2] = b;
        Pixels[i + 3] = a;
    }

    public (float R, float G, float B, float A) GetPixel(int x, int y)
    {
        int i = (y * Width + x) * 4;
        return (Pixels[i + 0], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]);
    }
}
