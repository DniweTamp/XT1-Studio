using Xt1.Core.Banks;
using Xt1.Core.Containers;
using Xt1.Core.Util;
using Xunit;

namespace Xt1.Core.Tests;

/// <summary>
/// <see cref="TextureBank"/>: parse/list/export/import/rebuild for Bayonetta 1 (inline DDS) and
/// Bayonetta 2 Wii U (GTX + stream). Real-file tests skip when the sample is absent; a synthetic
/// BNTX bank exercises the Switch path without game files.
/// </summary>
public class TextureBankTests
{
    [Fact]
    public void B1_Et0000Wtb_ParsesAsStandaloneDdsBank()
    {
        if (!TestPaths.Exists(TestPaths.B1CoreDat)) return;
        var dat = DatFile.Parse(File.ReadAllBytes(TestPaths.B1CoreDat));
        var wtb = dat.SubFiles.FirstOrDefault(s => s.Name == "et0000.wtb");
        Assert.NotNull(wtb);

        var bank = TextureBank.Parse(wtb!.Bytes);

        Assert.Equal(TextureBankFormat.Dds, bank.Format);
        Assert.True(bank.IsStandalone);
        Assert.Equal(1, bank.Count);
        Assert.False(bank.HasHashTable);

        var exported = bank.ExportTexture(bank.Textures[0]);
        Assert.True(exported.Length >= 4 && exported[0] == (byte)'D' && exported[1] == (byte)'D' && exported[2] == (byte)'S');
        Assert.Equal(bank.Textures[0].Data, exported);
        // Bayonetta 1 banks have no hash table, so textures are matched by index only.
        Assert.False(bank.Textures[0].HasHash);
    }

    [Fact]
    public void B1_DdsImport_RoundTripsAndLeavesOtherFieldsAlone()
    {
        if (!TestPaths.Exists(TestPaths.B1CoreDat)) return;
        var dat = DatFile.Parse(File.ReadAllBytes(TestPaths.B1CoreDat));
        var preTexture = dat.SubFiles.First(s => s.Name == "pre_texture.wtb");
        var original = TextureBank.Parse(preTexture.Bytes);
        Assert.True(original.Count >= 2);

        var beforeFlags = original.Textures.Select(t => (byte[])t.Flags.Clone()).ToArray();
        var beforeOther = original.ExportTexture(original.Textures[1]);

        // Replace texture 0 with a same-sized DDS (valid, unchanged content): the rebuild must
        // reproduce a bank whose texture 0 is that blob and whose other entries are untouched.
        var replacement = original.ExportTexture(original.Textures[0]);
        original.ImportTexture(original.Textures[0], replacement);
        Assert.True(original.HasChanged);

        var rebuilt = original.ToBytes();
        var reparsed = TextureBank.Parse(rebuilt);

        Assert.Equal(original.Count, reparsed.Count);
        Assert.Equal(replacement, reparsed.ExportTexture(reparsed.Textures[0]));
        Assert.Equal(beforeOther, reparsed.ExportTexture(reparsed.Textures[1]));
        for (int i = 0; i < original.Count; i++)
            Assert.Equal(beforeFlags[i], reparsed.Textures[i].Flags);
    }

    [Fact]
    public void B2WiiU_Wp0020Wta_ParsesAsBigEndianGtxBankWithHashes()
    {
        if (!TestPaths.Exists(TestPaths.B2WuWp0020Wta) || !TestPaths.Exists(TestPaths.B2WuWp0020Wtp)) return;
        var wta = File.ReadAllBytes(TestPaths.B2WuWp0020Wta);
        var wtp = File.ReadAllBytes(TestPaths.B2WuWp0020Wtp);

        var bank = TextureBank.Parse(wta, wtp);

        Assert.Equal(TextureBankFormat.Gtx, bank.Format);
        Assert.Equal(ByteOrder.Big, bank.Order);
        Assert.False(bank.IsStandalone);
        Assert.Equal(6, bank.Count);
        Assert.True(bank.HasHashTable);
        Assert.True(bank.HasMipmapTable);
        Assert.Equal(0x523dc96bu, bank.Textures[0].Hash);
        Assert.Equal(10u, Mem.ReadU32(bank.Textures[0].Descriptor, 0x10, ByteOrder.Big)); // mip count

        // Export is a standalone GTX whose first four bytes are "Gfx2".
        var gtx = bank.ExportTexture(bank.Textures[0]);
        Assert.Equal("Gfx2", System.Text.Encoding.ASCII.GetString(gtx, 0, 4));
        // Texture 0 is a 512x512 surface: the image data block is 0x20000 bytes (verified layout).
        Assert.Equal(0x20000, bank.Textures[0].Data.Length);
        Assert.True(bank.Textures[0].HasMipmaps);
    }

