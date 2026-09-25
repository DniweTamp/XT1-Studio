using Xt1.Core.Containers;
using Xt1.Core.Editing;
using Xt1.Core.Format;
using Xt1.Core.Imaging;
using Xunit;

namespace Xt1.Core.Tests;

/// <summary>
/// The shared PNG/DDS/XT1 writer and its folder-wide driver. Real-file tests use a loose .xt1
/// (written from a golden texture) so they need no sibling payload file, and skip when the romfs is
/// not present.
/// </summary>
public class TextureExportTests
{
    private sealed class NullProgress : IProgress<string>
    {
        public List<string> Lines { get; } = new();
        public void Report(string value) => Lines.Add(value);
    }

    private static string TempDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "Xt1Studio", "tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Theory]
    [InlineData("png", TextureExportFormat.Png)]
    [InlineData("DDS", TextureExportFormat.Dds)]
    [InlineData("xt1", TextureExportFormat.Xt1)]
    public void TryParse_AcceptsTheThreeShellSpellings(string text, TextureExportFormat expected)
    {
        Assert.True(TextureExport.TryParse(text, out var format));
        Assert.Equal(expected, format);
    }

    [Fact]
    public void TryParse_RejectsUnknown()
    {
        Assert.False(TextureExport.TryParse("tga", out _));
        Assert.False(TextureExport.TryParse(null, out _));
    }

    [Fact]
    public void Extensions_AreCanonicalLowerCase()
    {
        Assert.Equal(".png", TextureExport.Extension(TextureExportFormat.Png));
        Assert.Equal(".dds", TextureExport.Extension(TextureExportFormat.Dds));
        Assert.Equal(".xt1", TextureExport.Extension(TextureExportFormat.Xt1));
    }

