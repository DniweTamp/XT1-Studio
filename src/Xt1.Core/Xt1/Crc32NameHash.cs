using System.Text;

namespace Xt1.Core.Xt1;

/// <summary>
/// WTB <c>table3</c> name hash: <c>crc32(name) &amp; 0x7FFFFFFF</c> (zlib polynomial), stored
/// little-endian. Confirmed against six named banks (see XT1_FORMAT.md section 4).
/// </summary>
public static class Crc32NameHash
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    public static uint Compute(string text) => Compute(System.Text.Encoding.ASCII.GetBytes(text));

    /// <summary>The value stored in <c>table3</c> for a texture name.</summary>
    public static uint ComputeMasked(string name) => Compute(name) & 0x7FFFFFFFu;

    public static uint ComputeMasked(ReadOnlySpan<byte> asciiName) => Compute(asciiName) & 0x7FFFFFFFu;
}
