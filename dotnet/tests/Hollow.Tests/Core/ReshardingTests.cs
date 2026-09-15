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
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.List;
using Hollow.Core.Read.Engine.Map;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Read.Engine.Set;
using Hollow.Core.Read.Iterator;
using Hollow.Core.Schema;
using Hollow.Core.Tools.Checksum;
using Hollow.Core.Write;

namespace Hollow.Tests.Core;

/// <summary>
/// Rearranging a consumer's records to a different shard count.
/// </summary>
/// <remarks>
/// <para>
/// A record's shard is the low bits of its ordinal and its position within that shard is the remaining
/// high bits, so changing the shard count moves every record. The one thing that must not change is
/// what a consumer reads: the same global ordinal has to keep returning the same record.
/// </para>
/// <para>
/// The tests here state that as an equivalence. Rearranging a state read at one shard count must land
/// on exactly what the producer would have written at the other — same ordinals, same values, same hash
/// bucket positions — because a consumer that reshards then applies a delta is about to be compared,
/// record for record, against a producer that never had the old count at all.
/// </para>
/// </remarks>
public class ReshardingTests
{
    private const string ValueType = "Value";
    private const string ListType = "Values";
    private const string SetType = "ValueSet";
    private const string MapType = "ValueMap";

    /// <summary>
    /// Reads a state holding the same dataset in <paramref name="numShards"/> shards per type.
    /// </summary>
    /// <remarks>
    /// The dataset is the same whatever the shard count, because the write engine assigns ordinals
    /// before it decides how to lay them out. That is what makes two shard counts comparable at all.
    /// </remarks>
    private static HollowReadStateEngine ReadAt(int numShards)
    {
        HollowObjectSchema valueSchema = new(ValueType, 2);
        valueSchema.AddField("id", FieldType.Int);
        valueSchema.AddField("name", FieldType.String);

        HollowListSchema listSchema = new(ListType, ValueType);
        HollowSetSchema setSchema = new(SetType, ValueType);
        HollowMapSchema mapSchema = new(MapType, ValueType, ValueType);

        HollowWriteStateEngine engine = new();
        engine.AddTypeState(new HollowObjectTypeWriteState(valueSchema, numShards));
        engine.AddTypeState(new HollowListTypeWriteState(listSchema, numShards));
        engine.AddTypeState(new HollowSetTypeWriteState(setSchema, numShards));
        engine.AddTypeState(new HollowMapTypeWriteState(mapSchema, numShards));

        // Values 0..39. Every sixth one has a null name, so the joiner has to translate the
        // width-dependent null sentinel rather than copy it.
        HollowObjectWriteRecord valueRecord = new(valueSchema);
        int[] valueOrdinals = new int[40];
        for (int i = 0; i < valueOrdinals.Length; i++)
        {
            valueRecord.Reset();
            valueRecord.SetInt("id", i);
            if (i % 6 != 0)
            {
                valueRecord.SetString("name", $"value-{i}-{new string('x', i % 7)}");
            }

            valueOrdinals[i] = engine.Add(ValueType, valueRecord);
        }

        // Collections of varying size, starting with an empty one: an empty record occupies no elements
        // and no buckets, so it is the case where a pointer has to repeat its predecessor.
        for (int i = 0; i < 20; i++)
        {
            HollowListWriteRecord listRecord = new();
            HollowSetWriteRecord setRecord = new();
            HollowMapWriteRecord mapRecord = new();

            for (int j = 0; j < i; j++)
            {
                listRecord.AddElement(valueOrdinals[(i + j) % valueOrdinals.Length]);
                setRecord.AddElement(valueOrdinals[(i + j) % valueOrdinals.Length]);
                mapRecord.AddEntry(
                    valueOrdinals[(i + j) % valueOrdinals.Length],
                    valueOrdinals[(i + (2 * j)) % valueOrdinals.Length]);
            }

            engine.Add(ListType, listRecord);
            engine.Add(SetType, setRecord);
            engine.Add(MapType, mapRecord);
        }

        return StateEngineRoundTripper.RoundTripSnapshot(engine);
    }

