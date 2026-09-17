using Xt1.Core.Imaging;
using Xt1.Core.Rendering;
using Xunit;

namespace Xt1.Core.Tests;

/// <summary>
/// The 3D viewport orbits an object: the camera moves on a sphere and always looks at the origin, so
/// dragging swings the volume into view. These tests pin that invariant down without a GL context.
/// </summary>
public class OrbitCameraTests
{
    private static (float X, float Y, float Z) Mul(float[] m, float x, float y, float z) =>
        (m[0] * x + m[3] * y + m[6] * z,
         m[1] * x + m[4] * y + m[7] * z,
         m[2] * x + m[5] * y + m[8] * z);

    private static float Length((float X, float Y, float Z) v) =>
        MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

    [Fact]
    public void Reset_ReturnsToTheDefaults()
    {
        var camera = new OrbitCamera { Yaw = 9f, Pitch = -3f, Distance = 12f, Fov = 2f };

        camera.Reset();

        Assert.Equal(OrbitCamera.DefaultYaw, camera.Yaw);
        Assert.Equal(OrbitCamera.DefaultPitch, camera.Pitch);
        Assert.Equal(OrbitCamera.DefaultDistance, camera.Distance);
        Assert.Equal(OrbitCamera.DefaultFov, camera.Fov);
    }

    [Fact]
    public void Eye_IsOnTheOrbitSphereForEveryOrientation()
    {
        var camera = new OrbitCamera { Distance = 5f };

        for (float yaw = -3f; yaw <= 3f; yaw += 0.37f)
        {
            for (float pitch = -1.5f; pitch <= 1.5f; pitch += 0.31f)
            {
                camera.Yaw = yaw;
                camera.Pitch = pitch;
                var eye = camera.Eye();
                Assert.Equal(5f, Length(eye), 3);
            }
        }
    }

    [Fact]
    public void Eye_SitsInFrontOfTheObjectWhenUnrotated()
    {
        var camera = new OrbitCamera { Yaw = 0f, Pitch = 0f, Distance = 3.2f };

        var eye = camera.Eye();

        Assert.Equal(0f, eye.X, 4);
        Assert.Equal(0f, eye.Y, 4);
        Assert.Equal(3.2f, eye.Z, 4);
    }

    [Fact]
    public void Orbit_KeepsTheCameraAimedAtTheOrigin()
    {
        // This is what makes it an orbit rather than a first-person turn: the view direction is
        // always the negation of the eye direction, so the target cannot drift off-center.
        var camera = new OrbitCamera { Distance = 4.5f };

        for (float yaw = -2f; yaw <= 2f; yaw += 0.5f)
        {
            for (float pitch = -1.5f; pitch <= 1.5f; pitch += 0.5f)
            {
                camera.Yaw = yaw;
                camera.Pitch = pitch;
                var eye = camera.Eye();
                float length = Length(eye);
                var forward = Mul(camera.Rotation(), 0f, 0f, -1f);

                Assert.Equal(-eye.X / length, forward.X, 3);
                Assert.Equal(-eye.Y / length, forward.Y, 3);
                Assert.Equal(-eye.Z / length, forward.Z, 3);
            }
        }
    }

    [Fact]
    public void Rotation_IsOrthonormal()
    {
        var camera = new OrbitCamera();
        foreach (float angle in new[] { -2.4f, -0.7f, 0f, 0.35f, 1.2f, 2.9f })
        {
            camera.Yaw = angle;
            camera.Pitch = angle / 2f;
            var m = camera.Rotation();
            var column0 = (m[0], m[1], m[2]);
            var column1 = (m[3], m[4], m[5]);
            var column2 = (m[6], m[7], m[8]);

            Assert.Equal(1f, Length(column0), 3);
            Assert.Equal(1f, Length(column1), 3);
            Assert.Equal(1f, Length(column2), 3);
            Assert.Equal(0f, Dot(column0, column1), 3);
            Assert.Equal(0f, Dot(column0, column2), 3);
            Assert.Equal(0f, Dot(column1, column2), 3);
        }

        static float Dot((float X, float Y, float Z) a, (float X, float Y, float Z) b) =>
            a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    }

    [Fact]
    public void Orbit_ClampsPitchSoTheCameraNeverFlipsOverThePole()
    {
        var camera = new OrbitCamera();

        for (int i = 0; i < 200; i++) camera.Orbit(0f, 100f);
        Assert.InRange(camera.Pitch, 1.5f - 0.0001f, 1.5f);

        for (int i = 0; i < 400; i++) camera.Orbit(0f, -100f);
        Assert.InRange(camera.Pitch, -1.5f, -1.5f + 0.0001f);

        Assert.False(float.IsNaN(camera.Eye().X));
    }

