using System.Buffers.Binary;
using System.Text;

namespace Xt1.Core.Tests;

/// <summary>
/// Minimal read-only `.pkz` reader used only by tests to obtain real Oodle-compressed payloads
/// and validate the Oodle interop. The product itself does not read or write pkz archives.
/// </summary>
internal static class PkzReader
{
    internal sealed record Entry(string Name, string Compression, ulong DecompressedSize, byte[] CompressedBytes);

    internal static bool HasMagic(ReadOnlySpan<byte> b) =>
        b.Length >= 0x20 && b[0] == (byte)'p' && b[1] == (byte)'k' && b[2] == (byte)'z' && b[3] == (byte)'l';

    internal static IReadOnlyList<Entry> Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (!HasMagic(bytes))
            throw new InvalidDataException("not a pkz");

        uint entryCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x14, 4));
        uint tableOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x18, 4));
        int stringBase = checked((int)tableOffset + (int)entryCount * 0x20);

        var entries = new List<Entry>((int)entryCount);
        for (int i = 0; i < entryCount; i++)
        {
            int at = 0x20 + i * 0x20;
            uint nameOff = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at, 4));
            uint compOff = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 4, 4));
            ulong decSize = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at + 8, 8));
            ulong dataOff = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at + 0x10, 8));
            ulong encSize = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at + 0x18, 8));

            string name = ReadString(bytes, stringBase + (int)nameOff);
            string compression = ReadString(bytes, stringBase + (int)compOff);
            var data = bytes.AsSpan((int)dataOff, (int)encSize).ToArray();
            entries.Add(new Entry(name, compression, decSize, data));
        }
        return entries;
    }

    private static string ReadString(byte[] bytes, int offset)
    {
        int end = offset;
        while (end < bytes.Length && bytes[end] != 0) end++;
        return System.Text.Encoding.ASCII.GetString(bytes, offset, end - offset);
    }
}
