using Xt1.Core.Xt1;
using Xunit;

namespace Xt1.Core.Tests;

public class SwizzleTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (int bpb in new[] { 1, 4, 8, 16 })
        {
            foreach (int blockHeight in new[] { 1, 2, 4, 8, 16 })
            {
                foreach (var (wb, hb) in new[] { (1, 1), (4, 4), (17, 9), (32, 32), (64, 17), (86, 86) })
                    yield return new object[] { wb, hb, bpb, blockHeight };
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void SwizzleThenDeswizzle_IsIdentity(int wb, int hb, int bpb, int blockHeight)
    {
        var linear = new byte[wb * hb * bpb];
        for (int i = 0; i < linear.Length; i++)
            linear[i] = (byte)(i * 31 + (i >> 5));

        int surfaceSize = GobSwizzle.SurfaceSize(wb, hb, bpb, blockHeight);
        var surface = new byte[surfaceSize];
        GobSwizzle.Swizzle(linear, surface, wb, hb, bpb, blockHeight);

        var back = new byte[linear.Length];
        GobSwizzle.Deswizzle(surface, back, wb, hb, bpb, blockHeight);

        Assert.Equal(linear, back);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Swizzle_PlacesEveryByteInsideSurface(int wb, int hb, int bpb, int blockHeight)
    {
        var linear = new byte[wb * hb * bpb];
        for (int i = 0; i < linear.Length; i++) linear[i] = 1;

        int surfaceSize = GobSwizzle.SurfaceSize(wb, hb, bpb, blockHeight);
        var surface = new byte[surfaceSize];
        GobSwizzle.Swizzle(linear, surface, wb, hb, bpb, blockHeight);

        int written = surface.Count(b => b == 1);
        Assert.Equal(linear.Length, written);
    }

    [Fact]
    public void SurfaceSize_MatchesPitchTimesPaddedHeight()
    {
        // 171 blocks (1024/6) with 16 bytes -> pitch 2752, 64-line padding -> 528384.
        Assert.Equal(528384, GobSwizzle.SurfaceSize(171, 171, 16, 8));
        // 16 blocks of 16 bytes -> pitch 256, 16-line padding -> 4096.
        Assert.Equal(4096, GobSwizzle.SurfaceSize(16, 16, 16, 2));
        // 2 blocks of 16 bytes -> pitch 64, 8-line padding -> 512.
        Assert.Equal(512, GobSwizzle.SurfaceSize(2, 2, 16, 1));
    }

    [Fact]
    public void BlockLinearOffset_IsABijectionOverAGob()
    {
        const int wb = 4, hb = 8, bpb = 16, bh = 1;
        var seen = new HashSet<int>();
        for (int y = 0; y < hb; y++)
            for (int x = 0; x < wb; x++)
            {
                int offset = GobSwizzle.BlockLinearOffset(x, y, wb, bpb, bh);
                Assert.True(seen.Add(offset), $"duplicate offset {offset} at ({x},{y})");
                Assert.InRange(offset, 0, 511);
            }
    }
}