    [Fact]
    public void Orbit_ChangesYawWithHorizontalDrag()
    {
        var camera = new OrbitCamera { Yaw = 0f, Pitch = 0f };

        camera.Orbit(10f, 0f);

        Assert.Equal(10f * OrbitCamera.OrbitSpeed, camera.Yaw, 5);
        Assert.Equal(0f, camera.Pitch, 5);
    }

    [Fact]
    public void Zoom_MovesTheCameraAlongTheOrbitRadiusAndClamps()
    {
        var camera = new OrbitCamera { Distance = 3f };

        camera.Zoom(1f);
        Assert.True(camera.Distance < 3f);
        Assert.True(camera.Distance > OrbitCamera.MinDistance);

        camera.Zoom(-1f);
        Assert.Equal(3f, camera.Distance, 4);

        for (int i = 0; i < 200; i++) camera.Zoom(1f);
        Assert.Equal(OrbitCamera.MinDistance, camera.Distance, 4);

        for (int i = 0; i < 400; i++) camera.Zoom(-1f);
        Assert.Equal(OrbitCamera.MaxDistance, camera.Distance, 4);
    }

    [Fact]
    public void Zoom_DoesNotChangeTheFieldOfView()
    {
        // Zooming an object view is dollying, not lensing: the object must not distort.
        var camera = new OrbitCamera { Fov = 0.9f };

        camera.Zoom(5f);

        Assert.Equal(0.9f, camera.Fov, 5);
    }

    [Fact]
    public void ZoomFov_MagnifiesTheCubemapWithoutMovingTheCamera()
    {
        var camera = new OrbitCamera { Distance = 3.2f, Fov = 0.9f };

        camera.ZoomFov(1f);

        Assert.True(camera.Fov < 0.9f);
        Assert.Equal(3.2f, camera.Distance, 5);

        for (int i = 0; i < 200; i++) camera.ZoomFov(-1f);
        Assert.InRange(camera.Fov, 2.5f - 0.0001f, 2.5f);
    }

    [Fact]
    public void Scale_IsTheTangentOfHalfTheFieldOfView()
    {
        var camera = new OrbitCamera { Fov = 0.9f };

        Assert.Equal(MathF.Tan(0.45f), camera.Scale, 5);
    }

    // --- cubemap look control ----------------------------------------------------------------

    private static (float X, float Y, float Z) Forward(OrbitCamera camera) =>
        Mul(camera.LookRotation(), 0f, 0f, -1f);

    [Fact]
    public void LookRotation_KeepsTheHorizonLevelForEveryYawAndPitch()
    {
        // The bug this guards against: Rx(pitch)*Ry(yaw) tilts the camera's right vector by
        // sin(pitch)*sin(yaw), so the whole view rolls. The look rotation must not.
        var camera = new OrbitCamera();

        for (float yaw = -3f; yaw <= 3f; yaw += 0.23f)
        {
            for (float pitch = -OrbitCamera.MaxLookPitch; pitch <= OrbitCamera.MaxLookPitch; pitch += 0.17f)
            {
                camera.Yaw = yaw;
                camera.Pitch = pitch;
                var m = camera.LookRotation();
                var right = (X: m[0], Y: m[1], Z: m[2]);
                var up = (X: m[3], Y: m[4], Z: m[5]);

                Assert.Equal(0f, right.Y, 4);      // camera right stays horizontal -> no roll
                Assert.True(up.Y > 0f, "the horizon must never flip over");
                Assert.Equal(1f, Length(right), 3);
                Assert.Equal(1f, Length(up), 3);
            }
        }
    }

    [Fact]
    public void LookRotation_TurnsTheViewUpAndDownWithoutChangingTheRoll()
    {
        var camera = new OrbitCamera { Yaw = 0.8f, Pitch = 0f };
        var level = camera.LookRotation();

        camera.Pitch = 0.6f; // look up while yawed
        var lookingUp = camera.LookRotation();

        // The right vector (roll reference) must be identical: only the view direction changed.
        Assert.Equal(level[0], lookingUp[0], 5);
        Assert.Equal(level[1], lookingUp[1], 5);
        Assert.Equal(level[2], lookingUp[2], 5);
        Assert.True(Mul(lookingUp, 0f, 0f, -1f).Y > 0f, "pitching up must raise the view direction");
    }

    [Fact]
    public void Look_ResetPointsForwardWithALevelHorizon()
    {
        var camera = new OrbitCamera { Yaw = 2f, Pitch = 1.2f, Fov = 0.4f };

        camera.ResetLook();

        Assert.Equal(0f, camera.Yaw, 5);
        Assert.Equal(0f, camera.Pitch, 5);
        Assert.Equal(OrbitCamera.DefaultFov, camera.Fov, 5);
        var forward = Forward(camera);
        Assert.Equal(0f, forward.X, 4);
        Assert.Equal(0f, forward.Y, 4);
        Assert.Equal(-1f, forward.Z, 4);
    }

