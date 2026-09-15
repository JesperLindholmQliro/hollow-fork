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

using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.List;
using Hollow.Core.Read.Engine.Map;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Read.Engine.Set;
using Hollow.Core.Read.Iterator;
using Hollow.Core.Schema;
using Hollow.Core.Write;

namespace Hollow.Tests.Core;

/// <summary>
/// The same equivalence check <see cref="DeltaTests"/> makes for object types, applied to the three
/// collection types: after a delta, the consumer must hold exactly what a snapshot of that cycle would
/// have produced.
/// </summary>
/// <remarks>
/// Collections are the harder case, because a record is a run of elements rather than a set of
/// independently sized fields. The run widths and — for sets and maps — the bucket count and the
/// all-ones empty-bucket sentinel are all recomputed each cycle, so a carried-over record generally has
/// to be re-encoded rather than copied. Reading every record back whole is what catches a merge that
/// re-encodes it almost right.
/// </remarks>
public class CollectionDeltaTests
{
    /// <summary>
    /// A whole dataset read back as plain values, so two states can be compared outright.
    /// </summary>
    private sealed record Contents(
        Dictionary<int, int> Values,
        Dictionary<int, int[]> Lists,
        Dictionary<int, int[]> Sets,
        Dictionary<int, (int Key, int Value)[]> Maps);

    /// <summary>A cycle's worth of data, expressed against the value ids rather than ordinals.</summary>
    private sealed record Cycle(int[] Values, int[][] Lists, int[][] Sets, (int Key, int Value)[][] Maps);

    private static HollowObjectSchema ValueSchema()
    {
        HollowObjectSchema schema = new("Value", 1);
        schema.AddField("id", FieldType.Int);
        return schema;
    }

    private static HollowWriteStateEngine NewEngine(HollowObjectSchema valueSchema, int numShards)
    {
        HollowWriteStateEngine engine = new() { RandomizedTag = 1 };
        engine.AddTypeState(new HollowObjectTypeWriteState(valueSchema, numShards));
        engine.AddTypeState(new HollowListTypeWriteState(new HollowListSchema("Values", "Value"), numShards));
        engine.AddTypeState(new HollowSetTypeWriteState(new HollowSetSchema("ValueSet", "Value"), numShards));
        engine.AddTypeState(
            new HollowMapTypeWriteState(new HollowMapSchema("ValueMap", "Value", "Value"), numShards));
        return engine;
    }

    /// <summary>
    /// Writes one cycle's records. Collections are expressed in terms of value ids, which are mapped
    /// through to the ordinals the value records landed on.
    /// </summary>
    private static void WriteCycle(HollowWriteStateEngine engine, HollowObjectSchema valueSchema, Cycle cycle)
    {
        HollowObjectWriteRecord value = new(valueSchema);
        Dictionary<int, int> ordinalOf = [];
        foreach (int id in cycle.Values)
        {
            value.Reset();
            value.SetInt("id", id);
            ordinalOf[id] = engine.Add("Value", value);
        }

        HollowListWriteRecord list = new();
        foreach (int[] elements in cycle.Lists)
        {
            list.Reset();
            foreach (int id in elements)
            {
                list.AddElement(ordinalOf[id]);
            }

            engine.Add("Values", list);
        }

        HollowSetWriteRecord set = new();
        foreach (int[] elements in cycle.Sets)
        {
            set.Reset();
            foreach (int id in elements)
            {
                set.AddElement(ordinalOf[id]);
            }

            engine.Add("ValueSet", set);
        }

        HollowMapWriteRecord map = new();
        foreach ((int Key, int Value)[] entries in cycle.Maps)
        {
            map.Reset();
            foreach ((int key, int mapped) in entries)
            {
                map.AddEntry(ordinalOf[key], ordinalOf[mapped]);
            }

            engine.Add("ValueMap", map);
        }
    }

