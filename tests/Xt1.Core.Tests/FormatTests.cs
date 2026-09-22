using Xt1.Core.Codecs;
using Xt1.Core.Containers;
using Xt1.Core.Encoding;
using Xt1.Core.Format;
using Xt1.Core.Imaging;
using Xt1.Core.Nvtt;
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

    /// <summary>
    /// BC7 is stored under 0x4D/0x4E, the ids Bayonetta 3 mods use. Round-trip testing confirms these
    /// are standard BC7 blocks, so they decode and re-encode as BC7.
    /// </summary>
    [Theory]
    [InlineData(0x4Du, false)]
    [InlineData(0x4Eu, true)]
    public void Bc7Ids_DecodeAndEncodeAsBc7(uint value, bool srgb)
    {
        Assert.True(FormatTable.TryGet(value, out var info));
        Assert.False(info.IsAlias);
        Assert.Equal(CodecKind.Bc, info.Codec);
        Assert.Equal(srgb, info.IsSrgb);
        Assert.Equal(16, info.BytesPerBlock);
        Assert.Equal(4, info.BlockWidth);
        Assert.True(TextureCodec.CanDecode(info));
        Assert.True(TextureCodec.CanEncode(info));
        Assert.True(ManagedFormatMap.TryGetDecodeFormat(info, out var compression));
        Assert.Equal("Bc7", compression.ToString());
        Assert.True(ManagedFormatMap.TryGetEncodeFormat(info, out var encodeCompression));
        Assert.Equal("Bc7", encodeCompression.ToString());
    }

    /// <summary>
    /// Aliases are declared once (FormatTable.AliasOf) and every capability/DXGI table follows the
    /// canonical entry. This guards the drift the tables used to allow when each listed aliases
    /// itself.
    /// </summary>
    [Fact]
    public void Aliases_ResolveToCanonicalAndStayConsistent()
    {
        var aliases = FormatTable.Aliases.ToList();
        Assert.NotEmpty(aliases);

        foreach (var alias in aliases)
        {
            var canonical = FormatTable.Get(alias.AliasOf!.Value);
            Assert.NotNull(canonical);
            Assert.False(canonical!.IsAlias, "aliases must point at a canonical entry, not another alias");
            Assert.Equal(canonical.Codec, alias.Codec);
            Assert.Equal(canonical.BytesPerBlock, alias.BytesPerBlock);
            Assert.Equal(canonical.BlockWidth, alias.BlockWidth);
            Assert.Equal(canonical.BlockHeight, alias.BlockHeight);
            Assert.Equal(canonical.IsSrgb, alias.IsSrgb);
            Assert.Equal(canonical.Name, alias.Name);

            Assert.Equal(ManagedFormatMap.SupportsDecode(canonical), ManagedFormatMap.SupportsDecode(alias));
            Assert.Equal(ManagedFormatMap.SupportsEncode(canonical), ManagedFormatMap.SupportsEncode(alias));
            Assert.Equal(NvttFormatMap.ToNvtt(canonical.Format), NvttFormatMap.ToNvtt(alias.Format));
            Assert.Equal(NvttFormatMap.ToCliFormatName(canonical.Format),
                NvttFormatMap.ToCliFormatName(alias.Format));

            Assert.True(DdsIo.TryGetDxgi(canonical, out uint canonicalDxgi));
            Assert.True(DdsIo.TryGetDxgi(alias, out uint aliasDxgi));
            Assert.Equal(canonicalDxgi, aliasDxgi);
        }
    }

    [Fact]
    public void Bc7_IsTheOnlyBc7TargetFormat()
    {
        var bc7 = FormatTable.All.Where(f => f.Name.StartsWith("BC7", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, bc7.Count);
        Assert.Contains(bc7, f => f.Format == Xt1Format.Bc7Unorm);
        Assert.Contains(bc7, f => f.Format == Xt1Format.Bc7Srgb);
        Assert.Equal("BC7_UNORM (0x4D)", FormatTable.Get(Xt1Format.Bc7Unorm)!.DisplayName);
        Assert.Equal("BC7_SRGB (0x4E)", FormatTable.Get(Xt1Format.Bc7Srgb)!.DisplayName);
    }

    /// <summary>
    /// 0x51/0x52 were listed as BC7 by other tools but testing shows they are a different format, so
    /// the tool must not silently treat them as BC7 or offer them as encode targets.
    /// </summary>
    [Fact]
    public void Bc7LegacyIds_AreNotMapped()
    {
        Assert.Null(FormatTable.Get(0x51u));
        Assert.Null(FormatTable.Get(0x52u));
    }

    [Fact]
    public void Bc7_RoundTripsThroughItsRealId()
    {
        var format = FormatTable.Get(Xt1Format.Bc7Unorm)!;

        var image = new RgbaImage(32, 32);
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
                image.SetPixel(x, y, (byte)(x * 8), (byte)(y * 8), (byte)((x + y) * 4), 255);

        var encoded = new ManagedEncoder().Encode(image, format, srgb: false, EncodeQuality.Normal);
        Assert.True(encoded.Success, encoded.Error);

        var decoded = TextureCodec.DecodeLinear(encoded.Data, 32, 32, format, out _);
        Assert.NotNull(decoded);
    }

    [Fact]
    public void Bc7_ExportsToDds()
    {
        Assert.True(DdsIo.CanExport(FormatTable.Get(Xt1Format.Bc7Unorm)!));
        Assert.True(DdsIo.CanExport(FormatTable.Get(Xt1Format.Bc7Srgb)!));
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
