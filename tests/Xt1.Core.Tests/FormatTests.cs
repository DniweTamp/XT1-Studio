using Xt1.Core.Containers;
using Xt1.Core.Format;
using Xt1.Core.Xt1;
using Xunit;

namespace Xt1.Core.Tests;

public class FormatTests
{
    [Theory]
    [InlineData("gallery_concept_205", 0x421E5ED4u)]
    [InlineData("archive_angel_mainpic_01", 0x3128CBEDu)]
    [InlineData("archive_angel_mainpic_02", 0x28219A57u)]
    [InlineData("archive_angel_footprintspic_01", 0x67E26B85u)]
    [InlineData("archive_beast_mainpic_01", 0x20207052u)]
    [InlineData("alfheim_img_001", 0x1FE67C86u)]
    public void NameHash_MatchesDocumentedPairs(string name, uint expected)
    {
        Assert.Equal(expected, Crc32NameHash.ComputeMasked(name));
    }

    [Fact]
    public void AstcFormats_AreOrderedAndPaired()
    {
        for (int i = 0; i < FormatTable.AstcFootprintNames.Length; i++)
        {
            uint unorm = (uint)(0x79 + i);
            uint srgb = (uint)(0x87 + i);
            Assert.True(FormatTable.TryGet(unorm, out var u));
            Assert.True(FormatTable.TryGet(srgb, out var s));
            Assert.Equal(FormatTable.AstcFootprintNames[i], u.Name.Replace("ASTC_LDR_", "").Replace("_UNORM", ""));
            Assert.False(u.IsSrgb);
            Assert.True(s.IsSrgb);
            Assert.Equal(16, u.BytesPerBlock);
            Assert.Equal(i, u.AstcFootprintIndex);
            Assert.Equal(i, s.AstcFootprintIndex);
        }
    }

    [Theory]
    [InlineData(0x79u, 4, 4)]
    [InlineData(0x7du, 6, 6)]
    [InlineData(0x80u, 8, 8)]
    [InlineData(0x86u, 12, 12)]
    [InlineData(0x94u, 12, 12)]
    public void AstcFootprintGeometry(uint value, int bw, int bh)
    {
        Assert.True(FormatTable.TryGet(value, out var info));
        Assert.Equal(bw, info.BlockWidth);
        Assert.Equal(bh, info.BlockHeight);
    }

    [Fact]
    public void CoreFormats_HaveExpectedGeometry()
    {
        Assert.Equal(1, FormatTable.Get(0x01)!.BytesPerBlock);
        Assert.Equal(1, FormatTable.Get(0x01)!.BlockWidth);
        Assert.Equal(4, FormatTable.Get(0x25)!.BytesPerBlock);
        Assert.Equal(4, FormatTable.Get(0x38)!.BytesPerBlock);
        Assert.Equal(8, FormatTable.Get(0x42)!.BytesPerBlock);
        Assert.Equal(16, FormatTable.Get(0x44)!.BytesPerBlock);
        Assert.Equal(16, FormatTable.Get(0x50)!.BytesPerBlock);
        Assert.Equal(CodecKind.Raw, FormatTable.Get(0x01)!.Codec);
        // 0x6D is uncompressed 32bpp too, but its channel order is unconfirmed: decode only.
        Assert.Equal(CodecKind.Raw, FormatTable.Get(0x6d)!.Codec);
        Assert.Equal(4, FormatTable.Get(0x6d)!.BytesPerBlock);
        Assert.True(Core.Codecs.TextureCodec.CanDecode(FormatTable.Get(0x6d)));
        Assert.False(Core.Codecs.TextureCodec.CanEncode(FormatTable.Get(0x6d)));
    }

    [Theory]
    [InlineData(0x20000060u, true, false, false)]  // model bank, linear 2D
    [InlineData(0x20000064u, true, true, false)]   // model, sRGB
    [InlineData(0x20000068u, true, false, true)]   // model, compressed (af)
    [InlineData(0x200000E0u, true, false, false)]  // cube usage
    [InlineData(0x00000060u, false, false, false)] // effect bank
    public void Table2Flags_DecodeDocumentedBits(uint beWord, bool model, bool srgb, bool compressed)
    {
        var table2 = new byte[4];
        Util.Mem.WriteU32BE(table2, 0, beWord);
        var header = new Xt1Header { Magic = Xt1Header.MagicValue, Flags = Xt1Header.FlagsValue, HeaderSize = Xt1Header.Size, Layout2 = Xt1Header.Layout2Value };
        var tex = new Containers.EditableTexture(header, table2, 0, 0, 0, Array.Empty<byte>());

        Assert.Equal(beWord, tex.Table2WordBE);
        Assert.Equal(model, tex.IsModelBankClass);
        Assert.Equal(srgb, tex.IsSrgbFlag);
        Assert.Equal(compressed, tex.IsCompressedFlag);
        Assert.Equal((beWord & 0x80) != 0, tex.IsCubeUsageFlag);
    }

    [Fact]
    public void HashMode_ForcedValueIsNeverRecomputed()
    {
        var header = new Xt1Header { Magic = Xt1Header.MagicValue, Flags = Xt1Header.FlagsValue, HeaderSize = Xt1Header.Size, Layout2 = Xt1Header.Layout2Value };
        var tex = new Containers.EditableTexture(header, new byte[4], 0xDEADBEEF, 0, 0, Array.Empty<byte>(), "some_name")
        {
            HashMode = HashMode.Forced,
        };
        Assert.Equal(0xDEADBEEFu, tex.EffectiveHash());

        tex.HashMode = HashMode.AutoFromName;
        Assert.Equal(Crc32NameHash.ComputeMasked("some_name"), tex.EffectiveHash());
    }
}
