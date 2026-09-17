using System.Text.Json;
using System.Text.Json.Serialization;
using Xt1.Core.Encoding;
using Xt1.Core.Format;
using Xt1.Core.Interop;

namespace Xt1.Core.Settings;

/// <summary>Application settings, persisted to <c>%APPDATA%\Xt1Studio\settings.json</c>.</summary>
public sealed class AppSettings
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EncoderBackend PreferredBackend { get; set; } = EncoderBackend.Managed;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Xt1Format DefaultImportFormat { get; set; } = Xt1Format.Bc7Unorm;

    public bool GenerateMips { get; set; } = true;

    public bool ApplySrgbPreview { get; set; } = true;

    /// <summary>Preselect the "opaque alpha" viewer toggle (ignores the alpha channel on load).</summary>
    public bool OpaqueAlphaByDefault { get; set; }

    /// <summary>Include the alpha-only option in the viewer's channel selector.</summary>
    public bool ShowAlphaChannel { get; set; } = true;

    public bool RecompressOodleOnSave { get; set; }

    public int VolumeResolutionCap { get; set; } = 512;

    public string? NvttDllPath { get; set; }
    public string? NvttCliPath { get; set; }
    public string? OodlePath { get; set; }
    public string? AstcencPath { get; set; }
    public string? TexconvPath { get; set; }

    public string? LastDirectory { get; set; }

    [JsonIgnore]
    public static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Xt1Studio");

    [JsonIgnore]
    public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (loaded is not null) return loaded;
            }
        }
        catch
        {
            // Corrupt or unreadable settings fall back to defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // Never let settings persistence break the app.
        }
    }

    /// <summary>Verified on-disk locations of the optional native tools (null when absent).</summary>
    public ResolvedTools Resolve()
    {
        return new ResolvedTools(
            NvttDll: ToolLocator.Find(ToolLocator.NvttDllFileName, NvttDllPath),
            NvttCli: ToolLocator.Find(ToolLocator.NvttCliFileName, NvttCliPath),
            Oodle: ToolLocator.Find(ToolLocator.OodleFileName, OodlePath),
            Astcenc: string.IsNullOrWhiteSpace(AstcencPath) ? null : (File.Exists(AstcencPath) ? AstcencPath : null),
            Texconv: string.IsNullOrWhiteSpace(TexconvPath) ? null : (File.Exists(TexconvPath) ? TexconvPath : null));
    }
}

public sealed record ResolvedTools(string? NvttDll, string? NvttCli, string? Oodle, string? Astcenc, string? Texconv)
{
    public bool HasNvtt => NvttDll is not null;
    public bool HasNvttCli => NvttCli is not null;
    public bool HasOodle => Oodle is not null;
}
