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
}