    private static Contents ReadAll(HollowReadStateEngine engine)
    {
        HollowObjectTypeReadState values =
            Assert.IsType<HollowObjectTypeReadState>(engine.GetTypeState("Value"));
        HollowListTypeReadState lists = Assert.IsType<HollowListTypeReadState>(engine.GetTypeState("Values"));
        HollowSetTypeReadState sets = Assert.IsType<HollowSetTypeReadState>(engine.GetTypeState("ValueSet"));
        HollowMapTypeReadState maps = Assert.IsType<HollowMapTypeReadState>(engine.GetTypeState("ValueMap"));

        int idPosition = values.Schema.GetPosition("id");

        Dictionary<int, int> valueContents = [];
        foreach (int ordinal in values.PopulatedOrdinals.EnumerateSetBits())
        {
            valueContents[ordinal] = values.ReadInt(ordinal, idPosition);
        }

        Dictionary<int, int[]> listContents = [];
        foreach (int ordinal in lists.PopulatedOrdinals.EnumerateSetBits())
        {
            // Order is part of a list's identity, so it is compared as written.
            listContents[ordinal] = [.. lists.ElementOrdinals(ordinal).AsEnumerable()];
        }

        Dictionary<int, int[]> setContents = [];
        foreach (int ordinal in sets.PopulatedOrdinals.EnumerateSetBits())
        {
            // Iteration order is the hash table's, which the bucket count may legitimately change.
            setContents[ordinal] = [.. sets.ElementOrdinals(ordinal).AsEnumerable().Order()];
        }

        Dictionary<int, (int Key, int Value)[]> mapContents = [];
        foreach (int ordinal in maps.PopulatedOrdinals.EnumerateSetBits())
        {
            List<(int Key, int Value)> entries = [];
            foreach (HollowMapEntry entry in maps.Entries(ordinal))
            {
                entries.Add((entry.KeyOrdinal, entry.ValueOrdinal));
            }

            mapContents[ordinal] = [.. entries.Order()];
        }

        return new Contents(valueContents, listContents, setContents, mapContents);
    }

    private static void AssertEquivalent(Contents expected, Contents actual)
    {
        Assert.Equal(expected.Values, actual.Values);

        Assert.Equal(expected.Lists.Keys.Order(), actual.Lists.Keys.Order());
        foreach ((int ordinal, int[] elements) in expected.Lists)
        {
            Assert.Equal(elements, actual.Lists[ordinal]);
        }

        Assert.Equal(expected.Sets.Keys.Order(), actual.Sets.Keys.Order());
        foreach ((int ordinal, int[] elements) in expected.Sets)
        {
            Assert.Equal(elements, actual.Sets[ordinal]);
        }

        Assert.Equal(expected.Maps.Keys.Order(), actual.Maps.Keys.Order());
        foreach ((int ordinal, (int Key, int Value)[] entries) in expected.Maps)
        {
            Assert.Equal(entries, actual.Maps[ordinal]);
        }
    }

    private static HollowReadStateEngine ReadSnapshot(HollowWriteStateEngine engine)
    {
        using MemoryStream stream = new();
        new HollowBlobWriter(engine).WriteSnapshot(stream);
        stream.Position = 0;

        HollowReadStateEngine readEngine = new();
        new HollowBlobReader(readEngine).ReadSnapshot(stream);
        return readEngine;
    }

    private static void ApplyDelta(HollowWriteStateEngine engine, HollowReadStateEngine consumer)
    {
        using MemoryStream stream = new();
        new HollowBlobWriter(engine).WriteDelta(stream);
        stream.Position = 0;

        new HollowBlobReader(consumer).ApplyDelta(stream);
    }

    /// <summary>
    /// Runs every cycle in turn, checking after each transition that the delta-applied consumer matches
    /// a consumer that read that cycle's snapshot outright.
    /// </summary>
    private static void AssertDeltasMatchSnapshots(int numShards, params Cycle[] cycles)
    {
        HollowObjectSchema valueSchema = ValueSchema();
        HollowWriteStateEngine engine = NewEngine(valueSchema, numShards);

        WriteCycle(engine, valueSchema, cycles[0]);
        HollowReadStateEngine consumer = ReadSnapshot(engine);
        AssertEquivalent(ReadAll(ReadSnapshot(engine)), ReadAll(consumer));

        for (int i = 1; i < cycles.Length; i++)
        {
            engine.PrepareForNextCycle();
            engine.RandomizedTag = i + 1;
            WriteCycle(engine, valueSchema, cycles[i]);

            Contents viaSnapshot = ReadAll(ReadSnapshot(engine));
            ApplyDelta(engine, consumer);

            AssertEquivalent(viaSnapshot, ReadAll(consumer));
        }
    }

