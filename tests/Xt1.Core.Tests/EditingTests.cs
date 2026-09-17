using Xt1.Core.Codecs;
using Xt1.Core.Containers;
using Xt1.Core.Editing;
using Xt1.Core.Encoding;
using Xt1.Core.Format;
using Xt1.Core.Imaging;
using Xt1.Core.Nvtt;
using Xt1.Core.Xt1;
using Xunit;

namespace Xt1.Core.Tests;

public class EditingTests
{
    private static ITextureEncoder? ManagedOrNvtt(out IDisposable? owner)
    {
        owner = null;
        var managed = new ManagedEncoder();
        return managed;
    }

    [Fact]
    public void NoEditSave_ReproducesEveryOutputFileByteForByte()
    {
        if (!TestPaths.Exists(TestPaths.Em0000Dat)) return;
        var document = BankDocument.Load(TestPaths.Em0000Dat);
        var result = BankWriter.Write(document, new WriteOptions());

        var datOutput = result.Files.Single(f => f.Path.EndsWith(".dat", StringComparison.OrdinalIgnoreCase));
        var dttOutput = result.Files.Single(f => f.Path.EndsWith(".dtt", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(File.ReadAllBytes(TestPaths.Em0000Dat), datOutput.Bytes);
        Assert.Equal(File.ReadAllBytes(Path.ChangeExtension(TestPaths.Em0000Dat, ".dtt")), dttOutput.Bytes);
    }

    [Fact]
    public void EditedHeaderField_SurvivesWriteAndReload_AndPreservesSiblingSubFiles()
    {
        if (!TestPaths.Exists(TestPaths.Em0000Dat)) return;
        var document = BankDocument.Load(TestPaths.Em0000Dat);
        var section = document.Sections[0];
        var texture = section.Bank.Textures[0];

        var originalDat = File.ReadAllBytes(TestPaths.Em0000Dat);
        texture.Header.TextureFlag = 0x20;
        texture.Table2B3 = 0xE4;

        var outputDirectory = Path.Combine(Path.GetTempPath(), "Xt1Studio", "tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDirectory);
        try
        {
            var result = BankWriter.Write(document, new WriteOptions { OutputDirectory = outputDirectory });
            result.SaveAll();

            var datPath = result.Files.Single(f => f.Path.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)).Path;
            var reloaded = BankDocument.Load(datPath);
            var reloadedTexture = reloaded.Sections[0].Bank.Textures[0];

            Assert.Equal(0x20u, reloadedTexture.Header.TextureFlag);
            Assert.Equal(0xE4, reloadedTexture.Table2B3);

            // Other sub-files (e.g. the "bxm"/"bnk" blobs) must be preserved verbatim.
            var beforeDat = DatFile.Parse(originalDat);
            var afterDat = DatFile.Parse(File.ReadAllBytes(datPath));
            for (int i = 0; i < beforeDat.SubFiles.Count; i++)
            {
                if (beforeDat.SubFiles[i].Tag == "wta") continue;
                Assert.Equal(beforeDat.SubFiles[i].Bytes, afterDat.SubFiles[i].Bytes);
            }
        }
        finally
        {
            try { Directory.Delete(outputDirectory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Replace2D_ReEncodesSwizzlesAndDecodesBack()
    {
        if (!TestPaths.Exists(TestPaths.Em0000Dat)) return;
        var document = BankDocument.Load(TestPaths.Em0000Dat);
        var section = document.Sections[0];

        // Pick an encodable texture (BC or ASTC) so the managed encoder is used.
        var texture = section.Bank.Textures.FirstOrDefault(t => t.FormatInfo?.Format == Xt1Format.Bc3Unorm)
                      ?? section.Bank.Textures.First(t => t.FormatInfo is { Codec: CodecKind.Bc or CodecKind.Astc or CodecKind.Raw });
        var encoder = new ManagedEncoder();

        var replacement = new RgbaImage(64, 64);
        replacement.Fill(200, 30, 40, 255);

        var result = TextureOperations.Replace2D(texture, replacement, encoder, generateMips: true, EncodeQuality.Normal);
        Assert.True(result.Success, result.Error);

        Assert.Equal(64u, texture.Header.Width);
        Assert.Equal(64u, texture.Header.Height);
        Assert.True(texture.Header.MipCount >= 1);
        Assert.Equal(texture.Payload.Length, (int)texture.Header.ImageSize);
        Assert.Equal(texture.Payload.Length, (int)texture.PayloadSize);
        Assert.True(texture.Layout.SizeMatches, texture.Layout.Note);

        var decoded = TextureCodec.Decode(texture, 0, 0, 0, out var error);
        Assert.NotNull(decoded);
        Assert.Equal(64, decoded!.Width);

        var (r, g, b, a) = decoded.GetPixel(32, 32);
        Assert.InRange(r, 150, 255);
        Assert.InRange(g, 0, 90);
        Assert.InRange(b, 0, 100);
        Assert.Equal(255, a);
    }

    [Fact]
    public void CustomHash_WithAutoMode_Recomputes_ForcedModeDoesNot()
    {
        if (!TestPaths.Exists(TestPaths.Pl0080Dat)) return;
        var document = BankDocument.Load(TestPaths.Pl0080Dat);
        var section = document.Sections[0];
        var texture = section.Bank.Textures[0];
        texture.Name = "my_texture";
        texture.HashMode = HashMode.AutoFromName;

        section.Bank.IncludeTable3 = true;
        var bytes = section.Bank.ToBytes();
        var reparsed = WtbBank.Parse(bytes);
        Assert.Equal(Crc32NameHash.ComputeMasked("my_texture"), reparsed.Textures[0].Hash);

        // A forced value must survive even though AutoFromName would give a different result.
        texture.HashMode = HashMode.Forced;
        texture.Hash = 0x0BADF00D;
        var bytes2 = section.Bank.ToBytes();
        var reparsed2 = WtbBank.Parse(bytes2);
        Assert.Equal(0x0BADF00Du, reparsed2.Textures[0].Hash);
    }

    [Fact]
    public void StandaloneWtbWriter_RebasesTable0AndRoundTrips()
    {
        if (!TestPaths.Exists(TestPaths.GradingWtb)) return;
        var bank = WtbBank.Parse(File.ReadAllBytes(TestPaths.GradingWtb));
        var built = StandaloneWtbWriter.Build(bank, new WriteOptions());

        var reparsed = WtbBank.Parse(built);
        Assert.True(reparsed.IsStandalone);
        Assert.Equal(0x1000u, reparsed.Textures[0].PayloadOffset);
        Assert.Equal(bank.Count, reparsed.Count);
    }

    [Fact]
    public void EditHistory_UndoRedoRestoresHeaderField()
    {
        var header = new Xt1Header();
        header.Magic = Xt1Header.MagicValue;
        header.Flags = Xt1Header.FlagsValue;
        header.HeaderSize = Xt1Header.Size;
        header.Layout2 = Xt1Header.Layout2Value;
        header.TextureFlag = 0;

        var texture = new EditableTexture(header, new byte[4], 0, 0, 0, Array.Empty<byte>());
        var history = new EditHistory();

        var command = TextureOperations.CreateHeaderWordCommand(texture, 0x2C, 0x20, "Set textureFlag");
        history.Execute(command);
        Assert.Equal(0x20u, texture.Header.TextureFlag);

        Assert.True(history.Undo());
        Assert.Equal(0u, texture.Header.TextureFlag);

        Assert.True(history.Redo());
        Assert.Equal(0x20u, texture.Header.TextureFlag);
    }

    [Fact]
    public void RawPayloadDropIn_FlagsSizeMismatchButApplies()
    {
        if (!TestPaths.Exists(TestPaths.Em0000Dat)) return;
        var document = BankDocument.Load(TestPaths.Em0000Dat);
        var texture = document.Sections[0].Bank.Textures[0];

        var payload = new byte[16];
        var result = TextureOperations.ReplaceRawPayload(texture, payload, requireExactSize: true);
        Assert.False(result.Success); // advisory guard

        var forced = TextureOperations.ReplaceRawPayload(texture, payload, requireExactSize: false);
        Assert.True(forced.Success);
        Assert.NotNull(forced.Warning);
        Assert.Equal(payload, texture.Payload);
    }

    [Fact]
    public void ImageIo_RoundTripsPngAndTga()
    {
        var image = new RgbaImage(8, 4);
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 8; x++)
                image.SetPixel(x, y, (byte)(x * 30), (byte)(y * 60), 100, 255);

        var directory = Path.Combine(Path.GetTempPath(), "Xt1Studio", "imagio", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var extension in new[] { ".png", ".bmp", ".tga" })
            {
                var path = Path.Combine(directory, "test" + extension);
                ImageIo.Save(image, path);
                var loaded = ImageIo.Load(path);
                Assert.Equal(8, loaded.Width);
                Assert.Equal(4, loaded.Height);
                Assert.Equal(image.Pixels, loaded.Pixels);
            }
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Dds_RoundTripsCompressedStoredBytes()
    {
        var format = FormatTable.Get(Xt1Format.Bc3Unorm)!;
        int width = 16, height = 8, mipCount = 3;
        var data = new byte[1][][];
        data[0] = new byte[mipCount][];
        for (int m = 0; m < mipCount; m++)
        {
            int wb = Math.Max(1, (width >> m) / 4);
            int hb = Math.Max(1, (height >> m) / 4);
            data[0][m] = new byte[wb * hb * 16];
            for (int i = 0; i < data[0][m].Length; i++) data[0][m][i] = (byte)(i + m);
        }

        var bytes = DdsIo.Write(format, width, height, 1, mipCount, 1, false, data);
        var read = DdsIo.Read(bytes);

        Assert.Equal(width, read.Width);
        Assert.Equal(height, read.Height);
        Assert.Equal(mipCount, read.MipCount);
        Assert.Equal(format.Format, read.Format!.Format);
        for (int m = 0; m < mipCount; m++)
            Assert.Equal(data[0][m], read.Data[0][m]);
    }
}