    private static void ReshardEveryType(HollowReadStateEngine readEngine, int from, int to)
    {
        foreach (HollowTypeReadState typeState in readEngine.TypeStates.Values)
        {
            Assert.Equal(from, typeState.NumShards);

            HollowTypeReshardingStrategy.ForType(typeState).Reshard(typeState, from, to);

            Assert.Equal(to, typeState.NumShards);
        }
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(1, 8)]
    [InlineData(2, 4)]
    [InlineData(2, 16)]
    [InlineData(4, 8)]
    public void SplittingShardsLandsOnWhatTheProducerWouldHaveWritten(int from, int to)
    {
        HollowReadStateEngine resharded = ReadAt(from);
        ReshardEveryType(resharded, from, to);

        Assert.Equal(
            HollowChecksum.ForStateEngine(ReadAt(to)), HollowChecksum.ForStateEngine(resharded));
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(8, 1)]
    [InlineData(4, 2)]
    [InlineData(16, 2)]
    [InlineData(8, 4)]
    public void JoiningShardsLandsOnWhatTheProducerWouldHaveWritten(int from, int to)
    {
        HollowReadStateEngine resharded = ReadAt(from);
        ReshardEveryType(resharded, from, to);

        Assert.Equal(
            HollowChecksum.ForStateEngine(ReadAt(to)), HollowChecksum.ForStateEngine(resharded));
    }

    /// <summary>
    /// A record whose bits mean the same thing in the shard it is going to is moved rather than taken
    /// apart and rebuilt — which is most of them, since resharding only resizes a field when the number
    /// of bytes a shard holds changes what a var-length offset needs.
    /// </summary>
    /// <remarks>
    /// The two paths produce identical records, so the checksum tests above pass whichever ran. This is
    /// the one that says which did.
    /// </remarks>
    [Fact]
    public void AReshardMovesRecordsItDoesNotHaveToRebuild()
    {
        HollowReadStateEngine resharded = ReadAt(8);

        RecordCopyDiagnostics.Reset();
        ReshardEveryType(resharded, 8, 1);

        Assert.Equal(
            HollowChecksum.ForStateEngine(ReadAt(1)), HollowChecksum.ForStateEngine(resharded));

        Assert.True(
            RecordCopyDiagnostics.BulkResharded > 0,
            $"every record was rebuilt ({RecordCopyDiagnostics.ReencodedByReshard} of them), so the "
            + "path that moves them was never taken and this test proved nothing");
    }

    [Fact]
    public void ReshardingThereAndBackLeavesTheStateAsItWas()
    {
        HollowReadStateEngine resharded = ReadAt(4);
        HollowChecksum before = HollowChecksum.ForStateEngine(resharded);

        ReshardEveryType(resharded, 4, 16);
        ReshardEveryType(resharded, 16, 2);
        ReshardEveryType(resharded, 2, 4);

        Assert.Equal(before, HollowChecksum.ForStateEngine(resharded));
    }

    [Fact]
    public void EveryRecordStillReadsBackAfterAReshard()
    {
        HollowReadStateEngine before = ReadAt(8);
        HollowReadStateEngine after = ReadAt(8);
        ReshardEveryType(after, 8, 1);

        HollowObjectTypeReadState beforeValues = Values(before);
        HollowObjectTypeReadState afterValues = Values(after);

        for (int ordinal = 0; ordinal <= beforeValues.MaxOrdinal; ordinal++)
        {
            Assert.Equal(beforeValues.ReadInt(ordinal, 0), afterValues.ReadInt(ordinal, 0));
            Assert.Equal(beforeValues.ReadString(ordinal, 1), afterValues.ReadString(ordinal, 1));
        }

        HollowListTypeReadState beforeLists = Lists(before);
        HollowListTypeReadState afterLists = Lists(after);

        for (int ordinal = 0; ordinal <= beforeLists.MaxOrdinal; ordinal++)
        {
            int size = beforeLists.Size(ordinal);
            Assert.Equal(size, afterLists.Size(ordinal));

            for (int i = 0; i < size; i++)
            {
                Assert.Equal(beforeLists.GetElementOrdinal(ordinal, i), afterLists.GetElementOrdinal(ordinal, i));
            }
        }

        HollowSetTypeReadState beforeSets = Sets(before);
        HollowSetTypeReadState afterSets = Sets(after);

        for (int ordinal = 0; ordinal <= beforeSets.MaxOrdinal; ordinal++)
        {
            Assert.Equal(beforeSets.Size(ordinal), afterSets.Size(ordinal));

            // The iterator walks the hash table in bucket order, so comparing the sequences states that
            // the layout survived and not merely the membership. A consumer probes the table by bucket,
            // and an element that moved bucket is one it may fail to find.
            Assert.Equal(
                beforeSets.ElementOrdinals(ordinal).AsEnumerable(),
                afterSets.ElementOrdinals(ordinal).AsEnumerable());
        }

        HollowMapTypeReadState beforeMaps = Maps(before);
        HollowMapTypeReadState afterMaps = Maps(after);

        for (int ordinal = 0; ordinal <= beforeMaps.MaxOrdinal; ordinal++)
        {
            Assert.Equal(beforeMaps.Size(ordinal), afterMaps.Size(ordinal));

            Assert.Equal(Entries(beforeMaps, ordinal), Entries(afterMaps, ordinal));
        }
    }

