namespace Xt1.Core.Imaging;

/// <summary>
/// Scene-referred auto exposure for the cubemap preview. It derives a single exposure factor from the
/// cubemap's own luminance statistics (the geometric mean, mapped onto a middle-grey key), so the
/// factor is a property of the source data alone. The user's exposure and gamma are applied after
/// this factor, which keeps them independent: raising the exposure control never changes what auto
/// exposure does, because auto exposure never sees it.
/// </summary>
public static class AutoExposure
{
    /// <summary>Target average luminance once the factor has been applied.</summary>
    public const float MiddleGrey = 0.18f;

    /// <summary>Floor on a sample's luminance, so black texels do not drive the mean to zero.</summary>
    public const double MinLuminance = 1e-4;

    /// <summary>Keep the factor from swinging to extremes on near-black or very bright cubemaps.</summary>
    public const float MinScale = 1f / 32f;
    public const float MaxScale = 32f;

    /// <summary>
    /// Exposure factor for samples already reduced to the natural log of their luminance. Pass the
    /// accumulated <paramref name="logSum"/> and <paramref name="count"/> of the same space the shader
    /// samples; an empty set is left unchanged.
    /// </summary>
    public static float Scale(double logSum, long count)
    {
        if (count <= 0) return 1f;

        double logAverage = logSum / count;
        double scale = MiddleGrey / Math.Exp(logAverage);
        if (double.IsNaN(scale) || double.IsInfinity(scale)) return 1f;
        return (float)Math.Clamp(scale, MinScale, MaxScale);
    }
}
