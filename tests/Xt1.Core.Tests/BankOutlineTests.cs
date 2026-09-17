using Xt1.Core.Containers;
using Xt1.Core.Editing;
using Xt1.Core.Format;
using Xt1.Core.Imaging;
using Xt1.Core.Xt1;
using Xunit;

namespace Xt1.Core.Tests;

/// <summary>
/// The container → sub-file → texture hierarchy and the bank selector labels. A `.dat` holding
/// several "wta" banks must expose one branch per bank so the UI can show them collapsibly.
/// </summary>
public class BankOutlineTests
{
    /// <summary>
    /// A container with several banks is labeled by the DAT sub-file name, which is why the tree
    /// shows "core.wta" / "messcore.wta" instead of "wta[7]" / "wta[8]".
    /// </summary>
    /// <summary>
    /// A DAT can carry banks under two tags: `"wta"` (payload from the paired `"wtp"`) and `"wtb"`
    /// (a standalone bank with its payload inline). `core.dat` + `core.dtt` hold all three of
    /// pre_texture.wta / CubeMap.wtb / Grading.wtb, and previously only the `"wta"` was found.
    /// </summary>
    [Fact]
    public void WtbSubFiles_AreExposedAsBanksAlongsideWta()
    {
        if (!TestPaths.Exists(TestPaths.CoreDat)) return;

        var document = BankDocument.Load(TestPaths.CoreDat);

        Assert.Equal(3, document.Sections.Count);
        Assert.Equal("pre_texture.wta", document.Sections[0].Label);
        Assert.Equal("CubeMap.wtb", document.Sections[1].Label);
        Assert.Equal("Grading.wtb", document.Sections[2].Label);

        // The "wta" bank pairs with the .dtt's "wtp"; the "wtb" banks carry their payload inline.
        Assert.NotNull(document.Sections[0].WtpSubFile);
        Assert.Null(document.Sections[0].WtbSubFile);
        for (int i = 1; i < 3; i++)
        {
            Assert.NotNull(document.Sections[i].WtbSubFile);
            Assert.Null(document.Sections[i].WtpSubFile);
            Assert.True(document.Sections[i].Bank.IsStandalone);
            Assert.True(document.Sections[i].PayloadStream.Length > 0,
                $"section {i} should expose the bank's inline payload");
        }

        Assert.Equal(40, document.Sections[0].Bank.Count);
        Assert.Equal(11, document.Sections[1].Bank.Count);
        Assert.Equal(3, document.Sections[2].Bank.Count);

        var labels = BankOutline.SelectorLabels(document);
        Assert.Equal("pre_texture.wta  (40)", labels[0]);
        Assert.Equal("CubeMap.wtb  (11)", labels[1]);
        Assert.Equal("Grading.wtb  (3)", labels[2]);

        // Every bank decodes.
        foreach (var texture in document.AllTextures)
        {
            if (!Core.Codecs.TextureCodec.CanDecode(texture.FormatInfo)) continue;
            var image = Core.Codecs.TextureCodec.Decode(texture, 0, 0, 0, out var error);
            Assert.NotNull(image);
            Assert.Null(error);
        }
    }

    [Fact]
    public void WtbOnlyContainer_Loads()
    {
        if (!TestPaths.Exists(TestPaths.Z5000Dtt)) return;

        var document = BankDocument.Load(TestPaths.Z5000Dtt);
        Assert.Equal(4, document.Sections.Count);
        Assert.Equal("z5000.wta", document.Sections[0].Label);
        Assert.Contains(document.Sections, s => s.Label == "CubeMap.wtb");
        Assert.Contains(document.Sections, s => s.Label == "Grading.wtb");
        Assert.Contains(document.Sections, s => s.Label == "FogLUT.wtb");
    }