    [Fact]
    public void ASetStillFindsEveryElementItHeldAfterAReshard()
    {
        HollowReadStateEngine before = ReadAt(2);
        HollowReadStateEngine after = ReadAt(2);
        ReshardEveryType(after, 2, 16);

        HollowSetTypeReadState beforeSets = Sets(before);
        HollowSetTypeReadState afterSets = Sets(after);
        HollowMapTypeReadState beforeMaps = Maps(before);
        HollowMapTypeReadState afterMaps = Maps(after);

        for (int ordinal = 0; ordinal <= beforeSets.MaxOrdinal; ordinal++)
        {
            for (int element = 0; element <= Values(before).MaxOrdinal; element++)
            {
                Assert.Equal(beforeSets.Contains(ordinal, element), afterSets.Contains(ordinal, element));
                Assert.Equal(beforeMaps.Get(ordinal, element), afterMaps.Get(ordinal, element));
            }
        }
    }

    /// <summary>
    /// A one-type engine whose shard count follows the data size, with resharding turned on.
    /// </summary>
    private static (HollowWriteStateEngine Engine, HollowObjectSchema Schema) ReshardingEngine()
    {
        HollowObjectSchema schema = new("Movie", 2);
        schema.AddField("id", FieldType.Int);
        schema.AddField("title", FieldType.String);

        HollowWriteStateEngine engine = new() { AllowTypeResharding = true };
        engine.AddTypeState(new HollowObjectTypeWriteState(schema));

        return (engine, schema);
    }

    private static void AddMovies(HollowWriteStateEngine engine, HollowObjectSchema schema, int count)
    {
        HollowObjectWriteRecord record = new(schema);

        for (int i = 0; i < count; i++)
        {
            record.Reset();
            record.SetInt("id", i);
            record.SetString("title", $"movie-{i}-{new string('x', 60)}");

            engine.Add("Movie", record);
        }
    }

    private static Dictionary<int, string?> ReadMovies(HollowReadStateEngine engine)
    {
        HollowObjectTypeReadState readState =
            (HollowObjectTypeReadState)engine.GetTypeState("Movie")!;

        return readState.PopulatedOrdinals
            .EnumerateSetBits()
            .ToDictionary(ordinal => readState.ReadInt(ordinal, 0), ordinal => readState.ReadString(ordinal, 1));
    }

    [Fact]
    public void AConsumerFollowsAProducerThatSplitsAType()
    {
        (HollowWriteStateEngine engine, HollowObjectSchema schema) = ReshardingEngine();

        // Big enough that 60 records fit in one shard.
        engine.TargetMaxTypeShardSize = 1 << 20;
        AddMovies(engine, schema, 60);
        HollowReadStateEngine consumer = StateEngineRoundTripper.RoundTripSnapshot(engine);

        Assert.Equal(1, consumer.GetTypeState("Movie")!.NumShards);

        // Small enough that the same records now call for more shards than one.
        engine.TargetMaxTypeShardSize = 1024;
        AddMovies(engine, schema, 70);
        StateEngineRoundTripper.RoundTripDelta(engine, consumer);

        // A count moves by at most a factor of two per cycle, however far it has to go.
        Assert.Equal(2, consumer.GetTypeState("Movie")!.NumShards);
        Assert.Equal(
            "Movie:(1,2)", engine.GetHeaderTag(HollowHeaderTags.TypeReshardingInvoked));

        Assert.Equal(70, ReadMovies(consumer).Count);
        Assert.Equal($"movie-69-{new string('x', 60)}", ReadMovies(consumer)[69]);

        // And it keeps going on the cycles after that, until it gets where it is going.
        AddMovies(engine, schema, 70);
        StateEngineRoundTripper.RoundTripDelta(engine, consumer);

        Assert.Equal(4, consumer.GetTypeState("Movie")!.NumShards);
        Assert.Equal(70, ReadMovies(consumer).Count);
    }

    [Fact]
    public void AConsumerFollowsAProducerThatJoinsAType()
    {
        (HollowWriteStateEngine engine, HollowObjectSchema schema) = ReshardingEngine();

        engine.TargetMaxTypeShardSize = 1024;
        AddMovies(engine, schema, 200);
        HollowReadStateEngine consumer = StateEngineRoundTripper.RoundTripSnapshot(engine);

        int splitShards = consumer.GetTypeState("Movie")!.NumShards;
        Assert.True(splitShards > 1, $"expected the first snapshot to be sharded, got {splitShards}");

        engine.TargetMaxTypeShardSize = 1 << 20;
        AddMovies(engine, schema, 200);
        StateEngineRoundTripper.RoundTripDelta(engine, consumer);

        Assert.Equal(splitShards / 2, consumer.GetTypeState("Movie")!.NumShards);
        Assert.Equal(200, ReadMovies(consumer).Count);
    }

