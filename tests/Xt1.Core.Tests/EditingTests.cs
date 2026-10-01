using Xt1.Core.Codecs;
using Xt1.Core.Containers;
using Xt1.Core.Editing;
using Xt1.Core.Encoding;
using Xt1.Core.Format;
using Xt1.Core.Imaging;
using Xt1.Core.Nvtt;
using Xt1.Core.Util;
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

    /// <summary>
    /// ASTC DDS must use NVIDIA's DXGI numbering (four slots per footprint, id = 134 + 4i for UNORM
    /// and 135 + 4i for sRGB). A stride-3 table maps a footprint onto a neighbouring one, which is
    /// why an 8x6 import used to show up as 10x5.
    /// </summary>
    [Theory]
    [InlineData(Xt1Format.Astc4x4Unorm, 134u)]
    [InlineData(Xt1Format.Astc5x4Unorm, 138u)]
    [InlineData(Xt1Format.Astc6x6Unorm, 150u)]
    [InlineData(Xt1Format.Astc8x6Unorm, 158u)]
    [InlineData(Xt1Format.Astc8x8Unorm, 162u)]
    [InlineData(Xt1Format.Astc10x5Unorm, 166u)]
    [InlineData(Xt1Format.Astc8x8Srgb, 163u)]
    public void Dds_AstcUsesNvttDxgiIds(Xt1Format formatValue, uint expectedDxgi)
    {
        var format = FormatTable.Get(formatValue)!;
        var surface = new byte[format.BytesPerBlock];
        var bytes = DdsIo.Write(format, format.BlockWidth, format.BlockHeight, 1, 1, 1, false,
            new byte[1][][] { new[] { surface } });

        Assert.Equal(expectedDxgi, Mem.ReadU32LE(bytes, 0x80));
        var read = DdsIo.Read(bytes);
        Assert.Equal(formatValue, read.Format!.Format);
    }

    /// <summary>
    /// The alternate 0x49/0x4B ids are unsigned BC4/BC5, so they decode and export DDS as the UNORM
    /// DXGI ids (80/83) with full surfaces. They previously were treated as SNORM, which left them
    /// without a decoder and made DDS export write a header-only file.
    /// </summary>
    [Theory]
    [InlineData(Xt1Format.Bc4UnormAlt, 80u, Xt1Format.Bc4Unorm)]
    [InlineData(Xt1Format.Bc5UnormAlt, 83u, Xt1Format.Bc5Unorm)]
    public void Dds_ExportsUnsignedBc4Bc5Alternates(Xt1Format formatValue, uint expectedDxgi,
        Xt1Format readBack)
    {
        var info = FormatTable.Get(formatValue)!;
        Assert.True(TextureCodec.CanDecode(info));
        Assert.True(TextureCodec.CanEncode(info));

        var texture = EditableTexture.CreateNew(formatValue, 64, 64);
        Assert.True(DdsExport.CanExport(texture));

        var bytes = DdsExport.Build(texture);
        Assert.Equal(expectedDxgi, Mem.ReadU32LE(bytes, 0x80));

        var read = DdsIo.Read(bytes);
        Assert.Equal(readBack, read.Format!.Format);
        int expected = (64 / info.BlockWidth) * (64 / info.BlockHeight) * info.BytesPerBlock;
        Assert.Equal(expected, read.Data[0][0].Length);
        Assert.True(bytes.Length > 0x94, "the export must contain the surface, not just the header");
    }

    /// <summary>The DDS header uses the flags NVTT's reader expects (LINEARSIZE, MIPMAP, caps2).</summary>
    [Fact]
    public void Dds_HeaderMatchesNvttConvention()
    {
        var format = FormatTable.Get(Xt1Format.Astc8x6Unorm)!;
        int surfaceSize = format.WidthBlocks(64) * format.HeightBlocks(64) * format.BytesPerBlock;
        var mips = new[] { new byte[surfaceSize], new byte[surfaceSize], new byte[surfaceSize] };

        var twoD = DdsIo.Write(format, 64, 64, 1, 3, 1, false, new byte[1][][] { mips });
        Assert.Equal(0x000A1007u, Mem.ReadU32LE(twoD, 0x08)); // CAPS|HEIGHT|WIDTH|PIXELFORMAT|MIPMAPCOUNT|LINEARSIZE
        Assert.Equal((uint)surfaceSize, Mem.ReadU32LE(twoD, 0x14)); // dwPitchOrLinearSize
        Assert.Equal(0x00401008u, Mem.ReadU32LE(twoD, 0x6C)); // TEXTURE|COMPLEX|MIPMAP
        Assert.Equal(0u, Mem.ReadU32LE(twoD, 0x70)); // caps2 empty for a 2D texture

        var cubeFaces = new byte[6][][];
        for (int i = 0; i < 6; i++) cubeFaces[i] = new[] { new byte[surfaceSize] };
        var cube = DdsIo.Write(format, 64, 64, 1, 1, 6, true, cubeFaces);
        Assert.Equal(0x200u | 0xFC00u, Mem.ReadU32LE(cube, 0x70)); // CUBEMAP | ALLFACES
        Assert.Equal(4u, Mem.ReadU32LE(cube, 0x88)); // miscFlag cube
    }

    /// <summary>
    /// A 3D volume exports every depth slice of each level, not just the first slice; a short level
    /// is what made readers reject the file.
    /// </summary>
    [Fact]
    public void DdsExport_VolumesKeepAllSlices()
    {
        if (!TestPaths.Exists(TestPaths.CoreDat)) return;
        var document = BankDocument.Load(TestPaths.CoreDat);
        // Decode-only formats (the 0x6D volumes) have no DDS equivalent and are not offered.
        var volumes = document.AllTextures
            .Where(t => t.Header.Type.Is3D() && t.Header.Depth > 1 && DdsExport.CanExport(t))
            .ToList();
        Assert.NotEmpty(volumes);

        // The reported case, and every other 3D texture in the bank (including multi-mip ones).
        Assert.Contains(volumes, t => t.EffectiveHash() == 0x24AD32DDu);

        foreach (var volume in volumes)
        {
            var bytes = DdsExport.Build(volume);
            var read = DdsIo.Read(bytes);

            Assert.Equal(volume.Header.Format, read.Format!.Format);
            Assert.Equal((int)volume.Header.Width, read.Width);
            Assert.Equal((int)volume.Header.Height, read.Height);
            Assert.Equal((int)volume.Header.Depth, read.Depth);
            Assert.False(read.IsCube);

            var info = read.Format!;
            for (int mip = 0; mip < read.MipCount; mip++)
            {
                int w = Math.Max(1, read.Width >> mip);
                int h = Math.Max(1, read.Height >> mip);
                int d = Math.Max(1, read.Depth >> mip);
                int expected = Mem.DivUp(w, info.BlockWidth) * Mem.DivUp(h, info.BlockHeight)
                               * info.BytesPerBlock * d;
                Assert.Equal(expected, read.Data[0][mip].Length);
            }
        }
    }

    /// <summary>ASTC can be written to (and read back from) a DX10 DDS like any BC format.</summary>
    [Theory]
    [InlineData(Xt1Format.Astc4x4Unorm)]
    [InlineData(Xt1Format.Astc8x8Srgb)]
    public void Dds_RoundTripsAstc(Xt1Format xt1Format)
    {
        var format = FormatTable.Get(xt1Format)!;
        Assert.True(DdsIo.CanExport(format));

        int width = 32, height = 32;
        int wb = Mem.DivUp(width, format.BlockWidth);
        int hb = Mem.DivUp(height, format.BlockHeight);
        var surface = new byte[wb * hb * format.BytesPerBlock];
        for (int i = 0; i < surface.Length; i++) surface[i] = (byte)(i * 7);
        var data = new byte[1][][] { new[] { surface } };

        var bytes = DdsIo.Write(format, width, height, 1, 1, 1, false, data);
        var read = DdsIo.Read(bytes);
        Assert.Equal(format.Format, read.Format!.Format);
        Assert.Equal(surface, read.Data[0][0]);
    }

    /// <summary>
    /// Replacing from a DDS is lossless: the stored surfaces are copied straight through and the
    /// DDS's own mip chain is kept, without an encode/decode round trip.
    /// </summary>
    [Fact]
    public void ReplaceWithDds_IsLosslessAndKeepsMips()
    {
        var format = FormatTable.Get(Xt1Format.Astc6x6Unorm)!;
        int width = 64, height = 64, mips = 4;
        var data = new byte[1][][];
        data[0] = new byte[mips][];
        for (int m = 0; m < mips; m++)
        {
            int w = Math.Max(1, width >> m);
            int h = Math.Max(1, height >> m);
            int wb = Mem.DivUp(w, format.BlockWidth);
            int hb = Mem.DivUp(h, format.BlockHeight);
            data[0][m] = new byte[wb * hb * format.BytesPerBlock];
            for (int i = 0; i < data[0][m].Length; i++) data[0][m][i] = (byte)(i + m * 3);
        }

        var ddsBytes = DdsIo.Write(format, width, height, 1, mips, 1, false, data);
        var dds = DdsIo.Read(ddsBytes);

        var texture = EditableTexture.CreateNew(Xt1Format.Bc7Unorm, 8, 8);
        var result = TextureOperations.ReplaceWithDds(texture, dds);
        Assert.True(result.Success, result.Error);

        Assert.Equal(format.Format, texture.Header.Format);
        Assert.Equal(mips, texture.Header.MipCount);
        Assert.Equal((uint)width, texture.Header.Width);
        Assert.NotNull(texture.FormatInfo);
        Assert.True(texture.Layout.SizeMatches, texture.Layout.Note);

        // Every stored surface must come back byte-identical (no re-encode happened).
        for (int m = 0; m < mips; m++)
        {
            var linear = TextureCodec.ExtractLinearBlocks(texture, m, 0, 0, out _, out _);
            Assert.Equal(data[0][m], linear);
        }
    }

    /// <summary>
    /// The batch recompressor's core: decode the existing mip chain of an ASTC texture and re-encode
    /// those same levels as BC, without generating new mips.
    /// </summary>
    [Fact]
    public void EncodeLevels_RecompressesExistingAstcMipsToBc()
    {
        var astc = FormatTable.Get(Xt1Format.Astc8x8Unorm)!;
        var image = new RgbaImage(64, 64);
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                image.SetPixel(x, y, (byte)(x * 4), (byte)(y * 4), (byte)((x + y) * 2), (byte)(x < 32 ? 0 : 255));

        var texture = EditableTexture.CreateNew(astc.Format, 64, 64);
        var first = TextureOperations.Replace2D(texture, image, new ManagedEncoder(), true, EncodeQuality.Normal);
        Assert.True(first.Success, first.Error);
        Assert.Equal(Xt1Format.Astc8x8Unorm, texture.Header.Format);
        int sourceMips = texture.Header.MipCount;

        var levels = new List<RgbaImage>();
        for (int m = 0; m < sourceMips; m++)
        {
            var decoded = TextureCodec.Decode(texture, m, 0, 0, out var decodeError);
            Assert.NotNull(decoded);
            Assert.Null(decodeError);
            levels.Add(decoded!);
        }

        var bc1 = FormatTable.Get(Xt1Format.Bc1Unorm)!;
        texture.Header.Format = bc1.Format;
        var recompressed = TextureEncoder.EncodeLevels(texture, levels, new ManagedEncoder(), EncodeQuality.Normal);
        Assert.True(recompressed.Success, recompressed.Error);

        Assert.Equal(Xt1Format.Bc1Unorm, texture.Header.Format);
        Assert.Equal(sourceMips, texture.Header.MipCount); // no new mips were generated
        Assert.True(texture.Layout.SizeMatches, texture.Layout.Note);
        Assert.NotNull(TextureCodec.Decode(texture, 0, 0, 0, out _));
    }

    /// <summary>
    /// Flag syncing runs inside the replace operation, so Undo restores the table2 sRGB flag along
    /// with the format/payload instead of leaving the two disagreeing.
    /// </summary>
    [Fact]
    public void Replace2D_UndoRestoresSyncedFlags()
    {
        var texture = EditableTexture.CreateNew(Xt1Format.Bc1Unorm, 16, 16);
        Assert.False(texture.FormatInfo!.IsSrgb);
        var originalHeader = texture.Header.Raw.ToArray();
        var originalTable2 = (byte[])texture.Table2Bytes.Clone();

        var image = new RgbaImage(16, 16);
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                image.SetPixel(x, y, 200, 100, 50, 255);

        texture.Header.Format = Xt1Format.Bc1Srgb;
        var result = TextureOperations.Replace2D(texture, image, new ManagedEncoder(), true,
            EncodeQuality.Fastest, originalHeader, syncFlags: true);
        Assert.True(result.Success, result.Error);
        Assert.True(texture.FormatInfo!.IsSrgb);
        Assert.NotEqual(originalTable2, texture.Table2Bytes);

        var command = TextureOperations.TakePendingCommand();
        Assert.NotNull(command);
        command!.Undo();

        Assert.Equal(Xt1Format.Bc1Unorm, texture.Header.Format);
        Assert.Equal(originalTable2, texture.Table2Bytes);
    }

    /// <summary>A newly authored empty bank writes and reloads cleanly for both container shapes.</summary>
    [Theory]
    [InlineData(BankKind.EffectWta, ".wta")]
    [InlineData(BankKind.StandaloneWtb, ".wtb")]
    public void NewEmptyBank_RoundTrips(BankKind kind, string extension)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Xt1Studio", "tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "new" + extension);
            var document = BankDocument.CreateEmpty(kind, path);
            Assert.Single(document.Sections);
            Assert.Equal(0, document.Sections[0].Bank.Count);

            BankWriter.Write(document, new WriteOptions()).SaveAll();
            Assert.True(File.Exists(path));

            var reloaded = BankDocument.Load(path);
            Assert.Equal(kind, reloaded.Kind);
            Assert.Single(reloaded.Sections);
            Assert.Equal(0, reloaded.Sections[0].Bank.Count);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// A hash set on a texture added to a freshly authored split bank must survive save/reload, so
    /// the new bank has to carry a table3 array. Without it the hash is silently dropped and reads
    /// back as 0.
    /// </summary>
    [Fact]
    public void NewSplitBank_PersistsAddedTextureHash()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Xt1Studio", "tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "new.wta");
            var document = BankDocument.CreateEmpty(BankKind.EffectWta, path);

            var texture = EditableTexture.CreateNew(Xt1Format.Bc7Unorm, 8, 8);
            texture.Hash = 0xDEADBEEF;
            texture.HashMode = HashMode.Forced;
            texture.SetModelBankClass(true);
            document.Sections[0].Bank.Add(texture);

            BankWriter.Write(document, new WriteOptions()).SaveAll();

            var reloaded = BankDocument.Load(path);
            var bank = reloaded.Sections[0].Bank;
            Assert.True(bank.IncludeTable3);
            Assert.Single(bank.Textures);
            Assert.Equal(0xDEADBEEFu, bank.Textures[0].Hash);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// A bank opened without a table3 array (`t3 == 0`, e.g. an effect bank) must start writing one
    /// as soon as a texture claims a CRC32 hash; otherwise a hash set in the UI is silently dropped.
    /// </summary>
    [Fact]
    public void BankWithoutTable3_AddsItWhenATextureClaimsAHash()
    {
        var bank = WtbBank.CreateEmpty(includeTable3: false, standalone: false, payloadBase: 0);
        var texture = EditableTexture.CreateNew(Xt1Format.Bc7Unorm, 8, 8);
        bank.Add(texture);

        // With no hash claimed yet, the bank stays table3-less.
        Assert.False(WtbBank.Parse(bank.ToBytes()).IncludeTable3);

        texture.SetModelBankClass(true);
        texture.Hash = 0x12345678;
        texture.HashMode = HashMode.Forced;

        var reparsed = WtbBank.Parse(bank.ToBytes());
        Assert.True(reparsed.IncludeTable3);
        Assert.Equal(0x12345678u, reparsed.Textures[0].Hash);
    }
}
