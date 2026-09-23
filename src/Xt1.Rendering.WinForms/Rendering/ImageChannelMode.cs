namespace Xt1.Rendering.WinForms.Rendering;

/// <summary>
/// Which colour channel a preview shows: all four as they are stored, or one channel isolated.
/// Shared by the OpenGL viewport and the harnesses that drive it, so the two cannot disagree.
/// </summary>
public enum ImageChannelMode
{
    Rgba,
    Red,
    Green,
    Blue,
    Alpha,
}
