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
/// Round-trips the three collection types. Sets and maps are the interesting cases: the producer lays
/// each record's elements out in its own open-addressed hash table, and the consumer has to probe that
/// table the same way to find anything, so a disagreement about bucket count, hash mixing or the
/// empty-bucket sentinel shows up immediately.
/// </summary>
public class CollectionRoundTripTests
{
    private static HollowReadStateEngine RoundTrip(HollowWriteStateEngine writeEngine)
    {
        using MemoryStream stream = new();
        new HollowBlobWriter(writeEngine).WriteSnapshot(stream);

        stream.Position = 0;
        HollowReadStateEngine readEngine = new();
        new HollowBlobReader(readEngine).ReadSnapshot(stream);

        return readEngine;
    }

    /// <summary>
    /// Builds a "Value" object type holding a single int, plus whatever collection type the caller
    /// adds, and populates the values 0..count-1.
    /// </summary>
    private static (HollowWriteStateEngine Engine, HollowObjectSchema ValueSchema, int[] ValueOrdinals)
        WithValues(int count, params HollowTypeWriteState[] collectionStates)
    {
        HollowObjectSchema valueSchema = new("Value", 1);
        valueSchema.AddField("id", FieldType.Int);

        HollowWriteStateEngine engine = new();
        engine.AddTypeState(new HollowObjectTypeWriteState(valueSchema));
        foreach (HollowTypeWriteState state in collectionStates)
        {
            engine.AddTypeState(state);
        }

        HollowObjectWriteRecord record = new(valueSchema);
        int[] ordinals = new int[count];
        for (int i = 0; i < count; i++)
        {
            record.Reset();
            record.SetInt("id", i);
            ordinals[i] = engine.Add("Value", record);
        }

        return (engine, valueSchema, ordinals);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void ListsSurviveTheRoundTrip(int numShards)
    {
        HollowListSchema listSchema = new("Values", "Value");
        (HollowWriteStateEngine engine, _, int[] valueOrdinals) =
            WithValues(50, new HollowListTypeWriteState(listSchema, numShards));

        // Lists of varying length, including an empty one, and one with a repeated element.
        int[][] lists =
        [
            [],
            [valueOrdinals[0]],
            [valueOrdinals[3], valueOrdinals[1], valueOrdinals[2]],
            [valueOrdinals[7], valueOrdinals[7], valueOrdinals[7]],
            [.. valueOrdinals],
        ];

        HollowListWriteRecord record = new();
        int[] listOrdinals = new int[lists.Length];
        for (int i = 0; i < lists.Length; i++)
        {
            record.Reset();
            foreach (int element in lists[i])
            {
                record.AddElement(element);
            }

            listOrdinals[i] = engine.Add("Values", record);
        }

        HollowReadStateEngine readEngine = RoundTrip(engine);
        HollowListTypeReadState readState = Assert.IsType<HollowListTypeReadState>(readEngine.GetTypeState("Values"));

        for (int i = 0; i < lists.Length; i++)
        {
            int ordinal = listOrdinals[i];

            Assert.Equal(lists[i].Length, readState.Size(ordinal));

            // Order must be preserved exactly, both by index and by iteration.
            for (int j = 0; j < lists[i].Length; j++)
            {
                Assert.Equal(lists[i][j], readState.GetElementOrdinal(ordinal, j));
            }

            Assert.Equal(lists[i], readState.ElementOrdinals(ordinal).AsEnumerable());
        }
    }

    [Fact]
    public void ReadingPastTheEndOfAListThrows()
    {
        HollowListSchema listSchema = new("Values", "Value");
        (HollowWriteStateEngine engine, _, int[] valueOrdinals) =
            WithValues(3, new HollowListTypeWriteState(listSchema));

        HollowListWriteRecord record = new();
        record.AddElement(valueOrdinals[0]);
        int ordinal = engine.Add("Values", record);

        HollowListTypeReadState readState =
            Assert.IsType<HollowListTypeReadState>(RoundTrip(engine).GetTypeState("Values"));

        Assert.Equal(valueOrdinals[0], readState.GetElementOrdinal(ordinal, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => readState.GetElementOrdinal(ordinal, 1));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void SetsSurviveTheRoundTrip(int numShards)
    {
        HollowSetSchema setSchema = new("ValueSet", "Value");
        (HollowWriteStateEngine engine, _, int[] valueOrdinals) =
            WithValues(100, new HollowSetTypeWriteState(setSchema, numShards));

        int[][] sets =
        [
            [],
            [valueOrdinals[0]],
            [valueOrdinals[5], valueOrdinals[10], valueOrdinals[15]],
            [.. valueOrdinals],
        ];

        HollowSetWriteRecord record = new();
        int[] setOrdinals = new int[sets.Length];
        for (int i = 0; i < sets.Length; i++)
        {
            record.Reset();
            foreach (int element in sets[i])
            {
                record.AddElement(element);
            }

            setOrdinals[i] = engine.Add("ValueSet", record);
        }

        HollowReadStateEngine readEngine = RoundTrip(engine);
        HollowSetTypeReadState readState = Assert.IsType<HollowSetTypeReadState>(readEngine.GetTypeState("ValueSet"));

        for (int i = 0; i < sets.Length; i++)
        {
            int ordinal = setOrdinals[i];

            Assert.Equal(sets[i].Length, readState.Size(ordinal));

            // Iteration order is the hash table's, so compare as sets.
            Assert.Equal([.. sets[i].Order()], readState.ElementOrdinals(ordinal).AsEnumerable().Order());

            foreach (int element in sets[i])
            {
                Assert.True(readState.Contains(ordinal, element), $"set {i} should contain {element}");

                // A potential-match run must include the element it was seeded with.
                Assert.Contains(
                    element,
                    readState.PotentialMatchElementOrdinals(ordinal, element).AsEnumerable());
            }

            foreach (int absent in valueOrdinals.Except(sets[i]))
            {
                Assert.False(readState.Contains(ordinal, absent), $"set {i} should not contain {absent}");
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void MapsSurviveTheRoundTrip(int numShards)
    {
        HollowMapSchema mapSchema = new("ValueMap", "Value", "Value");
        (HollowWriteStateEngine engine, _, int[] valueOrdinals) =
            WithValues(100, new HollowMapTypeWriteState(mapSchema, numShards));

        Dictionary<int, int>[] maps =
        [
            [],
            new Dictionary<int, int> { [valueOrdinals[0]] = valueOrdinals[1] },
            new Dictionary<int, int>
            {
                [valueOrdinals[2]] = valueOrdinals[3],
                [valueOrdinals[4]] = valueOrdinals[5],
                [valueOrdinals[6]] = valueOrdinals[7],
            },
            valueOrdinals.ToDictionary(o => o, o => valueOrdinals[(o + 1) % valueOrdinals.Length]),
        ];

        HollowMapWriteRecord record = new();
        int[] mapOrdinals = new int[maps.Length];
        for (int i = 0; i < maps.Length; i++)
        {
            record.Reset();
            foreach ((int key, int value) in maps[i])
            {
                record.AddEntry(key, value);
            }

            mapOrdinals[i] = engine.Add("ValueMap", record);
        }

        HollowReadStateEngine readEngine = RoundTrip(engine);
        HollowMapTypeReadState readState = Assert.IsType<HollowMapTypeReadState>(readEngine.GetTypeState("ValueMap"));

        for (int i = 0; i < maps.Length; i++)
        {
            int ordinal = mapOrdinals[i];

            Assert.Equal(maps[i].Count, readState.Size(ordinal));

            foreach ((int key, int value) in maps[i])
            {
                Assert.Equal(value, readState.Get(ordinal, key));
            }

            foreach (int absentKey in valueOrdinals.Except(maps[i].Keys))
            {
                Assert.Equal(-1, readState.Get(ordinal, absentKey));
            }

            // Iterating must yield exactly the entries that were written.
            Dictionary<int, int> iterated = [];
            foreach (HollowMapEntry entry in readState.Entries(ordinal))
            {
                iterated[entry.KeyOrdinal] = entry.ValueOrdinal;
            }

            Assert.Equal(maps[i].Count, iterated.Count);
            foreach ((int key, int value) in maps[i])
            {
                Assert.Equal(value, iterated[key]);
            }
        }
    }

    [Fact]
    public void AllFourTypeKindsCoexistInOneBlob()
    {
        HollowObjectSchema valueSchema = new("Value", 1);
        valueSchema.AddField("id", FieldType.Int);

        HollowListSchema listSchema = new("Values", "Value");
        HollowSetSchema setSchema = new("ValueSet", "Value");
        HollowMapSchema mapSchema = new("ValueMap", "Value", "Value");

        HollowWriteStateEngine engine = new();
        engine.AddTypeState(new HollowObjectTypeWriteState(valueSchema));
        engine.AddTypeState(new HollowListTypeWriteState(listSchema));
        engine.AddTypeState(new HollowSetTypeWriteState(setSchema));
        engine.AddTypeState(new HollowMapTypeWriteState(mapSchema));

        HollowObjectWriteRecord value = new(valueSchema);
        int[] valueOrdinals = new int[4];
        for (int i = 0; i < valueOrdinals.Length; i++)
        {
            value.Reset();
            value.SetInt("id", i * 100);
            valueOrdinals[i] = engine.Add("Value", value);
        }

        HollowListWriteRecord list = new();
        list.AddElement(valueOrdinals[0]);
        list.AddElement(valueOrdinals[1]);
        int listOrdinal = engine.Add("Values", list);

        HollowSetWriteRecord set = new();
        set.AddElement(valueOrdinals[1]);
        set.AddElement(valueOrdinals[2]);
        int setOrdinal = engine.Add("ValueSet", set);

        HollowMapWriteRecord map = new();
        map.AddEntry(valueOrdinals[2], valueOrdinals[3]);
        int mapOrdinal = engine.Add("ValueMap", map);

        HollowReadStateEngine readEngine = RoundTrip(engine);

        HollowObjectTypeReadState values =
            Assert.IsType<HollowObjectTypeReadState>(readEngine.GetTypeState("Value"));
        HollowListTypeReadState lists = Assert.IsType<HollowListTypeReadState>(readEngine.GetTypeState("Values"));
        HollowSetTypeReadState sets = Assert.IsType<HollowSetTypeReadState>(readEngine.GetTypeState("ValueSet"));
        HollowMapTypeReadState maps = Assert.IsType<HollowMapTypeReadState>(readEngine.GetTypeState("ValueMap"));

        Assert.Equal([valueOrdinals[0], valueOrdinals[1]], lists.ElementOrdinals(listOrdinal).AsEnumerable());
        Assert.True(sets.Contains(setOrdinal, valueOrdinals[2]));
        Assert.Equal(valueOrdinals[3], maps.Get(mapOrdinal, valueOrdinals[2]));

        // Follow a reference all the way through to the underlying object field.
        int elementOrdinal = lists.GetElementOrdinal(listOrdinal, 1);
        Assert.Equal(100, values.ReadInt(elementOrdinal, valueSchema.GetPosition("id")));

        // Element type states should have been wired onto the collection schemas.
        Assert.Same(values, lists.Schema.ElementTypeState);
        Assert.Same(values, sets.Schema.ElementTypeState);
        Assert.Same(values, maps.Schema.KeyTypeState);
        Assert.Same(values, maps.Schema.ValueTypeState);
    }

    [Fact]
    public void IdenticalCollectionsShareAnOrdinal()
    {
        HollowListSchema listSchema = new("Values", "Value");
        (HollowWriteStateEngine engine, _, int[] valueOrdinals) =
            WithValues(5, new HollowListTypeWriteState(listSchema));

        HollowListWriteRecord record = new();
        record.AddElement(valueOrdinals[0]);
        record.AddElement(valueOrdinals[1]);
        int first = engine.Add("Values", record);

        record.Reset();
        record.AddElement(valueOrdinals[0]);
        record.AddElement(valueOrdinals[1]);
        int duplicate = engine.Add("Values", record);

        // Order matters for a list, so the reversed pair is a different record.
        record.Reset();
        record.AddElement(valueOrdinals[1]);
        record.AddElement(valueOrdinals[0]);
        int reversed = engine.Add("Values", record);

        Assert.Equal(first, duplicate);
        Assert.NotEqual(first, reversed);

        Assert.Equal(2, RoundTrip(engine).GetTypeState("Values")!.PopulatedOrdinals.Cardinality());
    }

    [Fact]
    public void SetElementOrderDoesNotAffectDeduplication()
    {
        HollowSetSchema setSchema = new("ValueSet", "Value");
        (HollowWriteStateEngine engine, _, int[] valueOrdinals) =
            WithValues(5, new HollowSetTypeWriteState(setSchema));

        HollowSetWriteRecord record = new();
        record.AddElement(valueOrdinals[0]);
        record.AddElement(valueOrdinals[1]);
        record.AddElement(valueOrdinals[2]);
        int first = engine.Add("ValueSet", record);

        // The same elements added in a different order must serialise identically, because the record
        // sorts them before writing.
        record.Reset();
        record.AddElement(valueOrdinals[2]);
        record.AddElement(valueOrdinals[0]);
        record.AddElement(valueOrdinals[1]);
        int reordered = engine.Add("ValueSet", record);

        Assert.Equal(first, reordered);
        Assert.Equal(1, RoundTrip(engine).GetTypeState("ValueSet")!.PopulatedOrdinals.Cardinality());
    }

    [Fact]
    public void LargeSetProbesCorrectlyUnderCollisions()
    {
        // A set large enough that the hash table is densely packed, so lookups have to probe past
        // several occupied buckets to find their element.
        HollowSetSchema setSchema = new("ValueSet", "Value");
        (HollowWriteStateEngine engine, _, int[] valueOrdinals) =
            WithValues(1000, new HollowSetTypeWriteState(setSchema));

        HollowSetWriteRecord record = new();
        foreach (int element in valueOrdinals)
        {
            record.AddElement(element);
        }

        int ordinal = engine.Add("ValueSet", record);

        HollowSetTypeReadState readState =
            Assert.IsType<HollowSetTypeReadState>(RoundTrip(engine).GetTypeState("ValueSet"));

        Assert.Equal(valueOrdinals.Length, readState.Size(ordinal));
        foreach (int element in valueOrdinals)
        {
            Assert.True(readState.Contains(ordinal, element));
        }

        Assert.Equal(valueOrdinals.Order(), readState.ElementOrdinals(ordinal).AsEnumerable().Order());
    }
}
