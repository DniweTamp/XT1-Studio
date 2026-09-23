using System.Text.Json;
using System.Text.Json.Serialization;
using Xt1.Core.Encoding;
using Xt1.Core.Format;
using Xt1.Core.Interop;

namespace Xt1.Core.Settings;

/// <summary>Which UI theme the app uses. <see cref="System"/> follows the OS light/dark preference.</summary>
public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>Application settings, persisted to <c>settings.json</c> next to the executable.</summary>
public sealed class AppSettings
{
    /// <summary>UI theme choice. Defaults to following the OS until the user picks one explicitly.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>NVTT is the default encoder; the managed encoder is the fallback when NVTT is absent.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EncoderBackend PreferredBackend { get; set; } = EncoderBackend.Nvtt;

    /// <summary>Format the import dialog preselects for image imports. ASTC 8x8 sRGB (0x8E) matches
    /// what most of the game's textures use.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Xt1Format DefaultImportFormat { get; set; } = Xt1Format.Astc8x8Srgb;

    /// <summary>
    /// Preselect the "opaque alpha" viewer toggle. On by default: most of the game's textures are
    /// UNORM/BC payloads whose fourth channel carries no display information, and compositing those
    /// over the checkerboard hides the RGB the user actually wants to inspect. Untick it (or use the
    /// Alpha channel selector) to look at the stored alpha over the checkerboard instead.
    /// </summary>
    public bool OpaqueAlphaByDefault { get; set; } = true;

    /// <summary>Include the alpha-only option in the viewer's channel selector.</summary>
    public bool ShowAlphaChannel { get; set; } = true;

    /// <summary>
    /// Keep the WTB sRGB/cubemap table2 bits in step with the texture's format when a texture is
    /// added or replaced. On by default because a re-encoded texture whose flags disagree with its
    /// format is a broken record.
    /// </summary>
    public bool SyncFlagsFromFormat { get; set; } = true;

    public int VolumeResolutionCap { get; set; } = 512;

    public string? NvttDllPath { get; set; }
    public string? NvttCliPath { get; set; }

    /// <summary>
    /// One-time marker for the switch from the managed encoder to NVTT as the default backend.
    /// Null means the file predates the marker (a legacy "Managed" default that is upgraded once);
    /// once written it records that the migration already ran, so a later deliberate choice of
    /// Managed is preserved. Nullable precisely so "absent" and "false" cannot be confused.
    /// </summary>
    public bool? NvttDefaultApplied { get; set; }

    public string? LastDirectory { get; set; }

    /// <summary>The folder holding <c>settings.json</c>: the executable's own directory, so the tool
    /// stays portable and its state travels with it.</summary>
    [JsonIgnore]
    public static string SettingsDirectory => AppContext.BaseDirectory;

    [JsonIgnore]
    public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    /// <summary>
    /// Roaming location: used both to migrate a pre-portable settings file and as a fallback when the
    /// executable's directory is not writable (e.g. an install under Program Files).
    /// </summary>
    [JsonIgnore]
    public static string FallbackSettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Xt1Studio");

    [JsonIgnore]
    public static string FallbackSettingsPath => Path.Combine(FallbackSettingsDirectory, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    public static AppSettings Load()
    {
        // Prefer the portable file next to the executable, then the roaming copy (which is also the
        // pre-portable location, so this migrates an existing configuration rather than losing it).
        // A brand-new install is born already migrated so its backend choice is not overwritten.
        var fresh = new AppSettings { NvttDefaultApplied = true };
        try
        {
            var path = File.Exists(SettingsPath) ? SettingsPath
                : File.Exists(FallbackSettingsPath) ? FallbackSettingsPath
                : null;
            if (path is null) return fresh;

            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
            if (loaded is null) return fresh;

            // Null (property absent) marks a legacy file: apply the new NVTT default once.
            if (loaded.NvttDefaultApplied is null)
            {
                loaded.PreferredBackend = EncoderBackend.Nvtt;
                loaded.NvttDefaultApplied = true;
            }
            if (path != SettingsPath) loaded.Save();
            return loaded;
        }
        catch
        {
            // Corrupt or unreadable settings fall back to defaults.
        }
        return fresh;
    }

    /// <summary>
    /// Persist to disk, preferring the executable's directory and falling back to the roaming folder
    /// when it is not writable. Written to a temporary file and moved into place so an interrupted or
    /// concurrent save cannot leave a truncated settings.json, which would silently reset every
    /// configured path and default on the next launch.
    /// </summary>
    public void Save() => Save(out _);

    /// <summary>Persist to disk; returns false (with the last error) when neither location is writable.</summary>
    public bool Save(out string? error)
    {
        error = null;
        var json = JsonSerializer.Serialize(this, Options);
        foreach (var path in new[] { SettingsPath, FallbackSettingsPath })
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
                File.WriteAllText(temp, json);
                File.Move(temp, path, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        }
        return false;
    }

    /// <summary>Verified on-disk locations of the optional native tools (null when absent).</summary>
    public ResolvedTools Resolve()
    {
        return new ResolvedTools(
            NvttDll: ToolLocator.Find(ToolLocator.NvttDllFileName, NvttDllPath),
            NvttCli: ToolLocator.Find(ToolLocator.NvttCliFileName, NvttCliPath));
    }
}

public sealed record ResolvedTools(string? NvttDll, string? NvttCli);
