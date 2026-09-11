/*
 *  Copyright 2016-2019 Netflix, Inc.
 *
 *     Licensed under the Apache License, Version 2.0 (the "License");
 *     you may not use this file except in compliance with the License.
 *     You may obtain a copy of the License at
 *
 *         http://www.apache.org/licenses/LICENSE-2.0
 *
 *     Unless required by applicable law or agreed to in writing, software
 *     distributed under the License is distributed on an "AS IS" BASIS,
 *     WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 *     See the License for the specific language governing permissions and
 *     limitations under the License.
 *
 */

using SystemEncoding = System.Text.Encoding;
using Hollow.Core.Memory;
using Hollow.Core.Memory.Pool;

namespace Hollow.Tests.Core.Memory;

/// <summary>
/// Ported from <c>FreeOrdinalTrackerTest</c>.
/// </summary>
public class FreeOrdinalTrackerTests
{
    [Fact]
    public void SortMinimizesTheNumberOfUpdatedShards()
    {
        FreeOrdinalTracker tracker = new();
        for (int i = 0; i < 100; i++)
        {
            tracker.GetFreeOrdinal();
        }

        // Shard 3.
        tracker.ReturnOrdinalToPool(15);

        // Shard 1.
        tracker.ReturnOrdinalToPool(13);
        tracker.ReturnOrdinalToPool(9);
        tracker.ReturnOrdinalToPool(17);
        tracker.ReturnOrdinalToPool(5);
        tracker.ReturnOrdinalToPool(1);
        tracker.ReturnOrdinalToPool(21);

        // Shard 2.
        tracker.ReturnOrdinalToPool(2);
        tracker.ReturnOrdinalToPool(10);
        tracker.ReturnOrdinalToPool(14);

        tracker.Sort(numShards: 4, mapIndexBits: 0, mapIndex: 0);

        int[] expected = [1, 5, 9, 13, 17, 21, 2, 10, 14, 15, 100, 101];
        int[] actual = [.. expected.Select(_ => tracker.GetFreeOrdinal())];

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void OrdinalsAreHandedOutInSequenceThenReused()
    {
        FreeOrdinalTracker tracker = new();

        Assert.Equal(0, tracker.GetFreeOrdinal());
        Assert.Equal(1, tracker.GetFreeOrdinal());
        Assert.Equal(2, tracker.GetFreeOrdinal());

        tracker.ReturnOrdinalToPool(1);
        Assert.Equal(1, tracker.GetFreeOrdinal());
        Assert.Equal(3, tracker.GetFreeOrdinal());

        tracker.Reset();
        Assert.Equal(0, tracker.GetFreeOrdinal());
    }
}

/// <summary>
/// Ported from <c>ThreadSafeBitSetTest</c>.
/// </summary>
public class ThreadSafeBitSetTests
{
    [Fact]
    public void Equality()
    {
        ThreadSafeBitSet first = new();
        ThreadSafeBitSet second = new(14, 16385);

        first.Set(100);
        second.Set(100);

        Assert.Equal(first, second);
        Assert.Equal(second, first);

        first.Set(100_000);

        Assert.NotEqual(first, second);
        Assert.NotEqual(second, first);

        first.ClearAll();

        Assert.NotEqual(first, second);
        Assert.NotEqual(second, first);

        first.Set(100);

        Assert.Equal(first, second);
        Assert.Equal(second, first);
    }

    [Fact]
    public void MaxSetBit()
    {
        ThreadSafeBitSet set = new();

        set.Set(100);
        Assert.Equal(100, set.MaxSetBit());

        set.Set(100_000);
        Assert.Equal(100_000, set.MaxSetBit());

        set.Set(1_000_000);
        Assert.Equal(1_000_000, set.MaxSetBit());

        set.ClearAll();
        set.Set(555_555);
        Assert.Equal(555_555, set.MaxSetBit());
    }

    [Fact]
    public void NextSetBit()
    {
        ThreadSafeBitSet set = new();

        set.Set(100);
        set.Set(101);
        set.Set(103);
        set.Set(100_000);
        set.Set(1_000_000);

        Assert.Equal(100, set.NextSetBit(0));
        Assert.Equal(101, set.NextSetBit(101));
        Assert.Equal(103, set.NextSetBit(102));
        Assert.Equal(100_000, set.NextSetBit(104));
        Assert.Equal(1_000_000, set.NextSetBit(100_001));
        Assert.Equal(-1, set.NextSetBit(1_000_001));
        Assert.Equal(-1, set.NextSetBit(1_015_809));

        set.ClearAll();
        set.Set(555_555);
        Assert.Equal(555_555, set.NextSetBit(0));
        Assert.Equal(-1, set.NextSetBit(555_556));
    }

