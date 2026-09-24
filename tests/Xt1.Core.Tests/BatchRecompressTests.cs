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

/// <summary>
/// Batch recompression (ASTC to BC). The real-file tests run against copies made in a temp folder
/// so a failure cannot damage the golden samples, and skip when the romfs is not present.
/// </summary>
public class BatchRecompressTests
{
    private static EncoderPicker ManagedOnly() => new(null, null);

    private static RecompressTarget Target(string label) =>
        RecompressTargets.All.First(t => t.Label == label);

    private static RecompressOptions Options(bool downscale = false, bool skipNoMips = true,
        string opaque = "BC1", string alpha = "BC3") =>
        new(Target(opaque), Target(alpha), downscale, skipNoMips, EncoderBackend.Managed,
            EncodeQuality.Fastest, EncodeQuality.Fastest, SyncFlags: true);

    [Theory]
    [InlineData(false, EncodeQuality.Fastest)]
    [InlineData(true, EncodeQuality.Highest)]
    public void QualityFor_UsesAlphaQualityOnlyWhenTheTextureHasAlpha(bool hasAlpha, EncodeQuality expected)
    {
        var options = new RecompressOptions(Target("BC1"), Target("BC7"), false, true, EncoderBackend.Nvtt,
            EncodeQuality.Fastest, EncodeQuality.Highest, true);

        Assert.Equal(expected, options.QualityFor(hasAlpha));
    }

    /// <summary>The pickers offer every encodable family, not just BC1/BC3/BC7.</summary>
    [Fact]
    public void RecompressTargets_CoverEveryEncodableFamily()
    {
        var labels = RecompressTargets.All.Select(t => t.Label).ToList();

        Assert.Contains("BC1", labels);
        Assert.Contains("BC4", labels);
        Assert.Contains("BC6H", labels);
        Assert.Contains("BC7", labels);
        Assert.Contains("RGBA8", labels);
        Assert.Contains("ASTC 8x8", labels);
        Assert.Contains(RecompressTargets.All, t => t.Unorm.Codec == CodecKind.Astc);
        Assert.Contains(RecompressTargets.All, t => t.Unorm.Codec == CodecKind.Raw);

        // The unconfirmed 0x6D and the alias ids are never offered, and every label is unique.
        Assert.DoesNotContain(RecompressTargets.All, t => t.Unorm.Format == Xt1Format.Unknown6d);
        Assert.DoesNotContain(RecompressTargets.All, t => t.Unorm.IsAlias || t.Srgb?.IsAlias == true);
        Assert.Equal(labels.Count, labels.Distinct().Count());
    }

    [Theory]
    [InlineData("BC1", true)]
    [InlineData("BC1", false)]
    [InlineData("BC7", true)]
    [InlineData("RGBA8", true)]
    [InlineData("ASTC 8x8", true)]
    [InlineData("ASTC 8x8", false)]
    public void Resolve_FollowsTheSourceColourSpace_WhenTheFamilyHasBothVariants(string label, bool srgb)
    {
        var resolved = Target(label).Resolve(srgb);

        Assert.Equal(srgb, resolved.IsSrgb);
    }

    [Theory]
    [InlineData("BC4")]
    [InlineData("BC5")]
    [InlineData("BC6H")]
    [InlineData("BGRA8")]
    [InlineData("AF_L8")]
    public void Resolve_KeepsTheOnlyVariant_WhenTheFamilyHasNoSrgbPair(string label)
    {
        var target = Target(label);

        Assert.Null(target.Srgb);
        Assert.Same(target.Unorm, target.Resolve(true));
        Assert.False(target.Resolve(true).IsSrgb);
    }

    /// <summary>Every target must be encodable by NVTT or the managed encoder, in both colour spaces.</summary>
    [Fact]
    public void EveryTarget_ResolvesToAFormatSomeEncoderCanHandle()
    {
        foreach (var target in RecompressTargets.All)
            foreach (bool srgb in new[] { false, true })
            {
                var format = target.Resolve(srgb);
                bool encodable = NvttFormatMap.InProcessSupports(format.Format)
                    || ManagedFormatMap.SupportsEncode(format);
                Assert.True(encodable, $"{target.Label} ({(srgb ? "sRGB" : "UNORM")}) has no encoder");
            }
    }

