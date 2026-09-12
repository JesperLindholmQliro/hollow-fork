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
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Schema;
using Hollow.Core.Tools.Checksum;
using Hollow.Core.Util;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Tests.Core;

/// <summary>
/// A type can spread its records across four ordinal maps instead of one, so that populating a cycle
/// from several threads contends on four write locks rather than one.
/// </summary>
/// <remarks>
/// The ordinals it hands out are interleaved — a record's low two bits name the map that assigned it —
/// so the thing to establish is that everything built on ordinals still holds: deduplication across the
/// whole type, ordinals reused across a delta, holes reused, and a restored producer keeping the
/// ordinals its predecessor published.
/// </remarks>
public class PartitionedOrdinalMapTests
{
    [HollowPrimaryKey("Id")]
    private sealed record Movie(int Id, string Title, int Year);

    private static HollowWriteStateEngine Engine(bool partitioned) =>
        new() { RandomizedTag = 1, PartitionedOrdinalMap = partitioned };

    private static Movie[] Catalogue(int count, string prefix = "movie") =>
        [.. Enumerable.Range(0, count).Select(i => new Movie(i, $"{prefix}-{i}", 1900 + i))];

    private static int[] Add(HollowWriteStateEngine engine, params Movie[] movies)
    {
        HollowObjectMapper mapper = new(engine);

        return [.. movies.Select(movie => mapper.Add(movie))];
    }

    /// <summary>The titles a consumer sees, keyed by ordinal.</summary>
    private static Dictionary<int, string> Read(HollowReadStateEngine engine)
    {
        HollowObjectTypeReadState movies =
            Assert.IsType<HollowObjectTypeReadState>(engine.GetTypeState("Movie"));
        HollowObjectTypeReadState strings =
            Assert.IsType<HollowObjectTypeReadState>(engine.GetTypeState("String"));

        int title = movies.Schema.GetPosition("Title");
        int value = strings.Schema.GetPosition("value");

        return movies.PopulatedOrdinals.EnumerateSetBits()
            .ToDictionary(ordinal => ordinal, ordinal => strings.ReadString(movies.ReadOrdinal(ordinal, title), value)!);
    }

    /// <summary>
    /// Four maps means four partitions, and a record's low bits say which one assigned it. Unless every
    /// partition is actually used, none of the tests below are testing anything.
    /// </summary>
    [Fact]
    public void APartitionedTypeSpreadsItsOrdinalsAcrossAllFourMaps()
    {
        HollowWriteStateEngine engine = Engine(partitioned: true);
        int[] ordinals = Add(engine, Catalogue(200));

        Assert.Equal([0, 1, 2, 3], [.. ordinals.Select(ordinal => ordinal & 3).Distinct().Order()]);

        // And an unpartitioned type numbers them consecutively from zero, which is the difference.
        Assert.Equal(
            [.. Enumerable.Range(0, 200)],
            [.. Add(Engine(partitioned: false), Catalogue(200)).Order()]);
    }

    /// <summary>
    /// Deduplication is across the whole type, not within one partition — two equal records share an
    /// ordinal however their hash routes them.
    /// </summary>
    [Fact]
    public void EqualRecordsStillShareAnOrdinal()
    {
        HollowWriteStateEngine engine = Engine(partitioned: true);
        Movie[] movies = Catalogue(200);

        int[] first = Add(engine, movies);
        int[] again = Add(engine, movies);

        Assert.Equal(first, again);
        Assert.Equal(200, first.Distinct().Count());
    }

