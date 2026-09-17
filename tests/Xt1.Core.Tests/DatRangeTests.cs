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
    public void GrownSubFile_RepacksInsteadOfOverwritingItsNeighbour()
    {
        if (!TestPaths.Exists(TestPaths.Em0000Dat)) return;

        var dat = DatFile.Parse(File.ReadAllBytes(TestPaths.Em0000Dat));

        // Find a sub-file whose original range is followed immediately by another (no slack), so
        // growing it is guaranteed to run into the neighbour if the layout is not repacked.
        int index = -1;
        for (int i = 0; i < dat.SubFiles.Count && index < 0; i++)
        {
            long end = (long)dat.SubFiles[i].OriginalOffset + dat.SubFiles[i].OriginalLength;
            for (int j = 0; j < dat.SubFiles.Count; j++)
            {
                if (j == i || dat.SubFiles[j].OriginalOffset != end) continue;
                index = i;
                break;
            }
        }
        Assert.True(index >= 0, "expected a zero-slack sub-file pair in em0000.dat to grow");

        var target = dat.SubFiles[index];
        var grown = new byte[target.Bytes.Length + 64];
        target.Bytes.CopyTo(grown, 0);
        for (int i = target.Bytes.Length; i < grown.Length; i++) grown[i] = 0xAB;
        target.Bytes = grown;

        Assert.True(dat.IsStructureDirty, "outgrowing the original slot must be treated as structural");

        var rebuilt = DatFile.Parse(dat.ToBytes());
        Assert.Equal(dat.SubFiles.Count, rebuilt.SubFiles.Count);
        for (int i = 0; i < dat.SubFiles.Count; i++)
        {
            Assert.True(dat.SubFiles[i].Bytes.AsSpan().SequenceEqual(rebuilt.SubFiles[i].Bytes),
                $"sub-file {i} (\"{dat.SubFiles[i].Tag}\") was overwritten by a neighbour");
        }
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

    [Fact]
    public void GrownSubFile_ForcesAStructuralRebuildAndKeepsNeighboursIntact()
    {
        var dat = DatFile.Parse(BuildTwoSubFileDat());
        Assert.False(dat.IsStructureDirty);

        var target = dat.SubFiles[0];
        var grown = new byte[target.Bytes.Length + 32];
        target.Bytes.CopyTo(grown, 0);
        for (int i = target.Bytes.Length; i < grown.Length; i++) grown[i] = 0xAB;
        target.Bytes = grown;

        Assert.True(dat.IsStructureDirty, "growing a sub-file must mark the container structural");

        var rebuilt = DatFile.Parse(dat.ToBytes());
        Assert.Equal(2, rebuilt.SubFiles.Count);
        Assert.Equal(grown.Length, rebuilt.SubFiles[0].Bytes.Length);
        Assert.True(grown.AsSpan().SequenceEqual(rebuilt.SubFiles[0].Bytes));
        Assert.True(dat.SubFiles[1].Bytes.AsSpan().SequenceEqual(rebuilt.SubFiles[1].Bytes),
            "the following sub-file must not be overwritten by the grown one");
    }

    [Fact]
    public void GrownSubFile_WithPinnedManualOffsets_IsRefusedInsteadOfOverlapping()
    {
        var dat = DatFile.Parse(BuildTwoSubFileDat());
        dat.RecomputeOffsetsOnWrite = false;

        var target = dat.SubFiles[0];
        var grown = new byte[target.Bytes.Length + 32];
        target.Bytes.CopyTo(grown, 0);
        target.Bytes = grown;

        var ex = Assert.Throws<InvalidOperationException>(() => dat.ToBytes());
        Assert.Contains("outgrew", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Two contiguous 16-byte sub-files with no slack, so growth must repack or overlap.</summary>
    private static byte[] BuildTwoSubFileDat()
    {
        const int n = 2;
        const int slot = 8;
        int pOffsets = 0x20;
        int pTags = pOffsets + 4 * n;
        int pNames = pTags + 4 * n;
        int pDesc = Align4(pNames + 4 + slot * n);
        int pExtra = Align4(pDesc + 4 * n);
        uint baseOffset = (uint)pExtra;
        var output = new byte[baseOffset + 32];

        U32(output, 0x00, DatFile.MagicValue);
        U32(output, 0x04, n);
        U32(output, 0x08, (uint)pOffsets);
        U32(output, 0x0C, (uint)pTags);
        U32(output, 0x10, (uint)pNames);
        U32(output, 0x14, (uint)pDesc);
        U32(output, 0x18, (uint)pExtra);

        U32(output, pOffsets, baseOffset);
        U32(output, pOffsets + 4, baseOffset + 16);

        WriteTag(output, pTags, "wta");
        WriteTag(output, pTags + 4, "wtp");

        U32(output, pNames, slot);
        WriteName(output, pNames + 4, "first", slot);
        WriteName(output, pNames + 4 + slot, "second", slot);

        U32(output, pDesc, 16);
        U32(output, pDesc + 4, 16);

        for (int i = 0; i < 16; i++)
        {
            output[baseOffset + i] = (byte)(i + 1);
            output[baseOffset + 16 + i] = (byte)(0x80 + i);
        }
        return output;
    }

    private static int Align4(int value) => (value + 3) & ~3;

    private static void U32(byte[] b, int at, uint value)
    {
        b[at] = (byte)value;
        b[at + 1] = (byte)(value >> 8);
        b[at + 2] = (byte)(value >> 16);
        b[at + 3] = (byte)(value >> 24);
    }

    private static void WriteTag(byte[] b, int at, string tag)
    {
        for (int i = 0; i < 4; i++) b[at + i] = i < tag.Length ? (byte)tag[i] : (byte)0;
    }

    private static void WriteName(byte[] b, int at, string name, int slot)
    {
        for (int i = 0; i < slot; i++) b[at + i] = i < name.Length ? (byte)name[i] : (byte)0;
    }
}
