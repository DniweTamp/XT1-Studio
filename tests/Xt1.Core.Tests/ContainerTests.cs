using Xt1.Core.Containers;
using Xt1.Core.Format;
using Xt1.Core.Xt1;
using Xunit;

namespace Xt1.Core.Tests;

public class ContainerTests
{
    [Fact]
    public void Dat_RoundTripsByteForByte()
    {
        foreach (var path in new[] { TestPaths.Pl0000Dtt, TestPaths.UiCoreDat, TestPaths.UiCoreDtt, TestPaths.Em0000Dat })
        {
            if (!TestPaths.Exists(path)) continue;
            var bytes = File.ReadAllBytes(path);
            var dat = DatFile.Parse(bytes);
            // ToBytesForced exercises the rebuild path (ToBytes short-circuits clean containers).
            var rebuilt = dat.ToBytesForced();
            Assert.Equal(bytes.Length, rebuilt.Length);
            Assert.True(bytes.AsSpan().SequenceEqual(rebuilt), $"{Path.GetFileName(path)} did not round-trip");
        }
    }

    [Fact]
    public void Dat_FindsSubFilesByTag_NotIndex()
    {
        if (!TestPaths.Exists(TestPaths.Em0000Dat)) return;
        var dat = DatFile.Parse(File.ReadAllBytes(TestPaths.Em0000Dat));

        var wta = dat.FindByTag("wta");
        Assert.NotNull(wta);
        Assert.Equal(3, dat.SubFiles.ToList().IndexOf(wta!)); // em0000.dat has "wta" at index 3
        Assert.True(WtbBank.HasMagic(wta!.Bytes));
    }

    [Fact]
    public void Dat_ParsesDttPayloadBase()
    {
        if (!TestPaths.Exists(TestPaths.Pl0000Dtt)) return;
        var dat = DatFile.Parse(File.ReadAllBytes(TestPaths.Pl0000Dtt));
        Assert.Equal(1u, (uint)dat.Count);
        Assert.Equal("wtp", dat.SubFiles[0].Tag);
        Assert.Equal(0x1000u, dat.SubFiles[0].Offset);
        Assert.True(dat.LooksLikeDtt);
    }

    [Fact]
    public void Wtb_RoundTripsByteForByte_Standalone()
    {
        if (!TestPaths.Exists(TestPaths.CubeMapWtb)) return;
        var bytes = File.ReadAllBytes(TestPaths.CubeMapWtb);
        var bank = WtbBank.Parse(bytes);
        var rebuilt = bank.ToBytes();
        Assert.Equal(bytes.Length, rebuilt.Length);
        Assert.True(bytes.AsSpan().SequenceEqual(rebuilt));
    }

    [Fact]
    public void Wtb_Standalone_Table0IsAbsolute()
    {
        if (!TestPaths.Exists(TestPaths.CubeMapWtb)) return;
        var bank = WtbBank.Parse(File.ReadAllBytes(TestPaths.CubeMapWtb));
        Assert.True(bank.IsStandalone);
        Assert.Equal(0x1000u, bank.PayloadBase);
        Assert.Equal(0x1000u, bank.Textures[0].PayloadOffset);
        Assert.Equal(11, bank.Count);
    }

    [Fact]
    public void Wtb_RoundTripsByteForByte_Split()
    {
        if (!TestPaths.Exists(TestPaths.Em0000Dat)) return;
        var dat = DatFile.Parse(File.ReadAllBytes(TestPaths.Em0000Dat));
        var wta = dat.FindByTag("wta")!;
        var bank = WtbBank.Parse(wta.Bytes);
        var rebuilt = bank.ToBytes();
        Assert.True(wta.Bytes.AsSpan().SequenceEqual(rebuilt));
    }

    [Fact]
    public void Wtb_CanOmitTable3AndStillRoundTripStructure()
    {
        if (!TestPaths.Exists(TestPaths.Em0000Dat)) return;
        var dat = DatFile.Parse(File.ReadAllBytes(TestPaths.Em0000Dat));
        var wta = dat.FindByTag("wta")!;
        var bank = WtbBank.Parse(wta.Bytes);

        bank.IncludeTable3 = false;
        var bytes = bank.ToBytes();
        var reparsed = WtbBank.Parse(bytes);
        Assert.False(reparsed.IncludeTable3);
        Assert.Equal(bank.Count, reparsed.Count);
        Assert.Equal(0u, reparsed.Table3Offset);
    }

