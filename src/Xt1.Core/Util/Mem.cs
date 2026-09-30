using System.Buffers.Binary;

namespace Xt1.Core.Util;

/// <summary>Byte order of a container. PlatinumGames PC/Switch data is little-endian, the
/// Wii U (PowerPC) ports are big-endian. XT1 headers themselves are always little-endian.</summary>
public enum ByteOrder
{
    Little,
    Big,
}

/// <summary>Little/big-endian read/write helpers over spans. All XT1 structures are
/// little-endian except the WTB <c>table2</c> flag bytes, which are stored big-endian.</summary>
public static class Mem
{
    public static uint ReadU32LE(ReadOnlySpan<byte> b, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(offset, 4));

    public static ulong ReadU64LE(ReadOnlySpan<byte> b, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(offset, 8));

    public static uint ReadU32BE(ReadOnlySpan<byte> b, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(b.Slice(offset, 4));

    public static void WriteU32LE(Span<byte> b, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(offset, 4), value);

    public static void WriteU64LE(Span<byte> b, int offset, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(b.Slice(offset, 8), value);

    public static void WriteU32BE(Span<byte> b, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(b.Slice(offset, 4), value);

    // ---- order-parameterised helpers (DAT/WTB containers; see ByteOrder) ----

    public static uint ReadU32(ReadOnlySpan<byte> b, int offset, ByteOrder order) =>
        order == ByteOrder.Little ? ReadU32LE(b, offset) : ReadU32BE(b, offset);

    public static ulong ReadU64(ReadOnlySpan<byte> b, int offset, ByteOrder order) =>
        order == ByteOrder.Little
            ? BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(offset, 8))
            : BinaryPrimitives.ReadUInt64BigEndian(b.Slice(offset, 8));

    public static ushort ReadU16(ReadOnlySpan<byte> b, int offset, ByteOrder order) =>
        order == ByteOrder.Little
            ? BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(offset, 2))
            : BinaryPrimitives.ReadUInt16BigEndian(b.Slice(offset, 2));

    public static short ReadI16(ReadOnlySpan<byte> b, int offset, ByteOrder order) =>
        order == ByteOrder.Little
            ? BinaryPrimitives.ReadInt16LittleEndian(b.Slice(offset, 2))
            : BinaryPrimitives.ReadInt16BigEndian(b.Slice(offset, 2));

    public static void WriteU32(Span<byte> b, int offset, uint value, ByteOrder order)
    {
        if (order == ByteOrder.Little) WriteU32LE(b, offset, value);
        else WriteU32BE(b, offset, value);
    }

    public static void WriteU64(Span<byte> b, int offset, ulong value, ByteOrder order)
    {
        if (order == ByteOrder.Little) WriteU64LE(b, offset, value);
        else BinaryPrimitives.WriteUInt64BigEndian(b.Slice(offset, 8), value);
    }

    public static void WriteU16(Span<byte> b, int offset, ushort value, ByteOrder order)
    {
        if (order == ByteOrder.Little) BinaryPrimitives.WriteUInt16LittleEndian(b.Slice(offset, 2), value);
        else BinaryPrimitives.WriteUInt16BigEndian(b.Slice(offset, 2), value);
    }

    public static void WriteI16(Span<byte> b, int offset, short value, ByteOrder order)
    {
        if (order == ByteOrder.Little) BinaryPrimitives.WriteInt16LittleEndian(b.Slice(offset, 2), value);
        else BinaryPrimitives.WriteInt16BigEndian(b.Slice(offset, 2), value);
    }

    public static uint Magic(char a, char b, char c, char d) =>
        (uint)a | ((uint)b << 8) | ((uint)c << 16) | ((uint)d << 24);

    /// <summary>Round up to the next multiple of <paramref name="align"/> (align must be a power of two).</summary>
    public static int Align(int value, int align) => (value + align - 1) & ~(align - 1);

    public static long Align(long value, long align) => (value + align - 1) & ~(align - 1);

    public static int DivUp(int a, int b) => (a + b - 1) / b;

    public static int Pow2RoundUp(int x)
    {
        if (x <= 1) return 1;
        x -= 1;
        x |= x >> 1;
        x |= x >> 2;
        x |= x >> 4;
        x |= x >> 8;
        x |= x >> 16;
        return x + 1;
    }

    public static string Hex(uint value) => "0x" + value.ToString("X8");
    public static string Hex(ulong value) => "0x" + value.ToString("X16");
    public static string HexBytes(ReadOnlySpan<byte> bytes)
    {
        var sb = new System.Text.StringBuilder(bytes.Length * 3);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(bytes[i].ToString("X2"));
        }
        return sb.ToString();
    }
}
