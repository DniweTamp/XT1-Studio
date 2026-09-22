using System.Runtime.InteropServices;
using Xt1.Core.Imaging;

namespace Xt1.Shell;

/// <summary>
/// Turns a decoded <see cref="RgbaImage"/> into the HBITMAP the shell expects from a thumbnail
/// handler: a top-down 32bpp DIB whose alpha is premultiplied. Built with GDI directly (no
/// System.Drawing), because this code runs inside the host process, which may be Explorer.
/// </summary>
public static class DibBitmap
{
    private const uint DibRgbColors = 0;
    private const uint BiRgb = 0;

    /// <summary>
    /// Create the bitmap. The caller owns the handle and must <see cref="Delete"/> it unless it hands
    /// it to somebody who takes ownership (the shell does).
    /// </summary>
    public static IntPtr Create(RgbaImage image, out bool hasAlpha)
    {
        hasAlpha = image.HasTransparency();

        var header = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            Width = image.Width,
            Height = -image.Height, // negative: top-down, so our row order is the DIB's row order
            Planes = 1,
            BitCount = 32,
            Compression = BiRgb,
        };
        var info = new BitmapInfo { Header = header };

        IntPtr bitmap = CreateDIBSection(IntPtr.Zero, ref info, DibRgbColors, out IntPtr bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            throw new InvalidOperationException("CreateDIBSection failed");
        }

        try
        {
            // RgbaImage is tightly packed RGBA; a 32bpp DIB is tightly packed BGRA, so only the
            // channel order and the premultiplication change.
            var pixels = image.Pixels;
            var bgra = new byte[pixels.Length];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte alpha = pixels[i + 3];
                bgra[i + 0] = Premultiply(pixels[i + 2], alpha);
                bgra[i + 1] = Premultiply(pixels[i + 1], alpha);
                bgra[i + 2] = Premultiply(pixels[i + 0], alpha);
                bgra[i + 3] = alpha;
            }

            Marshal.Copy(bgra, 0, bits, bgra.Length);
            return bitmap;
        }
        catch
        {
            DeleteObject(bitmap);
            throw;
        }
    }

    /// <summary>Read back the dimensions of a bitmap handle created by <see cref="Create"/>.</summary>
    public static bool Describe(IntPtr bitmap, out int width, out int height, out int bitsPerPixel)
    {
        width = height = bitsPerPixel = 0;
        if (bitmap == IntPtr.Zero) return false;
        if (GetObjectW(bitmap, Marshal.SizeOf<Win32Bitmap>(), out var info) == 0) return false;
        width = info.Width;
        height = info.Height;
        bitsPerPixel = info.BitsPixel;
        return true;
    }

    public static void Delete(IntPtr bitmap)
    {
        if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
    }

    private static byte Premultiply(byte channel, byte alpha) =>
        alpha == 255 ? channel : (byte)((channel * alpha + 127) / 255);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Bitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public IntPtr Bits;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo pbmi, uint usage,
        out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetObjectW(IntPtr handle, int size, out Win32Bitmap info);
}
