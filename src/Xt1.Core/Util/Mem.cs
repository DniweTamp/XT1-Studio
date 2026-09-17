using System.Buffers.Binary;

namespace Xt1.Core.Util;

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
