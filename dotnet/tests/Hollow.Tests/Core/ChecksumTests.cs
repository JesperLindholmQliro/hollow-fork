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
using Hollow.Core.Schema;
using Hollow.Core.Tools.Checksum;
using Hollow.Core.Util;
using Hollow.Core.Write;

namespace Hollow.Tests.Core;

/// <summary>
/// A checksum over a read state, which is how a producer decides whether the blobs it just wrote agree
/// with each other.
/// </summary>
/// <remarks>
/// What matters here is what the checksum distinguishes. Data it treats as equal is data a producer
/// would publish without noticing a difference, so anything a consumer can observe has to be in it —
/// ordinals and bucket positions included, not just field values.
/// </remarks>
public class ChecksumTests
{
    private static HollowObjectSchema MovieSchema()
    {
        HollowObjectSchema schema = new("Movie", 2);
        schema.AddField("id", FieldType.Int);
        schema.AddField("title", FieldType.String);
        return schema;
    }

    private static void AddMovie(HollowWriteStateEngine engine, HollowObjectSchema schema, int id, string title)
    {
        HollowObjectWriteRecord record = new(schema);
        record.SetInt("id", id);
        record.SetString("title", title);
        engine.Add("Movie", record);
    }

    private static HollowReadStateEngine Read(params (int Id, string Title)[] movies)
    {
        HollowObjectSchema schema = MovieSchema();
        HollowWriteStateEngine engine = HollowWriteStateCreator.CreateWithSchemas([schema]);

        foreach ((int id, string title) in movies)
        {
            AddMovie(engine, schema, id, title);
        }

        return StateEngineRoundTripper.RoundTripSnapshot(engine);
    }

    [Fact]
    public void TheSameDataChecksumsTheSame()
    {
        HollowChecksum first = HollowChecksum.ForStateEngine(Read((1, "one"), (2, "two")));
        HollowChecksum second = HollowChecksum.ForStateEngine(Read((1, "one"), (2, "two")));

        Assert.Equal(first, second);
        Assert.Equal(first.ToString(), second.ToString());
    }

    [Fact]
    public void DifferentDataChecksumsDifferently()
    {
        Assert.NotEqual(
            HollowChecksum.ForStateEngine(Read((1, "one"), (2, "two"))),
            HollowChecksum.ForStateEngine(Read((1, "one"), (2, "three"))));

        Assert.NotEqual(
            HollowChecksum.ForStateEngine(Read((1, "one"))),
            HollowChecksum.ForStateEngine(Read((1, "one"), (2, "two"))));
    }

    /// <summary>
    /// The same records at different ordinals are not the same state: a consumer's ordinals have to
    /// match the producer's, or an index built over them points at the wrong records.
    /// </summary>
    [Fact]
    public void TheSameRecordsAtDifferentOrdinalsChecksumDifferently()
    {
        Assert.NotEqual(
            HollowChecksum.ForStateEngine(Read((1, "one"), (2, "two"))),
            HollowChecksum.ForStateEngine(Read((2, "two"), (1, "one"))));
    }

