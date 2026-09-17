namespace Xt1.Core.Rendering;

/// <summary>
/// Orbit camera for the 3D preview: it sits on a sphere of radius <see cref="Distance"/> around the
/// origin and always looks at it, so dragging swings the object into view instead of turning a
/// first-person view from inside it. Wheel zoom changes the orbit radius, not the field of view.
///
/// This is deliberately free of GL types (and of OpenTK) so the basis and the projection scale can
/// be unit tested; the viewport only uploads <see cref="Rotation"/>, <see cref="Eye"/> and
/// <see cref="Scale"/> as uniforms.
/// </summary>
public sealed class OrbitCamera
{
    public const float DefaultYaw = 0.6f;
    public const float DefaultPitch = 0.35f;
    /// <summary>Far enough that a unit half-extent box leaves a margin around it in the viewport.</summary>
    public const float DefaultDistance = 3.6f;
    public const float DefaultFov = 0.9f;

    public const float MinDistance = 1.4f;
    public const float MaxDistance = 16f;

    /// <summary>Radians of rotation per mouse pixel.</summary>
    public const float OrbitSpeed = 0.008f;

    /// <summary>Radians of view turn per mouse pixel for the cubemap look control.</summary>
    public const float LookSpeed = 0.005f;

    /// <summary>Beyond this the camera would roll past the poles and the horizon flips.</summary>
    private const float MaxPitch = 1.5f;

    /// <summary>
    /// Look-around pitch limit. Kept a clear margin short of vertical: at the pole every yaw turn
    /// becomes a roll, which is what makes a skybox feel like it is tipping over.
    /// </summary>
    public const float MaxLookPitch = 1.4f; // ~80 degrees

    public float Yaw { get; set; } = DefaultYaw;
    public float Pitch { get; set; } = DefaultPitch;

    /// <summary>Orbit radius. Kept above <see cref="MinDistance"/> so the object never fills the near plane.</summary>
    public float Distance { get; set; } = DefaultDistance;

    /// <summary>Vertical field of view in radians.</summary>
    public float Fov { get; set; } = DefaultFov;

    public void Reset()
    {
        Yaw = DefaultYaw;
        Pitch = DefaultPitch;
        Distance = DefaultDistance;
        Fov = DefaultFov;
    }

    /// <summary>Swing the camera by a mouse drag, in pixels.</summary>
    public void Orbit(float dx, float dy)
    {
        Yaw = Wrap(Yaw + dx * OrbitSpeed);
        Pitch = Math.Clamp(Pitch + dy * OrbitSpeed, -MaxPitch, MaxPitch);
    }

    /// <summary>
    /// First-person "look around" for the inside-out cubemap view: dragging right turns the view
    /// right and dragging up looks up. Yaw turns about world up and pitch about the camera's own
    /// right, so the horizon stays level and the view can never roll or end up upside down.
    /// </summary>
    public void Look(float dx, float dy)
    {
        Yaw = Wrap(Yaw - dx * LookSpeed);
        Pitch = Math.Clamp(Pitch - dy * LookSpeed, -MaxLookPitch, MaxLookPitch);
    }

    /// <summary>Turn the view by explicit angles (keyboard arrows), in radians.</summary>
    public void LookBy(float dYaw, float dPitch)
    {
        Yaw = Wrap(Yaw + dYaw);
        Pitch = Math.Clamp(Pitch + dPitch, -MaxLookPitch, MaxLookPitch);
    }

    /// <summary>
    /// Point the inside-out view straight at the panorama with a level horizon. Used when the
    /// cubemap view is entered, so it never inherits the volume's orbit angles.
    /// </summary>
    public void ResetLook()
    {
        Yaw = 0f;
        Pitch = 0f;
        Fov = DefaultFov;
    }

    /// <summary>Keep the yaw in (-pi, pi] so a long drag cannot drift the float into imprecision.</summary>
    private static float Wrap(float angle)
    {
        const float twoPi = MathF.PI * 2f;
        angle %= twoPi;
        if (angle > MathF.PI) angle -= twoPi;
        else if (angle < -MathF.PI) angle += twoPi;
        return angle;
    }

    /// <summary>
    /// Wheel zoom, in notches (positive = closer). Multiplicative so each notch moves the same
    /// fraction of the remaining distance.
    /// </summary>
    public void Zoom(float notches) =>
        Distance = Math.Clamp(Distance * MathF.Pow(0.9f, notches), MinDistance, MaxDistance);

    /// <summary>
    /// Wheel zoom for the inside-out cubemap view, where the camera cannot move: narrowing the field
    /// of view is what magnifies it.
    /// </summary>
    public void ZoomFov(float notches) =>
        Fov = Math.Clamp(Fov * MathF.Pow(0.9f, notches), 0.15f, 2.5f);

    /// <summary>Half-height of the view plane at unit distance, i.e. <c>tan(fov/2)</c>.</summary>
    public float Scale => MathF.Tan(Fov * 0.5f);

    /// <summary>
    /// Orbit rotation <c>Rx(pitch) * Ry(yaw)</c> as a column-major 3x3 (the layout that
    /// <c>GL.UniformMatrix3</c> expects). Yaw turns about world Y and pitch about the world X that
    /// the yaw left behind, which is what keeps an orbited object's poles upright - but it also
    /// tilts the camera's right vector (<c>sin(pitch) * sin(yaw)</c> of Y), so it must not be used
    /// for the inside-out look camera.
    /// </summary>
    public float[] Rotation()
    {
        float cy = MathF.Cos(Yaw), sy = MathF.Sin(Yaw);
        float cp = MathF.Cos(Pitch), sp = MathF.Sin(Pitch);
        return new[]
        {
            cy, sp * sy, -cp * sy,
            0f, cp, sp,
            sy, -sp * cy, cp * cy,
        };
    }

    /// <summary>
    /// First-person look rotation <c>Ry(yaw) * Rx(pitch)</c>: pitch is applied in the camera's own
    /// frame first, then yaw about world Y. The camera's right vector is always horizontal, so the
    /// horizon stays level for any yaw/pitch combination - looking up or down turns the view up or
    /// down instead of rolling it.
    /// </summary>
    public float[] LookRotation()
    {
        float cy = MathF.Cos(Yaw), sy = MathF.Sin(Yaw);
        float cp = MathF.Cos(Pitch), sp = MathF.Sin(Pitch);
        return new[]
        {
            cy, 0f, -sy,
            sy * sp, cp, cy * sp,
            sy * cp, -sp, cy * cp,
        };
    }

    /// <summary>
    /// Camera position: the rotation applied to <c>(0, 0, Distance)</c>, i.e. the third column of
    /// <see cref="Rotation"/> scaled by the radius. The camera looks back along its local -Z, so the
    /// target stays at the origin for every yaw/pitch.
    /// </summary>
    public (float X, float Y, float Z) Eye()
    {
        var rotation = Rotation();
        return (rotation[6] * Distance, rotation[7] * Distance, rotation[8] * Distance);
    }
}
