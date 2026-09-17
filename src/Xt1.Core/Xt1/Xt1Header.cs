using Xt1.Core.Format;
using Xt1.Core.Util;

namespace Xt1.Core.Xt1;

/// <summary>
/// The 56-byte (0x38) XT1 texture header (XT1_FORMAT.md section 5). The full raw bytes are
/// preserved so unknown/reserved fields survive an edit round-trip; typed accessors write
/// straight into the raw buffer.
/// </summary>
public sealed class Xt1Header
{
    public const int Size = 0x38;

    /// <summary>"XT1\0" little-endian.</summary>
    public const uint MagicValue = 0x00315458;

    /// <summary>Constant 0x01000101 at offset 0x04.</summary>
    public const uint FlagsValue = 0x01000101;

    /// <summary>Constant 0x00010007 at offset 0x34.</summary>
    public const uint Layout2Value = 0x00010007;

    /// <summary>Codec tag "af" (little-endian 'a' | 'f'&lt;&lt;8) stored in the high word of 0x14 for format 0x01.</summary>
    public const ushort AfCodecTag = 0x6661;

    private readonly byte[] _raw;
    private readonly byte[] _original;

    public Xt1Header()
        : this(new byte[Size], isOriginal: true)
    {
    }

    private Xt1Header(byte[] raw, bool isOriginal)
    {
        _raw = raw;
        _original = isOriginal ? (byte[])raw.Clone() : new byte[Size];
    }

    private Xt1Header(byte[] raw, byte[] original)
    {
        _raw = raw;
        _original = original;
    }

    /// <summary>Parse a header from exactly 0x38 bytes.</summary>
    public static Xt1Header Parse(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size)
            throw new ArgumentException($"XT1 header needs {Size} bytes, got {source.Length}.", nameof(source));
        var raw = source[..Size].ToArray();
        var header = new Xt1Header(raw, isOriginal: false);
        header.AcceptOriginal();
        return header;
    }

    /// <summary>The live header bytes. Mutating this bypasses change tracking; prefer the typed setters.</summary>
    public byte[] Raw => _raw;

    /// <summary>The bytes as loaded from disk (for diff-based restore/save).</summary>
    public byte[] OriginalBytes => _original;

    public bool IsDirty => !_raw.AsSpan().SequenceEqual(_original);

    /// <summary>Snapshot the current bytes as the "original" reference.</summary>
    public void AcceptOriginal() => _raw.CopyTo(_original, 0);


    public Xt1Header Clone() => new((byte[])_raw.Clone(), (byte[])_original.Clone());

    public uint Magic { get => Mem.ReadU32LE(_raw, 0x00); set => Mem.WriteU32LE(_raw, 0x00, value); }
    public uint Flags { get => Mem.ReadU32LE(_raw, 0x04); set => Mem.WriteU32LE(_raw, 0x04, value); }
    public ulong ImageSize { get => Mem.ReadU64LE(_raw, 0x08); set => Mem.WriteU64LE(_raw, 0x08, value); }
    public uint HeaderSize { get => Mem.ReadU32LE(_raw, 0x10); set => Mem.WriteU32LE(_raw, 0x10, value); }

    /// <summary>Raw word at 0x14: low 16 bits = mip count, high 16 bits = codec tag.</summary>
    public uint MipCodecWord { get => Mem.ReadU32LE(_raw, 0x14); set => Mem.WriteU32LE(_raw, 0x14, value); }

    public int MipCount
    {
        get => (int)(MipCodecWord & 0xFFFF);
        set => MipCodecWord = (MipCodecWord & 0xFFFF0000u) | ((uint)value & 0xFFFFu);
    }

    public ushort CodecTag
    {
        get => (ushort)(MipCodecWord >> 16);
        set => MipCodecWord = (MipCodecWord & 0xFFFFu) | ((uint)value << 16);
    }

    public TextureType Type { get => (TextureType)Mem.ReadU32LE(_raw, 0x18); set => Mem.WriteU32LE(_raw, 0x18, (uint)value); }
    public uint FormatValue { get => Mem.ReadU32LE(_raw, 0x1C); set => Mem.WriteU32LE(_raw, 0x1C, value); }

    public Xt1Format Format
    {
        get => (Xt1Format)FormatValue;
        set => FormatValue = (uint)value;
    }

    public uint Width { get => Mem.ReadU32LE(_raw, 0x20); set => Mem.WriteU32LE(_raw, 0x20, value); }
    public uint Height { get => Mem.ReadU32LE(_raw, 0x24); set => Mem.WriteU32LE(_raw, 0x24, value); }
    public uint Depth { get => Mem.ReadU32LE(_raw, 0x28); set => Mem.WriteU32LE(_raw, 0x28, value); }

    /// <summary>The per-texture flag at 0x2C (0x00 or 0x20 observed). Semantics unconfirmed; preserved verbatim.</summary>
    public uint TextureFlag { get => Mem.ReadU32LE(_raw, 0x2C); set => Mem.WriteU32LE(_raw, 0x2C, value); }

    /// <summary>Raw textureLayout word at 0x30. Low 3 bits are blockHeightLog2; high bits unknown.</summary>
    public uint Layout { get => Mem.ReadU32LE(_raw, 0x30); set => Mem.WriteU32LE(_raw, 0x30, value); }

    public uint Layout2 { get => Mem.ReadU32LE(_raw, 0x34); set => Mem.WriteU32LE(_raw, 0x34, value); }

    public int BlockHeightLog2 => (int)(Layout & 7);

    public FormatInfo? Info => FormatTable.Get(FormatValue);

    public bool HasMagic => Magic == MagicValue;
    public bool HasValidFlags => Flags == FlagsValue;
    public bool HasValidHeaderSize => HeaderSize == Size;
    public bool HasValidLayout2 => Layout2 == Layout2Value;

    /// <summary>Advisory invariant check. Never blocks; the UI surfaces these as warnings.</summary>
    public IReadOnlyList<string> Validate()
    {
        var warnings = new List<string>();
        if (!HasMagic)
            warnings.Add($"magic is {Mem.Hex(Magic)}, expected {Mem.Hex(MagicValue)} (\"XT1\\0\")");
        if (!HasValidFlags)
            warnings.Add($"flags@0x04 is {Mem.Hex(Flags)}, expected {Mem.Hex(FlagsValue)}");
        if (!HasValidHeaderSize)
            warnings.Add($"headerSize@0x10 is {HeaderSize}, expected {Size}");
        if (!HasValidLayout2)
            warnings.Add($"layout2@0x34 is {Mem.Hex(Layout2)}, expected {Mem.Hex(Layout2Value)}");
        if (Info is null)
            warnings.Add($"format {Mem.Hex(FormatValue)} is not in the format table");
        if (FormatValue == (uint)Xt1Format.AfL8Unorm)
        {
            if (CodecTag != AfCodecTag)
                warnings.Add($"format 0x01 expects codec tag \"af\" (0x{AfCodecTag:X4}), got 0x{CodecTag:X4}");
        }
        else if (CodecTag != 0)
        {
            warnings.Add($"codec tag high word is 0x{CodecTag:X4}; expected 0 for other formats");
        }
        if (MipCount == 0)
            warnings.Add("mipCount is 0");
        return warnings;
    }
}
