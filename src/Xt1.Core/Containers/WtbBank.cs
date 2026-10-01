using Xt1.Core.Format;
using Xt1.Core.Util;
using Xt1.Core.Xt1;

namespace Xt1.Core.Containers;

/// <summary>
/// A `WTB` texture bank (XT1_FORMAT.md section 4): five parallel tables plus the XT1 header
/// array. Handles both the split form (`.wta` / `"wta"` sub-file, payload in a separate stream)
/// and the standalone `.wtb` form (payload appended inline, `table0` rebased absolute).
/// </summary>
public sealed class WtbBank
{
    /// <summary>"WTB\0" little-endian.</summary>
    public const uint MagicValue = 0x00425457;

    public const int HeaderSize = 0x20;
    public const int Xt1RecordSize = Xt1Header.Size;

    /// <summary>
    /// Total payload bytes one bank may materialize while parsing. Real banks hold tens of MB; the
    /// cap exists so a crafted count x size combination cannot allocate gigabytes from a small file,
    /// which the per-texture clamp alone does not prevent.
    /// </summary>
    private const long MaxTotalPayloadBytes = 512L * 1024 * 1024;

    private readonly List<EditableTexture> _textures = new();
    private readonly int _originalCount;
    private readonly uint _originalT0, _originalT1, _originalT2, _originalT3, _originalXt1;
    private readonly byte[] _originalBytes;
    private uint _originalVersion;

    public uint Version { get; set; } = 3;

    /// <summary>Raw stored table offsets (for the container editor).</summary>
    public uint Table0Offset { get; set; }
    public uint Table1Offset { get; set; }
    public uint Table2Offset { get; set; }
    public uint Table3Offset { get; set; }
    public uint Xt1Offset { get; set; }

    /// <summary>When false the table3 array is omitted unless a texture claims a hash; split
    /// `.wta`+`.wtp` banks can be authored without it.</summary>
    public bool IncludeTable3 { get; set; }

    /// <summary>
    /// Whether table3 is emitted: the bank declares one, or any texture claims a CRC32 hash (its
    /// table2 "Has CRC32 hash" flag) or carries an explicit value. Without the second condition a
    /// hash set on a texture in a bank opened without table3 would be silently dropped on save.
    /// </summary>
    private bool WriteTable3 =>
        IncludeTable3 || _textures.Any(t => t.IsModelBankClass || t.HashMode == HashMode.Forced || t.Hash != 0);

    /// <summary>Base subtracted from <c>table0</c> to obtain a stream-relative offset (0 split, 0x1000 standalone).</summary>
    public uint PayloadBase { get; set; }

    public bool IsStandalone { get; set; }

    /// <summary>The original raw WTB bytes (structural region for split banks; whole file for standalone).</summary>
    public byte[] OriginalBytes => _originalBytes;

    /// <summary>Payload stream for standalone banks (inline in the file); null for split banks.</summary>
    public byte[]? InlinePayload { get; private set; }

    /// <summary>When true, <c>table1</c> is derived from the header imageSize rather than the stored value.</summary>
    public bool SyncTable1WithImageSize { get; set; }

    /// <summary>Set when count / table offsets were changed and a full rebuild is required.</summary>
    public bool StructureDirty { get; set; }

    private WtbBank(byte[] originalBytes, int originalCount,
        uint t0, uint t1, uint t2, uint t3, uint xt1, bool includeTable3)
    {
        _originalBytes = originalBytes;
        _originalCount = originalCount;
        _originalT0 = t0;
        _originalT1 = t1;
        _originalT2 = t2;
        _originalT3 = t3;
        _originalXt1 = xt1;
        Table0Offset = t0;
        Table1Offset = t1;
        Table2Offset = t2;
        Table3Offset = t3;
        Xt1Offset = xt1;
        IncludeTable3 = includeTable3;
    }

    public IReadOnlyList<EditableTexture> Textures => _textures;

