namespace Xt1.Core.Xt1;

/// <summary>NVN texture type stored at XT1 0x18 (XT1_FORMAT.md section 7).</summary>
public enum TextureType : uint
{
    T1D = 0,
    T2D = 1,
    T3D = 2,
    TCube = 3,
    T1DArray = 4,
    T2DArray = 5,
    T2DMultisample = 6,
    T2DMultisampleArray = 7,
    TCubeArray = 8,
}

public static class TextureTypeExtensions
{
    public static bool IsCube(this TextureType type) => type is TextureType.TCube or TextureType.TCubeArray;

    public static bool Is3D(this TextureType type) => type == TextureType.T3D;

    public static int FaceCount(this TextureType type) => type.IsCube() ? 6 : 1;

    public static string Describe(this TextureType type) => type switch
    {
        TextureType.T1D => "1D",
        TextureType.T2D => "2D",
        TextureType.T3D => "3D",
        TextureType.TCube => "Cube",
        TextureType.T1DArray => "1D Array",
        TextureType.T2DArray => "2D Array",
        TextureType.T2DMultisample => "2D Multisample",
        TextureType.T2DMultisampleArray => "2D Multisample Array",
        TextureType.TCubeArray => "Cube Array",
        _ => "Unknown",
    };
}