    /// <summary>Texture leaves show the table3 hash after the index when the bank has one.</summary>
    [Fact]
    public void TextureLabel_IncludesTheTable3HashWhenPresent()
    {
        if (!TestPaths.Exists(TestPaths.CoreDat)) return;

        var document = BankDocument.Load(TestPaths.CoreDat);

        // Every core bank carries a table3, so check both label shapes on a hashed texture.
        var texture = document.Sections[0].Bank.Textures[0];
        Assert.True(document.Sections[0].Bank.IncludeTable3);

        var plain = BankOutline.TextureLabel(texture);
        var hashed = BankOutline.TextureLabel(texture, includeHash: true);

        Assert.StartsWith("#0  ", plain);
        Assert.DoesNotContain("0x", plain);
        Assert.StartsWith($"#0  0x{texture.EffectiveHash():X8}  ", hashed);
        Assert.Contains("BC1", hashed);
    }

    [Fact]
    public void NoEditSave_OfContainersWithWtbBanks_IsByteIdentical()
    {
        foreach (var path in new[] { TestPaths.CoreDat, TestPaths.Z5000Dtt })
        {
            if (!TestPaths.Exists(path)) continue;

            var document = BankDocument.Load(path);
            var result = BankWriter.Write(document, new WriteOptions());

            Assert.NotEmpty(result.Files);
            foreach (var file in result.Files)
            {
                if (!File.Exists(file.Path)) continue;
                Assert.True(File.ReadAllBytes(file.Path).AsSpan().SequenceEqual(file.Bytes),
                    $"{Path.GetFileName(file.Path)} changed on a no-edit save");
            }
        }
    }

    [Fact]
    public void MultiBankDat_IsLabeledBySubFileName()
    {
        if (!TestPaths.Exists(TestPaths.UiCoreDat)) return;

        var document = BankDocument.Load(TestPaths.UiCoreDat);

        Assert.Equal("core.wta", document.Sections[0].WtaSubFile!.Name);
        Assert.Equal("messcore.wta", document.Sections[1].WtaSubFile!.Name);
        Assert.Equal("core.wta", document.Sections[0].Label);
        Assert.Equal("messcore.wta", document.Sections[1].Label);

        var labels = BankOutline.SelectorLabels(document);
        Assert.Equal("core.wta  (10)", labels[0]);
        Assert.Equal("messcore.wta  (1)", labels[1]);
    }

    /// <summary>A single-bank container keeps the short label, but the name is still available.</summary>
    [Fact]
    public void SingleBankDat_KeepsShortLabelAndExposesTheName()
    {
        if (!TestPaths.Exists(TestPaths.Em0000Dat)) return;

        var document = BankDocument.Load(TestPaths.Em0000Dat);
        var section = Assert.Single(document.Sections);

        Assert.Equal("wta", section.Label);
        Assert.Equal("em0000.wta", section.WtaSubFile!.Name);
        Assert.Empty(BankOutline.SelectorLabels(document));
    }

    [Fact]
    public void MultiBankDat_ExposesOneBranchPerWtaSubFile()
    {
        if (!TestPaths.Exists(TestPaths.UiCoreDat)) return;

        var document = BankDocument.Load(TestPaths.UiCoreDat);
        Assert.Equal(BankKind.ModelDat, document.Kind);
        Assert.Equal(2, document.Sections.Count);

        var outline = BankOutline.Build(document);

        Assert.Equal(OutlineKind.Document, outline.Kind);
        Assert.Equal("ui_core.dat  (DAT)", outline.Label);

        // One branch per "wta" sub-file, each holding that bank's textures.
        Assert.Equal(2, outline.Children.Count);
        Assert.Equal("core.wta  (10 textures)", outline.Children[0].Label);
        Assert.Equal("messcore.wta  (1 texture)", outline.Children[1].Label);

        for (int i = 0; i < outline.Children.Count; i++)
        {
            var branch = outline.Children[i];
            Assert.Equal(OutlineKind.SubFile, branch.Kind);
            Assert.NotNull(branch.Section);
            Assert.Equal(document.Sections[i].Bank.Count, branch.Children.Count);

            int expectedIndex = 0;
            foreach (var leaf in branch.Children)
            {
                Assert.Equal(OutlineKind.Texture, leaf.Kind);
                Assert.NotNull(leaf.Texture);
                Assert.Same(branch.Section, leaf.Section);
                Assert.StartsWith($"#{expectedIndex}  ", leaf.Label);
                expectedIndex++;
            }
        }
    }

