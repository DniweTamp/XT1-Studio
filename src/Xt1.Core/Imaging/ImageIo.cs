using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Xt1.Core.Imaging;

/// <summary>PNG / BMP / JPG via ImageSharp, TGA via <see cref="TgaIo"/>.</summary>
public static class ImageIo
{
    public static RgbaImage Load(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".tga")
            return TgaIo.Read(File.ReadAllBytes(path));

        using var image = Image.Load<Rgba32>(path);
        return FromImageSharp(image);
    }

    public static void Save(RgbaImage image, string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".tga")
        {
            File.WriteAllBytes(path, TgaIo.Write(image));
            return;
        }

        using var sharp = ToImageSharp(image);
        switch (extension)
        {
            case ".png":
                sharp.SaveAsPng(path);
                break;
            case ".bmp":
                sharp.SaveAsBmp(path);
                break;
            case ".jpg":
            case ".jpeg":
                sharp.SaveAsJpeg(path);
                break;
            default:
                throw new NotSupportedException($"unsupported image extension '{extension}'");
        }
    }

    private static RgbaImage FromImageSharp(Image<Rgba32> image)
    {
        var result = new RgbaImage(image.Width, image.Height);
        image.CopyPixelDataTo(result.Pixels);
        return result;
    }

    private static Image<Rgba32> ToImageSharp(RgbaImage image)
    {
        return Image.LoadPixelData<Rgba32>(image.Pixels, image.Width, image.Height);
    }
}