    [Fact]
    public void B2WiiU_GtxImport_RoundTripsAndPreservesFlagsHashesAndSiblings()
    {
        if (!TestPaths.Exists(TestPaths.B2WuWp0020Wta) || !TestPaths.Exists(TestPaths.B2WuWp0020Wtp)) return;
        var wta = File.ReadAllBytes(TestPaths.B2WuWp0020Wta);
        var wtp = File.ReadAllBytes(TestPaths.B2WuWp0020Wtp);
        var bank = TextureBank.Parse(wta, wtp);

        var beforeFlags = bank.Textures.Select(t => (byte[])t.Flags.Clone()).ToArray();
        var beforeHashes = bank.Textures.Select(t => t.Hash).ToArray();
        var beforeSibling = bank.ExportTexture(bank.Textures[5]);

        // Export a texture to a standalone GTX and feed it straight back (replace-only).
        var gtx = bank.ExportTexture(bank.Textures[2]);
        bank.ImportTexture(bank.Textures[2], gtx);

        var rebuiltBank = bank.ToBytes();
        var rebuiltStream = bank.ToPayloadStream();
        Assert.NotNull(rebuiltStream);

        var reparsed = TextureBank.Parse(rebuiltBank, rebuiltStream!);
        Assert.Equal(6, reparsed.Count);
        Assert.Equal(gtx, reparsed.ExportTexture(reparsed.Textures[2]));
        Assert.Equal(beforeSibling, reparsed.ExportTexture(reparsed.Textures[5]));
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(beforeFlags[i], reparsed.Textures[i].Flags);
            Assert.Equal(beforeHashes[i], reparsed.Textures[i].Hash);
        }
    }

    [Fact]
    public void B2Switch_SyntheticBntxBank_RoundTrips()
    {
        // Build a little-endian WTB + WTP with 3 BNTX blobs and an identity-hash table.
        var blobs = new[]
        {
            BntxBlob(11), BntxBlob(22), BntxBlob(33),
        };
        var hashes = new uint[] { 0x11111111, 0x22222222, 0x33333333 };
        var (bankBytes, streamBytes) = BuildBntxBank(blobs, hashes);

        var bank = TextureBank.Parse(bankBytes, streamBytes);
        Assert.Equal(TextureBankFormat.Bntx, bank.Format);
        Assert.Equal(ByteOrder.Little, bank.Order);
        Assert.Equal(3, bank.Count);
        Assert.Equal(0x22222222u, bank.FindByHash(0x22222222).Single().Hash);

        var replacement = BntxBlob(999);
        bank.ImportTexture(bank.Textures[1], replacement);

        var reparsed = TextureBank.Parse(bank.ToBytes(), bank.ToPayloadStream()!);
        Assert.Equal(3, reparsed.Count);
        Assert.Equal(replacement, reparsed.Textures[1].Data);
        Assert.Equal(hashes[0], reparsed.Textures[0].Hash);
        Assert.Equal(hashes[2], reparsed.Textures[2].Hash);
        Assert.Equal(blobs[0], reparsed.Textures[0].Data);
    }

    private static byte[] BntxBlob(int seed)
    {
        var bytes = new byte[64];
        "BNTX"u8.ToArray().CopyTo(bytes, 0);
        for (int i = 4; i < bytes.Length; i++) bytes[i] = (byte)(seed + i);
        return bytes;
    }

    private static (byte[] Bank, byte[] Stream) BuildBntxBank(byte[][] blobs, uint[] hashes)
    {
        int n = blobs.Length;
        int stride = Mem.Align(n * 4, 0x20);
        uint t0 = 0x40;
        uint t1 = (uint)(t0 + stride);
        uint t2 = (uint)(t1 + stride);
        uint t3 = (uint)(t2 + stride);
        int structuralEnd = (int)(t3 + stride);

        var offsets = new uint[n];
        var stream = new byte[0x2000 * n];
        long cursor = 0;
        for (int i = 0; i < n; i++)
        {
            cursor = Mem.Align(cursor, 0x2000);
            offsets[i] = (uint)cursor;
            blobs[i].CopyTo(stream, (int)cursor);
            cursor = Mem.Align(cursor + blobs[i].Length, 0x2000);
        }

        var bank = new byte[structuralEnd];
        bank[0] = (byte)'W'; bank[1] = (byte)'T'; bank[2] = (byte)'B'; bank[3] = 0;
        Mem.WriteU32LE(bank, 0x04, 1);
        Mem.WriteU32LE(bank, 0x08, (uint)n);
        Mem.WriteU32LE(bank, 0x0C, t0);
        Mem.WriteU32LE(bank, 0x10, t1);
        Mem.WriteU32LE(bank, 0x14, t2);
        Mem.WriteU32LE(bank, 0x18, t3);
        Mem.WriteU32LE(bank, 0x1C, 0);
        for (int i = 0; i < n; i++)
        {
            Mem.WriteU32LE(bank, (int)t0 + 4 * i, offsets[i]);
            Mem.WriteU32LE(bank, (int)t1 + 4 * i, (uint)blobs[i].Length);
            Mem.WriteU32LE(bank, (int)t3 + 4 * i, hashes[i]);
        }
        return (bank, stream);
    }
}