    [Fact]
    public void AReverseDeltaTakesAConsumerBackAcrossAReshard()
    {
        (HollowWriteStateEngine engine, HollowObjectSchema schema) = ReshardingEngine();

        engine.TargetMaxTypeShardSize = 1 << 20;
        AddMovies(engine, schema, 60);
        HollowReadStateEngine consumer = StateEngineRoundTripper.RoundTripSnapshot(engine);

        Dictionary<int, string?> before = ReadMovies(consumer);
        HollowChecksum checksumBefore = HollowChecksum.ForStateEngine(consumer);

        engine.TargetMaxTypeShardSize = 1024;
        AddMovies(engine, schema, 70);

        // The forward and reverse deltas of one cycle have to be written before the engine rolls on,
        // because the reverse one carries the records this cycle dropped.
        using MemoryStream forward = new();
        using MemoryStream reverse = new();
        HollowBlobWriter writer = new(engine);
        writer.WriteDelta(forward);
        writer.WriteReverseDelta(reverse);
        engine.PrepareForNextCycle();

        forward.Position = 0;
        new HollowBlobReader(consumer).ApplyDelta(forward);

        Assert.Equal(2, consumer.GetTypeState("Movie")!.NumShards);
        Assert.Equal(70, ReadMovies(consumer).Count);

        reverse.Position = 0;
        new HollowBlobReader(consumer).ApplyDelta(reverse);

        // Back at the old shard count, holding exactly what it held before it moved forward.
        Assert.Equal(1, consumer.GetTypeState("Movie")!.NumShards);
        Assert.Equal(before, ReadMovies(consumer));
        Assert.Equal(checksumBefore, HollowChecksum.ForStateEngine(consumer));
    }

    [Fact]
    public void AShardCountHoldsStillWhenReshardingIsNotAllowed()
    {
        (HollowWriteStateEngine engine, HollowObjectSchema schema) = ReshardingEngine();
        engine.AllowTypeResharding = false;

        engine.TargetMaxTypeShardSize = 1 << 20;
        AddMovies(engine, schema, 60);
        HollowReadStateEngine consumer = StateEngineRoundTripper.RoundTripSnapshot(engine);

        engine.TargetMaxTypeShardSize = 1024;
        AddMovies(engine, schema, 70);
        StateEngineRoundTripper.RoundTripDelta(engine, consumer);

        Assert.Equal(1, consumer.GetTypeState("Movie")!.NumShards);
        Assert.Null(engine.GetHeaderTag(HollowHeaderTags.TypeReshardingInvoked));
        Assert.Equal(70, ReadMovies(consumer).Count);
    }

    [Fact]
    public void AShardCountCanOnlyChangeByAWholeMultiple()
    {
        Assert.Equal(4, HollowTypeReshardingStrategy.ShardingFactor(2, 8));
        Assert.Equal(4, HollowTypeReshardingStrategy.ShardingFactor(8, 2));

        Assert.Throws<InvalidOperationException>(() => HollowTypeReshardingStrategy.ShardingFactor(4, 4));
        Assert.Throws<InvalidOperationException>(() => HollowTypeReshardingStrategy.ShardingFactor(0, 4));
        Assert.Throws<InvalidOperationException>(() => HollowTypeReshardingStrategy.ShardingFactor(3, 4));
    }

    /// <summary>One map record's entries, in the bucket order the iterator walks them.</summary>
    private static List<(int Key, int Value)> Entries(HollowMapTypeReadState readState, int ordinal)
    {
        List<(int Key, int Value)> entries = [];

        foreach (HollowMapEntry entry in readState.Entries(ordinal))
        {
            entries.Add((entry.KeyOrdinal, entry.ValueOrdinal));
        }

        return entries;
    }

    private static HollowObjectTypeReadState Values(HollowReadStateEngine engine) =>
        (HollowObjectTypeReadState)engine.GetTypeState(ValueType)!;

    private static HollowListTypeReadState Lists(HollowReadStateEngine engine) =>
        (HollowListTypeReadState)engine.GetTypeState(ListType)!;

    private static HollowSetTypeReadState Sets(HollowReadStateEngine engine) =>
        (HollowSetTypeReadState)engine.GetTypeState(SetType)!;

    private static HollowMapTypeReadState Maps(HollowReadStateEngine engine) =>
        (HollowMapTypeReadState)engine.GetTypeState(MapType)!;
}