    /// <summary>
    /// The invariant the producer's integrity check rests on: applying a cycle's delta and reading that
    /// same cycle's snapshot land on states that check out identically.
    /// </summary>
    /// <remarks>
    /// Both blobs come from one write state, so the ordinals match by construction — which is the point.
    /// A snapshot written by a <em>different</em> producer run holding the same records would not match,
    /// because its ordinals would be packed rather than carrying the chain's holes.
    /// </remarks>
    [Fact]
    public void ACyclesDeltaAndSnapshotAgree()
    {
        HollowObjectSchema schema = MovieSchema();
        HollowWriteStateEngine producer = HollowWriteStateCreator.CreateWithSchemas([schema]);

        AddMovie(producer, schema, 1, "one");
        AddMovie(producer, schema, 2, "two");

        HollowReadStateEngine followsDeltas = StateEngineRoundTripper.RoundTripSnapshot(producer);

        AddMovie(producer, schema, 1, "one");
        AddMovie(producer, schema, 3, "three");

        // Both blobs for the same cycle, written before the write state rolls forward.
        using MemoryStream snapshotBytes = new();
        using MemoryStream deltaBytes = new();

        HollowBlobWriter writer = new(producer);
        writer.WriteSnapshot(snapshotBytes);
        writer.WriteDelta(deltaBytes);

        snapshotBytes.Position = 0;
        HollowReadStateEngine fromSnapshot = new();
        new Hollow.Core.Read.Engine.HollowBlobReader(fromSnapshot).ReadSnapshot(snapshotBytes);

        deltaBytes.Position = 0;
        new Hollow.Core.Read.Engine.HollowBlobReader(followsDeltas).ApplyDelta(deltaBytes);

        Assert.Equal(
            HollowChecksum.ForStateEngine(fromSnapshot), HollowChecksum.ForStateEngine(followsDeltas));
    }

    /// <summary>
    /// Records that reached their ordinals by different routes are a different state, even when every
    /// field matches. This is why a consumer loses its indexes on a double snapshot.
    /// </summary>
    [Fact]
    public void ADeltaChainAndAFreshSnapshotOfTheSameRecordsDiffer()
    {
        HollowObjectSchema schema = MovieSchema();
        HollowWriteStateEngine producer = HollowWriteStateCreator.CreateWithSchemas([schema]);

        AddMovie(producer, schema, 1, "one");
        AddMovie(producer, schema, 2, "two");

        HollowReadStateEngine followsDeltas = StateEngineRoundTripper.RoundTripSnapshot(producer);

        AddMovie(producer, schema, 1, "one");
        AddMovie(producer, schema, 3, "three");

        StateEngineRoundTripper.RoundTripDelta(producer, followsDeltas);

        // The chain left a hole where "two" was and put "three" past it; a fresh snapshot packs them.
        Assert.NotEqual(
            HollowChecksum.ForStateEngine(Read((1, "one"), (3, "three"))),
            HollowChecksum.ForStateEngine(followsDeltas));
    }

    /// <summary>
    /// Two states either side of a schema change still have to be comparable over what they share, or
    /// a producer could never check a cycle that added a field.
    /// </summary>
    [Fact]
    public void CommonSchemasAreComparedAcrossASchemaChange()
    {
        HollowObjectSchema narrow = MovieSchema();

        HollowObjectSchema wide = new("Movie", 3);
        wide.AddField("id", FieldType.Int);
        wide.AddField("title", FieldType.String);
        wide.AddField("year", FieldType.Int);

        HollowWriteStateEngine narrowEngine = HollowWriteStateCreator.CreateWithSchemas([narrow]);
        AddMovie(narrowEngine, narrow, 1, "one");
        HollowReadStateEngine narrowState = StateEngineRoundTripper.RoundTripSnapshot(narrowEngine);

        HollowWriteStateEngine wideEngine = HollowWriteStateCreator.CreateWithSchemas([wide]);
        HollowObjectWriteRecord record = new(wide);
        record.SetInt("id", 1);
        record.SetString("title", "one");
        record.SetInt("year", 1999);
        wideEngine.Add("Movie", record);
        HollowReadStateEngine wideState = StateEngineRoundTripper.RoundTripSnapshot(wideEngine);

        // Over the fields they share, the data is the same.
        Assert.Equal(
            HollowChecksum.ForStateEngineWithCommonSchemas(narrowState, wideState),
            HollowChecksum.ForStateEngineWithCommonSchemas(wideState, narrowState));

        // Over everything, it is not.
        Assert.NotEqual(
            HollowChecksum.ForStateEngine(narrowState), HollowChecksum.ForStateEngine(wideState));
    }