    /// <summary>
    /// The dataset a partitioned producer publishes has to be the one an unpartitioned producer would
    /// have published, record for record. Only the ordinals differ, and the checksum is over the
    /// records rather than their numbering.
    /// </summary>
    [Fact]
    public void APartitionedProducerPublishesTheSameRecords()
    {
        Dictionary<int, string> Publish(bool partitioned)
        {
            HollowWriteStateEngine engine = Engine(partitioned);
            Add(engine, Catalogue(200));

            return Read(StateEngineRoundTripper.RoundTripSnapshot(engine));
        }

        Assert.Equal(
            Publish(partitioned: false).Values.Order(StringComparer.Ordinal),
            Publish(partitioned: true).Values.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A record carried into the next cycle keeps its ordinal, which is what makes a delta small. That
    /// is the property partitioning is most likely to break, since the ordinal now has to come back
    /// from the same map.
    /// </summary>
    [Fact]
    public void ARecordKeepsItsOrdinalAcrossACycle()
    {
        HollowWriteStateEngine engine = Engine(partitioned: true);
        Movie[] movies = Catalogue(200);

        int[] first = Add(engine, movies);
        HollowReadStateEngine consumer = StateEngineRoundTripper.RoundTripSnapshot(engine);

        int[] second = Add(engine, movies);
        StateEngineRoundTripper.RoundTripDelta(engine, consumer);

        Assert.Equal(first, second);

        // A delta that changed nothing leaves every ordinal where it was.
        Assert.Equal(200, consumer.GetTypeState("Movie")!.PopulatedOrdinals.Cardinality());
    }

    /// <summary>
    /// A hole left by a removed record is reused rather than the type growing — but only by a record
    /// that hashes to the partition the hole is in, because that is where its free-ordinal pool lives.
    /// </summary>
    /// <remarks>
    /// That is a real cost of partitioning and not an accident: four pools reclaim holes more slowly
    /// than one, since a hole waits for a record routed to it rather than for the next record at all.
    /// Adding a handful is enough for every partition to be reached.
    /// </remarks>
    [Fact]
    public void AHoleIsReusedByTheNextRecordRoutedToItsPartition()
    {
        HollowWriteStateEngine engine = Engine(partitioned: true);
        Movie[] movies = Catalogue(200);

        int dropped = Add(engine, movies)[100];
        HollowReadStateEngine consumer = StateEngineRoundTripper.RoundTripSnapshot(engine);

        Movie[] withoutOne = [.. movies.Where(movie => movie.Id != 100)];

        // Dropping it returns its ordinal to its partition's pool.
        Add(engine, withoutOne);
        StateEngineRoundTripper.RoundTripDelta(engine, consumer);

        int[] added = Add(engine, [.. withoutOne, .. Catalogue(8, "later")])[^8..];
        StateEngineRoundTripper.RoundTripDelta(engine, consumer);

        Assert.Contains(dropped, added);
    }

    /// <summary>
    /// A restored producer continues someone else's delta chain, which only works if a record it
    /// re-adds comes back with the ordinal the published state gave it. With four maps that means the
    /// record has to land in the map its published ordinal belongs to rather than the one its hash
    /// would choose.
    /// </summary>
    [Fact]
    public void ARestoredProducerKeepsThePublishedOrdinals()
    {
        HollowWriteStateEngine published = Engine(partitioned: true);
        Movie[] movies = Catalogue(200);
        int[] before = Add(published, movies);

        HollowReadStateEngine readState = StateEngineRoundTripper.RoundTripSnapshot(published);

        HollowWriteStateEngine restarted = Engine(partitioned: true);
        HollowWriteStateCreator.PopulateStateEngineWithTypeWriteStates(restarted, readState.Schemas);
        restarted.RestoreFrom(readState);

        int[] after = Add(restarted, movies);

        Assert.Equal(before, after);
    }

    /// <summary>
    /// And the chain a restored partitioned producer writes is one a consumer can actually follow.
    /// </summary>
    [Fact]
    public void ARestoredPartitionedProducerCanContinueTheChain()
    {
        HollowWriteStateEngine published = Engine(partitioned: true);
        Movie[] movies = Catalogue(200);
        Add(published, movies);

        HollowReadStateEngine consumer = StateEngineRoundTripper.RoundTripSnapshot(published);

        // The producer restarts here, knowing only what the consumer holds.
        HollowWriteStateEngine restarted = new() { PartitionedOrdinalMap = true };
        HollowWriteStateCreator.PopulateStateEngineWithTypeWriteStates(restarted, consumer.Schemas);
        restarted.RestoreFrom(consumer);

        Add(restarted, [.. movies, new Movie(500, "late arrival", 2020)]);
        StateEngineRoundTripper.RoundTripDelta(restarted, consumer);

        Assert.Equal(201, consumer.GetTypeState("Movie")!.PopulatedOrdinals.Cardinality());
        Assert.Contains("late arrival", Read(consumer).Values);
    }

    /// <summary>
    /// Sharding divides the ordinal space, and a partitioned type's ordinals are interleaved through it
    /// — so the two have to agree, or a record lands in a shard nothing looks for it in.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    public void ShardingAPartitionedTypeStillReadsBack(int numShards)
    {
        HollowWriteStateEngine engine = Engine(partitioned: true);
        engine.AddTypeState(new HollowObjectTypeWriteState(MovieSchema(), numShards, true));

        HollowObjectWriteRecord record = new(MovieSchema());
        Dictionary<int, string> expected = [];

        for (int i = 0; i < 500; i++)
        {
            record.Reset();
            record.SetInt("id", i);
            record.SetString("title", $"movie-{i}");
            expected[engine.Add("Movie", record)] = $"movie-{i}";
        }

        HollowReadStateEngine consumer = StateEngineRoundTripper.RoundTripSnapshot(engine);
        HollowObjectTypeReadState movies =
            Assert.IsType<HollowObjectTypeReadState>(consumer.GetTypeState("Movie"));

        Assert.Equal(numShards, movies.NumShards);
        Assert.Equal(expected.Count, movies.PopulatedOrdinals.Cardinality());

        foreach ((int ordinal, string title) in expected)
        {
            Assert.Equal(title, movies.ReadString(ordinal, movies.Schema.GetPosition("title")));
        }
    }

    private static HollowObjectSchema MovieSchema()
    {
        HollowObjectSchema schema = new("Movie", 2);
        schema.AddField("id", FieldType.Int);
        schema.AddField("title", FieldType.String);

        return schema;
    }
}