    [Fact]
    public void MultiBankDat_SelectorListsEveryBank()
    {
        if (!TestPaths.Exists(TestPaths.UiCoreDat)) return;

        var document = BankDocument.Load(TestPaths.UiCoreDat);
        var labels = BankOutline.SelectorLabels(document);

        Assert.Equal(2, labels.Count);
        Assert.Equal("core.wta  (10)", labels[0]);
        Assert.Equal("messcore.wta  (1)", labels[1]);
    }

    [Fact]
    public void SingleBankContainer_HasNoSelector()
    {
        if (!TestPaths.Exists(TestPaths.Em0000Dat)) return;

        var document = BankDocument.Load(TestPaths.Em0000Dat);
        Assert.Single(document.Sections);
        Assert.Empty(BankOutline.SelectorLabels(document));

        var outline = BankOutline.Build(document);
        Assert.Single(outline.Children);
        Assert.Equal("wta  (12 textures)", outline.Children[0].Label);
    }

    [Fact]
    public void StandaloneWtb_IsLabeledAsWtb()
    {
        if (!TestPaths.Exists(TestPaths.CubeMapWtb)) return;

        var document = BankDocument.Load(TestPaths.CubeMapWtb);
        var outline = BankOutline.Build(document);

        Assert.Equal("CubeMap.wtb  (WTB)", outline.Label);
        Assert.Single(outline.Children);
        Assert.Equal("wtb  (11 textures)", outline.Children[0].Label);
        Assert.Equal(11, outline.Children[0].Children.Count);

        // Cube textures are called out in the leaf label.
        Assert.Contains("cube", outline.Children[0].Children[0].Label);
    }

    [Fact]
    public void RawXt1_ShowsASingleContainerBranch()
    {
        var header = new Xt1Header
        {
            Magic = Xt1Header.MagicValue,
            Flags = Xt1Header.FlagsValue,
            HeaderSize = Xt1Header.Size,
            Layout2 = Xt1Header.Layout2Value,
            MipCount = 1,
            Width = 4,
            Height = 4,
            Depth = 1,
            Type = TextureType.T2D,
        };
        var raw = new RawXt1(header, new byte[64]);
        var texture = EditableTexture.CreateNew(Xt1Format.Rgba8Unorm, 4, 4);

        var outline = BankOutline.Build(DocumentForRaw(raw), texture);

        Assert.Equal(OutlineKind.Document, outline.Kind);
        Assert.True(outline.Label.EndsWith("(XT1)"), outline.Label);
        Assert.Single(outline.Children);
        Assert.Equal("xt1  (raw payload)", outline.Children[0].Label);
        Assert.Single(outline.Children[0].Children);
        Assert.Same(texture, outline.Children[0].Children[0].Texture);
    }

    [Fact]
    public void TextureLabel_IncludesFormatSizeMipsAndFlags()
    {
        var texture = EditableTexture.CreateNew(Xt1Format.Bc3Unorm, 512, 256);
        texture.Header.MipCount = 4;
        var label = BankOutline.TextureLabel(texture);

        Assert.Contains("BC3_UNORM", label);
        Assert.Contains("512x256", label);
        Assert.Contains("m4", label);
        Assert.StartsWith("#0", label);

        var volume = EditableTexture.CreateNew(Xt1Format.Rgba8Unorm, 16, 16);
        volume.Header.Type = TextureType.T3D;
        volume.Header.Depth = 32;
        Assert.Contains("16x16x32", BankOutline.TextureLabel(volume));

        var cube = EditableTexture.CreateNew(Xt1Format.Bc6hUf16, 64, 64);
        cube.Header.Type = TextureType.TCubeArray;
        Assert.Contains("cube", BankOutline.TextureLabel(cube));
    }

    /// <summary>Build a raw-XT1 document without touching disk.</summary>
    private static BankDocument DocumentForRaw(RawXt1 raw)
    {
        var path = Path.Combine(Path.GetTempPath(), "Xt1Studio", "outline", Guid.NewGuid().ToString("N") + ".xt1");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, raw.ToBytes());
        try
        {
            return BankDocument.Load(path);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