    /// <summary>
    /// A set's bucket layout is part of what a consumer reads back, so two sets holding the same
    /// elements in different buckets are not the same state.
    /// </summary>
    [Fact]
    public void ASetsBucketLayoutIsPartOfTheChecksum()
    {
        static HollowReadStateEngine ReadSet(params int[] hashCodes)
        {
            HollowSetSchema schema = new("Tags", "String");
            HollowWriteStateEngine engine = HollowWriteStateCreator.CreateWithSchemas([schema]);

            HollowSetWriteRecord record = new();
            for (int i = 0; i < hashCodes.Length; i++)
            {
                record.AddElement(i, hashCodes[i]);
            }

            engine.Add("Tags", record);

            return StateEngineRoundTripper.RoundTripSnapshot(engine);
        }

        Assert.Equal(
            HollowChecksum.ForStateEngine(ReadSet(10, 20, 30)),
            HollowChecksum.ForStateEngine(ReadSet(10, 20, 30)));

        Assert.NotEqual(
            HollowChecksum.ForStateEngine(ReadSet(10, 20, 30)),
            HollowChecksum.ForStateEngine(ReadSet(11, 21, 31)));
    }

    /// <summary>
    /// The <c>Decimal</c> extension is 128 bits wide, so both halves have to reach the checksum; see
    /// PORTING.md.
    /// </summary>
    [Fact]
    public void DecimalFieldsAreCoveredInFull()
    {
        static HollowReadStateEngine ReadPriced(decimal? amount)
        {
            HollowObjectSchema schema = new("Priced", 1);
            schema.AddField("amount", FieldType.Decimal);

            HollowWriteStateEngine engine = HollowWriteStateCreator.CreateWithSchemas([schema]);

            HollowObjectWriteRecord record = new(schema);
            if (amount is { } value)
            {
                record.SetDecimal("amount", value);
            }

            engine.Add("Priced", record);

            return StateEngineRoundTripper.RoundTripSnapshot(engine);
        }

        Assert.Equal(
            HollowChecksum.ForStateEngine(ReadPriced(1.25m)), HollowChecksum.ForStateEngine(ReadPriced(1.25m)));

        Assert.NotEqual(
            HollowChecksum.ForStateEngine(ReadPriced(1.25m)), HollowChecksum.ForStateEngine(ReadPriced(1.26m)));

        // Differs only in the high 64 bits, which a checksum covering one half would miss.
        Assert.NotEqual(
            HollowChecksum.ForStateEngine(ReadPriced(1m)),
            HollowChecksum.ForStateEngine(ReadPriced(79228162514264337593543950335m)));

        Assert.NotEqual(
            HollowChecksum.ForStateEngine(ReadPriced(null)), HollowChecksum.ForStateEngine(ReadPriced(0m)));
    }

    [Fact]
    public void TheChecksumNamesTheTypesItCovers()
    {
        HollowChecksum checksum = HollowChecksum.ForStateEngine(Read((1, "one")));

        Assert.Equal(["Movie"], checksum.SortedTypeChecksums.Select(type => type.TypeName));
    }

    /// <summary>
    /// A collection schema has nothing to intersect, so unlike an object type it cannot be checksummed
    /// against a different one.
    /// </summary>
    [Fact]
    public void ACollectionTypeRefusesAMismatchedSchema()
    {
        HollowSetSchema schema = new("Tags", "String");
        HollowWriteStateEngine engine = HollowWriteStateCreator.CreateWithSchemas([schema]);

        HollowSetWriteRecord record = new();
        record.AddElement(0);
        engine.Add("Tags", record);

        HollowReadStateEngine readEngine = StateEngineRoundTripper.RoundTripSnapshot(engine);
        HollowTypeReadState tags = readEngine.GetTypeState("Tags")!;

        Assert.Throws<ArgumentException>(() => tags.GetChecksum(new HollowSetSchema("Tags", "Integer")));
    }
}
