using Xt1.Core.Containers;
using Xt1.Core.Editing;
using Xt1.Core.Encoding;
using Xt1.Core.Format;
using Xt1.Core.Imaging;
using Xt1.Core.Xt1;
using Xunit;

namespace Xt1.Core.Tests;

/// <summary>
/// Planning for the bank "batch replace from folder" command: matching is by the exported
/// <c>tex&lt;index&gt;_0x&lt;hash&gt;</c> name, one folder only, no file is read.
/// </summary>
public class BatchReplaceTests
{
    private static EditableTexture Texture(int index, uint hash, Xt1Format format = Xt1Format.Bc7Unorm)
    {
        var texture = EditableTexture.CreateNew(format, 16, 16);
        texture.Index = index;
        texture.Hash = hash;
        texture.HashMode = HashMode.Forced;
        return texture;
    }

    private static string NewFolder()
    {
        string path = Path.Combine(Path.GetTempPath(), "Xt1ToolTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Touch(string folder, string name) =>
        File.WriteAllBytes(Path.Combine(folder, name), new byte[] { 0 });

    [Fact]
    public void MatchesByIndexAndHash()
    {
        var folder = NewFolder();
        try
        {
            Touch(folder, "tex3_0xABCD1234.png");
            Touch(folder, "tex4_0x00000000.dds");

            var plan = TextureBatchReplacer.BuildPlan(new[] { Texture(3, 0xABCD1234), Texture(4, 0) }, folder);

            Assert.Equal(2, plan.Matched);
            Assert.Equal(BatchReplaceKind.Image, plan.Entries[0].Kind);
            Assert.Equal(BatchReplaceKind.Dds, plan.Entries[1].Kind);
            Assert.Empty(plan.UnusedFiles);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void PrecedencePrefersXt1ThenDds()
    {
        var folder = NewFolder();
        try
        {
            Touch(folder, "tex1_0xAAAAAAAA.png");
            Touch(folder, "tex1_0xAAAAAAAA.dds");
            Touch(folder, "tex1_0xAAAAAAAA.xt1");

            var plan = TextureBatchReplacer.BuildPlan(new[] { Texture(1, 0xAAAAAAAA) }, folder);

            Assert.Equal(1, plan.Matched);
            Assert.Equal(BatchReplaceKind.Xt1, plan.Entries[0].Kind);
            Assert.EndsWith(".xt1", plan.Entries[0].File);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void EquallyPreferredFilesAreAmbiguous()
    {
        var folder = NewFolder();
        try
        {
            Touch(folder, "tex1_0xAAAAAAAA.png");
            Touch(folder, "copy_tex1_0xAAAAAAAA.png");

            var plan = TextureBatchReplacer.BuildPlan(new[] { Texture(1, 0xAAAAAAAA) }, folder);

            Assert.Equal(BatchReplaceStatus.Ambiguous, plan.Entries[0].Status);
            Assert.Equal(0, plan.Matched);
            // Both candidates belong to the texture, so neither is an unused file.
            Assert.Empty(plan.UnusedFiles);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void MissingTextureAndUnusedFile()
    {
        var folder = NewFolder();
        try
        {
            Touch(folder, "tex9_0xDEADBEEF.png"); // matches no texture
            Touch(folder, "notes.txt");           // not an accepted extension

            var plan = TextureBatchReplacer.BuildPlan(new[] { Texture(1, 0xAAAAAAAA) }, folder);

            Assert.Equal(BatchReplaceStatus.Missing, plan.Entries[0].Status);
            Assert.Single(plan.UnusedFiles);
            Assert.EndsWith("tex9_0xDEADBEEF.png", plan.UnusedFiles[0]);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void WrongHashDoesNotMatch()
    {
        var folder = NewFolder();
        try
        {
            Touch(folder, "tex1_0xBBBBBBBB.png"); // right index, wrong hash

            var plan = TextureBatchReplacer.BuildPlan(new[] { Texture(1, 0xAAAAAAAA) }, folder);

            Assert.Equal(BatchReplaceStatus.Missing, plan.Entries[0].Status);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void SubfoldersAreIgnored()
    {
        var folder = NewFolder();
        try
        {
            var sub = Path.Combine(folder, "bank");
            Directory.CreateDirectory(sub);
            File.WriteAllBytes(Path.Combine(sub, "tex1_0xAAAAAAAA.png"), new byte[] { 0 });

            var plan = TextureBatchReplacer.BuildPlan(new[] { Texture(1, 0xAAAAAAAA) }, folder);

            Assert.Equal(BatchReplaceStatus.Missing, plan.Entries[0].Status);
            Assert.Empty(plan.UnusedFiles);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Non2DTexturesAreUnsupported()
    {
        var folder = NewFolder();
        try
        {
            var volume = Texture(1, 0xAAAAAAAA);
            volume.Header.Type = TextureType.T3D;

            var plan = TextureBatchReplacer.BuildPlan(new[] { volume }, folder);

            Assert.Equal(BatchReplaceStatus.Unsupported, plan.Entries[0].Status);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void ApplyReplacesAtImageSizeAndUndoesAsOneStep()
    {
        var folder = NewFolder();
        try
        {
            var texture = Texture(1, 0xAAAAAAAA); // Bc7Unorm, 16x16, 1 mip

            // A different-sized image: the texture must adopt the image's dimensions.
            var image = new RgbaImage(32, 24);
            for (int i = 0; i < image.Pixels.Length; i++) image.Pixels[i] = 200;
            ImageIo.Save(image, Path.Combine(folder, "tex1_0xAAAAAAAA.png"));

            var plan = TextureBatchReplacer.BuildPlan(new[] { texture }, folder);
            Assert.Equal(1, plan.Matched);

            var replacer = new TextureBatchReplacer(new EncoderPicker(nvtt: null, cli: null));
            var result = replacer.Apply(plan, new BatchReplaceOptions(EncodeQuality.Fastest, false, false),
                new Progress<string>(), CancellationToken.None);

            Assert.Equal(1, result.Stats.Replaced);
            Assert.Single(result.Commands);
            Assert.Equal(32u, texture.Header.Width);
            Assert.Equal(24u, texture.Header.Height);
            Assert.Equal(1, texture.Header.MipCount); // clamped no lower than the texture's 1

            // The whole run is a single undo.
            new CompositeCommand("test", result.Commands).Undo();
            Assert.Equal(16u, texture.Header.Width);
            Assert.Equal(16u, texture.Header.Height);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void RawOnlyMatchesNeedNoReencodeOptions()
    {
        var folder = NewFolder();
        try
        {
            Touch(folder, "tex1_0xAAAAAAAA.xt1");

            var plan = TextureBatchReplacer.BuildPlan(new[] { Texture(1, 0xAAAAAAAA) }, folder);

            Assert.True(plan.Matched == 1);
            Assert.False(plan.HasNonXt1);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