    [Fact]
    public void Look_RightDragTurnsRightWithoutAnyVerticalChange()
    {
        var camera = new OrbitCamera { Yaw = 0f, Pitch = 0f };

        camera.Look(10f, 0f); // drag right

        Assert.True(camera.Yaw < 0f, "yaw must decrease so the view turns right");
        var forward = Forward(camera);
        Assert.True(forward.X > 0f, "turning right must move the view toward the camera's right");
        Assert.Equal(0f, forward.Y, 5);
    }

    [Fact]
    public void Look_UpwardDragLooksUp()
    {
        var camera = new OrbitCamera { Yaw = 0f, Pitch = 0f };

        camera.Look(0f, -10f); // drag up

        Assert.True(camera.Pitch > 0f);
        Assert.True(Forward(camera).Y > 0f, "looking up must raise the view direction");
    }

    [Fact]
    public void Look_ClampsPitchAndNeverRollsTheHorizon()
    {
        var camera = new OrbitCamera { Yaw = 0f, Pitch = 0f };

        for (int i = 0; i < 400; i++) camera.Look(0f, -100f);
        Assert.Equal(OrbitCamera.MaxLookPitch, camera.Pitch, 5);
        Assert.True(camera.LookRotation()[4] > 0f, "the camera up vector must keep a positive Y (level horizon)");

        for (int i = 0; i < 800; i++) camera.Look(0f, 100f);
        Assert.Equal(-OrbitCamera.MaxLookPitch, camera.Pitch, 5);
        Assert.True(camera.LookRotation()[4] > 0f, "looking down to the limit must not flip the view");

        // A clear margin under 90 degrees keeps a yaw turn from becoming a roll at the pole.
        Assert.True(OrbitCamera.MaxLookPitch < MathF.PI / 2f - 0.1f);
    }

    [Fact]
    public void Look_YawWrapsAndHorizontalTurnStaysHorizontalNearThePitchLimit()
    {
        var camera = new OrbitCamera { Yaw = 0f, Pitch = OrbitCamera.MaxLookPitch };

        for (int i = 0; i < 1000; i++) camera.Look(50f, 0f);
        Assert.InRange(camera.Yaw, -MathF.PI, MathF.PI);

        // At the pitch limit a yaw turn must still move the view along the camera's right axis with
        // no up/down component: that is what "looking left/right" means.
        camera.Yaw = 0f;
        var before = Forward(camera);
        camera.Look(4f, 0f);
        var after = Forward(camera);
        var rotation = camera.LookRotation();
        float dx = after.X - before.X;
        float dy = after.Y - before.Y;
        float dz = after.Z - before.Z;
        float alongRight = dx * rotation[0] + dy * rotation[1] + dz * rotation[2];
        float alongUp = dx * rotation[3] + dy * rotation[4] + dz * rotation[5];
        Assert.True(MathF.Abs(alongRight) > MathF.Abs(alongUp) * 5f);
        Assert.False(float.IsNaN(before.X + after.X));
    }

    [Fact]
    public void LookBy_StepsTheViewAndClampsTheSameWay()
    {
        var camera = new OrbitCamera { Yaw = 0f, Pitch = 0f };

        camera.LookBy(0.1f, -0.1f);

        Assert.Equal(0.1f, camera.Yaw, 5);
        Assert.Equal(-0.1f, camera.Pitch, 5);
    }
}

/// <summary>Alpha uniformity drives the automatic "show RGB instead of a blank volume" decision.</summary>
public class RgbaImageAlphaTests
{
    [Fact]
    public void IsAlphaUniform_TrueForAFullyOpaqueVolume()
    {
        var image = new RgbaImage(4, 4, 3);
        image.Fill(10, 20, 30, 255);

        Assert.True(image.IsAlphaUniform());
    }

    [Fact]
    public void IsAlphaUniform_TrueForAFullyTransparentVolume()
    {
        var image = new RgbaImage(4, 4, 3);
        image.Fill(10, 20, 30, 0);

        Assert.True(image.IsAlphaUniform());
    }

    [Fact]
    public void IsAlphaUniform_FalseWhenAnyTexelDiffers()
    {
        var image = new RgbaImage(4, 4, 1);
        image.Fill(10, 20, 30, 255);
        image.SetPixel(2, 2, 10, 20, 30, 128);

        Assert.False(image.IsAlphaUniform());
    }

    [Fact]
    public void IsAlphaUniform_IgnoresRgbAndDepth()
    {
        var image = new RgbaImage(2, 2, 2);
        image.Fill(0, 0, 0, 7);
        image.SetPixel(1, 1, 250, 250, 250, 7, 1);

        Assert.True(image.IsAlphaUniform());
    }
}