    [Fact]
    public void Wtb_HashIncludeRoundTrip_PreservesCustomHashes()
    {
        if (!TestPaths.Exists(TestPaths.Pl0080Dat)) return;
        var dat = DatFile.Parse(File.ReadAllBytes(TestPaths.Pl0080Dat));
        var wta = dat.FindByTag("wta")!;
        var bank = WtbBank.Parse(wta.Bytes);

        bank.IncludeTable3 = true;
        bank.Textures[0].HashMode = HashMode.Forced;
        bank.Textures[0].Hash = 0x1234ABCD;
        bank.Textures[0].Name = "custom_name";

        var bytes = bank.ToBytes();
        var reparsed = WtbBank.Parse(bytes);
        Assert.True(reparsed.IncludeTable3);
        Assert.Equal(0x1234ABCDu, reparsed.Textures[0].Hash);
        // Forced values must never be replaced by the auto-computed hash.
        Assert.NotEqual(Crc32NameHash.ComputeMasked("custom_name"), reparsed.Textures[0].Hash);
    }

    [Fact]
    public void SplitEffectBank_LoadsSiblingWtpPayload()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Xt1Studio", "effect", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // Author a split bank: WTB bytes in the .wta, payload stream in the .wtp.
            var bank = WtbBank.CreateEmpty(includeTable3: false, standalone: false, payloadBase: 0);
            var expected = new List<byte[]>();
            for (int i = 0; i < 3; i++)
            {
                var texture = EditableTexture.CreateNew(Xt1Format.Rgba8Unorm, 16, 16, layout: 1);
                var payload = new byte[1024];
                for (int b = 0; b < payload.Length; b++) payload[b] = (byte)(i * 37 + b);
                texture.ResetOriginalPayload(payload);
                texture.PayloadSize = (uint)payload.Length;
                texture.Header.ImageSize = (ulong)payload.Length;
                bank.Add(texture);
                expected.Add(payload);
            }

            var section = new BankSection { Label = "wta", Bank = bank };
            var stream = Editing.BankWriter.BuildPayloadStream(section, recomputeOffsets: true);
            var wtbBytes = bank.ToBytes();

            var wtaPath = Path.Combine(directory, "004.wta");
            File.WriteAllBytes(wtaPath, wtbBytes);
            File.WriteAllBytes(Path.ChangeExtension(wtaPath, ".wtp"), stream);

            var document = BankDocument.Load(wtaPath);

            Assert.Equal(BankKind.EffectWta, document.Kind);
            var loaded = Assert.Single(document.Sections);
            Assert.Equal("wta", loaded.Label);
            Assert.Equal(stream.Length, loaded.PayloadStream.Length);

            for (int i = 0; i < expected.Count; i++)
                Assert.Equal(expected[i], loaded.Bank.Textures[i].Payload);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void StandaloneWtb_IsNotTreatedAsAnEffectBank()
    {
        if (!TestPaths.Exists(TestPaths.CubeMapWtb)) return;
        var document = BankDocument.Load(TestPaths.CubeMapWtb);
        Assert.Equal(BankKind.StandaloneWtb, document.Kind);
        var section = Assert.Single(document.Sections);
        Assert.Equal("wtb", section.Label);
    }

    [Fact]
    public void Xt1Header_RoundTripsAndPreservesUnknownBytes()
    {
        var header = new Xt1Header();
        header.Magic = Xt1Header.MagicValue;
        header.Flags = Xt1Header.FlagsValue;
        header.HeaderSize = Xt1Header.Size;
        header.Layout2 = Xt1Header.Layout2Value;
        header.Width = 512;
        header.Height = 256;
        header.MipCount = 4;

        // Reserved bytes that no typed accessor models.
        header.Raw[0x0A] = 0xAB;
        header.Raw[0x2D] = 0xCD;
        header.Raw[0x33] = 0xEF;

        var parsed = Xt1Header.Parse(header.Raw);
        Assert.Equal(512u, parsed.Width);
        Assert.Equal(4, parsed.MipCount);
        Assert.Equal(0xAB, parsed.Raw[0x0A]);
        Assert.Equal(0xCD, parsed.Raw[0x2D]);
        Assert.Equal(0xEF, parsed.Raw[0x33]);
    }

    [Fact]
    public void EditableTexture_ChangingOneHeaderByteLeavesOthersUntouched()
    {
        if (!TestPaths.Exists(TestPaths.Pl0080Dat)) return;
        var dat = DatFile.Parse(File.ReadAllBytes(TestPaths.Pl0080Dat));
        var wta = dat.FindByTag("wta")!;
        var bank = WtbBank.Parse(wta.Bytes);

        var before = (byte[])bank.Textures[0].Header.Raw.Clone();
        bank.Textures[0].Header.TextureFlag = 0x20;
        var after = bank.Textures[0].Header.Raw;

        int changed = 0;
        for (int i = 0; i < before.Length; i++)
            if (before[i] != after[i]) changed++;
        Assert.Equal(1, changed); // only 0x2C's low byte changed
    }
}
