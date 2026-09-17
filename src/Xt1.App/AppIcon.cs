namespace Xt1.App;

/// <summary>
/// The application icon. It is embedded in the assembly (<c>EmbeddedResource</c>) and set as the
/// executable icon (<c>ApplicationIcon</c> in the csproj), so the window/taskbar icon does not
/// depend on a file being next to the exe.
/// </summary>
internal static class AppIcon
{
    private const string ResourceSuffix = "Xt1Studio.ico";

    private static readonly Lazy<Icon> Cached = new(Load, isThreadSafe: true);

    /// <summary>The icon, or the generic application icon if the resource is missing.</summary>
    public static Icon Shared => Cached.Value;

    private static Icon Load()
    {
        var assembly = typeof(AppIcon).Assembly;
        var name = Array.Find(assembly.GetManifestResourceNames(),
            candidate => candidate.EndsWith(ResourceSuffix, StringComparison.OrdinalIgnoreCase));
        if (name is not null)
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is not null) return new Icon(stream);
        }
        return SystemIcons.Application;
    }
}
