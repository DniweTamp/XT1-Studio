using Xt1.Core.Xt1;

namespace Xt1.Core.Containers;

/// <summary>
/// A raw `.xt1` blob: the 56-byte XT1 header followed by its payload. The header-only form is
/// tolerated on load (payload empty).
/// </summary>
public sealed class RawXt1
{
    public Xt1Header Header { get; }

    public byte[] Payload { get; set; }

    public byte[] OriginalPayload { get; }

    public bool IsDirty => Header.IsDirty || !Payload.AsSpan().SequenceEqual(OriginalPayload);

    public RawXt1(Xt1Header header, byte[] payload, bool trackOriginal = true)
    {
        Header = header;
        Payload = payload;
        OriginalPayload = trackOriginal ? (byte[])payload.Clone() : payload;
    }

    public static bool HasMagic(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 4 && Util.Mem.ReadU32LE(bytes, 0) == Xt1Header.MagicValue;

    public static RawXt1 Parse(byte[] bytes)
        => Parse(bytes, trackOriginal: true);

    /// <summary>
    /// Parse a loose texture without an extra dirty-tracking payload clone. Intended for read-only
    /// consumers such as the Explorer thumbnail host.
    /// </summary>
    public static RawXt1 ParseReadOnly(byte[] bytes)
        => Parse(bytes, trackOriginal: false);

    private static RawXt1 Parse(byte[] bytes, bool trackOriginal)
    {
        if (!HasMagic(bytes))
            throw new InvalidDataException("not a raw XT1 blob (missing \"XT1\\0\" magic)");
        var header = Xt1Header.Parse(bytes.AsSpan(0, Math.Min(bytes.Length, Xt1Header.Size)));
        var payload = bytes.Length > Xt1Header.Size
            ? bytes.AsSpan(Xt1Header.Size).ToArray()
            : Array.Empty<byte>();
        return new RawXt1(header, payload, trackOriginal);
    }

    public static RawXt1 CreateEmpty()
    {
        var header = new Xt1Header
        {
            Magic = Xt1Header.MagicValue,
            Flags = Xt1Header.FlagsValue,
            HeaderSize = Xt1Header.Size,
            Layout2 = Xt1Header.Layout2Value,
            MipCount = 1,
            Width = 1,
            Height = 1,
            Depth = 1,
            Type = TextureType.T2D,
        };
        return new RawXt1(header, Array.Empty<byte>());
    }

    public byte[] ToBytes()
    {
        var output = new byte[Xt1Header.Size + Payload.Length];
        Header.Raw.CopyTo(output, 0);
        Payload.CopyTo(output, Xt1Header.Size);
        return output;
    }
}
