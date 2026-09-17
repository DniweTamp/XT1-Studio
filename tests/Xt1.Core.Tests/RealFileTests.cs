using Xt1.Core.Codecs;
using Xt1.Core.Containers;
using Xt1.Core.Format;
using Xt1.Core.Xt1;
using Xunit;

namespace Xt1.Core.Tests;

/// <summary>
/// End-to-end checks against the real Bayonetta 3 files. Tests are no-ops when the romfs
/// directory is absent so the suite still runs on a clean machine.
/// </summary>
public class RealFileTests
{
    private static bool RomfsAvailable => Directory.Exists(TestPaths.Romfs);

    private static void AssertInvariants(EditableTexture tex)
    {
        var h = tex.Header;
        Assert.Equal(Xt1Header.MagicValue, h.Magic);
        Assert.Equal(Xt1Header.FlagsValue, h.Flags);
        Assert.Equal((uint)Xt1Header.Size, h.HeaderSize);
        Assert.Equal(Xt1Header.Layout2Value, h.Layout2);
        Assert.True(h.MipCount >= 1);
        Assert.True(h.Width >= 1);
        Assert.True(h.Height >= 1);
    }

    private static bool IsLayoutExplained(LayoutResult layout) =>
        // The wide-pitch and tiny-cube rules are exact, so every applicable record must now match
        // its stored imageSize; a recovered single-mip pitch also counts as explained.
        layout.SizeMatches || layout.PitchRecoveredFromStoredSize;

    [Fact]
    public void LoosePlDat_AllRecordsSatisfyInvariants()
    {
        if (!RomfsAvailable) return;
        var dir = Path.Combine(TestPaths.Romfs, "pl");
        if (!Directory.Exists(dir)) return;

        int records = 0;
        foreach (var path in Directory.GetFiles(dir, "*.dat"))
        {
            var dat = DatFile.Parse(File.ReadAllBytes(path));
            foreach (var wta in dat.FindAllByTag("wta"))
            {
                if (!WtbBank.HasMagic(wta.Bytes)) continue;
                var bank = WtbBank.Parse(wta.Bytes);
                foreach (var tex in bank.Textures)
                {
                    AssertInvariants(tex);
                    var layout = tex.Layout;
                    if (layout.Applicable)
                        Assert.True(IsLayoutExplained(layout),
                            $"{Path.GetFileName(path)}: unexplained imageSize mismatch ({layout.Note})");
                    records++;
                }
            }
        }

        Assert.True(records > 100, $"expected a large sample, saw {records}");
    }

    [Fact]
    public void Pl0080_Astc6x6WidePitch_LoadsAndStillDecodesBaseMip()
    {
        if (!TestPaths.Exists(TestPaths.Pl0080Dat)) return;
        var document = BankDocument.Load(TestPaths.Pl0080Dat);
        var section = Assert.Single(document.Sections);
        Assert.Equal(11, section.Bank.Count);

        var anomalous = section.Bank.Textures.Where(t => t.FormatInfo?.AstcFootprintIndex == 4).ToList();
        Assert.NotEmpty(anomalous);
        Assert.All(anomalous, t => Assert.True(t.Layout.SizeMatches, t.Layout.Note));

        var image = TextureCodec.Decode(anomalous[0], 0, 0, 0, out var error);
        Assert.NotNull(image);
        Assert.Equal(1024, image!.Width);
        Assert.Equal(1024, image.Height);
    }

    [Fact]
    public void Em0000_LoadsTwelveTextures_AndDecodesOne()
    {
        if (!TestPaths.Exists(TestPaths.Em0000Dat)) return;
        var document = BankDocument.Load(TestPaths.Em0000Dat);
        var section = Assert.Single(document.Sections);
        Assert.Equal(12, section.Bank.Count);

        int decoded = 0;
        foreach (var tex in section.Bank.Textures)
        {
            if (!TextureCodec.CanDecode(tex.FormatInfo)) continue;
            var image = TextureCodec.Decode(tex, 0, 0, 0, out _);
            if (image is null) continue;
            Assert.Equal((int)tex.Header.Width, image.Width);
            Assert.Equal((int)tex.Header.Height, image.Height);
            decoded++;
        }
        Assert.True(decoded > 0, "expected at least one decodable texture");
    }

    [Fact]
    public void UiCore_HasTwoWtaSections()
    {
        if (!TestPaths.Exists(TestPaths.UiCoreDat)) return;
        var document = BankDocument.Load(TestPaths.UiCoreDat);
        Assert.Equal(2, document.Sections.Count);
        Assert.Equal("core.wta", document.Sections[0].Label);
        Assert.Equal("messcore.wta", document.Sections[1].Label);
        Assert.Equal(10, document.Sections[0].Bank.Count);
    }

