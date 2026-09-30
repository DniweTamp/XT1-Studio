using Xt1.Core.Containers;
using Xt1.Core.Util;
using Xt1.Core.Xt1;
using Xunit;

namespace Xt1.Core.Tests;

/// <summary>
/// DAT container coverage beyond Bayonetta 3: Bayonetta 1 (PC, little-endian, no member hash map)
/// and Bayonetta 2 (Wii U, big-endian, with a member hash map). Real-file tests skip when the
/// sample is absent; the synthetic tests always run.
/// </summary>
public class DatMultiGameTests
{
    [Fact]
    public void B1CoreDat_ParsesLittleEndian_AndRoundTripsByteIdentical()
    {
        if (!TestPaths.Exists(TestPaths.B1CoreDat)) return;
        var bytes = File.ReadAllBytes(TestPaths.B1CoreDat);

        var dat = DatFile.Parse(bytes);

        Assert.Equal(ByteOrder.Little, dat.ByteOrder);
        Assert.Equal(56, dat.Count);
        Assert.Contains(dat.SubFiles, s => s.Tag == "wtb");
        Assert.Contains(dat.SubFiles, s => s.Name == "pre_texture.wtb");
        // Bayonetta 1 PC DATs carry no member hash map (header word 0x18 is zero).
        Assert.Null(dat.HashMap);
        Assert.Equal(bytes, dat.ToBytes());
    }

    [Fact]
    public void B2WiiU_Wp0020Dat_ParsesBigEndian_WithHashMapForEveryMember()
    {
        if (!TestPaths.Exists(TestPaths.B2WuWp0020Dat)) return;
        var bytes = File.ReadAllBytes(TestPaths.B2WuWp0020Dat);

        var dat = DatFile.Parse(bytes);

        Assert.Equal(ByteOrder.Big, dat.ByteOrder);
        Assert.Equal(8, dat.Count);
        Assert.NotNull(dat.HashMap);
        Assert.Equal(dat.Count, dat.HashMap!.Count);

        // The member hash map stores crc32(name) & 0x7FFFFFFF, and pair it with the right member.
        var byIndex = new Dictionary<ushort, uint>();
        for (int i = 0; i < dat.HashMap.Count; i++)
            byIndex[dat.HashMap.FileIndices[i]] = dat.HashMap.Hashes[i];
        for (int i = 0; i < dat.Count; i++)
            Assert.Equal(Crc32NameHash.ComputeMasked(dat.SubFiles[i].Name), byIndex[(ushort)i]);

        // An unmodified big-endian container must serialize back byte-for-byte.
        Assert.Equal(bytes, dat.ToBytes());
    }

    [Fact]
    public void B2WiiU_Dat_ForcingLittleEndian_IsRejected()
    {
        if (!TestPaths.Exists(TestPaths.B2WuWp0020Dat)) return;
        var bytes = File.ReadAllBytes(TestPaths.B2WuWp0020Dat);
        Assert.Equal(ByteOrder.Big, DatFile.DetectByteOrder(bytes));
        Assert.Throws<InvalidDataException>(() => DatFile.Parse(bytes, ByteOrder.Little));
    }

    [Fact]
    public void B2WiiU_Dat_StructuralEdit_PreservesMembersAndHashMap()
    {
        if (!TestPaths.Exists(TestPaths.B2WuWp0020Dat)) return;
        var dat = DatFile.Parse(File.ReadAllBytes(TestPaths.B2WuWp0020Dat));
        var before = dat.SubFiles.Select(s => (s.Tag, s.Name)).ToArray();
        int originalHashCount = dat.HashMap!.Count;

        // Grow a member so the container must repack the data region.
        var target = dat.SubFiles[3];
        var bigger = new byte[target.Bytes.Length + 0x123];
        target.Bytes.CopyTo(bigger, 0);
        target.Bytes = bigger;

        var rebuilt = DatFile.Parse(dat.ToBytesForced());

        Assert.Equal(ByteOrder.Big, rebuilt.ByteOrder);
        Assert.Equal(before.Length, rebuilt.Count);
        for (int i = 0; i < before.Length; i++)
        {
            Assert.Equal(before[i].Tag, rebuilt.SubFiles[i].Tag);
            Assert.Equal(before[i].Name, rebuilt.SubFiles[i].Name);
        }
        Assert.Equal(target.Bytes.Length, rebuilt.SubFiles[3].Bytes.Length);
        // The member set is unchanged, so the hash map is carried over verbatim.
        Assert.Equal(originalHashCount, rebuilt.HashMap!.Count);
    }

    [Fact]
    public void B2WiiU_Dat_RepackRespectsPlatinumAlignment()
    {
        if (!TestPaths.Exists(TestPaths.B2WuWp0020Dat)) return;
        var dat = DatFile.Parse(File.ReadAllBytes(TestPaths.B2WuWp0020Dat));
        foreach (var kv in DatFile.PlatinumAlignments()) dat.Alignment[kv.Key] = kv.Value;

        var target = dat.SubFiles[0];
        var bigger = new byte[target.Bytes.Length + 5];
        target.Bytes.CopyTo(bigger, 0);
        target.Bytes = bigger;

        var rebuilt = DatFile.Parse(dat.ToBytesForced());
        // Only the bxm member changed; the 0x2000-aligned wtp must still land on 0x2000.
        var wtp = rebuilt.SubFiles.First(s => s.Tag == "wtp");
        Assert.Equal(0, (int)(wtp.Offset % 0x1000));
    }

    [Fact]
    public void EmptyDat_ParsesZeroMembers()
    {
        if (!TestPaths.Exists(TestPaths.Pl0000Dtt)) return;
        var dir = Path.Combine(TestPaths.Romfs, "pl");
        var empty = Path.Combine(dir, "pl3020.dat");
        if (!TestPaths.Exists(empty)) return;
        var bytes = File.ReadAllBytes(empty);
        var dat = DatFile.Parse(bytes);
        Assert.Equal(0, dat.Count);
        Assert.Equal(bytes, dat.ToBytes());
    }

    [Fact]
    public void DatHashMap_Build_RoundTripsThroughSerialization()
    {
        var names = new[] { "wp0020.wta", "wp0020.wtp", "wp0020.wmb", "a.bxm", "b.bxm" };
        var map = DatHashMap.Build(names);
        var serialized = map.ToBytes(ByteOrder.Big);

        Assert.True(DatHashMap.TryParse(serialized, 0, names.Length, ByteOrder.Big, serialized.Length, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(map.PreHashShift, parsed!.PreHashShift);
        Assert.Equal(map.BucketCount, parsed.BucketCount);
        Assert.Equal(map.IndicesByHashPosition(), parsed.IndicesByHashPosition());
        var byMember = parsed.HashesByMemberIndex();
        for (int i = 0; i < names.Length; i++)
            Assert.Equal(Crc32NameHash.ComputeMasked(names[i]), byMember[i]);
    }

    [Fact]
    public void DatHashMap_BucketsPointAtTheirFirstHash()
    {
        var names = new[] { "x.wta", "y.wtp", "z.wmb" };
        var map = DatHashMap.Build(names);
        int[] ranks = map.BucketRanks.ToArray();
        int position = 0;
        for (int b = 0; b < ranks.Length; b++)
        {
            if (ranks[b] == -1) continue;
            Assert.Equal(position, ranks[b]);
            // Every hash in this bucket's run must share the bucket index.
            while (position < map.Count && (map.Hashes[position] >> (int)map.PreHashShift) == b)
                position++;
        }
    }
}
