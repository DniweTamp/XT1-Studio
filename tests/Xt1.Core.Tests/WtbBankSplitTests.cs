using Xt1.Core.Containers;
using Xunit;

namespace Xt1.Core.Tests;

/// <summary>Covers <see cref="WtbBank.BuildSplit"/>, the split-bank rebuild the mod manager uses
/// after replacing a texture's header and payload.</summary>
public class WtbBankSplitTests
{
    [Fact]
    public void RealSplitBank_BuildSplit_RoundTripsTexturesAndHashes()
    {
        if (!TestPaths.Exists(TestPaths.PreTextureWta) || !TestPaths.Exists(TestPaths.PreTextureWtp))
            return;

        var wta = File.ReadAllBytes(TestPaths.PreTextureWta);
        var wtp = File.ReadAllBytes(TestPaths.PreTextureWtp);
        var bank = WtbBank.Parse(wta, wtp);

        var before = bank.Textures.Select(t => (t.Hash, t.Payload.Length, t.Table2WordBE)).ToArray();

        var (newBank, newStream) = bank.BuildSplit(0);
        var reparsed = WtbBank.Parse(newBank, newStream);

        Assert.Equal(before.Length, reparsed.Count);
        var after = reparsed.Textures.Select(t => (t.Hash, t.Payload.Length, t.Table2WordBE)).ToArray();
        Assert.Equal(before, after);
        // Payloads are stream-relative and 4096-aligned.
        Assert.All(reparsed.Textures, t => Assert.Equal(0u, t.PayloadOffset % 4096));
    }
}
