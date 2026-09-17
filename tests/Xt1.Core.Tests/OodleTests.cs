using Xt1.Core.Interop;
using Xunit;

namespace Xt1.Core.Tests;

public class OodleTests
{
    [Fact]
    public void Oodle_Loads_AndReportsCompressionSupport()
    {
        var path = ToolLocator.Find(ToolLocator.OodleFileName);
        if (path is null) return; // optional dependency

        using var oodle = OodleCodec.TryCreate(out var error, path);
        Assert.NotNull(oodle);
        Assert.True(string.IsNullOrEmpty(error));
        // Binding at least one export means the interop signatures resolved.
        Assert.False(string.IsNullOrEmpty(oodle!.LibraryPath));
    }

    [Fact]
    public void Oodle_DecompressesRealPkzPayload()
    {
        var path = ToolLocator.Find(ToolLocator.OodleFileName);
        if (path is null) return;

        var pkz = Path.Combine(TestPaths.Romfs, "effect_000.pkz");
        if (!File.Exists(pkz)) return;

        using var oodle = OodleCodec.TryCreate(out _, path);
        if (oodle is null) return;

        var entries = PkzReader.Read(pkz);
        var compressed = entries
            .Where(e => e.Compression == "OodleKraken" && e.DecompressedSize > 0 && e.DecompressedSize < int.MaxValue)
            .Take(3)
            .ToList();
        if (compressed.Count == 0) return;

        foreach (var entry in compressed)
        {
            var raw = oodle.Decompress(entry.CompressedBytes, (long)entry.DecompressedSize);
            Assert.Equal((long)entry.DecompressedSize, raw.LongLength);
        }
    }

    [Fact]
    public void Oodle_CompressionRoundTrips_WhenSupported()
    {
        var path = ToolLocator.Find(ToolLocator.OodleFileName);
        if (path is null) return;

        using var oodle = OodleCodec.TryCreate(out _, path);
        if (oodle is null) return;
        if (!oodle.SupportsCompression) return; // decompress-only build

        var data = new byte[100_000];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)('A' + i % 7);
        var compressed = oodle.Compress(data);
        Assert.True(compressed.Length > 0);
        Assert.Equal(data, oodle.Decompress(compressed, data.Length));
    }
}
