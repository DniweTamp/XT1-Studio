using System.Text;
using Xt1.Core.Util;

namespace Xt1.Core.Containers;

/// <summary>A sub-file inside a `DAT` container, identified by its 4-character tag.</summary>
public sealed class DatSubFile
{
    private string _tag;
    private string _name;
    private uint _offset;
    private uint _descriptorSize;
    private byte[] _bytes = Array.Empty<byte>();

    /// <summary>The container this sub-file belongs to, used to flag structural edits.</summary>
    internal DatFile? Owner { get; set; }

    public string Tag
    {
        get => _tag;
        set
        {
            if (_tag == value) return;
            _tag = value;
            Owner?.MarkStructureDirty();
        }
    }

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            Owner?.MarkStructureDirty();
        }
    }

    /// <summary>
    /// Current content. Replacing it marks the sub-file dirty, and marks the container structurally
    /// dirty when the new content outgrows the original slot: at the original offset those bytes would
    /// spill over the following sub-file, which is then written on top of them. Shrinking is left
    /// alone, since a smaller sub-file only leaves a gap and keeps the layout byte-identical.
    /// </summary>
    public byte[] Bytes
    {
        get => _bytes;
        set
        {
            _bytes = value;
            // OriginalBytes is assigned before this setter first runs, so the constructor's own
            // assignment compares equal and cannot mark the container dirty by accident.
            if (OutgrewSlot) Owner?.MarkStructureDirty();
        }
    }

    public byte[] OriginalBytes { get; }

    /// <summary>The offset as parsed from the file; kept so an untouched container can be rebuilt
    /// at its original layout (preserving aliased regions, stub entries and padding).</summary>
    public uint OriginalOffset { get; }

    /// <summary>User-editable file offset. Recomputed from the layout on save unless manual mode is on.</summary>
    public uint Offset
    {
        get => _offset;
        set
        {
            if (_offset == value) return;
            _offset = value;
            Owner?.MarkStructureDirty();
        }
    }

    /// <summary>The stored descriptor word (observed to equal the sub-file size for wta/wtp).</summary>
    public uint DescriptorSize
    {
        get => _descriptorSize;
        set
        {
            if (_descriptorSize == value) return;
            _descriptorSize = value;
            Owner?.MarkStructureDirty();
        }
    }

    public int OriginalLength => OriginalBytes.Length;
    public bool IsDirty => !Bytes.AsSpan().SequenceEqual(OriginalBytes);

    /// <summary>
    /// True when the current content is longer than the original slot it was parsed from. This is the
    /// single definition of "the sub-file outgrew its slot": at the original offset those bytes would
    /// spill over the following sub-file, so the content setter uses it to mark the container
    /// structural and the rebuild uses it to decide whether a repack is required.
    /// </summary>
    public bool OutgrewSlot => Bytes.Length > OriginalLength;

    public DatSubFile(string tag, string name, byte[] bytes, uint offset, uint descriptorSize)
    {
        _tag = tag;
        _name = name;
        // OriginalBytes first: the Bytes setter compares against the original slot size.
        OriginalBytes = (byte[])bytes.Clone();
        Bytes = bytes;
        _offset = offset;
        _descriptorSize = descriptorSize;
        OriginalOffset = offset;
    }

    public override string ToString() => $"\"{Tag}\" {Name} ({Bytes.Length} bytes)";
}

/// <summary>
/// PlatinumGames `DAT` container (`.dat` and `.dtt`, XT1_FORMAT.md section 3). Sub-files are
/// located by 4-character tag, never by index. The metadata tables are preserved verbatim and
/// rebuilt only when required.
/// </summary>
public sealed class DatFile
{
    /// <summary>"DAT\0" little-endian.</summary>
    public const uint MagicValue = 0x00544144;

    private static readonly System.Text.Encoding Text8 = System.Text.Encoding.Latin1;

    private readonly List<DatSubFile> _subFiles;
    private readonly byte[] _originalFileBytes;
    private bool _structureDirty;

    /// <summary>Opaque metadata table that is preserved byte-for-byte.</summary>
    public byte[] ExtraBlob { get; set; } = Array.Empty<byte>();

    /// <summary>Offset of the first sub-file (base of the data region).</summary>
    public uint BaseOffset { get; set; }

    /// <summary>Alignment used when the metadata region outgrows <see cref="BaseOffset"/>.</summary>
    public int BaseAlignment { get; set; }

