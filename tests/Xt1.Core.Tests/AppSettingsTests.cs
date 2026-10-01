using Xt1.Core.Encoding;
using Xt1.Core.Format;
using Xt1.Core.Settings;
using Xunit;

namespace Xt1.Core.Tests;

/// <summary>Defaults a fresh install starts from.</summary>
public class AppSettingsTests
{
    [Fact]
    public void Defaults_UseNvttAndAstc8x8Srgb()
    {
        var settings = new AppSettings();

        Assert.Equal(EncoderBackend.Nvtt, settings.PreferredBackend);
        Assert.Equal(Xt1Format.Astc8x8Srgb, settings.DefaultImportFormat);
        Assert.Equal(0x8Eu, (uint)settings.DefaultImportFormat);
        Assert.True(settings.SyncFlagsFromFormat);
    }

    /// <summary>No recorded filter means the import dialog offers every encodable format, which is how
    /// an older settings file (without the field) must behave.</summary>
    [Fact]
    public void ImportFormats_DefaultToNoFilter()
    {
        Assert.Null(new AppSettings().ImportFormats);
    }

    /// <summary>The Asset Browser starts as the list, and the zoom level survives a save/load.</summary>
    [Fact]
    public void AssetBrowserLevel_DefaultsToListAndRoundTrips()
    {
        Assert.Equal(AssetViewLevel.List, new AppSettings().AssetBrowserLevel);

        var path = AppSettings.SettingsPath;
        var backup = File.Exists(path) ? File.ReadAllBytes(path) : null;
        try
        {
            Assert.True(new AppSettings { AssetBrowserLevel = AssetViewLevel.Grid128 }.Save(out var error), error);
            Assert.Equal(AssetViewLevel.Grid128, AppSettings.Load().AssetBrowserLevel);
        }
        finally
        {
            if (backup is null) File.Delete(path);
            else File.WriteAllBytes(path, backup);
        }
    }

    /// <summary>
    /// The format filter survives a save/load round trip even when it holds an id this build does not
    /// define (a settings file from a newer version). Because the entries are written as numbers rather
    /// than names, an unknown value is ignored instead of failing the whole load and resetting every
    /// other setting.
    /// </summary>
    [Fact]
    public void ImportFormats_RoundTripToleratesUnknownIds()
    {
        var path = AppSettings.SettingsPath;
        var backup = File.Exists(path) ? File.ReadAllBytes(path) : null;
        try
        {
            var saved = new AppSettings
            {
                DefaultImportFormat = Xt1Format.Bc7Unorm,
                VolumeResolutionCap = 640,
                ImportFormats = new List<Xt1Format> { Xt1Format.Bc7Unorm, (Xt1Format)0x7FFF },
            };

            Assert.True(saved.Save(out var error), error);
            var loaded = AppSettings.Load();

            // The unknown id is preserved as a value, and the rest of the configuration is intact.
            Assert.Equal(saved.ImportFormats, loaded.ImportFormats);
            Assert.Equal(Xt1Format.Bc7Unorm, loaded.DefaultImportFormat);
            Assert.Equal(640, loaded.VolumeResolutionCap);
        }
        finally
        {
            if (backup is null) File.Delete(path);
            else File.WriteAllBytes(path, backup);
        }
    }

    /// <summary>The Open Recent history survives a save/load, newest first.</summary>
    [Fact]
    public void RecentFiles_RoundTripThroughSettingsFile()
    {
        var path = AppSettings.SettingsPath;
        var backup = File.Exists(path) ? File.ReadAllBytes(path) : null;
        try
        {
            var saved = new AppSettings
            {
                RecentFiles = new List<string> { @"C:\banks\one.dat", @"C:\banks\two.wta" },
            };

            Assert.True(saved.Save(out var error), error);
            Assert.Equal(saved.RecentFiles, AppSettings.Load().RecentFiles);
        }
        finally
        {
            if (backup is null) File.Delete(path);
            else File.WriteAllBytes(path, backup);
        }
    }
}
