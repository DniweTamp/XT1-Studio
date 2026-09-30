using Xt1.Core.Util;

namespace Xt1.Core.Banks;

/// <summary>Native texture container of a WTB bank.</summary>
public enum TextureBankFormat
{
    /// <summary>Bayonetta 1 (PC): <c>WTB\0</c>, version 0, DDS textures stored inline, no hashes.</summary>
    Dds,

    /// <summary>Bayonetta 2 (Wii U): <c>\0BTW</c> (big-endian), version 1. A 0xC0 GX2 record per
    /// texture in the bank and the pixel/mipmap data in the paired stream, matched by hash.</summary>
    Gtx,

    /// <summary>Bayonetta 2 (Switch): <c>WTB\0</c>, version 1, complete BNTX blobs in the paired
    /// stream, matched by hash.</summary>
    Bntx,
}

/// <summary>
/// One texture inside a <see cref="TextureBank"/>, held as raw native bytes. No pixel decoding,
/// encoding or swizzling happens here: the bytes are exactly what the game stores, so the mod
/// manager can export a texture to a file and put a replacement file back unchanged.
/// </summary>
public sealed class BankTexture
{
    public int Index { get; internal set; }

    /// <summary>Whether the bank stores an identity hash for this texture.</summary>
    public bool HasHash { get; internal set; }

    /// <summary>Stored <c>table3</c> value, matched against a mod file name.</summary>
    public uint Hash { get; internal set; }

    /// <summary>Raw 4-byte <c>table2</c> flags entry. Never rewritten by the import path.</summary>
    public byte[] Flags { get; internal set; } = new byte[4];

    /// <summary>Per-texture record in the bank's infos table (0xC0 GX2 record for
    /// <see cref="TextureBankFormat.Gtx"/>), or empty when the bank has no infos table.</summary>
    public byte[] Descriptor { get; internal set; } = Array.Empty<byte>();

    /// <summary>Pixel payload, or the complete native blob for DDS/BNTX.</summary>
    public byte[] Data { get; internal set; } = Array.Empty<byte>();

    /// <summary>Mipmap payload (GX2 banks only); empty when the texture has no mipmaps.</summary>
    public byte[] MipmapData { get; internal set; } = Array.Empty<byte>();

    public bool HasMipmaps => MipmapData.Length > 0;

    public override string ToString() =>
        HasHash ? $"#{Index} 0x{Hash:X8} ({Data.Length} bytes)" : $"#{Index} ({Data.Length} bytes)";
}

/// <summary>
/// A format-agnostic WTB texture bank for the mod manager: parse, list textures, export one texture
/// to its native file, replace one texture from a file, and rebuild the bank plus its payload stream.
///
/// Covers Bayonetta 1 (inline DDS) and both Bayonetta 2 variants (Wii U GTX, Switch BNTX). Bayonetta 3
/// XT1 banks keep their richer interactive editor in <see cref="Xt1.Core.Containers.WtbBank"/>; the
/// two share <see cref="Mem"/>, the masked-CRC32 hash and the same table walk.
///
/// Importing replaces only the target texture's own descriptor/data/offset/size. The flags table and
/// the hash table are always carried over byte-for-byte and every other texture is untouched, so a
/// bank's identity and metadata cannot drift.
/// </summary>
public sealed class TextureBank
{
    private const int MaxCount = 1_000_000;
    private const long MaxTotalPayload = 1L * 1024 * 1024 * 1024;

    private readonly List<BankTexture> _textures = new();
    private readonly byte[] _originalBankBytes;
    private readonly byte[]? _originalPayloadStream;
    private bool _changed;

    public TextureBankFormat Format { get; }
    public ByteOrder Order { get; }
    public uint Version { get; private set; }

    /// <summary>Whether the bank carries the Wii U mipmap-offset array (header word 0x20).</summary>
    public bool HasMipmapTable { get; private set; }

