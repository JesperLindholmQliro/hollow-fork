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

using Hollow.Core;
using Hollow.Core.Memory;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.List;
using Hollow.Core.Read.Engine.Map;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Read.Engine.Set;
using Hollow.Core.Read.Iterator;
using Hollow.Core.Schema;
using Hollow.Core.Write;

namespace Hollow.Tests.Core.Read.Engine;

/// <summary>
/// A delta drops the records it removes on the floor. These tests check that the four delta historical
/// state creators pick them up again: that every removed record is still readable afterwards, whole and
/// unchanged, from storage that no longer references the live state.
/// </summary>
public class DeltaHistoricalStateCreatorTests
{
    private const string ValueType = "Value";
    private const string ListType = "Values";
    private const string SetType = "ValueSet";
    private const string MapType = "ValueMap";

    /// <summary>One cycle's records, collections expressed against value ids rather than ordinals.</summary>
    private sealed record Cycle(
        (int Id, string Name)[] Values,
        int[][] Lists,
        int[][] Sets,
        (int Key, int Value)[][] Maps);

    /// <summary>A delta-applied consumer, alongside one still holding the state before the delta.</summary>
    private sealed record Run(HollowReadStateEngine Consumer, HollowReadStateEngine Before);

    [Fact]
    public void ObjectRecordsSurviveTheDeltaThatRemovedThem()
    {
        Run run = Transition(FirstCycle, SecondCycle);

        HollowObjectTypeReadState live = ObjectState(run.Consumer);
        HollowObjectTypeReadState before = ObjectState(run.Before);

        HollowObjectDeltaHistoricalStateCreator creator = new(live);
        creator.PopulateHistory();

        HollowObjectTypeReadState historical = creator.CreateHistoricalTypeReadState(new HollowReadStateEngine());

        int[] removed = [.. Removed(live)];

        Assert.NotEmpty(removed);
        Assert.Equal(removed.Length, creator.OrdinalMapping.Count);
        Assert.Equal(removed.Length - 1, historical.MaxOrdinal);

        int id = historical.Schema.GetPosition("id");
        int name = historical.Schema.GetPosition("name");

        foreach (int ordinal in removed)
        {
            int mapped = creator.OrdinalMapping.Get(ordinal);

            Assert.NotEqual(HollowConstants.OrdinalNone, mapped);
            Assert.Equal(before.ReadInt(ordinal, id), historical.ReadInt(mapped, id));
            Assert.Equal(before.ReadString(ordinal, name), historical.ReadString(mapped, name));
        }
    }

    [Fact]
    public void ListRecordsSurviveTheDeltaThatRemovedThem()
    {
        Run run = Transition(FirstCycle, SecondCycle);

        HollowListTypeReadState live = ListState(run.Consumer);
        HollowListTypeReadState before = ListState(run.Before);

        HollowListDeltaHistoricalStateCreator creator = new(live);
        creator.PopulateHistory();

        HollowListTypeReadState historical = creator.CreateHistoricalTypeReadState(new HollowReadStateEngine());

        int[] removed = [.. Removed(live)];

        Assert.NotEmpty(removed);

        foreach (int ordinal in removed)
        {
            int mapped = creator.OrdinalMapping.Get(ordinal);

            // Order is part of a list's identity, so it is compared as written.
            Assert.Equal(
                before.ElementOrdinals(ordinal).AsEnumerable(),
                historical.ElementOrdinals(mapped).AsEnumerable());
        }
    }

    [Fact]
    public void SetRecordsSurviveTheDeltaThatRemovedThem()
    {
        Run run = Transition(FirstCycle, SecondCycle);

        HollowSetTypeReadState live = SetState(run.Consumer);
        HollowSetTypeReadState before = SetState(run.Before);

        HollowSetDeltaHistoricalStateCreator creator = new(live);
        creator.PopulateHistory();

        HollowSetTypeReadState historical = creator.CreateHistoricalTypeReadState(new HollowReadStateEngine());

        int[] removed = [.. Removed(live)];

        Assert.NotEmpty(removed);

        foreach (int ordinal in removed)
        {
            int mapped = creator.OrdinalMapping.Get(ordinal);

            Assert.Equal(before.Size(ordinal), historical.Size(mapped));

            // A set has no order of its own, so comparing it sorted is what the type actually promises.
            Assert.Equal(
                before.ElementOrdinals(ordinal).AsEnumerable().Order(),
                historical.ElementOrdinals(mapped).AsEnumerable().Order());
        }
    }