    /// <summary>A golden texture round-trips through all three writers and each output reloads.</summary>
    [Fact]
    public void Write_PngDdsXt1_ProducesReloadableFiles()
    {
        if (!TestPaths.Exists(TestPaths.Em0000Dat)) return;
        var document = BankDocument.Load(TestPaths.Em0000Dat);
        var texture = document.AllTextures.FirstOrDefault(t =>
            TextureExport.CanExport(t, TextureExportFormat.Png) &&
            TextureExport.CanExport(t, TextureExportFormat.Dds));
        if (texture is null) return;

        string dir = TempDirectory();
        try
        {
            string png = Path.Combine(dir, "tex.png");
            string dds = Path.Combine(dir, "tex.dds");
            string xt1 = Path.Combine(dir, "tex.xt1");

            Assert.True(TextureExport.TryWrite(texture, TextureExportFormat.Png, png, out var pngError), pngError);
            Assert.True(TextureExport.TryWrite(texture, TextureExportFormat.Dds, dds, out var ddsError), ddsError);
            Assert.True(TextureExport.TryWrite(texture, TextureExportFormat.Xt1, xt1, out var xt1Error), xt1Error);

            Assert.Equal((int)texture.Header.Width, ImageIo.Load(png).Width);
            Assert.Equal((int)texture.Header.Width, DdsIo.Read(File.ReadAllBytes(dds)).Width);
            Assert.True(RawXt1.HasMagic(File.ReadAllBytes(xt1)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Dry-run validation must catch what the real write would skip, without writing.</summary>
    [Fact]
    public void TryValidate_CatchesAnOutOfRangePngMip()
    {
        if (!TestPaths.Exists(TestPaths.Em0000Dat)) return;
        var document = BankDocument.Load(TestPaths.Em0000Dat);
        var texture = document.AllTextures.FirstOrDefault(t => TextureExport.CanExport(t, TextureExportFormat.Png));
        if (texture is null) return;

        Assert.True(TextureExport.TryValidate(texture, TextureExportFormat.Png, out var okError), okError);
        Assert.False(TextureExport.TryValidate(texture, TextureExportFormat.Png, out var error, mip: 99));
        Assert.NotNull(error);
    }

    /// <summary>An unsupported format is reported by the validator with the same reason shape.</summary>
    [Fact]
    public void TryValidate_UnsupportedFormat_ReturnsReason()
    {
        var texture = EditableTexture.CreateNew(Xt1Format.AfL8Unorm, 4, 4);

        Assert.False(TextureExport.TryValidate(texture, TextureExportFormat.Dds, out var error));
        Assert.NotNull(error);
        Assert.True(TextureExport.TryValidate(texture, TextureExportFormat.Xt1, out _));
    }

    /// <summary>The atomic writer leaves no `.tmp` staging file behind.</summary>
    [Fact]
    public void FolderExporter_LeavesNoTempFiles()
    {
        if (!TestPaths.Exists(TestPaths.Em0000Dat)) return;
        var document = BankDocument.Load(TestPaths.Em0000Dat);
        var texture = document.AllTextures.FirstOrDefault(t => TextureExport.CanExport(t, TextureExportFormat.Png));
        if (texture is null) return;

        string root = TempDirectory();
        try
        {
            string source = Path.Combine(root, "src");
            Directory.CreateDirectory(source);
            string xt1 = Path.Combine(source, "loose.xt1");
            TextureExport.Write(texture, TextureExportFormat.Xt1, xt1);

            string output = Path.Combine(root, "out");
            var stats = new TextureFolderExporter().Run(new[] { xt1 },
                new FolderExportOptions(TextureExportFormat.Png, source, output, false),
                new NullProgress(), CancellationToken.None);

            Assert.Equal(1, stats.Written);
            Assert.Empty(Directory.GetFiles(output, "*.tmp*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CanExport_FollowsEachFormat()
    {
        // AF (0x01) has no DXGI/DDS mapping, but raw XT1 always applies.
        var texture = EditableTexture.CreateNew(Xt1Format.AfL8Unorm, 4, 4);

        Assert.False(TextureExport.CanExport(texture, TextureExportFormat.Dds));
        Assert.True(TextureExport.CanExport(texture, TextureExportFormat.Xt1));

        string dir = TempDirectory();
        try
        {
            string path = Path.Combine(dir, "af.xt1");
            Assert.True(TextureExport.TryWrite(texture, TextureExportFormat.Xt1, path, out var error), error);
            Assert.True(RawXt1.HasMagic(File.ReadAllBytes(path)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Flat mode dedupes same-named banks; per-bank mode keeps them separate.</summary>
    [Fact]
    public void FolderExporter_FlatDedupes_AndPerBankSeparates()
    {
        if (!TestPaths.Exists(TestPaths.Em0000Dat)) return;
        var document = BankDocument.Load(TestPaths.Em0000Dat);
        var texture = document.AllTextures.FirstOrDefault(t => TextureExport.CanExport(t, TextureExportFormat.Png));
        if (texture is null) return;

        string root = TempDirectory();
        try
        {
            string sourceA = Path.Combine(root, "src", "a");
            string sourceB = Path.Combine(root, "src", "b");
            Directory.CreateDirectory(sourceA);
            Directory.CreateDirectory(sourceB);
            string xt1A = Path.Combine(sourceA, "same.xt1");
            string xt1B = Path.Combine(sourceB, "same.xt1");
            TextureExport.Write(texture, TextureExportFormat.Xt1, xt1A);
            TextureExport.Write(texture, TextureExportFormat.Xt1, xt1B);
            var files = new[] { xt1A, xt1B };

            string flat = Path.Combine(root, "flat");
            var flatStats = new TextureFolderExporter().Run(files,
                new FolderExportOptions(TextureExportFormat.Png, Path.Combine(root, "src"), flat, false),
                new NullProgress(), CancellationToken.None);
            Assert.Equal(2, flatStats.Written);
            Assert.Equal(2, Directory.GetFiles(flat).Length);

            string perBank = Path.Combine(root, "perbank");
            var perBankStats = new TextureFolderExporter().Run(files,
                new FolderExportOptions(TextureExportFormat.Png, Path.Combine(root, "src"), perBank, true),
                new NullProgress(), CancellationToken.None);
            Assert.Equal(2, perBankStats.Written);
            // The source-relative a/b directories are mirrored, one bank subfolder inside each.
            Assert.True(Directory.Exists(Path.Combine(perBank, "a", "same")));
            Assert.True(Directory.Exists(Path.Combine(perBank, "b", "same")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A loose .xt1 has no WTB bank, but its one texture must still be exported.</summary>
    [Fact]
    public void FolderExporter_IncludesLooseXt1()
    {
        if (!TestPaths.Exists(TestPaths.Em0000Dat)) return;
        var document = BankDocument.Load(TestPaths.Em0000Dat);
        var texture = document.AllTextures.FirstOrDefault(t => TextureExport.CanExport(t, TextureExportFormat.Png));
        if (texture is null) return;

        string root = TempDirectory();
        try
        {
            string source = Path.Combine(root, "src");
            Directory.CreateDirectory(source);
            string xt1 = Path.Combine(source, "loose.xt1");
            TextureExport.Write(texture, TextureExportFormat.Xt1, xt1);

            string output = Path.Combine(root, "out");
            var stats = new TextureFolderExporter().Run(new[] { xt1 },
                new FolderExportOptions(TextureExportFormat.Png, source, output, false),
                new NullProgress(), CancellationToken.None);

            Assert.Equal(1, stats.Textures);
            Assert.Equal(1, stats.Written);
            Assert.Single(Directory.GetFiles(output, "*.png"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