    /// <summary>Whether the bank carries a <c>table3</c> identity-hash array.</summary>
    public bool HasHashTable { get; private set; }

    /// <summary>True for the self-contained Bayonetta 1 form (payload inline); false when pixel data
    /// lives in a paired stream (<c>.wtp</c> / <c>"wtp"</c>).</summary>
    public bool IsStandalone { get; private set; }

    public IReadOnlyList<BankTexture> Textures => _textures;
    public int Count => _textures.Count;

    /// <summary>True when a texture was replaced and the layout must be rebuilt on save.</summary>
    public bool HasChanged => _changed;

    private TextureBank(TextureBankFormat format, ByteOrder order, byte[] bankBytes, byte[]? payloadStream)
    {
        Format = format;
        Order = order;
        _originalBankBytes = bankBytes;
        _originalPayloadStream = payloadStream;
    }

    // ---- parse ----

    /// <summary>
    /// Parse a bank. <paramref name="payloadStream"/> is the paired stream (<c>.wtp</c> or the
    /// <c>"wtp"</c> sub-file) for split banks; omit it for the standalone Bayonetta 1 form. The
    /// format and byte order are detected from the magic and stream unless forced.
    /// </summary>
    public static TextureBank Parse(byte[] bankBytes, byte[]? payloadStream = null,
        TextureBankFormat? format = null, ByteOrder? order = null)
    {
        if (bankBytes.Length < 0x20)
            throw new InvalidDataException("WTB bank is too small");

        ByteOrder bo = order ?? DetectOrder(bankBytes);
        TextureBankFormat fmt = format ?? DetectFormat(bankBytes, payloadStream, bo);

        uint version = Mem.ReadU32(bankBytes, 0x04, bo);
        int count = checked((int)Mem.ReadU32(bankBytes, 0x08, bo));
        if (count < 0 || count > MaxCount)
            throw new InvalidDataException($"implausible WTB texture count {count}");

        uint t0 = Mem.ReadU32(bankBytes, 0x0C, bo);
        uint t1 = Mem.ReadU32(bankBytes, 0x10, bo);
        uint t2 = Mem.ReadU32(bankBytes, 0x14, bo);
        uint t3 = Mem.ReadU32(bankBytes, 0x18, bo);
        uint t4 = Mem.ReadU32(bankBytes, 0x1C, bo);
        uint mipTable = fmt == TextureBankFormat.Gtx && bankBytes.Length >= 0x24
            ? Mem.ReadU32(bankBytes, 0x20, bo)
            : 0;

        var bank = new TextureBank(fmt, bo, (byte[])bankBytes.Clone(), payloadStream)
        {
            Version = version,
            HasMipmapTable = mipTable != 0,
            HasHashTable = t3 != 0,
            IsStandalone = payloadStream is null && fmt == TextureBankFormat.Dds,
        };

        long total = 0;
        for (int i = 0; i < count; i++)
        {
            uint offset = Table(bankBytes, t0, i, bo);
            uint size = Table(bankBytes, t1, i, bo);
            byte[] flags = ReadFixed(bankBytes, t2 + (uint)(4 * i), 4);
            byte[] descriptor = fmt == TextureBankFormat.Gtx
                ? ReadFixed(bankBytes, t4 + (uint)(0xC0 * i), 0xC0)
                : Array.Empty<byte>();

            byte[] data = Array.Empty<byte>();
            byte[] mip = Array.Empty<byte>();
            switch (fmt)
            {
                case TextureBankFormat.Dds:
                case TextureBankFormat.Bntx:
                    data = Slice(bank.IsStandalone ? bankBytes : payloadStream, offset, size);
                    break;
                case TextureBankFormat.Gtx:
                {
                    byte[]? stream = payloadStream ?? bankBytes;
                    int dataLength = descriptor.Length >= 0x24 ? (int)Mem.ReadU32(descriptor, 0x20, bo) : 0;
                    int mipLength = descriptor.Length >= 0x2C ? (int)Mem.ReadU32(descriptor, 0x28, bo) : 0;
                    int mipCount = descriptor.Length >= 0x14 ? (int)Mem.ReadU32(descriptor, 0x10, bo) : 0;
                    data = Slice(stream, offset, (uint)Math.Max(0, dataLength));
                    if (mipCount > 1 && mipLength > 0)
                        mip = Slice(stream, Table(bankBytes, mipTable, i, bo), (uint)mipLength);
                    break;
                }
            }

            total += data.Length + mip.Length;
            if (total > MaxTotalPayload)
                throw new InvalidDataException($"WTB payloads exceed {MaxTotalPayload / (1024 * 1024)} MB");

            bank._textures.Add(new BankTexture
            {
                Index = i,
                HasHash = t3 != 0,
                Hash = t3 != 0 ? Table(bankBytes, t3, i, bo) : 0,
                Flags = flags,
                Descriptor = descriptor,
                Data = data,
                MipmapData = mip,
            });
        }

        return bank;
    }

