using Xt1.Core.Format;
using Xt1.Core.Util;

namespace Xt1.Core.Imaging;

/// <summary>A parsed DDS texture (2D, cube, or volume; all mips).</summary>
public sealed class DdsImage
{
    public uint DxgiFormat { get; init; }
    public FormatInfo? Format { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Depth { get; init; } = 1;
    public int MipCount { get; init; } = 1;
    public int ArraySize { get; init; } = 1;
    public bool IsCube { get; init; }

    /// <summary>[array/face][mip] raw surface bytes.</summary>
    public required byte[][][] Data { get; init; }

    public int FaceCount => IsCube ? ArraySize * 6 : ArraySize;
}

/// <summary>DDS DX10 reader/writer for the formats this tool uses.</summary>
public static class DdsIo
{
    private const uint Magic = 0x20534444; // "DDS "

    private const int MaxDimension = 1 << 16;
    private const int MaxVolumeDepth = 1 << 12;
    private const int MaxMipCount = 32;
    private const int MaxArraySize = 2048;

    private static readonly Dictionary<uint, Xt1Format> DxgiToXt1 = new()
    {
        [28] = Xt1Format.Rgba8Unorm,
        [29] = Xt1Format.Rgba8Srgb,
        [87] = Xt1Format.Bgra8Unorm,
        [71] = Xt1Format.Bc1Unorm,
        [72] = Xt1Format.Bc1Srgb,
        [74] = Xt1Format.Bc2Unorm,
        [75] = Xt1Format.Bc2Srgb,
        [77] = Xt1Format.Bc3Unorm,
        [78] = Xt1Format.Bc3Srgb,
        [80] = Xt1Format.Bc4Unorm,
        [81] = Xt1Format.Bc4Snorm,
        [83] = Xt1Format.Bc5Unorm,
        [84] = Xt1Format.Bc5Snorm,
        [95] = Xt1Format.Bc6hUf16,
        [98] = Xt1Format.Bc7Unorm,
        [99] = Xt1Format.Bc7Srgb,
    };

    private static readonly Dictionary<Xt1Format, uint> Xt1ToDxgi =
        DxgiToXt1.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);

    public static bool CanExport(FormatInfo format) => Xt1ToDxgi.ContainsKey(format.Format);