    /// <summary>Number of textures. Setting this resizes the texture list (hand-edit of `count`).</summary>
    public int Count
    {
        get => _textures.Count;
        set
        {
            if (value == _textures.Count) return;
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            if (value < _textures.Count)
                _textures.RemoveRange(value, _textures.Count - value);
            else
                while (_textures.Count < value)
                    _textures.Add(CreatePlaceholder(_textures.Count));
            StructureDirty = true;
        }
    }

    public bool IsDirty => StructureDirty || _textures.Any(t => t.IsDirty) ||
                           TableOffsetsChanged() || WriteTable3 != (_originalT3 != 0) ||
                           Version != _originalVersion || SyncTable1WithImageSize;

    private bool TableOffsetsChanged() =>
        Table0Offset != _originalT0 || Table1Offset != _originalT1 ||
        Table2Offset != _originalT2 || Table3Offset != _originalT3 || Xt1Offset != _originalXt1;

    public static bool HasMagic(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 0x20 && Mem.ReadU32LE(bytes, 0) == MagicValue;

    /// <summary>
    /// Parse a WTB. For split banks pass the payload stream (the `.wtp` file or the `"wtp"`
    /// sub-file bytes); the standalone form carries its own payload inline.
    /// </summary>
    public static WtbBank Parse(byte[] wtbBytes, byte[]? payloadStream = null)
    {
        if (!HasMagic(wtbBytes))
            throw new InvalidDataException("not a WTB bank (missing \"WTB\\0\" magic)");
        if (wtbBytes.Length < HeaderSize)
            throw new InvalidDataException("WTB bank is too small");

        uint version = Mem.ReadU32LE(wtbBytes, 0x04);
        int count = checked((int)Mem.ReadU32LE(wtbBytes, 0x08));
        uint t0 = Mem.ReadU32LE(wtbBytes, 0x0C);
        uint t1 = Mem.ReadU32LE(wtbBytes, 0x10);
        uint t2 = Mem.ReadU32LE(wtbBytes, 0x14);
        uint t3 = Mem.ReadU32LE(wtbBytes, 0x18);
        uint xt1 = Mem.ReadU32LE(wtbBytes, 0x1C);

        if (count < 0 || count > 1_000_000)
            throw new InvalidDataException($"implausible WTB texture count {count}");

        bool includeTable3 = t3 != 0;
        int tableStride = Mem.Align(count * 4, 32);

        var table0 = ReadTable(wtbBytes, t0, count);
        var table1 = ReadTable(wtbBytes, t1, count);
        var table2 = ReadBytes(wtbBytes, t2, count * 4);
        var table3 = includeTable3 ? ReadTable(wtbBytes, t3, count) : new uint[count];

        // Standalone if the first payload offset is non-zero (rebased absolute).
        bool standalone = count > 0 && table0[0] != 0;
        uint payloadBase = standalone ? table0[0] : 0;

        byte[]? stream = payloadStream;
        if (standalone)
        {
            int at = (int)Math.Min(payloadBase, (uint)wtbBytes.Length);
            stream = wtbBytes.AsSpan(at).ToArray();
        }

        var bank = new WtbBank((byte[])wtbBytes.Clone(), count, t0, t1, t2, t3, xt1, includeTable3)
        {
            Version = version,
            IsStandalone = standalone,
            PayloadBase = payloadBase,
            InlinePayload = standalone ? stream : null,
            SyncTable1WithImageSize = false,
        };
        bank._originalVersion = version;

        long totalPayload = 0;
        for (int i = 0; i < count; i++)
        {
            int headerAt = checked((int)xt1 + i * Xt1RecordSize);
            Xt1Header header;
            if (headerAt + Xt1RecordSize <= wtbBytes.Length)
            {
                header = Xt1Header.Parse(wtbBytes.AsSpan(headerAt, Xt1RecordSize));
            }
            else
            {
                header = new Xt1Header();
                header.Magic = Xt1Header.MagicValue;
                header.Flags = Xt1Header.FlagsValue;
                header.HeaderSize = Xt1Header.Size;
                header.Layout2 = Xt1Header.Layout2Value;
            }

            var table2Bytes = new byte[4];
            Array.Copy(table2, i * 4, table2Bytes, 0, Math.Min(4, Math.Max(0, table2.Length - i * 4)));

            byte[] payload = Array.Empty<byte>();
            if (stream is not null)
            {
                long rel = (long)table0[i] - payloadBase;
                long size = table1[i];
                if (rel < 0) rel = 0;
                if (rel <= stream.Length)
                {
                    long max = stream.Length - rel;
                    if (size < 0 || size > max) size = max;

                    totalPayload += size;
                    if (totalPayload > MaxTotalPayloadBytes)
                        throw new InvalidDataException(
                            $"WTB payloads exceed {MaxTotalPayloadBytes / (1024 * 1024)} MB");

                    payload = stream.AsSpan((int)rel, (int)size).ToArray();
                }
            }

            bank._textures.Add(new EditableTexture(header, table2Bytes, table3[i], table0[i], table1[i], payload)
            {
                Index = i,
            });
        }

        return bank;
    }

    public void Add(EditableTexture texture)
    {
        texture.Index = _textures.Count;
        _textures.Add(texture);
        StructureDirty = true;
    }

    /// <summary>Insert a texture at a specific index (used by undo of a delete).</summary>
    public void Insert(int index, EditableTexture texture)
    {
        index = Math.Clamp(index, 0, _textures.Count);
        _textures.Insert(index, texture);
        StructureDirty = true;
    }

    public bool Remove(EditableTexture texture)
    {
        bool removed = _textures.Remove(texture);
        if (removed)
        {
            Reindex();
            StructureDirty = true;
        }
        return removed;
    }

    public void Reindex()
    {
        for (int i = 0; i < _textures.Count; i++)
            _textures[i].Index = i;
    }

    /// <summary>Recompute <c>table0</c> payload offsets as align4096 cumulative sizes.</summary>
    /// <param name="baseOffset">Stream base (0 split, 0x1000 standalone).</param>
    public void RecalculatePayloadOffsets(uint baseOffset)    {
        long cursor = 0;
        foreach (var tex in _textures)
        {
            tex.PayloadOffset = baseOffset + (uint)cursor;
            uint size = SyncTable1WithImageSize
                ? (uint)Math.Min(tex.Header.ImageSize, uint.MaxValue)
                : tex.PayloadSize != 0
                    ? tex.PayloadSize
                    : (uint)Math.Min((long)tex.Payload.Length, uint.MaxValue);
            tex.PayloadSize = size;
            cursor = Mem.Align(cursor + size, 4096);
        }
    }

    /// <summary>
    /// Rebuild a split bank after texture edits: recompute <c>table0</c> as 4096-aligned cumulative
    /// payload sizes, serialize the bank, and return the paired payload stream (<c>.wtp</c>) the
    /// textures point into. Used by the mod manager after replacing a texture's header and payload.
    /// </summary>
    public (byte[] Bank, byte[] PayloadStream) BuildSplit(uint streamBase = 0)
    {
        RecalculatePayloadOffsets(streamBase);
        byte[] bank = ToBytes();

        long end = 0;
        foreach (var tex in _textures)
            end = Math.Max(end, (long)tex.PayloadOffset + tex.Payload.Length);

        var stream = new byte[end];
        foreach (var tex in _textures)
        {
            if (tex.Payload.Length == 0) continue;
            int at = (int)tex.PayloadOffset;
            if (at >= 0 && at + tex.Payload.Length <= stream.Length)
                tex.Payload.CopyTo(stream, at);
        }
        return (bank, stream);
    }

    private static EditableTexture CreatePlaceholder(int index)
    {
        var header = new Xt1Header();
        header.Magic = Xt1Header.MagicValue;
        header.Flags = Xt1Header.FlagsValue;
        header.HeaderSize = Xt1Header.Size;
        header.Layout2 = Xt1Header.Layout2Value;
        header.MipCount = 1;
        header.Width = 1;
        header.Height = 1;
        header.Depth = 1;
        header.Type = Xt1.TextureType.T2D;
        header.Format = Xt1Format.Rgba8Unorm;
        return new EditableTexture(header, new byte[4], 0, 0, 0, Array.Empty<byte>()) { Index = index };
    }

    private static uint[] ReadTable(byte[] bytes, uint offset, int count)
    {
        var table = new uint[count];
        for (int i = 0; i < count; i++)
        {
            int at = checked((int)offset + 4 * i);
            table[i] = at >= 0 && at + 4 <= bytes.Length ? Mem.ReadU32LE(bytes, at) : 0u;
        }
        return table;
    }

    private static byte[] ReadBytes(byte[] bytes, uint offset, int length)
    {
        var result = new byte[Math.Max(0, length)];
        int at = (int)Math.Min(offset, (uint)bytes.Length);
        int available = Math.Max(0, bytes.Length - at);
        Array.Copy(bytes, at, result, 0, Math.Min(result.Length, available));
        return result;
    }

    /// <summary>Serialize the WTB. Returns the exact original bytes when nothing changed.</summary>
    public byte[] ToBytes()
    {
        if (!IsDirty)
            return (byte[])_originalBytes.Clone();

        if (!StructureDirty && TableOffsetsUnchanged() &&
            WriteTable3 == (_originalT3 != 0) && _originalBytes.Length > 0 &&
            CanPatchInPlace(out bool writePayloads))
        {
            return PatchInPlace(writePayloads);
        }

        return BuildCanonical();
    }

    private bool TableOffsetsUnchanged() =>
        Table0Offset == _originalT0 && Table1Offset == _originalT1 &&
        Table2Offset == _originalT2 && Table3Offset == _originalT3 && Xt1Offset == _originalXt1;

    private bool CanPatchInPlace(out bool writePayloads)
    {
        writePayloads = false;
        if (_textures.Count != _originalCount) return false;
        if (IsStandalone)
        {
            foreach (var tex in _textures)
            {
                if (tex.PayloadDirty || tex.OffsetsDirty)
                {
                    long end = (long)tex.PayloadOffset + tex.Payload.Length;
                    if (end > _originalBytes.Length) return false;
                }
            }
            writePayloads = true;
        }
        return true;
    }

    private byte[] PatchInPlace(bool writePayloads)
    {
        var output = (byte[])_originalBytes.Clone();
        int count = _textures.Count;
        Mem.WriteU32LE(output, 0x04, Version);

        for (int i = 0; i < count; i++)
        {
            var tex = _textures[i];
            WriteU32IfRoom(output, Table0Offset + 4 * i, tex.PayloadOffset);
            uint size = SyncTable1WithImageSize ? (uint)Math.Min(tex.Header.ImageSize, uint.MaxValue) : tex.PayloadSize;
            WriteU32IfRoom(output, Table1Offset + 4 * i, size);
            int t2At = (int)Table2Offset + i * 4;
            if (t2At >= 0 && t2At + 4 <= output.Length)
                tex.Table2Bytes.CopyTo(output, t2At);
            if (WriteTable3 && Table3Offset != 0)
                WriteU32IfRoom(output, Table3Offset + 4 * i, tex.EffectiveHash());
            int xt1At = (int)Xt1Offset + i * Xt1RecordSize;
            if (xt1At >= 0 && xt1At + Xt1RecordSize <= output.Length)
                tex.Header.Raw.CopyTo(output, xt1At);
        }

        if (writePayloads)
        {
            foreach (var tex in _textures)
            {
                int at = (int)tex.PayloadOffset;
                if (at >= 0 && at + tex.Payload.Length <= output.Length)
                    tex.Payload.CopyTo(output, at);
            }
        }

        return output;
    }

    private static void WriteU32IfRoom(byte[] buffer, long offset, uint value)
    {
        if (offset < 0 || offset > int.MaxValue - 4) return;
        int at = (int)offset;
        if (at + 4 <= buffer.Length)
            Mem.WriteU32LE(buffer, at, value);
    }

    private byte[] BuildCanonical()
    {
        int count = _textures.Count;
        int stride = Mem.Align(count * 4, 32);

        uint t0 = HeaderSize;
        uint t1 = (uint)(t0 + stride);
        uint t2 = (uint)(t1 + stride);
        bool writeTable3 = WriteTable3;
        uint t3 = writeTable3 ? (uint)(t2 + stride) : 0u;
        uint xt1 = writeTable3 ? (uint)(t3 + stride) : (uint)(t2 + stride);
        int structuralEnd = checked((int)xt1 + count * Xt1RecordSize);

        Table0Offset = t0;
        Table1Offset = t1;
        Table2Offset = t2;
        Table3Offset = t3;
        Xt1Offset = xt1;

        int bufferLength;
        if (IsStandalone)
        {
            // The payload must start after the whole structural region. If raising the base
            // moves it, every payload offset has to move with it, otherwise the payloads would
            // be written on top of the tables and XT1 headers.
            long oldBase = PayloadBase;                 // 0 means the offsets are stream-relative
            long newBase = Math.Max(oldBase == 0 ? 0x1000 : oldBase, Mem.Align(structuralEnd, 16));
            long delta = newBase - oldBase;
            if (delta != 0)
            {
                foreach (var tex in _textures)
                    tex.PayloadOffset = (uint)(tex.PayloadOffset + delta);
            }
            PayloadBase = (uint)newBase;

            long maxEnd = newBase;
            foreach (var tex in _textures)
                maxEnd = Math.Max(maxEnd, (long)tex.PayloadOffset + tex.Payload.Length);
            if (maxEnd > int.MaxValue) throw new InvalidOperationException("standalone bank is too large");
            bufferLength = (int)maxEnd;
        }
        else
        {
            bufferLength = Mem.Align(structuralEnd, 8);
        }

        var output = new byte[bufferLength];
        Mem.WriteU32LE(output, 0x00, MagicValue);
        Mem.WriteU32LE(output, 0x04, Version);
        Mem.WriteU32LE(output, 0x08, (uint)count);
        Mem.WriteU32LE(output, 0x0C, t0);
        Mem.WriteU32LE(output, 0x10, t1);
        Mem.WriteU32LE(output, 0x14, t2);
        Mem.WriteU32LE(output, 0x18, t3);
        Mem.WriteU32LE(output, 0x1C, xt1);

        for (int i = 0; i < count; i++)
        {
            var tex = _textures[i];
            Mem.WriteU32LE(output, (int)t0 + 4 * i, tex.PayloadOffset);
            uint size = SyncTable1WithImageSize ? (uint)Math.Min(tex.Header.ImageSize, uint.MaxValue) : tex.PayloadSize;
            Mem.WriteU32LE(output, (int)t1 + 4 * i, size);
            int t2At = (int)t2 + i * 4;
            tex.Table2Bytes.CopyTo(output, t2At);
            if (writeTable3)
                Mem.WriteU32LE(output, (int)t3 + 4 * i, tex.EffectiveHash());
            tex.Header.Raw.CopyTo(output, (int)xt1 + i * Xt1RecordSize);
        }

        if (IsStandalone)
        {
            foreach (var tex in _textures)
            {
                int at = (int)tex.PayloadOffset;
                if (at >= 0 && at + tex.Payload.Length <= output.Length)
                    tex.Payload.CopyTo(output, at);
            }
        }

        return output;
    }

    /// <summary>Create an empty bank (used when authoring a new `.wta`/`.wtb`).</summary>
    public static WtbBank CreateEmpty(bool includeTable3, bool standalone, uint payloadBase)
    {
        var bank = new WtbBank(Array.Empty<byte>(), 0, 0, 0, 0, 0, 0, includeTable3)
        {
            IsStandalone = standalone,
            PayloadBase = payloadBase,
            StructureDirty = true,
        };
        return bank;
    }
}