    /// <summary>When true (default) sub-file offsets and descriptor sizes are recomputed on save.</summary>
    public bool RecomputeOffsetsOnWrite { get; set; } = true;

    /// <summary>Original name slot size (bytes per name); preserved unless a name outgrows it.</summary>
    public int NameSlotSize { get; set; }

    /// <summary>Header word at 0x1C, always 0 in observed files; preserved and editable.</summary>
    public uint HeaderWord1C { get; set; }

    private DatFile(List<DatSubFile> subFiles, byte[] originalFileBytes)
    {
        _subFiles = subFiles;
        _originalFileBytes = originalFileBytes;
    }

    public IReadOnlyList<DatSubFile> SubFiles => _subFiles;

    public byte[] OriginalFileBytes => _originalFileBytes;

    public int Count => _subFiles.Count;

    /// <summary>True when the file is a `.dtt` (payload base 0x1000).</summary>
    public bool LooksLikeDtt => BaseOffset >= 0x1000 && BaseOffset % 0x1000 == 0;

    public DatSubFile? FindByTag(string tag)
    {
        foreach (var sf in _subFiles)
            if (string.Equals(sf.Tag, tag, StringComparison.Ordinal))
                return sf;
        return null;
    }

    public IEnumerable<DatSubFile> FindAllByTag(string tag)
    {
        foreach (var sf in _subFiles)
            if (string.Equals(sf.Tag, tag, StringComparison.Ordinal))
                yield return sf;
    }

    public bool IsDirty => _structureDirty || _subFiles.Any(s => s.IsDirty);

    /// <summary>True when the tables/offsets/names changed and the container must be rebuilt.</summary>
    public bool IsStructureDirty => _structureDirty;

    /// <summary>Flag a structural edit so the container is rebuilt on save.</summary>
    public void MarkStructureDirty() => _structureDirty = true;

    /// <summary>
    /// Serialize the container. Unmodified containers return their exact original bytes: some
    /// Bayonetta DATs alias sub-file regions (a "wta" nested inside an "eff" DAT) or carry stub
    /// offsets, which contiguous repacking cannot reproduce, so a clean file is never rebuilt.
    /// </summary>
    public byte[] ToBytes()
    {
        if (!IsDirty)
            return (byte[])_originalFileBytes.Clone();
        return ToBytesForced();
    }

    /// <summary>Rebuild the container unconditionally (used by tests and after structural edits).</summary>
    public byte[] ToBytesForced() => ToBytesRebuild();