    [Fact]
    public void ClearAffectsOnlyTheGivenBit()
    {
        ThreadSafeBitSet set = new();

        set.Set(10);
        set.Set(20);
        set.Set(21);
        set.Set(22);

        set.Clear(21);

        Assert.Equal(3, set.Cardinality());
    }

    [Fact]
    public void SetGetAndCardinality()
    {
        ThreadSafeBitSet set = new();
        int[] ordinals = [1, 5, 10];

        foreach (int ordinal in ordinals)
        {
            set.Set(ordinal);
        }

        Assert.All(ordinals, ordinal => Assert.True(set.Get(ordinal)));
        Assert.Equal(ordinals.Length, set.Cardinality());

        set.Clear(ordinals[0]);

        Assert.False(set.Get(ordinals[0]));
        Assert.Equal(ordinals.Length - 1, set.Cardinality());
    }

    [Fact]
    public void EnumerateSetBitsYieldsThemInOrder()
    {
        ThreadSafeBitSet set = new();
        int[] ordinals = [1, 5, 10];

        foreach (int ordinal in ordinals)
        {
            set.Set(ordinal);
        }

        Assert.Equal(ordinals, set.EnumerateSetBits());
    }

    [Fact]
    public void OrAll()
    {
        int[] ordinals = [1, 5, 10];
        ThreadSafeBitSet[] sets = [.. ordinals.Select(ordinal =>
        {
            ThreadSafeBitSet set = new();
            set.Set(ordinal);
            return set;
        })];

        ThreadSafeBitSet result = ThreadSafeBitSet.OrAll(sets);

        Assert.Equal(ordinals.Length, result.Cardinality());
        Assert.Equal(ordinals, result.EnumerateSetBits());
    }

    [Fact]
    public void AndNot()
    {
        ThreadSafeBitSet first = new();
        ThreadSafeBitSet second = new();
        for (int i = 0; i < 3; i++)
        {
            first.Set(i);
            second.Set(i * 2);
        }

        Assert.NotEqual(first, second);

        ThreadSafeBitSet result = first.AndNot(second);

        // first is {0,1,2}, second is {0,2,4}, so only bit 1 survives.
        Assert.Equal([1], result.EnumerateSetBits());
    }
}

/// <summary>
/// Ported from <c>ByteArrayOrdinalTest</c>.
/// </summary>
/// <remarks>
/// Java's soft-ordinal-limit tests reach into a private static field by reflection to shrink the limit;
/// that field is a <c>const</c> here, so those cases are not ported.
/// </remarks>
public class ByteArrayOrdinalMapTests
{
    [Fact]
    public void ResizePreservesOrdinals()
    {
        ByteArrayOrdinalMap map = new();

        int[] ordinals = [.. Enumerable.Range(0, 179).Select(i => map.GetOrAssignOrdinal(CreateBuffer($"TEST{i}")))];

        map.Resize(4096);

        int[] newOrdinals = [.. Enumerable.Range(0, 179).Select(i => map.Get(CreateBuffer($"TEST{i}")))];

        Assert.Equal(ordinals, newOrdinals);
    }

    [Fact]
    public void ResizeWhenEmptyPreservesOrdinals()
    {
        ByteArrayOrdinalMap map = new();
        map.Resize(4096);

        int[] ordinals = [.. Enumerable.Range(0, 179).Select(i => map.GetOrAssignOrdinal(CreateBuffer($"TEST{i}")))];

        map.Resize(16384);

        int[] newOrdinals = [.. Enumerable.Range(0, 179).Select(i => map.Get(CreateBuffer($"TEST{i}")))];

        Assert.Equal(ordinals, newOrdinals);
    }

