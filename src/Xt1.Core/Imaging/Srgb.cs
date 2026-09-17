namespace Xt1.Core.Imaging;

/// <summary>
/// Single source of truth for the sRGB transfer curve, used by mip generation (linear-space
/// filtering) and by the display gamma LUT so the two can never drift apart.
/// </summary>
public static class Srgb
{
    private const int LinearScale = 65535;

    private static readonly int[] ToLinearTable = BuildToLinear();
    private static readonly byte[] FromLinearTable = BuildFromLinear();
    private static byte[]? _displayLut;

    /// <summary>sRGB byte to linear at 16-bit precision.</summary>
    public static int ToLinear(byte srgb) => ToLinearTable[srgb];

    /// <summary>Linear (16-bit) back to an sRGB byte.</summary>
    public static byte FromLinear(int linear) => FromLinearTable[Math.Clamp(linear, 0, LinearScale)];

    /// <summary>Lookup table that maps an sRGB byte to the sRGB byte of its linear value.</summary>
    public static byte[] DisplayLut
    {
        get
        {
            if (_displayLut is not null) return _displayLut;
            var lut = new byte[256];
            for (int i = 0; i < 256; i++)
                lut[i] = FromLinear(ToLinear((byte)i));
            _displayLut = lut;
            return lut;
        }
    }

    private static int[] BuildToLinear()
    {
        var table = new int[256];
        for (int i = 0; i < 256; i++)
        {
            double c = i / 255.0;
            double linear = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            table[i] = (int)Math.Round(linear * LinearScale);
        }
        return table;
    }

    private static byte[] BuildFromLinear()
    {
        var table = new byte[LinearScale + 1];
        for (int i = 0; i < table.Length; i++)
        {
            double c = (double)i / LinearScale;
            double srgb = c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055;
            table[i] = (byte)Math.Clamp((int)Math.Round(srgb * 255.0), 0, 255);
        }
        return table;
    }
}
