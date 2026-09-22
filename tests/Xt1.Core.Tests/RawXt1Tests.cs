using Xt1.Core.Containers;
using Xt1.Core.Format;
using Xt1.Core.Xt1;
using Xunit;

namespace Xt1.Core.Tests;

public sealed class RawXt1Tests
{
    [Fact]
    public void ReadOnlyParse_DoesNotClonePayloadForDirtyTracking()
    {
        var header = new Xt1Header
        {
            Magic = Xt1Header.MagicValue,
            HeaderSize = Xt1Header.Size,
            Width = 1,
            Height = 1,
            Depth = 1,
            MipCount = 1,
            Type = TextureType.T2D,
            Format = Xt1Format.Rgba8Unorm,
        };
        var bytes = new RawXt1(header, new byte[] { 1, 2, 3, 4 }).ToBytes();

        var raw = RawXt1.ParseReadOnly(bytes);
        var texture = EditableTexture.FromRawReadOnly(raw);

        Assert.Same(raw.Payload, raw.OriginalPayload);
        Assert.Same(raw.Payload, texture.Payload);
        Assert.Same(texture.Payload, texture.OriginalPayload);
    }
}
