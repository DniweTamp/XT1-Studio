using Xt1.Core.Imaging;
using Xunit;

namespace Xt1.Core.Tests;

/// <summary>
/// Auto exposure is scene-referred: it normalises the cubemap to a middle-grey key from the source
/// luminance alone. These lock the statistic (geometric mean), its clamps, and its monotonicity.
/// The factor's independence from the display controls is structural: <see cref="AutoExposure.Scale"/>
/// only ever receives scene samples, so a user exposure/gamma value cannot reach it.
/// </summary>
public class AutoExposureTests
{
    /// <summary>Accumulate the log-luminance of a constant scene, the way the renderer samples.</summary>
    private static float ScaleForConstant(double luminance) =>
        AutoExposure.Scale(Math.Log(luminance + AutoExposure.MinLuminance), 1);

    private static void AssertClose(float expected, float actual) =>
        Assert.True(Math.Abs(expected - actual) < 1e-3f, $"expected {expected}, got {actual}");

    [Fact]
    public void MiddleGreySceneIsLeftAlone()
    {
        // A scene already at the key needs no correction.
        AssertClose(1f, ScaleForConstant(AutoExposure.MiddleGrey));
    }

    [Fact]
    public void DarkSceneIsBrightened()
    {
        Assert.True(ScaleForConstant(0.01) > 1f);
    }

    [Fact]
    public void BrightSceneIsDarkened()
    {
        Assert.True(ScaleForConstant(4.0) < 1f);
    }

    [Fact]
    public void DarkerScenesGetAHigherFactor()
    {
        Assert.True(ScaleForConstant(0.05) > ScaleForConstant(0.20));
        Assert.True(ScaleForConstant(0.20) > ScaleForConstant(2.00));
    }

    [Fact]
    public void BlackSceneIsClampedInsteadOfExploding()
    {
        // An all-black cubemap must not produce an infinite factor.
        Assert.InRange(ScaleForConstant(0.0), AutoExposure.MinScale, AutoExposure.MaxScale);
    }

    [Fact]
    public void EmptySampleSetIsUnchanged()
    {
        Assert.Equal(1f, AutoExposure.Scale(0, 0));
    }

    [Fact]
    public void FactorIsClampedToTheSafeRange()
    {
        AssertClose(AutoExposure.MaxScale, ScaleForConstant(1e-12));
        AssertClose(AutoExposure.MinScale, ScaleForConstant(1e6));
    }
}