    [Fact]
    public void MapRecordsSurviveTheDeltaThatRemovedThem()
    {
        Run run = Transition(FirstCycle, SecondCycle);

        HollowMapTypeReadState live = MapState(run.Consumer);
        HollowMapTypeReadState before = MapState(run.Before);

        HollowMapDeltaHistoricalStateCreator creator = new(live);
        creator.PopulateHistory();

        HollowMapTypeReadState historical = creator.CreateHistoricalTypeReadState(new HollowReadStateEngine());

        int[] removed = [.. Removed(live)];

        Assert.NotEmpty(removed);

        foreach (int ordinal in removed)
        {
            int mapped = creator.OrdinalMapping.Get(ordinal);

            Assert.Equal(before.Size(ordinal), historical.Size(mapped));
            Assert.Equal(Entries(before, ordinal), Entries(historical, mapped));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void RecordsAreRecoveredWhateverTheShardCount(int numShards)
    {
        Run run = Transition(FirstCycle, SecondCycle, numShards);

        HollowObjectTypeReadState live = ObjectState(run.Consumer);
        HollowObjectTypeReadState before = ObjectState(run.Before);

        Assert.Equal(numShards, live.NumShards);

        HollowObjectDeltaHistoricalStateCreator creator = new(live);
        creator.PopulateHistory();

        HollowObjectTypeReadState historical = creator.CreateHistoricalTypeReadState(new HollowReadStateEngine());

        // Whatever the shard count, the history holds one shard: the records were gathered up rather
        // than read from a blob, so there is nothing to spread.
        Assert.Equal(1, historical.NumShards);

        int name = historical.Schema.GetPosition("name");

        Assert.NotEmpty(Removed(live));

        foreach (int ordinal in Removed(live))
        {
            Assert.Equal(
                before.ReadString(ordinal, name),
                historical.ReadString(creator.OrdinalMapping.Get(ordinal), name));
        }
    }

    [Fact]
    public void ReverseTakesTheRecordsTheDeltaAdded()
    {
        Run run = Transition(FirstCycle, SecondCycle);

        HollowObjectTypeReadState live = ObjectState(run.Consumer);

        HollowObjectDeltaHistoricalStateCreator creator = new(live, reverse: true);
        creator.PopulateHistory();

        HollowObjectTypeReadState historical = creator.CreateHistoricalTypeReadState(new HollowReadStateEngine());

        int[] added = [.. Added(live)];

        Assert.NotEmpty(added);
        Assert.Equal(added.Length - 1, historical.MaxOrdinal);

        int id = historical.Schema.GetPosition("id");

        foreach (int ordinal in added)
        {
            Assert.Equal(
                live.ReadInt(ordinal, id),
                historical.ReadInt(creator.OrdinalMapping.Get(ordinal), id));
        }
    }

    [Fact]
    public void ADeltaThatRemovesNothingProducesAnEmptyState()
    {
        // The same cycle twice: every record carries over, so there is nothing to keep.
        Run run = Transition(FirstCycle, FirstCycle);

        HollowObjectTypeReadState live = ObjectState(run.Consumer);

        HollowObjectDeltaHistoricalStateCreator creator = new(live);
        creator.PopulateHistory();

        HollowObjectTypeReadState historical = creator.CreateHistoricalTypeReadState(new HollowReadStateEngine());

        Assert.Empty(Removed(live));
        Assert.Equal(0, creator.OrdinalMapping.Count);
        Assert.Equal(HollowConstants.OrdinalNone, historical.MaxOrdinal);
    }

    [Fact]
    public void AnOrdinalThatWasNotRemovedIsNotInTheMapping()
    {
        Run run = Transition(FirstCycle, SecondCycle);

        HollowObjectTypeReadState live = ObjectState(run.Consumer);

        HollowObjectDeltaHistoricalStateCreator creator = new(live);
        creator.PopulateHistory();

        foreach (int ordinal in live.PopulatedOrdinals.EnumerateSetBits())
        {
            Assert.Equal(HollowConstants.OrdinalNone, creator.OrdinalMapping.Get(ordinal));
        }
    }

    [Fact]
    public void TheHistoricalStateOutlivesTheTypeStateItCameFrom()
    {
        Run run = Transition(FirstCycle, SecondCycle);

        HollowObjectTypeReadState live = ObjectState(run.Consumer);
        HollowObjectTypeReadState before = ObjectState(run.Before);

        HollowObjectDeltaHistoricalStateCreator creator = new(live);
        creator.PopulateHistory();

        HollowObjectTypeReadState historical = creator.CreateHistoricalTypeReadState(new HollowReadStateEngine());
        int[] removed = [.. Removed(live)];

        creator.DereferenceTypeState();

        int id = historical.Schema.GetPosition("id");

        foreach (int ordinal in removed)
        {
            Assert.Equal(
                before.ReadInt(ordinal, id),
                historical.ReadInt(creator.OrdinalMapping.Get(ordinal), id));
        }

        // Java leaves a creator that has let go of its type state to fail with a null reference
        // whenever it is next asked for anything. Saying what happened is more use.
        Assert.Throws<InvalidOperationException>(creator.PopulateHistory);
    }

    [Fact]
    public void ATypeStateThatIsNotTrackingItsOrdinalsIsRefused()
    {
        HollowObjectTypeReadState orphan = new(new HollowReadStateEngine(), MemoryMode.OnHeap, ValueSchema());

        // Without a populated-ordinal listener there is no record of what used to be there, so there is
        // no way to work out what went.
        Assert.Throws<ArgumentException>(() => new HollowObjectDeltaHistoricalStateCreator(orphan));
    }

    private static Cycle FirstCycle { get; } = new(
        [(1, "one"), (2, "two"), (3, "three"), (4, "four"), (5, "five"), (6, "six")],
        [[1, 2, 3], [4, 5], [6], [1, 3, 5]],
        [[1, 2, 3, 4], [5, 6], [1, 3, 5]],
        [[(1, 2), (3, 4)], [(5, 6)], [(1, 3), (5, 5)]]);

    /// <summary>
    /// The same dataset with records dropped, kept and added in each type, so that every creator has
    /// all three cases to deal with.
    /// </summary>
    /// <remarks>
    /// Values 1, 3 and 5 carry over and so keep their ordinals, which is what lets the collections
    /// built out of them — <c>[1, 3, 5]</c> and <c>[(1, 3), (5, 5)]</c> — carry over too.
    /// </remarks>
    private static Cycle SecondCycle { get; } = new(
        [(1, "one"), (3, "three"), (5, "five"), (7, "seven"), (8, "an altogether longer name")],
        [[1, 3, 5], [7, 8, 7, 8], [1, 3]],
        [[1, 3, 5], [3, 5, 7, 8]],
        [[(1, 3), (5, 5)], [(7, 8), (8, 7)]]);

    /// <summary>
    /// Runs <paramref name="first"/>, then applies a delta to <paramref name="second"/>, and hands back
    /// the delta-applied consumer alongside a separate consumer still holding the first cycle.
    /// </summary>
    private static Run Transition(Cycle first, Cycle second, int numShards = 1)
    {
        HollowObjectSchema valueSchema = ValueSchema();
        HollowWriteStateEngine producer = new() { RandomizedTag = 1 };

        producer.AddTypeState(new HollowObjectTypeWriteState(valueSchema, numShards));
        producer.AddTypeState(new HollowListTypeWriteState(new HollowListSchema(ListType, ValueType), numShards));
        producer.AddTypeState(new HollowSetTypeWriteState(new HollowSetSchema(SetType, ValueType), numShards));
        producer.AddTypeState(
            new HollowMapTypeWriteState(new HollowMapSchema(MapType, ValueType, ValueType), numShards));

        WriteCycle(producer, valueSchema, first);

        HollowReadStateEngine consumer = new();
        HollowReadStateEngine before = new();

        using (MemoryStream snapshot = new())
        {
            new HollowBlobWriter(producer).WriteSnapshot(snapshot);

            snapshot.Position = 0;
            new HollowBlobReader(consumer).ReadSnapshot(snapshot);

            snapshot.Position = 0;
            new HollowBlobReader(before).ReadSnapshot(snapshot);
        }

        producer.PrepareForNextCycle();
        producer.RandomizedTag = 2;
        WriteCycle(producer, valueSchema, second);

        using (MemoryStream delta = new())
        {
            new HollowBlobWriter(producer).WriteDelta(delta);

            delta.Position = 0;
            new HollowBlobReader(consumer).ApplyDelta(delta);
        }

        return new Run(consumer, before);
    }

    private static HollowObjectSchema ValueSchema()
    {
        HollowObjectSchema schema = new(ValueType, 2);

        schema.AddField("id", FieldType.Int);
        schema.AddField("name", FieldType.String);

        return schema;
    }

    private static void WriteCycle(HollowWriteStateEngine producer, HollowObjectSchema valueSchema, Cycle cycle)
    {
        HollowObjectWriteRecord value = new(valueSchema);
        Dictionary<int, int> ordinalOf = [];

        foreach ((int id, string name) in cycle.Values)
        {
            value.Reset();
            value.SetInt("id", id);
            value.SetString("name", name);

            ordinalOf[id] = producer.Add(ValueType, value);
        }

        HollowListWriteRecord list = new();

        foreach (int[] elements in cycle.Lists)
        {
            list.Reset();

            foreach (int id in elements)
            {
                list.AddElement(ordinalOf[id]);
            }

            producer.Add(ListType, list);
        }

        HollowSetWriteRecord set = new();

        foreach (int[] elements in cycle.Sets)
        {
            set.Reset();

            foreach (int id in elements)
            {
                set.AddElement(ordinalOf[id]);
            }

            producer.Add(SetType, set);
        }

        HollowMapWriteRecord map = new();

        foreach ((int Key, int Value)[] entries in cycle.Maps)
        {
            map.Reset();

            foreach ((int key, int mapped) in entries)
            {
                map.AddEntry(ordinalOf[key], ordinalOf[mapped]);
            }

            producer.Add(MapType, map);
        }
    }

    private static HollowObjectTypeReadState ObjectState(HollowReadStateEngine engine) =>
        Assert.IsType<HollowObjectTypeReadState>(engine.GetTypeState(ValueType));

    private static HollowListTypeReadState ListState(HollowReadStateEngine engine) =>
        Assert.IsType<HollowListTypeReadState>(engine.GetTypeState(ListType));

    private static HollowSetTypeReadState SetState(HollowReadStateEngine engine) =>
        Assert.IsType<HollowSetTypeReadState>(engine.GetTypeState(SetType));

    private static HollowMapTypeReadState MapState(HollowReadStateEngine engine) =>
        Assert.IsType<HollowMapTypeReadState>(engine.GetTypeState(MapType));

    private static IEnumerable<int> Removed(HollowTypeReadState typeState) =>
        typeState.PreviousOrdinals.EnumerateSetBits().Where(ordinal => !typeState.PopulatedOrdinals.Get(ordinal));

    private static IEnumerable<int> Added(HollowTypeReadState typeState) =>
        typeState.PopulatedOrdinals.EnumerateSetBits().Where(ordinal => !typeState.PreviousOrdinals.Get(ordinal));

    private static (int Key, int Value)[] Entries(HollowMapTypeReadState typeState, int ordinal)
    {
        List<(int Key, int Value)> entries = [];
        foreach (HollowMapEntry entry in typeState.Entries(ordinal))
        {
            entries.Add((entry.KeyOrdinal, entry.ValueOrdinal));
        }

        return [.. entries.Order()];
    }
}
