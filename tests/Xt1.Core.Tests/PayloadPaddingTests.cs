using Xt1.Core.Containers;
using Xt1.Core.Editing;
using Xt1.Core.Format;
using Xunit;

namespace Xt1.Core.Tests;

/// <summary>
/// An untouched split bank used to be written back with whatever trailing padding its `.wtp` carried.
/// A stream copied out of a container can include the container's whole trailing pad (thousands of
/// zeros no table0/table1 entry references), so saving trims it unless
/// <see cref="WriteOptions.TrimTrailingPadding"/> is disabled.
/// </summary>
public class PayloadPaddingTests
{
    private static string NewTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Xt1Studio", "padding", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    /// <summary>
    /// Author a split bank of the given payload sizes into <paramref name="directory"/>, then pad its
    /// `.wtp` with <paramref name="padBytes"/> zero bytes to simulate a stream that was copied out of
    /// a container. Returns the exact end of the referenced payloads.
    /// </summary>
    private static long WritePaddedSplitBank(string directory, int[] sizes, int padBytes)
    {
        var bank = WtbBank.CreateEmpty(includeTable3: true, standalone: false, payloadBase: 0);
        for (int i = 0; i < sizes.Length; i++)
        {
            var texture = EditableTexture.CreateNew(Xt1Format.Rgba8Unorm, 16, 16, layout: 1);
            var payload = new byte[sizes[i]];
            // Non-zero bytes so "the tail is all zeros" cannot be mistaken for the trim rule.
            for (int b = 0; b < payload.Length; b++) payload[b] = (byte)(i * 53 + b % 251 + 1);
            texture.ResetOriginalPayload(payload);
            texture.PayloadSize = (uint)payload.Length;
            texture.Header.ImageSize = (ulong)payload.Length;
            bank.Add(texture);
        }

        var (bankBytes, stream) = bank.BuildSplit(0);
        var wtaPath = Path.Combine(directory, "bank.wta");
        File.WriteAllBytes(wtaPath, bankBytes);

        var padded = new byte[stream.Length + padBytes];
        stream.CopyTo(padded, 0);
        File.WriteAllBytes(Path.ChangeExtension(wtaPath, ".wtp"), padded);
        return stream.Length;
    }

    private static byte[] WrittenPayload(BankDocument document, WriteOptions options)
    {
        var result = BankWriter.Write(document, options);
        return result.Files.Single(f => f.Path.EndsWith(".wtp", StringComparison.OrdinalIgnoreCase)).Bytes;
    }

    [Fact]
    public void UntouchedPaddedSplitBank_TrimsTrailingPadding()
    {
        var directory = NewTempDirectory();
        try
        {
            // Sizes are not 4096-aligned, so the content end sits inside the last slot and the tail
            // of the .wtp is pure pad.
            long contentEnd = WritePaddedSplitBank(directory, new[] { 3000, 5000, 1024 }, padBytes: 123456);

            var document = BankDocument.Load(Path.Combine(directory, "bank.wta"));
            var section = Assert.Single(document.Sections);

            Assert.False(section.Bank.IsDirty, "the bank must load clean for the passthrough path to run");
            Assert.True(section.PayloadStream.Length > contentEnd, "the fixture must actually be padded");

            var payload = WrittenPayload(document, new WriteOptions());

            Assert.Equal(contentEnd, payload.Length);
            Assert.True(section.PayloadStream.AsSpan(0, (int)contentEnd).SequenceEqual(payload),
                "trimming must keep every referenced payload byte");
        }
        finally { TryDeleteDirectory(directory); }
    }

    [Fact]
    public void UntouchedPaddedSplitBank_KeepsPaddingWhenDisabled()
    {
        var directory = NewTempDirectory();
        try
        {
            long contentEnd = WritePaddedSplitBank(directory, new[] { 3000, 5000 }, padBytes: 8192);

            var document = BankDocument.Load(Path.Combine(directory, "bank.wta"));
            var section = Assert.Single(document.Sections);

            var payload = WrittenPayload(document, new WriteOptions { TrimTrailingPadding = false });

            Assert.Equal(section.PayloadStream.Length, payload.Length);
            Assert.True(section.PayloadStream.AsSpan().SequenceEqual(payload));
            Assert.True(payload.Length > contentEnd);
        }
        finally { TryDeleteDirectory(directory); }
    }

    [Fact]
    public void UntouchedSplitBankWithoutPadding_IsWrittenByteForByte()
    {
        var directory = NewTempDirectory();
        try
        {
            WritePaddedSplitBank(directory, new[] { 4096, 90112 }, padBytes: 0);

            var document = BankDocument.Load(Path.Combine(directory, "bank.wta"));
            var section = Assert.Single(document.Sections);

            var payload = WrittenPayload(document, new WriteOptions());

            Assert.Equal(section.PayloadStream.Length, payload.Length);
            Assert.True(section.PayloadStream.AsSpan().SequenceEqual(payload));
        }
        finally { TryDeleteDirectory(directory); }
    }

    [Fact]
    public void EmptyBank_NeverTrimsItsStream()
    {
        var directory = NewTempDirectory();
        try
        {
            long contentEnd = WritePaddedSplitBank(directory, Array.Empty<int>(), padBytes: 4096);
            Assert.Equal(0, contentEnd);

            var document = BankDocument.Load(Path.Combine(directory, "bank.wta"));
            var section = Assert.Single(document.Sections);
            Assert.Empty(section.Bank.Textures);

            var payload = WrittenPayload(document, new WriteOptions());

            // Nothing anchors the end of an empty bank's stream, so it is kept as stored.
            Assert.Equal(section.PayloadStream.Length, payload.Length);
            Assert.True(section.PayloadStream.AsSpan().SequenceEqual(payload));
        }
        finally { TryDeleteDirectory(directory); }
    }
}