    public static bool HasMagic(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 4 && Mem.ReadU32LE(bytes, 0) == MagicValue;

    public static DatFile Parse(byte[] fileBytes)
    {
        if (!HasMagic(fileBytes))
            throw new InvalidDataException("not a DAT file (missing \"DAT\\0\" magic)");
        if (fileBytes.Length < 0x20)
            throw new InvalidDataException("DAT file is too small");

        uint count = Mem.ReadU32LE(fileBytes, 0x04);
        int pOffsets = checked((int)Mem.ReadU32LE(fileBytes, 0x08));
        int pTags = checked((int)Mem.ReadU32LE(fileBytes, 0x0C));
        int pNames = checked((int)Mem.ReadU32LE(fileBytes, 0x10));
        int pDesc = checked((int)Mem.ReadU32LE(fileBytes, 0x14));
        int pExtra = checked((int)Mem.ReadU32LE(fileBytes, 0x18));
        uint word1C = Mem.ReadU32LE(fileBytes, 0x1C);

        if (count > 0x10000)
            throw new InvalidDataException($"implausible DAT sub-file count {count}");

        int n = (int)count;
        var offsets = new uint[n];
        for (int i = 0; i < n; i++)
            offsets[i] = Mem.ReadU32LE(fileBytes, pOffsets + 4 * i);

        var tags = new string[n];
        for (int i = 0; i < n; i++)
            tags[i] = ReadFixedTag(fileBytes, pTags + 4 * i);

        int slot = Mem.ReadU32LE(fileBytes, pNames) is var s and > 0 and < 0x400 ? (int)s : 0;
        var names = new string[n];
        for (int i = 0; i < n; i++)
        {
            int at = pNames + 4 + i * slot;
            names[i] = slot > 0 && at + slot <= fileBytes.Length
                ? ReadNullTerminated(fileBytes, at, slot)
                : string.Empty;
        }

        var descriptors = new uint[n];
        for (int i = 0; i < n; i++)
            descriptors[i] = Mem.ReadU32LE(fileBytes, pDesc + 4 * i);

        uint baseOffset = n > 0 ? offsets.Min() : (uint)Math.Max(0x20, pExtra);
        int extraLength = (int)Math.Max(0, baseOffset - pExtra);
        if (pExtra < 0 || pExtra > fileBytes.Length) extraLength = 0;
        if (pExtra + extraLength > fileBytes.Length) extraLength = fileBytes.Length - pExtra;
        var extra = extraLength > 0 ? fileBytes.AsSpan(pExtra, extraLength).ToArray() : Array.Empty<byte>();

        var subFiles = new List<DatSubFile>(n);
        var sortedOffsets = offsets.Where(o => o != 0).Distinct().OrderBy(o => o).ToArray();
        for (int i = 0; i < n; i++)
        {
            uint start = offsets[i];
            if (start > fileBytes.Length) start = (uint)fileBytes.Length;

            // Offsets in these containers are not always monotonic: stub entries are 0 and some
            // sub-files alias a region nested inside another (e.g. an effect .dat whose "wta"
            // lives inside its embedded "eff" DAT). Derive the range from the declared descriptor
            // size when it fits before the next region, otherwise from the next larger offset.
            uint next = NextRegionStart(sortedOffsets, start, (uint)fileBytes.Length);
            long available = (long)fileBytes.Length - start;
            long length = descriptors[i] > 0 && start + (long)descriptors[i] <= next
                ? descriptors[i]
                : Math.Max(0, (long)next - start);
            length = Math.Min(length, available);

            var bytes = fileBytes.AsSpan((int)start, (int)length).ToArray();
            subFiles.Add(new DatSubFile(tags[i], names[i], bytes, start, descriptors[i]));
        }

        var parsed = new DatFile(subFiles, fileBytes)
        {
            ExtraBlob = extra,
            BaseOffset = baseOffset,
            BaseAlignment = baseOffset >= 0x1000 && baseOffset % 0x1000 == 0 ? 0x1000 : 16,
            NameSlotSize = slot,
            HeaderWord1C = word1C,
        };

        foreach (var subFile in subFiles)
            subFile.Owner = parsed;

        return parsed;
    }

    /// <summary>The smallest sub-file offset strictly greater than <paramref name="start"/>.</summary>
    private static uint NextRegionStart(uint[] sortedOffsets, uint start, uint fallback)
    {
        foreach (var offset in sortedOffsets)
            if (offset > start)
                return offset;
        return fallback;
    }

    /// <summary>Serialize the container. Untouched sub-files keep their exact bytes.</summary>
    private byte[] ToBytesRebuild()
    {
        int n = _subFiles.Count;

        int slot = NameSlotSize;
        foreach (var sf in _subFiles)
        {
            int need = Text8.GetByteCount(sf.Name) + 1;
            if (need > slot) slot = need;
        }
        if (slot <= 0) slot = 1;

        int lenOffsets = 4 * n;
        int lenTags = 4 * n;
        int lenNames = 4 + slot * n;
        int lenDesc = 4 * n;
        int lenExtra = ExtraBlob.Length;

        int pOffsets = 0x20;
        int pTags = Align4(pOffsets + lenOffsets);
        int pNames = Align4(pTags + lenTags);
        int pDesc = Align4(pNames + lenNames);
        int pExtra = Align4(pDesc + lenDesc);
        int regionEnd = pExtra + lenExtra;

        uint baseOffset = BaseOffset;
        if (regionEnd > baseOffset)
            baseOffset = (uint)Mem.Align(regionEnd, Math.Max(16, BaseAlignment));

        // Assign offsets. When the container's structure is untouched each sub-file stays at its
        // original offset, which preserves aliased regions, stub entries and trailing padding
        // exactly; only a structural edit (or manual offsets) repacks contiguously.
        //
        // A sub-file that outgrew its original slot must repack even if nothing else marked the
        // container: at the original offset its bytes would run over the next sub-file, which is
        // then written on top of them, silently truncating the grown sub-file. "Outgrew" has one
        // definition, DatSubFile.OutgrewSlot, shared with the content setter's dirty marking.
        bool outgrewSlot = _subFiles.Exists(s => s.OutgrewSlot);

        // Manual offsets pin every sub-file, so a grown one cannot be moved out of its neighbour's
        // way. Refuse rather than emit overlapping bytes; silently repacking would discard the
        // user's pinned offsets, which is the point of this mode.
        if (outgrewSlot && !RecomputeOffsetsOnWrite)
            throw new InvalidOperationException(
                "a sub-file outgrew its original slot while manual offsets are active; writing at " +
                "the pinned offsets would truncate it under the following sub-file. Re-enable " +
                "automatic offset recomputation (or shrink the sub-file back) before saving.");

        bool structural = _structureDirty || outgrewSlot;
        var offsets = new uint[n];
        if (structural && RecomputeOffsetsOnWrite)
        {
            long cursor = baseOffset;
            for (int i = 0; i < n; i++)
            {
                offsets[i] = (uint)cursor;
                cursor += _subFiles[i].Bytes.Length;
            }
        }
        else if (structural)
        {
            for (int i = 0; i < n; i++)
                offsets[i] = _subFiles[i].Offset;
        }
        else
        {
            for (int i = 0; i < n; i++)
                offsets[i] = _subFiles[i].OriginalOffset;
        }

        long totalLength = structural ? baseOffset : _originalFileBytes.Length;
        for (int i = 0; i < n; i++)
            totalLength = Math.Max(totalLength, (long)offsets[i] + _subFiles[i].Bytes.Length);

        var output = new byte[totalLength];
        Mem.WriteU32LE(output, 0x00, MagicValue);
        Mem.WriteU32LE(output, 0x04, (uint)n);
        Mem.WriteU32LE(output, 0x08, (uint)pOffsets);
        Mem.WriteU32LE(output, 0x0C, (uint)pTags);
        Mem.WriteU32LE(output, 0x10, (uint)pNames);
        Mem.WriteU32LE(output, 0x14, (uint)pDesc);
        Mem.WriteU32LE(output, 0x18, (uint)pExtra);
        Mem.WriteU32LE(output, 0x1C, HeaderWord1C);

        for (int i = 0; i < n; i++)
            Mem.WriteU32LE(output, pOffsets + 4 * i, offsets[i]);

        for (int i = 0; i < n; i++)
            WriteFixedTag(output, pTags + 4 * i, _subFiles[i].Tag);

        Mem.WriteU32LE(output, pNames, (uint)slot);
        for (int i = 0; i < n; i++)
        {
            int at = pNames + 4 + i * slot;
            var bytes = Text8.GetBytes(_subFiles[i].Name);
            int copy = Math.Min(bytes.Length, slot - 1);
            Array.Copy(bytes, 0, output, at, copy);
        }

        for (int i = 0; i < n; i++)
        {
            var sf = _subFiles[i];
            uint desc = sf.DescriptorSize;
            if (RecomputeOffsetsOnWrite)
            {
                // Only rewrite the descriptor when it behaved as a plain size field, or the
                // sub-file actually changed. This keeps odd declared sizes (e.g. wmb) intact.
                if (sf.IsDirty || sf.DescriptorSize == (uint)sf.OriginalLength)
                    desc = (uint)sf.Bytes.Length;
            }
            Mem.WriteU32LE(output, pDesc + 4 * i, desc);
        }

        if (lenExtra > 0)
            ExtraBlob.CopyTo(output, pExtra);

        for (int i = 0; i < n; i++)
        {
            var sf = _subFiles[i];
            int at = (int)offsets[i];
            if (at >= 0 && at + sf.Bytes.Length <= output.Length)
                sf.Bytes.CopyTo(output, at);
        }

        return output;
    }

    private static int Align4(int value) => (value + 3) & ~3;

    private static string ReadFixedTag(ReadOnlySpan<byte> b, int offset)
    {
        if (offset + 4 > b.Length) return string.Empty;
        Span<char> chars = stackalloc char[4];
        int len = 0;
        for (int i = 0; i < 4; i++)
        {
            byte c = b[offset + i];
            if (c == 0) break;
            chars[len++] = (char)c;
        }
        return new string(chars[..len]);
    }

    private static void WriteFixedTag(Span<byte> b, int offset, string tag)
    {
        for (int i = 0; i < 4; i++)
            b[offset + i] = i < tag.Length ? (byte)tag[i] : (byte)0;
    }

    private static string ReadNullTerminated(ReadOnlySpan<byte> b, int offset, int max)
    {
        int len = 0;
        while (len < max && b[offset + len] != 0) len++;
        return Text8.GetString(b.Slice(offset, len));
    }
}