    /// <summary>
    /// The same as <see cref="AssertDeltasMatchSnapshots"/>, reporting how many records of each kind the
    /// applicators carried across in bulk rather than merging one at a time.
    /// </summary>
    private static (long Objects, long Lists, long Sets, long Maps) RunCyclesCountingBulkCopies(
        int numShards, params Cycle[] cycles)
    {
        HollowObjectSchema valueSchema = ValueSchema();
        HollowWriteStateEngine engine = NewEngine(valueSchema, numShards);

        WriteCycle(engine, valueSchema, cycles[0]);
        HollowReadStateEngine consumer = ReadSnapshot(engine);

        RecordCopyDiagnostics.Reset();

        for (int i = 1; i < cycles.Length; i++)
        {
            engine.PrepareForNextCycle();
            engine.RandomizedTag = i + 1;
            WriteCycle(engine, valueSchema, cycles[i]);

            Contents viaSnapshot = ReadAll(ReadSnapshot(engine));
            ApplyDelta(engine, consumer);

            AssertEquivalent(viaSnapshot, ReadAll(consumer));
        }

        return (
            RecordCopyDiagnostics.BulkCopiedObjects,
            RecordCopyDiagnostics.BulkCopiedLists,
            RecordCopyDiagnostics.BulkCopiedSets,
            RecordCopyDiagnostics.BulkCopiedMaps);
    }

    /// <summary>A cycle of collections over a stable set of values, with one collection of each kind
    /// differing from the last cycle's.</summary>
    private static Cycle StableCycle(int generation)
    {
        int[] values = [.. Enumerable.Range(0, 200)];

        int[][] lists =
        [
            .. Enumerable.Range(0, 200).Select(i =>
                i == 100 ? new[] { generation, 1, 2 } : new[] { i, (i + 1) % 200, (i + 2) % 200 }),
        ];

        int[][] sets =
        [
            .. Enumerable.Range(0, 200).Select(i =>
                i == 100 ? new[] { generation, 3, 4 } : new[] { i, (i + 3) % 200, (i + 5) % 200 }),
        ];

        (int Key, int Value)[][] maps =
        [
            .. Enumerable.Range(0, 200).Select(i =>
                i == 100
                    ? new[] { (generation, 6), (7, 8) }
                    : new[] { (i, (i + 7) % 200), ((i + 11) % 200, (i + 13) % 200) }),
        ];

        return CycleOf(values, lists, sets, maps);
    }