    /// <summary>An opaque texture takes the opaque target, a transparent one the alpha target.</summary>
    [Fact]
    public void Convert_RoutesOpaqueAndAlphaTexturesToTheirOwnTargets()
    {
        var opaque = BuildAstc(Xt1Format.Astc8x8Unorm, withAlpha: false);
        var alpha = BuildAstc(Xt1Format.Astc8x8Unorm, withAlpha: true);
        Assert.False(TextureCodec.Decode(opaque, 0, 0, 0, out _)!.HasTransparency(), "opaque source decoded with alpha");
        Assert.True(TextureCodec.Decode(alpha, 0, 0, 0, out _)!.HasTransparency(), "alpha source decoded opaque");

        // Deliberately the reverse of the old BC1/BC3 rule, so a hard-coded pair would fail.
        var options = Options(opaque: "BC7", alpha: "BC3");
        var recompressor = new BatchRecompressor(ManagedOnly());

        Assert.True(recompressor.Convert(opaque, options, new SyncProgress()));
        Assert.True(recompressor.Convert(alpha, options, new SyncProgress()));

        Assert.Equal(Xt1Format.Bc7Unorm, opaque.Header.Format);
        Assert.Equal(Xt1Format.Bc3Unorm, alpha.Header.Format);
    }

    /// <summary>The concrete format written is the target family's sRGB or UNORM member, per the source.</summary>
    [Theory]
    [InlineData(Xt1Format.Astc8x8Unorm, "BC1", Xt1Format.Bc1Unorm)]
    [InlineData(Xt1Format.Astc8x8Srgb, "BC1", Xt1Format.Bc1Srgb)]
    [InlineData(Xt1Format.Astc8x8Srgb, "BC7", Xt1Format.Bc7Srgb)]
    [InlineData(Xt1Format.Astc8x8Unorm, "BC4", Xt1Format.Bc4Unorm)]
    [InlineData(Xt1Format.Astc8x8Srgb, "BC4", Xt1Format.Bc4Unorm)]
    [InlineData(Xt1Format.Astc8x8Unorm, "RGBA8", Xt1Format.Rgba8Unorm)]
    [InlineData(Xt1Format.Astc8x8Srgb, "RGBA8", Xt1Format.Rgba8Srgb)]
    [InlineData(Xt1Format.Astc8x8Unorm, "ASTC 6x6", Xt1Format.Astc6x6Unorm)]
    [InlineData(Xt1Format.Astc8x8Srgb, "ASTC 6x6", Xt1Format.Astc6x6Srgb)]
    public void Convert_ResolvesTheTargetsColourSpaceFromTheSource(
        Xt1Format sourceFormat, string target, Xt1Format expected)
    {
        var texture = BuildAstc(sourceFormat, withAlpha: false);
        var recompressor = new BatchRecompressor(ManagedOnly());

        Assert.True(recompressor.Convert(texture, Options(opaque: target), new SyncProgress()));

        Assert.Equal(expected, texture.Header.Format);
    }

    /// <summary>A target no available encoder supports is refused and the texture is left untouched.</summary>
    [Fact]
    public void Convert_LeavesTheTextureUntouched_WhenNoEncoderSupportsTheTarget()
    {
        var texture = BuildAstc(Xt1Format.Astc8x8Unorm, withAlpha: false);
        var recompressor = new BatchRecompressor(ManagedOnly());

        // The managed encoder cannot encode BC6H, and no NVTT instance is supplied here.
        Assert.False(recompressor.Convert(texture, Options(opaque: "BC6H"), new SyncProgress()));

        Assert.Equal(Xt1Format.Astc8x8Unorm, texture.Header.Format);
        Assert.Equal(CodecKind.Astc, texture.FormatInfo!.Codec);
    }

