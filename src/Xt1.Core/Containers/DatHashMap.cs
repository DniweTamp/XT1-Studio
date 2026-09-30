using Xt1.Core.Util;

namespace Xt1.Core.Containers;

/// <summary>
/// The DAT <c>off_hash_map</c> table (header word 0x18). It is an open-addressing index from
/// <c>crc32(member_name) &amp; 0x7FFFFFFF</c> to the member index, used by the engine to resolve
/// logical names inside the container.
///
/// Layout (offsets relative to the hash-map header), verified on Bayonetta 2 (Wii U, big-endian)
/// and matching Kerilk's <c>bayonetta_tools</c> <c>DATFile::HashMap</c>:
/// <code>
/// u32 pre_hash_shift;
/// u32 offset_bucket_ranks;   // int16[(1 &lt;&lt; (31 - pre_hash_shift))]
/// u32 offset_hashes;         // u32[member_count]
/// u32 offset_file_indices;   // u16[member_count]
/// </code>
/// Ranks are -1 for an empty bucket, otherwise the first index of that bucket's run in the
/// hashes/indices arrays. Bayonetta 3 DATs use the same table (their parser and XT1 texture
/// hashes share the masked CRC32).
/// </summary>
public sealed class DatHashMap
{
    /// <summary>Default shift for 31-bit hashes: 8 buckets, every masked hash maps to one.</summary>
    public const uint DefaultPreHashShift = 28;

    private readonly int[] _bucketRanks;
    private readonly uint[] _hashes;
    private readonly ushort[] _fileIndices;

    public uint PreHashShift { get; }
    public IReadOnlyList<int> BucketRanks => _bucketRanks;
    public IReadOnlyList<uint> Hashes => _hashes;
    public IReadOnlyList<ushort> FileIndices => _fileIndices;
    public int BucketCount => _bucketRanks.Length;
    public int Count => _hashes.Length;

    private DatHashMap(uint preHashShift, int[] bucketRanks, uint[] hashes, ushort[] fileIndices)
    {
        PreHashShift = preHashShift;
        _bucketRanks = bucketRanks;
        _hashes = hashes;
        _fileIndices = fileIndices;
    }

    /// <summary>Serialized size in bytes.</summary>
    public int SerializedSize => 0x10 + BucketCount * 2 + Count * 4 + Count * 2;

    public static bool TryParse(
        ReadOnlySpan<byte> file, int mapOffset, int memberCount, ByteOrder order, int regionEnd,
        out DatHashMap? map)
    {
        map = null;
        if (mapOffset < 0 || memberCount <= 0 || mapOffset + 0x10 > file.Length) return false;

        uint shift = Mem.ReadU32(file, mapOffset, order);
        if (shift is < 1 or > 30) return false;

        int buckets = 1 << (int)(31 - shift);
        if (buckets is < 1 or > 1 << 20) return false;

        uint ranksRel = Mem.ReadU32(file, mapOffset + 4, order);
        uint hashesRel = Mem.ReadU32(file, mapOffset + 8, order);
        uint indicesRel = Mem.ReadU32(file, mapOffset + 0xC, order);

        long need = (long)Math.Max(ranksRel + (long)buckets * 2,
            Math.Max(hashesRel + (long)memberCount * 4, indicesRel + (long)memberCount * 2));
        if (need <= 0 || mapOffset + need > file.Length) return false;
        int limit = regionEnd > 0 ? Math.Min(file.Length, mapOffset + regionEnd) : file.Length;
        if (mapOffset + need > limit) return false;

        var ranks = new int[buckets];
        for (int i = 0; i < buckets; i++)
            ranks[i] = Mem.ReadI16(file, (int)(mapOffset + ranksRel) + i * 2, order);

        var hashes = new uint[memberCount];
        for (int i = 0; i < memberCount; i++)
            hashes[i] = Mem.ReadU32(file, (int)(mapOffset + hashesRel) + i * 4, order);

        var indices = new ushort[memberCount];
        for (int i = 0; i < memberCount; i++)
            indices[i] = Mem.ReadU16(file, (int)(mapOffset + indicesRel) + i * 2, order);

        // _fileIndices is by-hash-index; expose it only when it is a valid permutation.
        for (int i = 0; i < memberCount; i++)
            if (indices[i] >= memberCount) return false;

        map = new DatHashMap(shift, ranks, hashes, indices);
        return true;
    }

    /// <summary>
    /// Build a table for the given member names (in member order). Hashes are
    /// <c>crc32(name) &amp; 0x7FFFFFFF</c>.
    /// </summary>
    public static DatHashMap Build(IReadOnlyList<string> names, uint preHashShift = DefaultPreHashShift)
    {
        int n = names.Count;
        if (preHashShift is < 1 or > 30) preHashShift = DefaultPreHashShift;
        int buckets = 1 << (int)(31 - preHashShift);

        var hashes = new uint[n];
        for (int i = 0; i < n; i++) hashes[i] = Xt1.Crc32NameHash.ComputeMasked(names[i]);

        // Bucket membership by (hash >> shift), stable within a bucket by member order.
        var perBucket = new List<int>[buckets];
        for (int b = 0; b < buckets; b++) perBucket[b] = new List<int>();
        for (int i = 0; i < n; i++) perBucket[hashes[i] >> (int)preHashShift].Add(i);

        var ranks = new int[buckets];
        var orderedHashes = new uint[n];
        var orderedIndices = new ushort[n];
        int rank = 0;
        for (int b = 0; b < buckets; b++)
        {
            if (perBucket[b].Count == 0)
            {
                ranks[b] = -1;
                continue;
            }
            ranks[b] = rank;
            foreach (int i in perBucket[b])
            {
                orderedHashes[rank] = hashes[i];
                orderedIndices[rank] = (ushort)i;
                rank++;
            }
        }

        return new DatHashMap(preHashShift, ranks, orderedHashes, orderedIndices);
    }

    /// <summary>Serialize the table; offsets are written relative to the table start.</summary>
    public byte[] ToBytes(ByteOrder order)
    {
        var output = new byte[SerializedSize];
        uint ranksRel = 0x10;
        uint hashesRel = ranksRel + (uint)(BucketCount * 2);
        uint indicesRel = hashesRel + (uint)(Count * 4);

        Mem.WriteU32(output, 0, PreHashShift, order);
        Mem.WriteU32(output, 4, ranksRel, order);
        Mem.WriteU32(output, 8, hashesRel, order);
        Mem.WriteU32(output, 0xC, indicesRel, order);

        for (int i = 0; i < BucketCount; i++)
            Mem.WriteI16(output, (int)ranksRel + i * 2, (short)_bucketRanks[i], order);
        for (int i = 0; i < Count; i++)
            Mem.WriteU32(output, (int)hashesRel + i * 4, _hashes[i], order);
        for (int i = 0; i < Count; i++)
            Mem.WriteU16(output, (int)indicesRel + i * 2, _fileIndices[i], order);

        return output;
    }

    /// <summary>Member indices ordered by hash-table position (for diagnostics/tests).</summary>
    public ushort[] IndicesByHashPosition() => (ushort[])_fileIndices.Clone();

    /// <summary>Hashes ordered by member index (the table itself is stored in bucket order).</summary>
    public uint[] HashesByMemberIndex()
    {
        var result = new uint[Count];
        for (int i = 0; i < Count; i++) result[_fileIndices[i]] = _hashes[i];
        return result;
    }
}
