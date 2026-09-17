namespace Xt1.Core.Interop;

/// <summary>Locates optional native tools. Paths are auto-detected and always overridable.</summary>
public static class ToolLocator
{
    public const string NvttDllFileName = "nvtt30205.dll";
    public const string NvttCliFileName = "nvtt_export.exe";

    public static readonly string[] NvttInstallDirectories =
    {
        @"C:\Program Files\NVIDIA Corporation\NVIDIA Texture Tools",
        @"C:\Program Files (x86)\NVIDIA Corporation\NVIDIA Texture Tools",
    };

    /// <summary>
    /// Resolve a native file by name using, in order: an explicit configured path, the
    /// application directory, each known install directory, then PATH.
    /// </summary>
    public static string? Find(string fileName, string? explicitPath = null,
        IEnumerable<string>? extraDirectories = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
            return explicitPath;

        var directories = new List<string>();
        directories.Add(AppContext.BaseDirectory);
        if (extraDirectories is not null) directories.AddRange(extraDirectories);
        directories.AddRange(DefaultDirectoriesFor(fileName));

        foreach (var dir in directories)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // ignore malformed directory entries
            }
        }

        var onPath = SearchPath(fileName);
        return onPath;
    }

    private static IEnumerable<string> DefaultDirectoriesFor(string fileName)
    {
        if (string.Equals(fileName, NvttDllFileName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, NvttCliFileName, StringComparison.OrdinalIgnoreCase))
            return NvttInstallDirectories;
        return Array.Empty<string>();
    }

    private static string? SearchPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
            }
        }
        return null;
    }
}
