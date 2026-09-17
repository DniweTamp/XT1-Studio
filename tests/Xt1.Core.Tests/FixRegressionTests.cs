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

/// <summary>
/// Regression tests for the defects found in review. Each test is written so that it fails
/// against the pre-fix behavior (stale payload offsets, standalone rebasing, truncated payload
/// throws, dropped dirty state, non-atomic saves).
/// </summary>
public class FixRegressionTests
{
    private static string NewTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Xt1Studio", "regression", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    /// <summary>
    /// A payload-size change must leave table0 consistent with the payload stream. Before the fix
    /// the WTB was serialized before offsets were recomputed, so every texture after the resized
    /// one pointed at the wrong payload.
    /// </summary>
    [Fact]
    public void EditedPayloadSize_KeepsTable0ConsistentWithPayloadStream()
    {
        if (!TestPaths.Exists(TestPaths.Em0000Dat)) return;
        var document = BankDocument.Load(TestPaths.Em0000Dat);
        var section = document.Sections[0];
        var texture = section.Bank.Textures.First(t => t.FormatInfo is { Codec: CodecKind.Bc or CodecKind.Astc });
        Assert.True(texture.Index + 1 < section.Bank.Count,
            "need at least one texture after the edited one for offsets to shift");

        var replacement = new RgbaImage(128, 96);
        replacement.Fill(20, 180, 60, 255);
        var encoder = new ManagedEncoder();

        var result = TextureOperations.Replace2D(texture, replacement, encoder, true, EncodeQuality.Normal);
        Assert.True(result.Success, result.Error);

        var directory = NewTempDirectory();
        try
        {
            var output = BankWriter.Write(document, new WriteOptions { OutputDirectory = directory });
            output.SaveAll();

            var datPath = Path.Combine(directory, Path.GetFileName(TestPaths.Em0000Dat));
            var reloaded = BankDocument.Load(datPath);
            var reloadedSection = reloaded.Sections[0];

            // table0 must be the align4096 cumulative sum of table1 ...
            long expected = 0;
            foreach (var tex in reloadedSection.Bank.Textures)
            {
                Assert.Equal((uint)expected, tex.PayloadOffset);
                Assert.Equal(tex.Header.ImageSize, tex.PayloadSize);
                expected = Mem.Align(expected + tex.PayloadSize, 4096);
            }

            // ... and the payload stream must cover exactly that layout (no invented padding).
            Assert.Equal(expected, reloadedSection.PayloadStream.Length);

            // every texture must still decode from its own offset
            foreach (var tex in reloadedSection.Bank.Textures.Where(t => TextureCodec.CanDecode(t.FormatInfo)))
            {
                var image = TextureCodec.Decode(tex, 0, 0, 0, out var error);
                Assert.NotNull(image);
                Assert.Equal((int)tex.Header.Width, image!.Width);
                Assert.Null(error);
            }
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    /// <summary>
    /// A standalone bank whose tables/XT1 region exceeds 0x1000 must move its payloads above the
    /// structural region. Before the fix the payload base was raised but the payload offsets were
    /// not rebased, so payloads overwrote the tables and XT1 headers.
    /// </summary>
    [Fact]
    public void StandaloneBank_LargeStructuralRegion_RebasesPayloads()
    {
        const int textureCount = 70; // enough that the structural region exceeds 0x1000
        var bank = WtbBank.CreateEmpty(includeTable3: true, standalone: true, payloadBase: 0x1000);

        for (int i = 0; i < textureCount; i++)
        {
            var texture = EditableTexture.CreateNew(Xt1Format.Rgba8Unorm, 2, 2, layout: 1);
            var payload = new byte[16];
            for (int b = 0; b < payload.Length; b++) payload[b] = (byte)(i * 16 + b);
            texture.ResetOriginalPayload(payload);
            texture.PayloadSize = (uint)payload.Length;
            texture.Header.ImageSize = (ulong)payload.Length;
            bank.Add(texture);
        }

        var bytes = StandaloneWtbWriter.Build(bank, new WriteOptions());
        var reparsed = WtbBank.Parse(bytes);

        Assert.Equal(textureCount, reparsed.Count);
        Assert.True(reparsed.IsStandalone);

        int structuralEnd = (int)reparsed.Xt1Offset + textureCount * Xt1Header.Size;
        Assert.True(structuralEnd > 0x1000, "test setup must exceed the default payload base");
        Assert.True(reparsed.PayloadBase >= structuralEnd,
            $"payload base 0x{reparsed.PayloadBase:X} must clear the structural end 0x{structuralEnd:X}");

        for (int i = 0; i < textureCount; i++)
        {
            var texture = reparsed.Textures[i];

            // The XT1 header must still be readable, i.e. the tables were not clobbered.
            Assert.Equal(Xt1Header.MagicValue, texture.Header.Magic);
            Assert.Equal(2u, texture.Header.Width);
            Assert.Equal(1, texture.Header.MipCount);

            // The payload must sit above the structural region and round-trip.
            Assert.True(texture.PayloadOffset >= reparsed.PayloadBase,
                $"texture {i} payload at 0x{texture.PayloadOffset:X} overlaps the structural region");
            Assert.Equal(16, texture.Payload.Length);
            Assert.Equal((byte)(i * 16), texture.Payload[0]);
            Assert.Equal((byte)(i * 16 + 15), texture.Payload[15]);
        }
    }

    /// <summary>
    /// A payload shorter than the computed surface must decode with zero-fill. Before the fix the
    /// deswizzle read past the span and threw.
    /// </summary>
    [Fact]
    public void TruncatedPayload_DecodesWithZeroFillInsteadOfThrowing()
    {
        var texture = EditableTexture.CreateNew(Xt1Format.Bc3Unorm, 64, 64, layout: 1);
        var payload = new byte[100]; // far less than one 64x64 BC3 surface (4096 bytes)
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)i;
        texture.Payload = payload;
        texture.PayloadSize = (uint)payload.Length;
        texture.Header.ImageSize = (ulong)payload.Length;

        var image = TextureCodec.Decode(texture, 0, 0, 0, out var warning);

        Assert.NotNull(image);
        Assert.Equal(64, image!.Width);
        Assert.Equal(64, image.Height);
        Assert.NotNull(warning); // the short-payload warning is still surfaced
    }

    /// <summary>Truncation must not throw even at the extreme (single byte present).</summary>
    [Fact]
    public void SeverelyTruncatedPayload_DoesNotThrow()
    {
        var texture = EditableTexture.CreateNew(Xt1Format.Bc1Unorm, 256, 256, layout: 1);
        texture.Payload = new byte[] { 0xFF };
        texture.PayloadSize = 1;
        texture.Header.ImageSize = 1;

        var image = TextureCodec.Decode(texture, 0, 0, 0, out _);
        Assert.NotNull(image);
    }

    /// <summary>
    /// The "sync table1 from imageSize" preference must mark the bank dirty and take effect,
    /// instead of being silently dropped by the early-return path.
    /// </summary>
    [Fact]
    public void SyncTable1WithImageSize_IsHonouredAndNotSilentlyDropped()
    {
        if (!TestPaths.Exists(TestPaths.Em0000Dat)) return;
        var dat = DatFile.Parse(File.ReadAllBytes(TestPaths.Em0000Dat));
        var wta = dat.FindByTag("wta");
        Assert.NotNull(wta);

        var bank = WtbBank.Parse(wta!.Bytes);
        Assert.False(bank.IsDirty);

        bank.SyncTable1WithImageSize = true;
        Assert.True(bank.IsDirty);

        var reparsed = WtbBank.Parse(bank.ToBytes());
        foreach (var texture in reparsed.Textures)
            Assert.Equal((uint)texture.Header.ImageSize, texture.PayloadSize);
    }

    /// <summary>
    /// The in-process and CLI NVTT capability lists are derived from one table, so a format that
    /// only one backend supports must be reported consistently (and never silently conflict).
    /// </summary>
    [Fact]
    public void NvttSupport_IsConsistentBetweenBackends()
    {
        // Signed BC4/BC5 exist in-process but have no nvtt_export CLI id.
        Assert.True(NvttFormatMap.InProcessSupports(Xt1Format.Bc4Snorm));
        Assert.False(NvttFormatMap.CliSupports(Xt1Format.Bc4Snorm));
        Assert.True(NvttFormatMap.InProcessSupports(Xt1Format.Bc5Snorm));
        Assert.False(NvttFormatMap.CliSupports(Xt1Format.Bc5Snorm));

        foreach (var format in new[]
                 {
                     Xt1Format.Bc1Unorm, Xt1Format.Bc1Srgb, Xt1Format.Bc2Srgb, Xt1Format.Bc3Unorm,
                     Xt1Format.Bc4Unorm, Xt1Format.Bc5Unorm, Xt1Format.Bc6hUf16, Xt1Format.Bc7Srgb,
                     Xt1Format.Astc4x4Unorm, Xt1Format.Astc12x12Srgb,
                 })
        {
            Assert.True(NvttFormatMap.InProcessSupports(format), $"in-process should support {format}");
            Assert.True(NvttFormatMap.CliSupports(format), $"CLI should support {format}");
            Assert.True(NvttFormatMap.TryGetCliFormatId(format, out int id));
            Assert.True(id > 0);
        }

        Assert.False(NvttFormatMap.InProcessSupports(Xt1Format.Rgba8Unorm));
        Assert.False(NvttFormatMap.CliSupports(Xt1Format.Rgba8Unorm));
    }

    /// <summary>SaveAll must write every file and leave no staging files behind.</summary>
    [Fact]
    public void WriteResult_SaveAll_WritesEveryFileWithoutStagingLeftovers()
    {
        var directory = NewTempDirectory();
        try
        {
            var first = Path.Combine(directory, "a.dat");
            var second = Path.Combine(directory, "a.dtt");
            var result = new WriteResult(new[]
            {
                new OutputFile(first, new byte[] { 1, 2, 3, 4 }),
                new OutputFile(second, new byte[] { 5, 6, 7, 8 }),
            }, Array.Empty<string>());

            result.SaveAll();

            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(first));
            Assert.Equal(new byte[] { 5, 6, 7, 8 }, File.ReadAllBytes(second));
            Assert.Empty(Directory.GetFiles(directory, "*.xt1-staging-*"));
            Assert.Empty(Directory.GetFiles(directory, "*.xt1-backup-*"));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    /// <summary>
    /// A failure while staging must leave pre-existing files untouched (the old implementation
    /// wrote directly to the final paths).
    /// </summary>
    [Fact]
    public void WriteResult_SaveAll_FailureLeavesOriginalsIntact()
    {
        var directory = NewTempDirectory();
        try
        {
            var first = Path.Combine(directory, "a.dat");
            File.WriteAllBytes(first, new byte[] { 1, 2, 3 });

            // An invalid character in the path makes the second staging write fail.
            var bad = Path.Combine(directory, "bad|name.dtt");
            var result = new WriteResult(new[]
            {
                new OutputFile(first, new byte[] { 9, 9, 9 }),
                new OutputFile(bad, new byte[] { 8 }),
            }, Array.Empty<string>());

            Assert.ThrowsAny<Exception>(() => result.SaveAll());

            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(first));
            Assert.Empty(Directory.GetFiles(directory, "*.xt1-staging-*"));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    /// <summary>
    /// A failure while *swapping* must undo the files that were already replaced. The staging-failure
    /// test above cannot catch this: there the swap never starts. Here the first move succeeds and the
    /// second fails, so without a rollback the bank is left with one side updated and its partner stale.
    /// </summary>
    [Fact]
    public void WriteResult_SaveAll_SwapFailureRestoresAlreadyReplacedFiles()
    {
        var directory = NewTempDirectory();
        try
        {
            var first = Path.Combine(directory, "a.dat");
            File.WriteAllBytes(first, new byte[] { 1, 2, 3 });

            // A directory at the second destination makes the second move fail, after the first
            // destination has already been replaced.
            var blocked = Path.Combine(directory, "a.dtt");
            Directory.CreateDirectory(blocked);

            var result = new WriteResult(new[]
            {
                new OutputFile(first, new byte[] { 9, 9, 9 }),
                new OutputFile(blocked, new byte[] { 8 }),
            }, Array.Empty<string>());

            Assert.ThrowsAny<Exception>(() => result.SaveAll());

            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(first));
            Assert.Empty(Directory.GetFiles(directory, "*.xt1-staging-*"));
            Assert.Empty(Directory.GetFiles(directory, "*.xt1-backup-*"));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    /// <summary>
    /// Untrusted dimensions must be refused, not allocated. A crafted 18-byte TGA used to size its
    /// buffers straight from the 16-bit header fields.
    /// </summary>
    [Fact]
    public void TgaIo_RejectsImplausibleDimensions()
    {
        var data = new byte[18];
        data[2] = 2;            // uncompressed truecolor
        data[12] = 0xFF;        // width = 65535
        data[13] = 0xFF;
        data[14] = 0xFF;        // height = 8191
        data[15] = 0x1F;
        data[16] = 32;          // 32bpp

        Assert.Throws<InvalidDataException>(() => TgaIo.Read(data));
    }

    /// <summary>
    /// A header can advertise dimensions whose derived block counts would allocate gigabytes and spin
    /// the swizzle loops; such geometry must produce no layout so the decoders refuse it.
    /// </summary>
    [Fact]
    public void SurfaceLayout_RejectsImplausibleDimensions()
    {
        var header = new Xt1Header
        {
            Width = 32767,
            Height = 8192,
            Depth = 1,
            MipCount = 1,
            Type = TextureType.T2D,
            Layout = 1,
            FormatValue = (uint)Xt1Format.Rgba8Unorm,
        };

        var layout = SurfaceLayout.Compute(header);

        Assert.False(layout.Applicable);
        Assert.Contains("implausible", layout.Note);
    }

    /// <summary>
    /// The managed path must not claim signed BC4/BC5: BCnEncoder's Bc4/Bc5 are unsigned, so claiming
    /// them decoded signed data as unsigned and re-encoded it as unsigned under a SNORM header. They
    /// route to NVTT, which has BC4_SIGNED/BC5_SIGNED.
    /// </summary>
    [Fact]
    public void ManagedPath_DoesNotClaimSignedBc4OrBc5()
    {
        var bc4Snorm = FormatTable.Get(Xt1Format.Bc4Snorm)!;
        var bc5Snorm = FormatTable.Get(Xt1Format.Bc5Snorm)!;
        var bc4Unorm = FormatTable.Get(Xt1Format.Bc4Unorm)!;
        var bc5Unorm = FormatTable.Get(Xt1Format.Bc5Unorm)!;

        Assert.False(ManagedFormatMap.SupportsDecode(bc4Snorm));
        Assert.False(ManagedFormatMap.SupportsEncode(bc4Snorm));
        Assert.False(ManagedFormatMap.SupportsDecode(bc5Snorm));
        Assert.False(ManagedFormatMap.SupportsEncode(bc5Snorm));

        // The unsigned variants stay in the managed path.
        Assert.True(ManagedFormatMap.SupportsDecode(bc4Unorm));
        Assert.True(ManagedFormatMap.SupportsEncode(bc4Unorm));
        Assert.True(ManagedFormatMap.SupportsDecode(bc5Unorm));
        Assert.True(ManagedFormatMap.SupportsEncode(bc5Unorm));

        // ... and NVTT still covers the signed ones, so nothing became unencodable.
        Assert.True(NvttFormatMap.InProcessSupports(Xt1Format.Bc4Snorm));
        Assert.True(NvttFormatMap.InProcessSupports(Xt1Format.Bc5Snorm));
    }

    /// <summary>The layout cache must reflect header edits (imageSize drives SizeMatches).</summary>
    [Fact]
    public void LayoutCache_InvalidatesWhenHeaderChanges()
    {
        var texture = EditableTexture.CreateNew(Xt1Format.Rgba8Unorm, 64, 64, layout: 1);
        ulong expected = (ulong)texture.Layout.ComputedImageSize;
        texture.Header.ImageSize = expected;

        var first = texture.Layout;
        Assert.True(first.SizeMatches);

        texture.Header.ImageSize = expected - 1;
        var second = texture.Layout;

        Assert.False(second.SizeMatches);
        Assert.Equal(expected - 1, second.StoredImageSize);
        Assert.NotSame(first, second);
    }
}
