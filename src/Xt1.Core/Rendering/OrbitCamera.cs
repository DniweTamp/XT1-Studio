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

    /// <summary>Beyond this the camera would roll past the poles and the horizon flips.</summary>
    private const float MaxPitch = 1.5f;

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
        Yaw += dx * OrbitSpeed;
        Pitch = Math.Clamp(Pitch + dy * OrbitSpeed, -MaxPitch, MaxPitch);
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
    /// Rotation <c>Rx(pitch) * Ry(yaw)</c> as a column-major 3x3 (the layout that
    /// <c>GL.UniformMatrix3</c> expects).
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