    private static Cycle CycleOf(int[] values, int[][] lists, int[][] sets, (int Key, int Value)[][] maps) =>
        new(values, lists, sets, maps);

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void AddingCollectionRecords(int numShards)
    {
        int[] values = [.. Enumerable.Range(0, 10)];

        Cycle first = CycleOf(
            values,
            [[0, 1], [2, 3, 4]],
            [[0, 1, 2], [5]],
            [[(0, 1), (2, 3)]]);

        Cycle second = CycleOf(
            values,
            [[0, 1], [2, 3, 4], [5, 6, 7, 8, 9]],
            [[0, 1, 2], [5], [6, 7, 8]],
            [[(0, 1), (2, 3)], [(4, 5), (6, 7), (8, 9)]]);

        AssertDeltasMatchSnapshots(numShards, first, second);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void RemovingCollectionRecords(int numShards)
    {
        int[] values = [.. Enumerable.Range(0, 10)];

        Cycle first = CycleOf(
            values,
            [[0, 1], [2, 3, 4], [5, 6, 7, 8, 9]],
            [[0, 1, 2], [5], [6, 7, 8]],
            [[(0, 1), (2, 3)], [(4, 5), (6, 7), (8, 9)]]);

        // The middle record of each type is dropped, which leaves a hole in the ordinal space.
        Cycle second = CycleOf(
            values,
            [[0, 1], [5, 6, 7, 8, 9]],
            [[0, 1, 2], [6, 7, 8]],
            [[(0, 1), (2, 3)]]);

        AssertDeltasMatchSnapshots(numShards, first, second);
    }

    /// <summary>
    /// A hole left by a removal is refilled by a later cycle, so a carried-over record and a new one end
    /// up interleaved in the ordinal space — the case a merge that assumes ascending runs gets wrong.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void RemovalsAndAdditionsInterleave(int numShards)
    {
        int[] values = [.. Enumerable.Range(0, 20)];

        Cycle first = CycleOf(
            values,
            [[0], [1, 2], [3, 4, 5], [6], [7, 8]],
            [[0], [1, 2], [3, 4, 5], [6], [7, 8]],
            [[(0, 1)], [(2, 3), (4, 5)], [(6, 7)], [(8, 9)], [(10, 11)]]);

        Cycle second = CycleOf(
            values,
            [[0], [3, 4, 5], [7, 8], [9, 10, 11, 12], [13]],
            [[0], [3, 4, 5], [7, 8], [9, 10, 11, 12], [13]],
            [[(0, 1)], [(6, 7)], [(10, 11)], [(12, 13), (14, 15)], [(16, 17)]]);

        Cycle third = CycleOf(
            values,
            [[3, 4, 5], [9, 10, 11, 12], [14, 15, 16, 17, 18, 19]],
            [[3, 4, 5], [9, 10, 11, 12], [14, 15, 16, 17, 18, 19]],
            [[(6, 7)], [(12, 13), (14, 15)], [(18, 19)]]);

        AssertDeltasMatchSnapshots(numShards, first, second, third);
    }

    /// <summary>
    /// Element width, bucket count and the empty-bucket sentinel are all recomputed per cycle, so a
    /// cycle that pushes any of them wider forces every carried-over record to be re-encoded.
    /// </summary>
    [Fact]
    public void WideningAnElementWidthRewritesCarriedOverRecords()
    {
        // The first cycle needs few enough values that an element ordinal fits in a handful of bits;
        // the second adds enough to widen it.
        int[] few = [.. Enumerable.Range(0, 4)];
        int[] many = [.. Enumerable.Range(0, 600)];

        Cycle first = CycleOf(few, [[0, 1], [2, 3]], [[0, 1], [2, 3]], [[(0, 1)], [(2, 3)]]);

        Cycle second = CycleOf(
            many,
            [[0, 1], [2, 3], [.. Enumerable.Range(0, 600)]],
            [[0, 1], [2, 3], [.. Enumerable.Range(0, 600)]],
            [[(0, 1)], [(2, 3)], [.. Enumerable.Range(0, 300).Select(i => (i, i + 300))]]);

        AssertDeltasMatchSnapshots(1, first, second);

        // And back down again: the third cycle drops the wide records, so the widths narrow.
        AssertDeltasMatchSnapshots(1, first, second, first);
    }

    /// <summary>
    /// Empty collections carry no elements at all, so their runs are zero-length — easy for a merge to
    /// lose track of, since nothing is copied for them.
    /// </summary>
    [Fact]
    public void EmptyCollectionsSurviveADelta()
    {
        int[] values = [.. Enumerable.Range(0, 5)];

        Cycle first = CycleOf(values, [[], [0, 1]], [[], [0, 1]], [[], [(0, 1)]]);
        Cycle second = CycleOf(values, [[], [2, 3], [4]], [[], [2, 3], [4]], [[], [(2, 3)], [(4, 0)]]);
        Cycle third = CycleOf(values, [[]], [[]], [[]]);

        AssertDeltasMatchSnapshots(1, first, second, third);
    }

    [Fact]
    public void SeveralCollectionDeltasInSequence()
    {
        int[] values = [.. Enumerable.Range(0, 40)];

        // Each cycle keeps some records, drops others and adds new ones, so every generation exercises
        // the merge rather than degenerating into a wholesale replacement.
        Cycle[] cycles =
        [
            .. Enumerable.Range(0, 8).Select(generation =>
            {
                int[][] collections =
                [
                    .. Enumerable.Range(generation, 5)
                        .Select(i => Enumerable.Range(i, (i % 4) + 1).Select(v => v % 40).Distinct().ToArray()),
                ];

                (int Key, int Value)[][] maps =
                [
                    .. Enumerable.Range(generation, 4)
                        .Select(i => Enumerable.Range(i, (i % 3) + 1)
                            .Select(v => (Key: v % 40, Value: (v + 7) % 40))
                            .DistinctBy(entry => entry.Key)
                            .ToArray()),
                ];

                return CycleOf(values, collections, collections, maps);
            }),
        ];

        AssertDeltasMatchSnapshots(1, cycles);
        AssertDeltasMatchSnapshots(4, cycles);
    }

    /// <summary>
    /// The populated-ordinal listeners are driven by the delta's own added and removed ordinal streams
    /// rather than by a bit set in the blob, so they are worth checking separately from the records.
    /// </summary>
    [Fact]
    public void PopulatedOrdinalsTrackACollectionDelta()
    {
        HollowObjectSchema valueSchema = ValueSchema();
        HollowWriteStateEngine engine = NewEngine(valueSchema, 1);

        WriteCycle(
            engine,
            valueSchema,
            CycleOf([0, 1, 2, 3], [[0, 1], [2, 3]], [[0, 1], [2, 3]], [[(0, 1)], [(2, 3)]]));

        HollowReadStateEngine consumer = ReadSnapshot(engine);

        Assert.Equal(2, consumer.GetTypeState("Values")!.PopulatedOrdinals.Cardinality());
        Assert.Equal(2, consumer.GetTypeState("ValueSet")!.PopulatedOrdinals.Cardinality());
        Assert.Equal(2, consumer.GetTypeState("ValueMap")!.PopulatedOrdinals.Cardinality());

        engine.PrepareForNextCycle();
        engine.RandomizedTag = 2;

        // One record of each type is kept, one dropped and one added.
        WriteCycle(
            engine,
            valueSchema,
            CycleOf([0, 1, 2, 3], [[0, 1], [1, 2]], [[0, 1], [1, 2]], [[(0, 1)], [(1, 2)]]));

        ApplyDelta(engine, consumer);

        foreach (string typeName in (string[])["Values", "ValueSet", "ValueMap"])
        {
            HollowTypeReadState state = consumer.GetTypeState(typeName)!;

            Assert.Equal(2, state.PopulatedOrdinals.Cardinality());
            Assert.Equal(2, state.PreviousOrdinals.Cardinality());
            Assert.NotEqual(state.PopulatedOrdinals, state.PreviousOrdinals);
        }
    }

    /// <summary>
    /// A dataset large enough that one edit per kind leaves every other record untouched, and stable
    /// enough that no width moves — which is when each applicator stops merging record by record and
    /// carries the untouched run across wholesale.
    /// </summary>
    /// <remarks>
    /// This is the shape of a real delta, and the only shape that reaches the bulk path at all, so
    /// without a case like it that path would be dead code the other tests never run. The second cycle
    /// also proves the case a first delta cannot: by then the previous cycle's removals are pending, so
    /// a run has to stop at one and what follows has to be shifted by a different amount.
    /// </remarks>
    [Fact]
    public void AnUnchangedRunOfEachCollectionIsCarriedAcrossInBulk()
    {
        (long objects, long lists, long sets, long maps) =
            RunCyclesCountingBulkCopies(1, StableCycle(10), StableCycle(11), StableCycle(12));

        // 200 records of each kind carried across each of the two deltas.
        Assert.Equal(400, lists);
        Assert.Equal(400, sets);
        Assert.Equal(400, maps);

        // And none at all for the values, which no cycle touches: a type that did not change is left
        // out of the delta entirely, so there is nothing to carry across in the first place.
        Assert.Equal(0, objects);
    }
}