    /// <summary>Build a one-mip ASTC texture of the given colour space, opaque or with a transparent half.</summary>
    private static EditableTexture BuildAstc(Xt1Format astcFormat, bool withAlpha, int size = 64)
    {
        var format = FormatTable.Get(astcFormat)!;
        var image = new RgbaImage(size, size);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                image.SetPixel(x, y, (byte)(x * 4), (byte)(y * 4), (byte)((x + y) * 2),
                    (byte)(withAlpha && x < size / 2 ? 0 : 255));

        var texture = EditableTexture.CreateNew(format.Format, (uint)size, (uint)size);
        var built = TextureOperations.Replace2D(texture, image, new ManagedEncoder(), true,
            EncodeQuality.Fastest);
        Assert.True(built.Success, built.Error);
        return texture;
    }

    private static string NewTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Xt1Studio", "recompress", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Recompress one ASTC texture from a real model bank and prove the saved file reloads.</summary>
    [Fact]
    public void RecompressesRealModelBankTexture()
    {
        if (!TestPaths.Exists(TestPaths.Pl0080Dat)) return;
        var sourceDtt = Path.ChangeExtension(TestPaths.Pl0080Dat, ".dtt");
        if (!File.Exists(sourceDtt)) return;

        var directory = NewTempDirectory();
        try
        {
            var dat = Path.Combine(directory, "pl0080.dat");
            File.Copy(TestPaths.Pl0080Dat, dat);
            File.Copy(sourceDtt, Path.Combine(directory, "pl0080.dtt"));

            var document = BankDocument.Load(dat);
            var texture = document.AllTextures
                .Where(t => t.FormatInfo?.Codec == CodecKind.Astc)
                .OrderBy(t => (long)t.Header.Width * t.Header.Height)
                .First();
            bool wasSrgb = texture.FormatInfo!.IsSrgb;
            int mipCount = texture.Header.MipCount;
            int width = (int)texture.Header.Width;

            var recompressor = new BatchRecompressor(ManagedOnly());
            Assert.True(recompressor.Convert(texture, Options(), new SyncProgress()));

            Assert.Equal(CodecKind.Bc, texture.FormatInfo!.Codec);
            Assert.Equal(wasSrgb, texture.FormatInfo!.IsSrgb);
            Assert.Equal(mipCount, texture.Header.MipCount);
            Assert.Equal(width, (int)texture.Header.Width);
            Assert.True(texture.Layout.SizeMatches, texture.Layout.Note);
            Assert.NotNull(TextureCodec.Decode(texture, 0, 0, 0, out _));

            BankWriter.Write(document, new WriteOptions()).SaveAll();

            var reloaded = BankDocument.Load(dat);
            var saved = reloaded.AllTextures.First(t => t.Index == texture.Index);
            Assert.Equal(CodecKind.Bc, saved.FormatInfo!.Codec);
            Assert.Equal(mipCount, saved.Header.MipCount);
            Assert.NotNull(TextureCodec.Decode(saved, 0, 0, 0, out _));
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void RecompressTitleTextureToBc3_KeepsOrdinaryGobPitch()
    {
        var path = Path.Combine(TestPaths.Romfs, "ui", "ui_title_us.dat");
        if (!File.Exists(path)) return;

        var document = BankDocument.Load(path);
        var texture = document.Sections[0].Bank.Textures[1];
        Assert.Equal(Xt1Format.Astc12x12Unorm, texture.Header.Format);
        Assert.Equal(0x3u, texture.Header.Layout);

        var recompressor = new BatchRecompressor(ManagedOnly());
        Assert.True(recompressor.Convert(texture, Options(), new SyncProgress()));

        Assert.Equal(Xt1Format.Bc3Unorm, texture.Header.Format);
        Assert.False(texture.Layout.WidePitchApplied);
        Assert.Equal(4160, texture.Layout.Mips[0].Pitch);
        Assert.Equal(798720UL, texture.Header.ImageSize);
        Assert.True(texture.Layout.SizeMatches, texture.Layout.Note);
    }

    /// <summary>Downscaling drops mip 0: the saved texture is half the size with one fewer mip.</summary>
    [Fact]
    public void DownscaleOmitsBaseMip_OnRealBank()
    {
        if (!TestPaths.Exists(TestPaths.Pl0080Dat)) return;
        var sourceDtt = Path.ChangeExtension(TestPaths.Pl0080Dat, ".dtt");
        if (!File.Exists(sourceDtt)) return;

        var directory = NewTempDirectory();
        try
        {
            var dat = Path.Combine(directory, "pl0080.dat");
            File.Copy(TestPaths.Pl0080Dat, dat);
            File.Copy(sourceDtt, Path.Combine(directory, "pl0080.dtt"));

            var document = BankDocument.Load(dat);
            var texture = document.AllTextures
                .Where(t => t.FormatInfo?.Codec == CodecKind.Astc)
                .OrderBy(t => (long)t.Header.Width * t.Header.Height)
                .First();
            Assert.True(texture.Header.MipCount > 1, "expected the golden texture to have mips");
            int width = (int)texture.Header.Width;
            int mipCount = texture.Header.MipCount;

            var recompressor = new BatchRecompressor(ManagedOnly());
            Assert.True(recompressor.Convert(texture, Options(downscale: true), new SyncProgress()));

            Assert.Equal(width / 2, (int)texture.Header.Width);
            Assert.Equal(mipCount - 1, texture.Header.MipCount);
            Assert.True(texture.Layout.SizeMatches, texture.Layout.Note);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    /// <summary>Folder/file mode writes in place: a synthetic standalone WTB round-trips through it.</summary>
    [Fact]
    public void RunOnFiles_RecompressesStandaloneWtb()
    {
        var directory = NewTempDirectory();
        try
        {
            var path = Path.Combine(directory, "sample.wtb");
            var document = BankDocument.CreateEmpty(BankKind.StandaloneWtb, path);

            var astc = FormatTable.Get(Xt1Format.Astc8x8Unorm)!;
            var image = new RgbaImage(64, 64);
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++)
                    image.SetPixel(x, y, (byte)(x * 4), (byte)(y * 4), (byte)((x + y) * 2),
                        (byte)(x < 32 ? 0 : 255));

            var texture = EditableTexture.CreateNew(astc.Format, 64, 64);
            var first = TextureOperations.Replace2D(texture, image, new ManagedEncoder(), true,
                EncodeQuality.Fastest);
            Assert.True(first.Success, first.Error);
            int mipCount = texture.Header.MipCount;
            document.Sections[0].Bank.Add(texture);

            BankWriter.Write(document, new WriteOptions()).SaveAll();
            Assert.True(File.Exists(path));

            var recompressor = new BatchRecompressor(ManagedOnly());
            var stats = recompressor.RunOnFiles(new[] { path }, Options(), new SyncProgress(),
                CancellationToken.None);
            Assert.Equal(1, stats.Converted);

            var reloaded = BankDocument.Load(path);
            var saved = Assert.Single(reloaded.AllTextures);
            Assert.Equal(CodecKind.Bc, saved.FormatInfo!.Codec);
            Assert.Equal(mipCount, saved.Header.MipCount);
            Assert.True(saved.Layout.SizeMatches, saved.Layout.Note);
            Assert.NotNull(TextureCodec.Decode(saved, 0, 0, 0, out _));
        }
        finally
        {
            TryDelete(directory);
        }
    }

    /// <summary>
    /// A texture whose payload is shorter than its layout must be skipped rather than re-encoded from
    /// the zero fill, otherwise folder mode would overwrite a broken source with an encode of zeros.
    /// </summary>
    [Fact]
    public void Convert_SkipsTextureWithTruncatedPayload()
    {
        var astc = FormatTable.Get(Xt1Format.Astc8x8Unorm)!;
        var image = new RgbaImage(64, 64);
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                image.SetPixel(x, y, (byte)(x * 4), (byte)(y * 4), 128, 255);

        var texture = EditableTexture.CreateNew(astc.Format, 64, 64);
        var built = TextureOperations.Replace2D(texture, image, new ManagedEncoder(), true,
            EncodeQuality.Fastest);
        Assert.True(built.Success, built.Error);
        TextureOperations.TakePendingCommand();

        Assert.True(texture.Layout.Applicable);
        var truncated = new byte[Math.Max(1, texture.Payload.Length / 2)];
        texture.Payload = truncated;
        texture.PayloadSize = (uint)truncated.Length;

        var recompressor = new BatchRecompressor(ManagedOnly());
        Assert.False(recompressor.Convert(texture, Options(), new SyncProgress()));
    }

    /// <summary>Raw `.xt1` files are recompressed too (the wrapper's payload is written back).</summary>
    [Fact]
    public void RunOnFiles_RecompressesRawXt1()
    {
        var directory = NewTempDirectory();
        try
        {
            var path = Path.Combine(directory, "sample.xt1");
            var astc = FormatTable.Get(Xt1Format.Astc6x6Unorm)!;
            var image = new RgbaImage(48, 48);
            for (int y = 0; y < 48; y++)
                for (int x = 0; x < 48; x++)
                    image.SetPixel(x, y, (byte)(x * 5), (byte)(y * 5), 90, 255);

            var texture = EditableTexture.CreateNew(astc.Format, 48, 48);
            var first = TextureOperations.Replace2D(texture, image, new ManagedEncoder(), true,
                EncodeQuality.Fastest);
            Assert.True(first.Success, first.Error);
            int mipCount = texture.Header.MipCount;

            File.WriteAllBytes(path, new RawXt1(texture.Header.Clone(), texture.Payload).ToBytes());

            var recompressor = new BatchRecompressor(ManagedOnly());
            var stats = recompressor.RunOnFiles(new[] { path }, Options(), new SyncProgress(),
                CancellationToken.None);
            Assert.Equal(1, stats.Converted);

            var raw = RawXt1.Parse(File.ReadAllBytes(path));
            var info = FormatTable.Get(raw.Header.Format);
            Assert.NotNull(info);
            Assert.Equal(CodecKind.Bc, info!.Codec);
            Assert.Equal(mipCount, raw.Header.MipCount);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    /// <summary>
    /// The HDR cubemap decode keeps the values above 1.0 that the 8-bit preview tonemaps away, so
    /// the exposure control has real headroom to work with.
    /// </summary>
    [Fact]
    public void HdrCubeDecode_PreservesValuesAboveOne()
    {
        if (!TestPaths.Exists(TestPaths.CubeMapWtb)) return;
        var document = BankDocument.Load(TestPaths.CubeMapWtb);
        var cube = document.AllTextures.FirstOrDefault(t => t.Header.Type.IsCube() && TextureCodec.IsHdr(t.FormatInfo));
        if (cube is null) return;

        var faces = TextureCodec.DecodeCubeFacesHdr(cube, 0, out var error);
        Assert.Equal(6, faces.Count);
        Assert.Null(error);

        float max = 0f;
        foreach (var face in faces)
            foreach (var value in face.Pixels)
                if (value > max) max = value;
        Assert.True(max > 1f, $"expected HDR headroom, max was {max}");

        // The 8-bit preview path clamps: at least one HDR pixel must differ from its tonemapped byte.
        var tonemapped = TextureCodec.Decode(cube, 0, 0, 0, out _);
        Assert.NotNull(tonemapped);
    }

    private sealed class SyncProgress : IProgress<string>
    {
        public List<string> Lines { get; } = new();

        public void Report(string value) => Lines.Add(value);
    }

    private static void TryDelete(string directory)
    {
        try { Directory.Delete(directory, recursive: true); } catch { }
    }
}