    [Fact]
    public void CoreDat_ThreeDimensionalLayoutMatches()
    {
        if (!TestPaths.Exists(TestPaths.CoreDat)) return;
        var document = BankDocument.Load(TestPaths.CoreDat);
        var textures = document.AllTextures.Where(t => t.Header.Type == TextureType.T3D).ToList();
        Assert.NotEmpty(textures);

        foreach (var tex in textures)
        {
            var layout = tex.Layout;
            Assert.True(layout.Applicable);
            Assert.True(layout.SizeMatches, $"3D {tex.Header.Width}x{tex.Header.Height}x{tex.Header.Depth}: {layout.Note}");
        }

        // core.dat holds two 0x6D volumes; they are uncompressed 32bpp and decode for viewing.
        var unknown = textures.Where(t => t.FormatInfo?.Format == Xt1Format.Unknown6d).ToList();
        Assert.Equal(2, unknown.Count);
        foreach (var tex in unknown)
        {
            Assert.True(Core.Codecs.TextureCodec.CanDecode(tex.FormatInfo));
            var slice = Core.Codecs.TextureCodec.Decode(tex, 0, 0, 0, out var error);
            Assert.NotNull(slice);
            Assert.Null(error);
            Assert.Equal(16, slice!.Width);
            Assert.Equal(16, slice.Height);
        }
    }

    [Fact]
    public void CubeMapWtb_LoadsElevenCubeTextures()
    {
        if (!TestPaths.Exists(TestPaths.CubeMapWtb)) return;
        var document = BankDocument.Load(TestPaths.CubeMapWtb);
        Assert.Equal(BankKind.StandaloneWtb, document.Kind);
        var section = Assert.Single(document.Sections);
        Assert.Equal(11, section.Bank.Count);
        foreach (var tex in section.Bank.Textures)
        {
            Assert.True(tex.Header.Type.IsCube());
            Assert.Equal(6, tex.Layout.FaceCount);
        }
    }

    [Fact]
    public void DatNoEditRoundTrip_PreservesAllSubFileBytes()
    {
        foreach (var path in new[] { TestPaths.Pl0080Dat, TestPaths.Em0000Dat })
        {
            if (!TestPaths.Exists(path)) continue;
            var original = File.ReadAllBytes(path);
            var dat = DatFile.Parse(original);
            var rebuilt = dat.ToBytes();
            Assert.True(original.AsSpan().SequenceEqual(rebuilt), $"{Path.GetFileName(path)} changed");
        }
    }

    /// <summary>
    /// The extracted effect textures are split banks: `NNN.wta` (WTB) + `NNN.wtp` (payload). The
    /// loader must pair them, attach the payloads and decode.
    /// </summary>
    [Fact]
    public void EffectWta_RealFiles_LoadAsSplitBanksWithDecodablePayloads()
    {
        var directory = TestPaths.EffectTextureDirectory;
        if (!Directory.Exists(directory)) return;

        var files = Directory.GetFiles(directory, "*.wta")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.NotEmpty(files);

        int textures = 0, decoded = 0, unsupported = 0;
        foreach (var path in files)
        {
            var wtpPath = Path.ChangeExtension(path, ".wtp");
            if (!File.Exists(wtpPath)) continue;

            var document = BankDocument.Load(path);
            Assert.Equal(BankKind.EffectWta, document.Kind);

            var section = Assert.Single(document.Sections);
            Assert.Equal("wta", section.Label);

            // The payload stream must be exactly the sibling .wtp.
            Assert.Equal(new FileInfo(wtpPath).Length, section.PayloadStream.LongLength);

            foreach (var texture in section.Bank.Textures)
            {
                textures++;

                // table0 is stream-relative for split banks and every payload is attached.
                long available = (long)section.PayloadStream.Length - texture.PayloadOffset;
                Assert.True(available > 0, $"{Path.GetFileName(path)} #{texture.Index}: offset past the stream");
                Assert.Equal((int)Math.Min(texture.PayloadSize, available), texture.Payload.Length);

                if (!TextureCodec.CanDecode(texture.FormatInfo))
                {
                    unsupported++;
                    continue;
                }

                var image = TextureCodec.Decode(texture, 0, 0, 0, out var error);
                Assert.NotNull(image);
                Assert.Null(error);
                Assert.Equal((int)texture.Header.Width, image!.Width);
                decoded++;
            }
        }

        Assert.True(textures > 500, $"expected a large sample, saw {textures}");
        // Every texture with a decoder must have decoded (failures throw inside the loop).
        Assert.Equal(textures - unsupported, decoded);
        Assert.True(decoded > 500, $"expected many decodes, saw {decoded}");
    }

    [Fact]
    public void Table3_HashPairsAppearInNamedBanks()
    {
        // The loose files do not carry per-texture names, so verify the documented name->hash
        // rule directly (also covered by FormatTests) and that a known bank's table3 values are
        // masked to 31 bits.
        if (!TestPaths.Exists(TestPaths.UiCoreDat)) return;
        var dat = DatFile.Parse(File.ReadAllBytes(TestPaths.UiCoreDat));
        var wta = dat.FindByTag("wta");
        if (wta is null) return;
        var bank = WtbBank.Parse(wta.Bytes);
        if (!bank.IncludeTable3) return;
        foreach (var tex in bank.Textures)
            Assert.True(tex.Hash <= 0x7FFFFFFF);
    }
}
