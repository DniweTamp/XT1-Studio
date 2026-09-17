using Xt1.Core.Codecs;
using Xt1.Core.Containers;
using Xt1.Core.Xt1;
using Xunit;

namespace Xt1.Core.Tests;

/// <summary>
/// Sub-file ranges in a DAT. The offset table is not always monotonic: stub entries are 0 and some
/// sub-files alias a region nested inside another, so the declared descriptor size must be used
/// when it fits. Getting this wrong silently turned real texture banks into zero-length stubs.
/// </summary>
public class DatRangeTests
{
    [Fact]
    public void AliasedOffsets_StillExposeTheWtaTextureBank()
    {
        if (!TestPaths.Exists(TestPaths.Es0001Dat)) return;

        var dat = DatFile.Parse(File.ReadAllBytes(TestPaths.Es0001Dat));

        // The "wta" entry's data really is a WTB even though the offset table is not sorted.
        var wta = dat.FindByTag("wta");
        Assert.NotNull(wta);
        Assert.True(WtbBank.HasMagic(wta!.Bytes), "the wta sub-file must be a WTB bank");
        Assert.True(wta.Bytes.Length > 100);

        // ... and it loads end to end, using the sibling .dtt for the payload.
        var document = BankDocument.Load(TestPaths.Es0001Dat);
        Assert.Equal(BankKind.ModelDat, document.Kind);
        var section = Assert.Single(document.Sections);
        Assert.Equal(5, section.Bank.Count);
        Assert.NotNull(section.WtpSubFile);
        Assert.True(section.PayloadStream.Length > 0, "payload must come from the sibling .dtt");

        foreach (var texture in section.Bank.Textures)
        {
            var image = TextureCodec.Decode(texture, 0, 0, 0, out var error);
            Assert.NotNull(image);
            Assert.Null(error);
            Assert.Equal((int)texture.Header.Width, image!.Width);
            Assert.Equal((int)texture.Header.Height, image.Height);
        }
    }

    [Fact]
    public void StubOffsets_ProduceEmptyRangesRatherThanNegativeOnes()
    {
        if (!TestPaths.Exists(TestPaths.Es0001Dat)) return;
        var dat = DatFile.Parse(File.ReadAllBytes(TestPaths.Es0001Dat));

        // "hkx" / "eff" carry offset 0 with no declared size; they must not read past their region
        // or throw, and every sub-file must stay inside the file.
        long total = new FileInfo(TestPaths.Es0001Dat).Length;
        foreach (var subFile in dat.SubFiles)
        {
            Assert.True(subFile.Offset <= total);
            Assert.True(subFile.Bytes.LongLength <= total);
            Assert.True(subFile.Bytes.LongLength <= Math.Max(0, total - subFile.Offset));
        }
    }

    [Fact]
    public void UnmodifiedContainer_ToBytesReturnsTheOriginalBytes()
    {
        foreach (var path in new[] { TestPaths.Pl0080Dat, TestPaths.Em0000Dat, TestPaths.UiCoreDat, TestPaths.Es0001Dat })
        {
            if (!TestPaths.Exists(path)) continue;

            var original = File.ReadAllBytes(path);
            var dat = DatFile.Parse(original);

            Assert.False(dat.IsDirty);
            Assert.False(dat.IsStructureDirty);

            // Clean containers are never repacked, which keeps aliased/stub layouts intact.
            Assert.True(original.AsSpan().SequenceEqual(dat.ToBytes()),
                $"{Path.GetFileName(path)} changed on a clean save");
        }
    }

    [Fact]
    public void StructuralEdit_MarksTheContainerDirty()
    {
        if (!TestPaths.Exists(TestPaths.Pl0080Dat)) return;

        var dat = DatFile.Parse(File.ReadAllBytes(TestPaths.Pl0080Dat));
        Assert.False(dat.IsDirty);

        dat.SubFiles[0].Name = dat.SubFiles[0].Name + "_renamed";
        Assert.True(dat.IsStructureDirty);
        Assert.True(dat.IsDirty);

        var rebuilt = dat.ToBytes();
        Assert.False(rebuilt.AsSpan().SequenceEqual(File.ReadAllBytes(TestPaths.Pl0080Dat)));
        Assert.Contains(DatFile.Parse(rebuilt).SubFiles, s => s.Name.EndsWith("_renamed"));
    }

    [Fact]
    public void SimpleContainers_RebuildByteIdentically()
    {
        // These have plain monotonic layouts, so the rebuild path must still round-trip exactly.
        foreach (var path in new[] { TestPaths.Pl0000Dtt, TestPaths.Pl0080Dat, TestPaths.Em0000Dat })
        {
            if (!TestPaths.Exists(path)) continue;
            var original = File.ReadAllBytes(path);
            var rebuilt = DatFile.Parse(original).ToBytesForced();
            Assert.True(original.AsSpan().SequenceEqual(rebuilt),
                $"{Path.GetFileName(path)} rebuild is not byte-identical");
        }
    }
}