    private static ByteOrder DetectOrder(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == (byte)'B' && bytes[2] == (byte)'T' && bytes[3] == (byte)'W'
            ? ByteOrder.Big
            : ByteOrder.Little;

    /// <summary>True when the bytes are a WTB bank header (either byte order).</summary>
    public static bool HasMagic(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 4 &&
        ((bytes[0] == (byte)'W' && bytes[1] == (byte)'T' && bytes[2] == (byte)'B' && bytes[3] == 0) ||
         (bytes[0] == 0 && bytes[1] == (byte)'B' && bytes[2] == (byte)'T' && bytes[3] == (byte)'W'));

    /// <summary>Detect a bank's pixel format and byte order from its header and optional stream.</summary>
    public static (TextureBankFormat Format, ByteOrder Order) Detect(byte[] bankBytes, byte[]? payloadStream)
    {
        ByteOrder order = DetectOrder(bankBytes);
        return (DetectFormat(bankBytes, payloadStream, order), order);
    }

    private static TextureBankFormat DetectFormat(byte[] bankBytes, byte[]? payloadStream, ByteOrder order)
    {
        if (order == ByteOrder.Big) return TextureBankFormat.Gtx;
        if (payloadStream is { Length: >= 4 } &&
            payloadStream[0] == (byte)'B' && payloadStream[1] == (byte)'N' &&
            payloadStream[2] == (byte)'T' && payloadStream[3] == (byte)'X')
            return TextureBankFormat.Bntx;
        return TextureBankFormat.Dds;
    }

    public BankTexture? FindByIndex(int index) =>
        index >= 0 && index < _textures.Count ? _textures[index] : null;

    /// <summary>Every texture whose stored identity hash matches (duplicates all match).</summary>
    public IEnumerable<BankTexture> FindByHash(uint hash) =>
        _textures.Where(t => t.HasHash && t.Hash == hash);

    // ---- export / import ----

    /// <summary>
    /// Export one texture to its native file bytes: a standalone GTX for <see cref="TextureBankFormat.Gtx"/>,
    /// otherwise the stored blob (DDS/BNTX).
    /// </summary>
    public byte[] ExportTexture(BankTexture texture) =>
        Format == TextureBankFormat.Gtx ? BuildGtx(texture) : (byte[])texture.Data.Clone();

    /// <summary>
    /// Replace one texture from a native file. The file must match the bank format; descriptor, data
    /// and (GX2) mipmaps come from it. Flags, hashes and other textures are left untouched.
    /// </summary>
    public void ImportTexture(BankTexture texture, byte[] fileBytes)
    {
        switch (Format)
        {
            case TextureBankFormat.Dds:
                RequireMagic(fileBytes, "DDS ", "DDS");
                texture.Data = (byte[])fileBytes.Clone();
                break;
            case TextureBankFormat.Bntx:
                RequireMagic(fileBytes, "BNTX", "BNTX");
                texture.Data = (byte[])fileBytes.Clone();
                break;
            case TextureBankFormat.Gtx:
            {
                RequireMagic(fileBytes, "Gfx2", "GTX");
                var (gx2, data, mip) = ParseGtx(fileBytes);
                // Keep the trailing 0x24 bytes of the 0xC0 record (GX2 registers) that the GTX
                // surface block does not carry.
                var descriptor = texture.Descriptor.Length == 0xC0 ? texture.Descriptor : new byte[0xC0];
                Array.Copy(gx2, 0, descriptor, 0, Math.Min(0x9C, gx2.Length));
                texture.Descriptor = descriptor;
                texture.Data = data;
                texture.MipmapData = mip;
                break;
            }
            default:
                throw new NotSupportedException($"cannot import into a {Format} bank");
        }
        _changed = true;
    }

    private static void RequireMagic(ReadOnlySpan<byte> bytes, string ascii, string label)
    {
        if (bytes.Length < 4)
            throw new InvalidDataException($"{label} file is too small");
        for (int i = 0; i < ascii.Length; i++)
            if (bytes[i] != (byte)ascii[i])
                throw new InvalidDataException($"not a {label} file (bad magic)");
    }

    // ---- rebuild ----

    /// <summary>Rebuild the bank file; the exact original bytes are returned when nothing changed.</summary>
    public byte[] ToBytes()
    {
        if (!_changed) return (byte[])_originalBankBytes.Clone();
        Rebuild();
        return _rebuiltBank!;
    }

    /// <summary>Rebuild the paired payload stream (<c>.wtp</c>); null for standalone banks.</summary>
    public byte[]? ToPayloadStream()
    {
        if (IsStandalone) return null;
        if (!_changed) return _originalPayloadStream is null ? null : (byte[])_originalPayloadStream.Clone();
        Rebuild();
        return _rebuiltStream;
    }

    private byte[]? _rebuiltBank;
    private byte[]? _rebuiltStream;

    private void Rebuild()
    {
        int count = _textures.Count;
        int stride = Mem.Align(count * 4, 0x20);
        int tableBase = Format == TextureBankFormat.Dds ? 0x20 : 0x40;

        uint t0 = (uint)tableBase;
        uint t1 = (uint)(t0 + stride);
        uint t2 = (uint)(t1 + stride);
        uint t3 = _textures.Any(t => t.HasHash) ? (uint)(t2 + stride) : 0u;
        uint t4 = Format == TextureBankFormat.Gtx ? (uint)((t3 != 0 ? t3 : t2) + stride) : 0u;
        uint afterInfos = t4 != 0
            ? (uint)(t4 + Mem.Align(0xC0 * count, 0x20))
            : (t3 != 0 ? t3 : t2) + (uint)stride;
        uint mipTable = HasMipmapTable ? afterInfos : 0u;
        uint structuralEndU = (mipTable != 0 ? mipTable : afterInfos) + (uint)stride;
        int structuralEnd = (int)structuralEndU;

        int align = Format switch
        {
            TextureBankFormat.Dds => 0x1000,
            _ => 0x2000,
        };

        // Walk payloads; offsets are stream-relative for split banks, dataBase-relative for standalone.
        var dataOffsets = new uint[count];
        var mipOffsets = new uint[count];
        long cursor = 0;
        for (int i = 0; i < count; i++)
        {
            cursor = Mem.Align(cursor, align);
            dataOffsets[i] = (uint)cursor;
            cursor = Mem.Align(cursor + _textures[i].Data.Length, align);
        }
        for (int i = 0; i < count; i++)
        {
            if (!_textures[i].HasMipmaps) { mipOffsets[i] = 0; continue; }
            cursor = Mem.Align(cursor, align);
            mipOffsets[i] = (uint)cursor;
            cursor = Mem.Align(cursor + _textures[i].MipmapData.Length, align);
        }

        long dataBase = IsStandalone ? Mem.Align(structuralEnd, align) : 0;
        long bankLength = IsStandalone ? dataBase + cursor : Math.Max(structuralEnd, (int)t0 + stride * 4);
        var bank = new byte[bankLength];

        WriteMagic(bank);
        Mem.WriteU32(bank, 0x04, Version, Order);
        Mem.WriteU32(bank, 0x08, (uint)count, Order);
        Mem.WriteU32(bank, 0x0C, t0, Order);
        Mem.WriteU32(bank, 0x10, t1, Order);
        Mem.WriteU32(bank, 0x14, t2, Order);
        Mem.WriteU32(bank, 0x18, t3, Order);
        Mem.WriteU32(bank, 0x1C, t4, Order);
        if (Format == TextureBankFormat.Gtx && bank.Length >= 0x24)
            Mem.WriteU32(bank, 0x20, mipTable, Order);

        for (int i = 0; i < count; i++)
        {
            var tex = _textures[i];
            uint stored = (uint)(dataBase + dataOffsets[i]);
            Mem.WriteU32(bank, (int)t0 + 4 * i, stored, Order);
            uint size = Format == TextureBankFormat.Gtx ? (uint)GtxSize(tex) : (uint)tex.Data.Length;
            Mem.WriteU32(bank, (int)t1 + 4 * i, size, Order);
            tex.Flags.CopyTo(bank, (int)t2 + 4 * i);
            if (t3 != 0) Mem.WriteU32(bank, (int)t3 + 4 * i, tex.Hash, Order);
            if (t4 != 0)
            {
                int at = (int)t4 + 0xC0 * i;
                int copy = Math.Min(tex.Descriptor.Length, 0xC0);
                Array.Copy(tex.Descriptor, 0, bank, at, copy);
            }
        }
        if (mipTable != 0)
            for (int i = 0; i < count; i++)
                Mem.WriteU32(bank, (int)mipTable + 4 * i, mipOffsets[i], Order);

        if (IsStandalone)
        {
            for (int i = 0; i < count; i++)
                _textures[i].Data.CopyTo(bank, (int)(dataBase + dataOffsets[i]));
            _rebuiltStream = null;
        }
        else
        {
            var stream = new byte[cursor];
            for (int i = 0; i < count; i++)
            {
                var tex = _textures[i];
                tex.Data.CopyTo(stream, dataOffsets[i]);
                if (tex.HasMipmaps) tex.MipmapData.CopyTo(stream, mipOffsets[i]);
            }
            _rebuiltStream = stream;
        }

        _rebuiltBank = bank;
    }

    private void WriteMagic(byte[] output)
    {
        if (Order == ByteOrder.Big)
        {
            output[0] = 0; output[1] = (byte)'B'; output[2] = (byte)'T'; output[3] = (byte)'W';
        }
        else
        {
            output[0] = (byte)'W'; output[1] = (byte)'T'; output[2] = (byte)'B'; output[3] = 0;
        }
    }

    // ---- GTX container (GX2 surface bytes copied verbatim; only the block framing is added) ----

    /// <summary>Total size of the standalone GTX Kerilk's layout produces for one texture.</summary>
    private static int GtxSize(BankTexture tex)
    {
        int size = 0x20 + 0x20 + 0x9C + 0x20 + tex.Data.Length + 0x20;
        if (tex.HasMipmaps) size += 0x20 + tex.MipmapData.Length;
        return size;
    }

    private static byte[] BuildGtx(BankTexture tex)
    {
        var output = new byte[GtxSize(tex)];
        int p = 0;
        Ascii(output, ref p, "Gfx2");
        U32(output, ref p, 0x20); U32(output, ref p, 7); U32(output, ref p, 1);
        U32(output, ref p, 2); U32(output, ref p, 0); U32(output, ref p, 0); U32(output, ref p, 0);

        Blk(output, ref p, 0x0B, 0x9C);
        Array.Copy(tex.Descriptor, 0, output, p, Math.Min(0x9C, tex.Descriptor.Length)); p += 0x9C;

        Blk(output, ref p, 0x0C, tex.Data.Length);
        tex.Data.CopyTo(output, p); p += tex.Data.Length;

        if (tex.HasMipmaps)
        {
            Blk(output, ref p, 0x0D, tex.MipmapData.Length);
            tex.MipmapData.CopyTo(output, p); p += tex.MipmapData.Length;
        }

        Blk(output, ref p, 0x01, 0);
        return output;
    }

    private static (byte[] Descriptor, byte[] Data, byte[] Mipmap) ParseGtx(byte[] gtx)
    {
        byte[] descriptor = new byte[0xC0];
        byte[] data = Array.Empty<byte>();
        byte[] mip = Array.Empty<byte>();

        uint headerSize = gtx.Length >= 0x08 ? Mem.ReadU32LE(gtx, 0x04) : 0x20;
        int p = headerSize is > 0 and < 0x1000 ? (int)headerSize : 0x20;
        while (p + 0x20 <= gtx.Length)
        {
            if (gtx[p] != (byte)'B' || gtx[p + 1] != (byte)'L' || gtx[p + 2] != (byte)'K' || gtx[p + 3] != (byte)'{')
                break;
            uint type = Mem.ReadU32LE(gtx, p + 0x10);
            int length = (int)Mem.ReadU32LE(gtx, p + 0x14);
            p += 0x20;
            if (type == 0x01) break;
            if (length < 0 || p + length > gtx.Length)
                throw new InvalidDataException("truncated GTX block");
            switch (type)
            {
                case 0x0B: Array.Copy(gtx, p, descriptor, 0, Math.Min(length, descriptor.Length)); break;
                case 0x0C: data = gtx.AsSpan(p, length).ToArray(); break;
                case 0x0D: mip = gtx.AsSpan(p, length).ToArray(); break;
                default: throw new InvalidDataException($"unsupported GTX block type 0x{type:X}");
            }
            p += length;
        }

        return (descriptor, data, mip);
    }

    private static void Blk(byte[] output, ref int p, uint type, int length)
    {
        Ascii(output, ref p, "BLK{");
        U32(output, ref p, 0x20); U32(output, ref p, 1); U32(output, ref p, 0);
        U32(output, ref p, type); U32(output, ref p, (uint)length);
        U32(output, ref p, 0); U32(output, ref p, 0);
    }

    private static void Ascii(byte[] output, ref int p, string s)
    {
        for (int i = 0; i < s.Length; i++) output[p + i] = (byte)s[i];
        p += s.Length;
    }

    private static void U32(byte[] output, ref int p, uint value)
    {
        Mem.WriteU32LE(output, p, value);
        p += 4;
    }

    // ---- helpers ----

    private static uint Table(byte[] bytes, uint tableOffset, int index, ByteOrder order)
    {
        if (tableOffset == 0) return 0;
        int at = (int)tableOffset + 4 * index;
        return at >= 0 && at + 4 <= bytes.Length ? Mem.ReadU32(bytes, at, order) : 0u;
    }

    private static byte[] ReadFixed(byte[] bytes, uint offset, int length)
    {
        var result = new byte[length];
        int at = (int)offset;
        if (at < 0 || at >= bytes.Length) return result;
        Array.Copy(bytes, at, result, 0, Math.Min(length, bytes.Length - at));
        return result;
    }

    private static byte[] Slice(byte[]? bytes, uint offset, uint size)
    {
        if (bytes is null || size == 0) return Array.Empty<byte>();
        long at = offset;
        if (at < 0 || at >= bytes.Length) return Array.Empty<byte>();
        long len = Math.Min(size, bytes.Length - at);
        return bytes.AsSpan((int)at, (int)len).ToArray();
    }
}
