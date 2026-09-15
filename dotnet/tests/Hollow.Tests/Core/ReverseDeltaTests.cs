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
using Hollow.Core.Read;
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
/// A reverse delta takes a consumer back a cycle, which a consumer needs when it has to abandon a
/// state it has already moved to.
/// </summary>
/// <remarks>
/// The check throughout is the same one <see cref="DeltaTests"/> makes in the other direction: after
/// applying the reverse delta the consumer must hold exactly what it held before it moved forward, and
/// exactly what a snapshot of that cycle would have produced.
/// </remarks>
public class ReverseDeltaTests
{
    private sealed record Movie(int Id, string? Title, decimal Price);

    private static HollowObjectSchema MovieSchema()
    {
        HollowObjectSchema schema = new("Movie", 3);
        schema.AddField("id", FieldType.Int);
        schema.AddField("title", FieldType.String);
        schema.AddField("price", FieldType.Decimal);
        return schema;
    }

    private static void AddMovies(
        HollowWriteStateEngine engine, HollowObjectSchema schema, IEnumerable<Movie> movies)
    {
        HollowObjectWriteRecord record = new(schema);

        foreach (Movie movie in movies)
        {
            record.Reset();
            record.SetInt("id", movie.Id);
            record.SetString("title", movie.Title);
            record.SetDecimal("price", movie.Price);
            engine.Add("Movie", record);
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

    private static void ApplyReverseDelta(HollowWriteStateEngine engine, HollowReadStateEngine consumer)
    {
        using MemoryStream stream = new();
        new HollowBlobWriter(engine).WriteReverseDelta(stream);
        stream.Position = 0;

        new HollowBlobReader(consumer).ApplyDelta(stream);
    }

    /// <summary>
    /// Every populated record, keyed by ordinal, so two states can be compared whole.
    /// </summary>
    private static Dictionary<int, Movie> ReadAll(HollowReadStateEngine engine)
    {
        HollowObjectTypeReadState state =
            Assert.IsType<HollowObjectTypeReadState>(engine.GetTypeState("Movie"));

        int id = state.Schema.GetPosition("id");
        int title = state.Schema.GetPosition("title");
        int price = state.Schema.GetPosition("price");

        Dictionary<int, Movie> movies = [];
        foreach (int ordinal in state.PopulatedOrdinals.EnumerateSetBits())
        {
            movies[ordinal] = new Movie(
                state.ReadInt(ordinal, id),
                state.ReadString(ordinal, title),
                state.ReadDecimal(ordinal, price) ?? 0m);
        }

        return movies;
    }

    /// <summary>
    /// Runs two cycles, moves a consumer forward, then back, and checks that it ends up exactly where
    /// it started.
    /// </summary>
    private static void AssertReverseDeltaRestoresTheFirstCycle(Movie[] firstCycle, Movie[] secondCycle)
    {
        HollowObjectSchema schema = MovieSchema();

        HollowWriteStateEngine engine = new() { RandomizedTag = 111 };
        engine.AddTypeState(new HollowObjectTypeWriteState(schema));

        AddMovies(engine, schema, firstCycle);
        HollowReadStateEngine consumer = ReadSnapshot(engine);
        Dictionary<int, Movie> beforeMovingForward = ReadAll(consumer);

        engine.PrepareForNextCycle();
        engine.RandomizedTag = 222;
        AddMovies(engine, schema, secondCycle);

        ApplyDelta(engine, consumer);
        Assert.NotEqual(beforeMovingForward, ReadAll(consumer));

        ApplyReverseDelta(engine, consumer);

        Assert.Equal(beforeMovingForward, ReadAll(consumer));
        Assert.Equal(111, consumer.RandomizedTag);
    }

    [Fact]
    public void AddingRecordsIsUndone()
    {
        Movie[] first = [new(1, "A", 1.50m), new(2, "B", 2.50m)];
        AssertReverseDeltaRestoresTheFirstCycle(first, [.. first, new Movie(3, "C", 3.50m)]);
    }

    [Fact]
    public void RemovingRecordsIsUndone()
    {
        Movie[] first = [new(1, "A", 1.50m), new(2, "B", 2.50m), new(3, "C", 3.50m)];
        AssertReverseDeltaRestoresTheFirstCycle(first, [first[0], first[2]]);
    }

    [Fact]
    public void AddingAndRemovingTogetherIsUndone()
    {
        Movie[] first = [new(1, "A", 1m), new(2, "B", 2m), new(3, "C", 3m)];
        Movie[] second = [first[0], new Movie(4, "D", 4m), new Movie(5, "E", 5m)];
        AssertReverseDeltaRestoresTheFirstCycle(first, second);
    }

    /// <summary>
    /// The second cycle widens the fields, so going back has to narrow them again — every record the
    /// reverse delta carries over has to be re-encoded at the first cycle's widths.
    /// </summary>
    [Fact]
    public void WideningAndNarrowingAFieldIsUndone()
    {
        Movie[] first = [new(1, "a", 0.01m), new(2, "b", 0.02m)];
        Movie[] second =
        [
            .. first,
            new Movie(int.MaxValue / 2, new string('x', 500), decimal.MaxValue),
        ];

        AssertReverseDeltaRestoresTheFirstCycle(first, second);
    }

    [Fact]
    public void NullsSurviveAReverseDelta()
    {
        Movie[] first = [new(1, null, 1m), new(2, "B", 2m)];
        Movie[] second = [first[0], new Movie(3, null, 3m)];
        AssertReverseDeltaRestoresTheFirstCycle(first, second);
    }

    [Fact]
    public void VariableLengthDataStaysAlignedAcrossAReverseDelta()
    {
        Movie[] first =
            [.. Enumerable.Range(0, 40).Select(i => new Movie(i, new string('x', i), i / 100m))];
        Movie[] second =
        [
            .. first.Where(m => m.Id % 3 != 0),
            .. Enumerable.Range(100, 10).Select(i => new Movie(i, new string('y', i - 60), i / 100m)),
        ];

        AssertReverseDeltaRestoresTheFirstCycle(first, second);
    }

    /// <summary>
    /// A consumer that goes back must hold the same records a consumer that read the earlier snapshot
    /// outright holds — not merely the same records it happened to hold before moving forward.
    /// </summary>
    [Fact]
    public void TheRestoredStateMatchesASnapshotOfThatCycle()
    {
        HollowObjectSchema schema = MovieSchema();

        HollowWriteStateEngine engine = new() { RandomizedTag = 1 };
        engine.AddTypeState(new HollowObjectTypeWriteState(schema));

        Movie[] first = [.. Enumerable.Range(0, 30).Select(i => new Movie(i, $"movie-{i}", i / 8m))];
        AddMovies(engine, schema, first);

        HollowReadStateEngine viaSnapshot = ReadSnapshot(engine);
        HollowReadStateEngine consumer = ReadSnapshot(engine);

        engine.PrepareForNextCycle();
        engine.RandomizedTag = 2;
        AddMovies(engine, schema, [.. first.Where(m => m.Id % 4 != 0), new Movie(99, "new", 99m)]);

        ApplyDelta(engine, consumer);
        ApplyReverseDelta(engine, consumer);

        Assert.Equal(ReadAll(viaSnapshot), ReadAll(consumer));
        Assert.Equal(
            viaSnapshot.GetTypeState("Movie")!.PopulatedOrdinals.Cardinality(),
            consumer.GetTypeState("Movie")!.PopulatedOrdinals.Cardinality());
    }

    /// <summary>
    /// A reverse delta names the state it undoes. Applying it to the state it would have produced —
    /// which is the easy mistake, since that is where a consumer that already went back is — must be
    /// refused.
    /// </summary>
    [Fact]
    public void AReverseDeltaCannotBeAppliedTwice()
    {
        HollowObjectSchema schema = MovieSchema();

        HollowWriteStateEngine engine = new() { RandomizedTag = 1 };
        engine.AddTypeState(new HollowObjectTypeWriteState(schema));
        AddMovies(engine, schema, [new Movie(1, "A", 1m)]);

        HollowReadStateEngine consumer = ReadSnapshot(engine);

        engine.PrepareForNextCycle();
        engine.RandomizedTag = 2;
        AddMovies(engine, schema, [new Movie(2, "B", 2m)]);

        ApplyDelta(engine, consumer);
        ApplyReverseDelta(engine, consumer);

        using MemoryStream stream = new();
        new HollowBlobWriter(engine).WriteReverseDelta(stream);
        stream.Position = 0;

        Assert.Throws<InvalidDataException>(() => new HollowBlobReader(consumer).ApplyDelta(stream));
    }

    /// <summary>
    /// A reverse delta describes a transition back to the earlier state, so it carries that state's
    /// header tags rather than the current ones.
    /// </summary>
    [Fact]
    public void AReverseDeltaCarriesThePreviousCyclesHeaderTags()
    {
        HollowObjectSchema schema = MovieSchema();

        HollowWriteStateEngine engine = new() { RandomizedTag = 1 };
        engine.AddTypeState(new HollowObjectTypeWriteState(schema));
        engine.AddHeaderTag("version", "one");
        AddMovies(engine, schema, [new Movie(1, "A", 1m)]);

        HollowReadStateEngine consumer = ReadSnapshot(engine);
        Assert.Equal("one", consumer.HeaderTags["version"]);

        engine.PrepareForNextCycle();
        engine.RandomizedTag = 2;
        engine.AddHeaderTag("version", "two");
        AddMovies(engine, schema, [new Movie(2, "B", 2m)]);

        ApplyDelta(engine, consumer);
        Assert.Equal("two", consumer.HeaderTags["version"]);

        ApplyReverseDelta(engine, consumer);
        Assert.Equal("one", consumer.HeaderTags["version"]);
    }

    /// <summary>
    /// Collections have the same reverse path as object types, and a set or map has the extra wrinkle
    /// that its bucket count changes with its size, so a record carried back has to be re-hashed.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void CollectionsSurviveAReverseDelta(int numShards)
    {
        HollowObjectSchema valueSchema = new("Value", 1);
        valueSchema.AddField("id", FieldType.Int);

        HollowWriteStateEngine engine = new() { RandomizedTag = 1 };
        engine.AddTypeState(new HollowObjectTypeWriteState(valueSchema, numShards));
        engine.AddTypeState(new HollowListTypeWriteState(new HollowListSchema("Values", "Value"), numShards));
        engine.AddTypeState(new HollowSetTypeWriteState(new HollowSetSchema("ValueSet", "Value"), numShards));
        engine.AddTypeState(
            new HollowMapTypeWriteState(new HollowMapSchema("ValueMap", "Value", "Value"), numShards));

        WriteCollections(engine, valueSchema, 0, 12);
        HollowReadStateEngine consumer = ReadSnapshot(engine);
        Contents before = ReadCollections(consumer);

        engine.PrepareForNextCycle();
        engine.RandomizedTag = 2;
        WriteCollections(engine, valueSchema, 4, 30);

        ApplyDelta(engine, consumer);
        AssertDiffer(before, ReadCollections(consumer));

        ApplyReverseDelta(engine, consumer);
        AssertEquivalent(before, ReadCollections(consumer));
    }

    /// <summary>
    /// Compares two datasets entry by entry. The dictionaries inside <see cref="Contents"/> compare by
    /// reference, so the record's own equality would pass for any two of them.
    /// </summary>
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

    /// <summary>
    /// Asserts that the transition actually changed something, or the round trip proves nothing.
    /// </summary>
    private static void AssertDiffer(Contents expected, Contents actual) =>
        Assert.Throws<Xunit.Sdk.EqualException>(() => AssertEquivalent(expected, actual));

    private sealed record Contents(
        Dictionary<int, int> Values,
        Dictionary<int, int[]> Lists,
        Dictionary<int, int[]> Sets,
        Dictionary<int, (int Key, int Value)[]> Maps);

    /// <summary>
    /// Writes one cycle's worth of values, plus a list, a set and a map holding runs of them.
    /// </summary>
    private static void WriteCollections(
        HollowWriteStateEngine engine, HollowObjectSchema valueSchema, int from, int count)
    {
        HollowObjectWriteRecord value = new(valueSchema);
        int[] ordinals = new int[count];

        for (int i = 0; i < count; i++)
        {
            value.Reset();
            value.SetInt("id", from + i);
            ordinals[i] = engine.Add("Value", value);
        }

        HollowListWriteRecord list = new();
        HollowSetWriteRecord set = new();
        HollowMapWriteRecord map = new();

        for (int start = 0; start + 4 <= count; start += 4)
        {
            list.Reset();
            set.Reset();
            map.Reset();

            for (int i = start; i < start + 4; i++)
            {
                list.AddElement(ordinals[i]);
                set.AddElement(ordinals[i]);
                map.AddEntry(ordinals[i], ordinals[(i + 1) % count]);
            }

            engine.Add("Values", list);
            engine.Add("ValueSet", set);
            engine.Add("ValueMap", map);
        }
    }

    private static Contents ReadCollections(HollowReadStateEngine engine)
    {
        HollowObjectTypeReadState values =
            Assert.IsType<HollowObjectTypeReadState>(engine.GetTypeState("Value"));
        HollowListTypeReadState lists = Assert.IsType<HollowListTypeReadState>(engine.GetTypeState("Values"));
        HollowSetTypeReadState sets = Assert.IsType<HollowSetTypeReadState>(engine.GetTypeState("ValueSet"));
        HollowMapTypeReadState maps = Assert.IsType<HollowMapTypeReadState>(engine.GetTypeState("ValueMap"));

        int idField = values.Schema.GetPosition("id");

        Dictionary<int, int> valueContents = [];
        foreach (int ordinal in values.PopulatedOrdinals.EnumerateSetBits())
        {
            valueContents[ordinal] = values.ReadInt(ordinal, idField);
        }

        Dictionary<int, int[]> listContents = [];
        foreach (int ordinal in lists.PopulatedOrdinals.EnumerateSetBits())
        {
            listContents[ordinal] = [.. lists.ElementOrdinals(ordinal).AsEnumerable()];
        }

        Dictionary<int, int[]> setContents = [];
        foreach (int ordinal in sets.PopulatedOrdinals.EnumerateSetBits())
        {
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

    /// <summary>
    /// Several cycles forward and then all the way back, which is where a reverse delta that merges
    /// almost right would drift.
    /// </summary>
    [Fact]
    public void SeveralCyclesCanBeUnwoundOneAtATime()
    {
        HollowObjectSchema schema = MovieSchema();

        HollowWriteStateEngine engine = new() { RandomizedTag = 1 };
        engine.AddTypeState(new HollowObjectTypeWriteState(schema));

        List<Movie[]> generations = [];
        List<Dictionary<int, Movie>> snapshots = [];
        List<byte[]> reverseDeltas = [];

        Movie[] current = [.. Enumerable.Range(0, 10).Select(i => new Movie(i, $"gen0-{i}", i / 4m))];
        AddMovies(engine, schema, current);
        generations.Add(current);

        HollowReadStateEngine consumer = ReadSnapshot(engine);
        snapshots.Add(ReadAll(consumer));

        for (int generation = 1; generation <= 5; generation++)
        {
            engine.PrepareForNextCycle();
            engine.RandomizedTag = generation + 1;

            current =
            [
                .. generations[^1].Where(m => m.Id % 3 != generation % 3),
                .. Enumerable.Range(generation * 100, 4)
                    .Select(i => new Movie(i, $"gen{generation}-{i}", i / 4m)),
            ];

            AddMovies(engine, schema, current);
            generations.Add(current);

            // The reverse delta has to be written now: the records it carries are gone after the next
            // PrepareForNextCycle.
            using MemoryStream reverse = new();
            new HollowBlobWriter(engine).WriteReverseDelta(reverse);
            reverseDeltas.Add(reverse.ToArray());

            ApplyDelta(engine, consumer);
            snapshots.Add(ReadAll(consumer));
        }

        // Now unwind, checking at each step that the consumer holds what it held at that generation.
        for (int generation = 5; generation >= 1; generation--)
        {
            using MemoryStream reverse = new(reverseDeltas[generation - 1]);
            new HollowBlobReader(consumer).ApplyDelta(reverse);

            Assert.Equal(snapshots[generation - 1], ReadAll(consumer));
        }
    }

    /// <summary>
    /// Going back and then forward again along the same transition has to land on the same state, which
    /// it only does if neither direction loses anything.
    /// </summary>
    [Fact]
    public void AConsumerCanGoBackAndForwardAgain()
    {
        HollowObjectSchema schema = MovieSchema();

        HollowWriteStateEngine engine = new() { RandomizedTag = 1 };
        engine.AddTypeState(new HollowObjectTypeWriteState(schema));

        Movie[] first = [.. Enumerable.Range(0, 20).Select(i => new Movie(i, $"movie-{i}", i / 3m))];
        AddMovies(engine, schema, first);

        HollowReadStateEngine consumer = ReadSnapshot(engine);
        Dictionary<int, Movie> atFirst = ReadAll(consumer);

        engine.PrepareForNextCycle();
        engine.RandomizedTag = 2;
        AddMovies(engine, schema, [.. first.Where(m => m.Id % 2 == 0), new Movie(50, "extra", 50m)]);

        // Capture both directions before moving, so the same pair of blobs is used each way.
        using MemoryStream forward = new();
        new HollowBlobWriter(engine).WriteDelta(forward);

        using MemoryStream reverse = new();
        new HollowBlobWriter(engine).WriteReverseDelta(reverse);

        forward.Position = 0;
        new HollowBlobReader(consumer).ApplyDelta(forward);
        Dictionary<int, Movie> atSecond = ReadAll(consumer);

        reverse.Position = 0;
        new HollowBlobReader(consumer).ApplyDelta(reverse);
        Assert.Equal(atFirst, ReadAll(consumer));

        forward.Position = 0;
        new HollowBlobReader(consumer).ApplyDelta(forward);
        Assert.Equal(atSecond, ReadAll(consumer));
    }

    /// <summary>
    /// The listeners are driven by the blob's own added and removed ordinal streams, which a reverse
    /// delta has the other way round.
    /// </summary>
    [Fact]
    public void PopulatedOrdinalsTrackAReverseDelta()
    {
        HollowObjectSchema schema = MovieSchema();

        HollowWriteStateEngine engine = new() { RandomizedTag = 1 };
        engine.AddTypeState(new HollowObjectTypeWriteState(schema));
        AddMovies(engine, schema, [new Movie(1, "A", 1m), new Movie(2, "B", 2m)]);

        HollowReadStateEngine consumer = ReadSnapshot(engine);

        engine.PrepareForNextCycle();
        engine.RandomizedTag = 2;
        AddMovies(engine, schema, [new Movie(1, "A", 1m), new Movie(3, "C", 3m), new Movie(4, "D", 4m)]);

        ApplyDelta(engine, consumer);
        Assert.Equal(3, consumer.GetTypeState("Movie")!.PopulatedOrdinals.Cardinality());

        ApplyReverseDelta(engine, consumer);

        HollowTypeReadState state = consumer.GetTypeState("Movie")!;
        Assert.Equal(2, state.PopulatedOrdinals.Cardinality());
        Assert.Equal(3, state.PreviousOrdinals.Cardinality());
    }

    /// <summary>
    /// A cycle that changed nothing produces a reverse delta that changes nothing, rather than one that
    /// cannot be applied.
    /// </summary>
    [Fact]
    public void AnUnchangedCycleProducesAnEmptyReverseDelta()
    {
        HollowObjectSchema schema = MovieSchema();

        HollowWriteStateEngine engine = new() { RandomizedTag = 1 };
        engine.AddTypeState(new HollowObjectTypeWriteState(schema));
        AddMovies(engine, schema, [new Movie(1, "A", 1m)]);

        HollowReadStateEngine consumer = ReadSnapshot(engine);
        Dictionary<int, Movie> before = ReadAll(consumer);

        engine.PrepareForNextCycle();
        engine.RandomizedTag = 2;
        AddMovies(engine, schema, [new Movie(1, "A", 1m)]);

        ApplyDelta(engine, consumer);
        ApplyReverseDelta(engine, consumer);

        Assert.Equal(before, ReadAll(consumer));
        Assert.Equal(1, consumer.RandomizedTag);
    }
}