    [Fact]
    public void IdenticalRecordsAreDeduplicated()
    {
        ByteArrayOrdinalMap map = new();

        int first = map.GetOrAssignOrdinal(CreateBuffer("alpha"));
        int again = map.GetOrAssignOrdinal(CreateBuffer("alpha"));
        int other = map.GetOrAssignOrdinal(CreateBuffer("beta"));

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public void AbsentRecordsReturnMinusOne()
    {
        ByteArrayOrdinalMap map = new();
        map.GetOrAssignOrdinal(CreateBuffer("alpha"));

        Assert.Equal(-1, map.Get(CreateBuffer("beta")));
    }

    [Fact]
    public void GrowingBeyondTheLoadFactorKeepsEveryRecordFindable()
    {
        ByteArrayOrdinalMap map = new();

        Dictionary<string, int> assigned = [];
        for (int i = 0; i < 10_000; i++)
        {
            assigned[$"record-{i}"] = map.GetOrAssignOrdinal(CreateBuffer($"record-{i}"));
        }

        foreach ((string record, int ordinal) in assigned)
        {
            Assert.Equal(ordinal, map.Get(CreateBuffer(record)));
        }

        Assert.True(map.LoadFactor <= 0.7f);
    }

    [Fact]
    public void PrepareForWriteExposesEachRecordsBytes()
    {
        ByteArrayOrdinalMap map = new();

        string[] records = ["alpha", "beta", "gamma"];
        int[] ordinals = [.. records.Select(record => map.GetOrAssignOrdinal(CreateBuffer(record)))];

        Assert.False(map.IsReadyForWriting);
        Assert.Equal(ordinals.Max(), map.PrepareForWrite());
        Assert.True(map.IsReadyForWriting);

        for (int i = 0; i < records.Length; i++)
        {
            long pointer = map.GetPointerForData(ordinals[i]);
            byte[] expected = SystemEncoding.UTF8.GetBytes(records[i]);

            for (int j = 0; j < expected.Length; j++)
            {
                Assert.Equal(expected[j], map.ByteData.Get(pointer + j));
            }
        }
    }

    [Fact]
    public void PrepareForWriteOnAnEmptyMapReportsNoOrdinals()
    {
        ByteArrayOrdinalMap map = new();

        Assert.Equal(-1, map.PrepareForWrite());
    }

    [Fact]
    public void PutRejectsOutOfRangeOrdinals()
    {
        ByteArrayOrdinalMap map = new();

        Assert.Throws<ArgumentOutOfRangeException>(() => map.Put(CreateBuffer("alpha"), -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.Put(CreateBuffer("alpha"), 1 << 29));
    }

    private static ByteDataArray CreateBuffer(string value)
    {
        ByteDataArray buffer = new();
        foreach (byte b in SystemEncoding.UTF8.GetBytes(value))
        {
            buffer.Write(b);
        }

        return buffer;
    }
}

/// <summary>
/// Coverage for the segmented byte storage that backs variable-length record data.
/// </summary>
public class SegmentedByteArrayTests
{
    [Fact]
    public void GrowsAcrossSegmentBoundaries()
    {
        SegmentedByteArray array = new(new WastefulRecycler(4, 4));

        for (int i = 0; i < 1000; i++)
        {
            array.Set(i, (byte)i);
        }

        for (int i = 0; i < 1000; i++)
        {
            Assert.Equal((byte)i, array.Get(i));
        }
    }

    [Fact]
    public void CopyBetweenSegmentedArraysPreservesContent()
    {
        SegmentedByteArray source = new(new WastefulRecycler(4, 4));
        for (int i = 0; i < 500; i++)
        {
            source.Set(i, (byte)(i * 7));
        }

        SegmentedByteArray destination = new(new WastefulRecycler(5, 4));
        destination.Copy(source, 10, 100, 300);

        for (int i = 0; i < 300; i++)
        {
            Assert.Equal(source.Get(10 + i), destination.Get(100 + i));
        }
    }

    [Fact]
    public void RangeEqualsComparesOnlyTheGivenRange()
    {
        SegmentedByteArray first = new(WastefulRecycler.SmallArrayRecycler);
        SegmentedByteArray second = new(WastefulRecycler.SmallArrayRecycler);

        for (int i = 0; i < 100; i++)
        {
            first.Set(i, (byte)i);
            second.Set(i, (byte)(i == 50 ? 0xFF : i));
        }

        Assert.True(first.RangeEquals(0, second, 0, 50));
        Assert.False(first.RangeEquals(0, second, 0, 60));
    }

    [Fact]
    public void WriteToEmitsTheRequestedRange()
    {
        SegmentedByteArray array = new(new WastefulRecycler(4, 4));
        for (int i = 0; i < 100; i++)
        {
            array.Set(i, (byte)i);
        }

        using MemoryStream stream = new();
        array.WriteTo(stream, 10, 50);

        Assert.Equal([.. Enumerable.Range(10, 50).Select(i => (byte)i)], stream.ToArray());
    }
}