    public static bool IsDds(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && Mem.ReadU32LE(data, 0) == Magic;

    public static DdsImage Read(byte[] data)
    {
        if (!IsDds(data)) throw new InvalidDataException("not a DDS file");
        int height = (int)Mem.ReadU32LE(data, 0x0C);
        int width = (int)Mem.ReadU32LE(data, 0x10);
        int depth = (int)Mem.ReadU32LE(data, 0x18);
        int mipCount = (int)Mem.ReadU32LE(data, 0x1C);
        uint fourCc = Mem.ReadU32LE(data, 0x54);
        bool hasDx10 = fourCc == 0x30315844; // "DX10"

        // Dimensions and counts come straight from an untrusted file; clamp them before they are
        // used as array lengths or loop bounds.
        width = Math.Clamp(width, 1, MaxDimension);
        height = Math.Clamp(height, 1, MaxDimension);
        depth = Math.Clamp(depth, 1, MaxVolumeDepth);
        mipCount = Math.Clamp(mipCount <= 0 ? 1 : mipCount, 1, MaxMipCount);

        uint dxgi = 0;
        int arraySize = 1;
        bool isCube = false;
        int dataOffset;
        if (hasDx10)
        {
            dxgi = Mem.ReadU32LE(data, 0x80);
            int resourceDimension = (int)Mem.ReadU32LE(data, 0x84);
            uint miscFlag = Mem.ReadU32LE(data, 0x88);
            arraySize = Math.Clamp((int)Mem.ReadU32LE(data, 0x8C), 1, MaxArraySize);
            isCube = (miscFlag & 0x4) != 0;
            dataOffset = 0x94;
            if (resourceDimension == 4 && depth <= 1) depth = 1;
        }
        else
        {
            // Legacy DXT formats.
            dxgi = fourCc switch
            {
                0x31545844 => 71, // DXT1
                0x33545844 => 74, // DXT3
                0x35545844 => 77, // DXT5
                _ => 0u,
            };
            dataOffset = 0x80;
        }

        var xt1Format = DxgiToXt1.TryGetValue(dxgi, out var mapped) ? mapped : (Xt1Format?)null;
        var info = xt1Format is null ? null : FormatTable.Get((uint)xt1Format);

        int faceCount = isCube ? arraySize * 6 : arraySize;
        var faces = new byte[faceCount][][];
        int offset = dataOffset;
        for (int face = 0; face < faceCount; face++)
        {
            faces[face] = new byte[mipCount][];
            for (int mip = 0; mip < mipCount; mip++)
            {
                int w = Math.Max(1, width >> mip);
                int h = Math.Max(1, height >> mip);
                int d = Math.Max(1, depth >> mip);
                int size = info is null ? 0 : SurfaceSize(info, w, h, d);
                if (offset < 0 || offset + size > data.Length) size = Math.Max(0, data.Length - offset);
                faces[face][mip] = size > 0 ? data.AsSpan(offset, size).ToArray() : Array.Empty<byte>();
                offset += size;
            }
        }

        return new DdsImage
        {
            DxgiFormat = dxgi,
            Format = info,
            Width = width,
            Height = height,
            Depth = depth,
            MipCount = mipCount,
            ArraySize = arraySize,
            IsCube = isCube,
            Data = faces,
        };
    }

    /// <summary>Write a DX10 DDS from raw stored surfaces (no swizzle; DDS is linear).</summary>
    public static byte[] Write(FormatInfo format, int width, int height, int depth, int mipCount,
        int faceCount, bool isCube, byte[][][] data)
    {
        if (!Xt1ToDxgi.TryGetValue(format.Format, out uint dxgi))
            throw new NotSupportedException($"format {format.Name} cannot be written to DDS");

        int arraySize = isCube ? Math.Max(1, faceCount / 6) : Math.Max(1, faceCount);
        bool volume = depth > 1;

        var header = new byte[0x94];
        Mem.WriteU32LE(header, 0x00, Magic);
        Mem.WriteU32LE(header, 0x04, 124);
        Mem.WriteU32LE(header, 0x08, 0x0002100F); // standard texture flags
        Mem.WriteU32LE(header, 0x0C, (uint)height);
        Mem.WriteU32LE(header, 0x10, (uint)width);
        Mem.WriteU32LE(header, 0x18, (uint)depth);
        Mem.WriteU32LE(header, 0x1C, (uint)mipCount);
        Mem.WriteU32LE(header, 0x4C, 32);        // pixel format size
        Mem.WriteU32LE(header, 0x50, 0x4);       // DDPF_FOURCC
        Mem.WriteU32LE(header, 0x54, 0x30315844); // "DX10"
        Mem.WriteU32LE(header, 0x6C, 0x1000 | 0x8 | (volume ? 0x200000u : 0u) | (isCube ? 0x200u : 0u));

        Mem.WriteU32LE(header, 0x80, dxgi);
        Mem.WriteU32LE(header, 0x84, volume ? 4u : 3u); // resourceDimension 3D / 2D
        Mem.WriteU32LE(header, 0x88, isCube ? 4u : 0u);
        Mem.WriteU32LE(header, 0x8C, (uint)arraySize);

        using var stream = new MemoryStream();
        stream.Write(header);
        for (int face = 0; face < data.Length; face++)
            for (int mip = 0; mip < data[face].Length; mip++)
                stream.Write(data[face][mip]);
        return stream.ToArray();
    }

    private static int SurfaceSize(FormatInfo info, int w, int h, int d)
    {
        int wb = Mem.DivUp(w, info.BlockWidth);
        int hb = Mem.DivUp(h, info.BlockHeight);
        return wb * hb * info.BytesPerBlock * d;
    }
}
