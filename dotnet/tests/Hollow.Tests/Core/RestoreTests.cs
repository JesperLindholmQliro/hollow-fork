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

using Hollow.Core.Read;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.List;
using Hollow.Core.Read.Engine.Map;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Read.Engine.Set;
using Hollow.Core.Schema;
using Hollow.Core.Util;
using Hollow.Core.Write;

namespace Hollow.Tests.Core;

/// <summary>
/// A producer that restarts restores its write state from the last published read state and carries on
/// producing deltas, rather than starting a new delta chain with a fresh snapshot.
/// </summary>
/// <remarks>
/// <para>
/// What has to hold throughout is that a record which did not change keeps the ordinal the published
/// state gave it. If it did not, the first delta after a restart would claim that every record in the
/// dataset had been replaced — correct data, but a delta the size of a snapshot, and every consumer
/// index rebuilt for nothing.
/// </para>
/// <para>
/// The scenarios and their expected ordinals come from the Java <c>core.write.restore</c> tests.
/// </para>
/// </remarks>
public class RestoreTests
{
    private static HollowObjectSchema ObjectSchema()
    {
        HollowObjectSchema schema = new("TestObject", 2);
        schema.AddField("f1", FieldType.Int);
        schema.AddField("f2", FieldType.String);
        return schema;
    }

    private static void AddObject(HollowWriteStateEngine engine, HollowObjectSchema schema, int f1, string f2)
    {
        HollowObjectWriteRecord record = new(schema);
        record.SetInt("f1", f1);
        record.SetString("f2", f2);
        engine.Add("TestObject", record);
    }

    private static void AssertObject(HollowReadStateEngine readEngine, int ordinal, int f1, string f2)
    {
        HollowObjectTypeReadState state = Assert.IsType<HollowObjectTypeReadState>(readEngine.GetTypeState("TestObject"));

        Assert.Equal(f1, state.ReadInt(ordinal, state.Schema.GetPosition("f1")));
        Assert.Equal(f2, state.ReadString(ordinal, state.Schema.GetPosition("f2")));
    }

    /// <summary>
    /// Restores into a new engine the way a restarting producer does: build the data model first, then
    /// restore, then start adding this cycle's records.
    /// </summary>
    private static HollowWriteStateEngine Restore(
        HollowReadStateEngine readEngine, params HollowSchema[] schemas)
    {
        HollowWriteStateEngine engine = HollowWriteStateCreator.CreateWithSchemas(schemas);
        engine.RestoreFrom(readEngine);

        return engine;
    }

    [Fact]
    public void ObjectRecordsKeepTheirOrdinalsAcrossARestore()
    {
        HollowObjectSchema schema = ObjectSchema();
        HollowWriteStateEngine producer = HollowWriteStateCreator.CreateWithSchemas([schema]);

        AddObject(producer, schema, 1, "one");
        AddObject(producer, schema, 2, "two");
        AddObject(producer, schema, 3, "three");

        HollowReadStateEngine consumer = StateEngineRoundTripper.RoundTripSnapshot(producer);

        AddObject(producer, schema, 1, "one");
        AddObject(producer, schema, 3, "three");
        AddObject(producer, schema, 1000, "one thousand");

        StateEngineRoundTripper.RoundTripDelta(producer, consumer);

        // The producer restarts here. Everything after this comes out of a brand-new write engine that
        // has only ever seen the consumer's state.
        producer = Restore(consumer, schema);

        AddObject(producer, schema, 1, "one");
        AddObject(producer, schema, 4, "four");
        AddObject(producer, schema, 1000, "one thousand");
        AddObject(producer, schema, 1000000, "one million");

        StateEngineRoundTripper.RoundTripDelta(producer, consumer);

        Assert.Equal(4, consumer.GetTypeState("TestObject")!.MaxOrdinal);
        AssertObject(consumer, 0, 1, "one");
        AssertObject(consumer, 1, 4, "four");
        AssertObject(consumer, 2, 3, "three");   // a ghost: dropped this cycle, still addressable
        AssertObject(consumer, 3, 1000, "one thousand");
        AssertObject(consumer, 4, 1000000, "one million");

        AddObject(producer, schema, 1, "one");
        AddObject(producer, schema, 4, "four");
        AddObject(producer, schema, 1000, "one thousand");
        AddObject(producer, schema, 1000000, "one million");
        AddObject(producer, schema, 20, "twenty");

        StateEngineRoundTripper.RoundTripDelta(producer, consumer);

        // The ghost's ordinal is reclaimed by the new record rather than the type growing.
        Assert.Equal(4, consumer.GetTypeState("TestObject")!.MaxOrdinal);
        AssertObject(consumer, 1, 4, "four");
        AssertObject(consumer, 2, 20, "twenty");
        AssertObject(consumer, 3, 1000, "one thousand");
    }

    /// <summary>
    /// The point of the whole exercise: after a restart the next delta is small.
    /// </summary>
    [Fact]
    public void TheFirstDeltaAfterARestoreCarriesOnlyWhatChanged()
    {
        HollowObjectSchema schema = ObjectSchema();
        HollowWriteStateEngine producer = HollowWriteStateCreator.CreateWithSchemas([schema]);

        for (int i = 0; i < 200; i++)
        {
            AddObject(producer, schema, i, $"record {i}");
        }

        HollowReadStateEngine consumer = StateEngineRoundTripper.RoundTripSnapshot(producer);

        producer = Restore(consumer, schema);

        for (int i = 0; i < 200; i++)
        {
            AddObject(producer, schema, i, $"record {i}");
        }

        AddObject(producer, schema, 200, "record 200");

        using MemoryStream delta = new();
        new HollowBlobWriter(producer).WriteDelta(delta);

        using MemoryStream snapshot = new();
        new HollowBlobWriter(producer).WriteSnapshot(snapshot);

        // One record changed out of 201, so the delta has to be a small fraction of a snapshot. Were
        // the restore not reusing ordinals it would be the larger of the two.
        Assert.True(
            delta.Length * 4 < snapshot.Length,
            $"delta was {delta.Length} bytes against a snapshot of {snapshot.Length}");

        delta.Position = 0;
        new HollowBlobReader(consumer).ApplyDelta(delta);

        Assert.Equal(200, consumer.GetTypeState("TestObject")!.MaxOrdinal);
        AssertObject(consumer, 0, 0, "record 0");
        AssertObject(consumer, 200, 200, "record 200");
    }

    [Fact]
    public void RestoreRejectsADifferentShardCount()
    {
        HollowObjectSchema schema = ObjectSchema();
        HollowWriteStateEngine producer = HollowWriteStateCreator.CreateWithSchemas([schema]);

        AddObject(producer, schema, 1, "one");

        HollowReadStateEngine consumer = StateEngineRoundTripper.RoundTripSnapshot(producer);

        HollowWriteStateEngine misconfigured = new();
        misconfigured.AddTypeState(new HollowObjectTypeWriteState(schema, numShards: 4));

        InvalidOperationException e =
            Assert.Throws<InvalidOperationException>(() => misconfigured.RestoreFrom(consumer));

        Assert.Contains("TestObject", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoringIntoAPopulatedStateThrows()
    {
        HollowObjectSchema schema = ObjectSchema();
        HollowWriteStateEngine producer = HollowWriteStateCreator.CreateWithSchemas([schema]);

        AddObject(producer, schema, 1, "one");

        HollowReadStateEngine consumer = StateEngineRoundTripper.RoundTripSnapshot(producer);

        HollowWriteStateEngine alreadyUsed = HollowWriteStateCreator.CreateWithSchemas([schema]);
        AddObject(alreadyUsed, schema, 7, "seven");

        Assert.Throws<InvalidOperationException>(() => alreadyUsed.RestoreFrom(consumer));
    }

    /// <summary>
    /// A type registered after the restore holds none of the published ordinals, so a delta written
    /// from it would not apply to the published state. That has to be an error rather than a silently
    /// broken delta chain.
    /// </summary>
    [Fact]
    public void ATypeRegisteredAfterTheRestoreIsRejected()
    {
        HollowObjectSchema schema = ObjectSchema();
        HollowWriteStateEngine producer = HollowWriteStateCreator.CreateWithSchemas([schema]);

        AddObject(producer, schema, 1, "one");

        HollowReadStateEngine consumer = StateEngineRoundTripper.RoundTripSnapshot(producer);

        HollowWriteStateEngine restored = new();
        restored.RestoreFrom(consumer);
        restored.AddTypeState(new HollowObjectTypeWriteState(schema));

        AddObject(restored, schema, 1, "one");

        using MemoryStream delta = new();
        InvalidOperationException e = Assert.Throws<InvalidOperationException>(
            () => new HollowBlobWriter(restored).WriteDelta(delta));

        Assert.Contains("TestObject", e.Message, StringComparison.Ordinal);
    }

    private static HollowListSchema ListSchema() => new("TestList", "TestObject");

    private static void AddList(HollowWriteStateEngine engine, params int[] ordinals)
    {
        HollowListWriteRecord record = new();
        foreach (int ordinal in ordinals)
        {
            record.AddElement(ordinal);
        }

        engine.Add("TestList", record);
    }

    private static void AssertList(HollowReadStateEngine readEngine, int ordinal, params int[] elements)
    {
        HollowListTypeReadState state = Assert.IsType<HollowListTypeReadState>(readEngine.GetTypeState("TestList"));

        Assert.Equal(elements.Length, state.Size(ordinal));
        Assert.Equal(
            elements.AsEnumerable(),
            Enumerable.Range(0, elements.Length).Select(i => state.GetElementOrdinal(ordinal, i)));
    }

    [Fact]
    public void ListRecordsKeepTheirOrdinalsAcrossARestore()
    {
        HollowListSchema schema = ListSchema();
        HollowWriteStateEngine producer = HollowWriteStateCreator.CreateWithSchemas([schema]);

        AddList(producer, 1, 2, 3);
        AddList(producer, 2, 3, 4);
        AddList(producer, 3, 4, 5);

        HollowReadStateEngine consumer = StateEngineRoundTripper.RoundTripSnapshot(producer);

        AddList(producer, 1, 2, 3);
        AddList(producer, 3, 4, 5);
        AddList(producer, 1000, 1001, 1002);

        StateEngineRoundTripper.RoundTripDelta(producer, consumer);

        producer = Restore(consumer, schema);

        AddList(producer, 1, 2, 3);
        AddList(producer, 4, 5, 6);
        AddList(producer, 1000, 1001, 1002);
        AddList(producer, 1000000, 10001, 10002);

        StateEngineRoundTripper.RoundTripDelta(producer, consumer);

        Assert.Equal(4, consumer.GetTypeState("TestList")!.MaxOrdinal);
        AssertList(consumer, 0, 1, 2, 3);
        AssertList(consumer, 1, 4, 5, 6);
        AssertList(consumer, 3, 1000, 1001, 1002);
        AssertList(consumer, 4, 1000000, 10001, 10002);
    }

    private static HollowSetSchema SetSchema() => new("TestSet", "TestObject");

    /// <summary>
    /// Adds a set whose elements hash to something other than their own ordinals, which is what a
    /// declared hash key produces and what makes the bucket layout worth preserving.
    /// </summary>
    private static void AddSet(HollowWriteStateEngine engine, params int[] ordinals)
    {
        HollowSetWriteRecord record = new();
        foreach (int ordinal in ordinals)
        {
            record.AddElement(ordinal, ordinal + 10);
        }

        engine.Add("TestSet", record);
    }

    private static void AssertSet(HollowReadStateEngine readEngine, int ordinal, params int[] elements)
    {
        HollowSetTypeReadState state = Assert.IsType<HollowSetTypeReadState>(readEngine.GetTypeState("TestSet"));

        Assert.Equal(elements.Length, state.Size(ordinal));

        foreach (int element in elements)
        {
            Assert.True(state.Contains(ordinal, element, element + 10), $"set {ordinal} did not contain {element}");
        }
    }

    [Fact]
    public void SetRecordsKeepTheirOrdinalsAcrossARestore()
    {
        HollowSetSchema schema = SetSchema();
        HollowWriteStateEngine producer = HollowWriteStateCreator.CreateWithSchemas([schema]);

        AddSet(producer, 1, 2, 3);
        AddSet(producer, 2, 3, 4);
        AddSet(producer, 3, 4, 5);

        HollowReadStateEngine consumer = StateEngineRoundTripper.RoundTripSnapshot(producer);

        AddSet(producer, 1, 2, 3);
        AddSet(producer, 3, 4, 5);
        AddSet(producer, 1000, 1001, 1002);

        StateEngineRoundTripper.RoundTripDelta(producer, consumer);

        AssertSet(consumer, 1, 2, 3, 4);

        producer = Restore(consumer, schema);

        AddSet(producer, 1, 2, 3);
        AddSet(producer, 4, 5, 6);
        AddSet(producer, 1000, 1001, 1002);
        AddSet(producer, 1000000, 10001, 10002, 10003, 10004, 10005, 10006, 10007);

        StateEngineRoundTripper.RoundTripDelta(producer, consumer);

        Assert.Equal(4, consumer.GetTypeState("TestSet")!.MaxOrdinal);
        AssertSet(consumer, 0, 1, 2, 3);
        AssertSet(consumer, 1, 4, 5, 6);
        AssertSet(consumer, 3, 1000, 1001, 1002);
        AssertSet(consumer, 4, 1000000, 10001, 10002, 10003, 10004, 10005, 10006, 10007);

        AddSet(producer, 1000, 1001, 1002);
        StateEngineRoundTripper.RoundTripDelta(producer, consumer);

        Assert.Equal(4, consumer.GetTypeState("TestSet")!.MaxOrdinal);

        StateEngineRoundTripper.RoundTripDelta(producer, consumer);

        Assert.Equal(3, consumer.GetTypeState("TestSet")!.MaxOrdinal);
    }

    private static HollowMapSchema MapSchema() => new("TestMap", "TestKey", "TestValue");

    /// <summary>
    /// Adds a map from triples of (key ordinal, value ordinal, hash code).
    /// </summary>
    private static void AddMap(HollowWriteStateEngine engine, params int[] ordinalsAndHashCodes)
    {
        HollowMapWriteRecord record = new();
        for (int i = 0; i < ordinalsAndHashCodes.Length; i += 3)
        {
            record.AddEntry(ordinalsAndHashCodes[i], ordinalsAndHashCodes[i + 1], ordinalsAndHashCodes[i + 2]);
        }

        engine.Add("TestMap", record);
    }

    private static void AssertMapContains(
        HollowReadStateEngine readEngine, int mapOrdinal, int keyOrdinal, int valueOrdinal, int hashCode)
    {
        HollowMapTypeReadState state = Assert.IsType<HollowMapTypeReadState>(readEngine.GetTypeState("TestMap"));

        Assert.Equal(valueOrdinal, state.Get(mapOrdinal, keyOrdinal, hashCode));
    }

    [Fact]
    public void MapRecordsKeepTheirOrdinalsAcrossARestore()
    {
        HollowMapSchema schema = MapSchema();
        HollowWriteStateEngine producer = HollowWriteStateCreator.CreateWithSchemas([schema]);

        AddMap(producer, 1, 1, 3, 2, 2, 2);
        AddMap(producer, 3, 3, 3, 4, 4, 2);

        HollowReadStateEngine consumer = StateEngineRoundTripper.RoundTripSnapshot(producer);

        producer = Restore(consumer, schema);

        AddMap(producer, 1, 1, 3, 2, 2, 2);
        AddMap(producer, 5, 5, 1, 6, 6, 4);

        StateEngineRoundTripper.RoundTripDelta(producer, consumer);

        AssertMapContains(consumer, 0, 1, 1, 3);
        AssertMapContains(consumer, 0, 2, 2, 2);
        AssertMapContains(consumer, 2, 5, 5, 1);
        AssertMapContains(consumer, 2, 6, 6, 4);
    }

    /// <summary>
    /// A reverse delta written from a restored state has to take a consumer back to exactly the state
    /// it was restored from — which only works if the records the restored cycle dropped are still in
    /// the ordinal map, ghosts and all.
    /// </summary>
    [Fact]
    public void AReverseDeltaAfterARestoreReturnsToTheRestoredState()
    {
        HollowMapSchema schema = MapSchema();
        HollowWriteStateEngine producer = HollowWriteStateCreator.CreateWithSchemas([schema]);

        AddMap(producer, 1, 1, 3, 2, 2, 2);

        HollowReadStateEngine consumer = StateEngineRoundTripper.RoundTripSnapshot(producer);

        AssertMapContains(consumer, 0, 1, 1, 3);
        AssertMapContains(consumer, 0, 2, 2, 2);

        producer = Restore(consumer, schema);

        AddMap(producer, 3, 3, 3, 4, 4, 2);

        using MemoryStream reverseDelta = new();
        using MemoryStream delta = new();

        HollowBlobWriter writer = new(producer);
        writer.WriteReverseDelta(reverseDelta);
        writer.WriteDelta(delta);

        delta.Position = 0;
        new HollowBlobReader(consumer).ApplyDelta(delta);

        AssertMapContains(consumer, 1, 3, 3, 3);

        reverseDelta.Position = 0;
        new HollowBlobReader(consumer).ApplyDelta(reverseDelta);

        AssertMapContains(consumer, 0, 1, 1, 3);
        AssertMapContains(consumer, 0, 2, 2, 2);
    }

    [Fact]
    public void ASetSurvivesAReverseDeltaAfterARestore()
    {
        HollowSetSchema schema = SetSchema();
        HollowWriteStateEngine producer = HollowWriteStateCreator.CreateWithSchemas([schema]);

        AddSet(producer, 1, 2, 3);

        HollowReadStateEngine consumer = StateEngineRoundTripper.RoundTripSnapshot(producer);

        producer = Restore(consumer, schema);

        AddSet(producer, 4, 5, 6);

        using MemoryStream reverseDelta = new();
        using MemoryStream delta = new();

        HollowBlobWriter writer = new(producer);
        writer.WriteReverseDelta(reverseDelta);
        writer.WriteDelta(delta);

        delta.Position = 0;
        new HollowBlobReader(consumer).ApplyDelta(delta);

        AssertSet(consumer, 1, 4, 5, 6);

        reverseDelta.Position = 0;
        new HollowBlobReader(consumer).ApplyDelta(reverseDelta);

        AssertSet(consumer, 0, 1, 2, 3);
    }

    /// <summary>
    /// Restoring across a schema change: a field is dropped, a field is added, the order changes, and a
    /// referenced type is replaced. Records still match on the fields the two schemas share.
    /// </summary>
    [Fact]
    public void RecordsMatchOnTheirCommonFieldsWhenTheSchemaChanges()
    {
        HollowObjectSchema typeBSchema = new("TypeB", 2);
        typeBSchema.AddField("b1", FieldType.Long);
        typeBSchema.AddField("b2", FieldType.Float);

        HollowObjectSchema typeCSchema = new("TypeC", 1);
        typeCSchema.AddField("c1", FieldType.String);

        HollowObjectSchema typeABefore = new("TypeA", 3);
        typeABefore.AddField("a1", FieldType.Boolean);
        typeABefore.AddField("a2", FieldType.String);
        typeABefore.AddField("a3", FieldType.Reference, "TypeB");

        // Same type, later data model: a3 is gone, a4 arrives, and a1 and a2 have swapped places.
        HollowObjectSchema typeAAfter = new("TypeA", 3);
        typeAAfter.AddField("a2", FieldType.String);
        typeAAfter.AddField("a1", FieldType.Boolean);
        typeAAfter.AddField("a4", FieldType.Reference, "TypeC");

        HollowWriteStateEngine producer = HollowWriteStateCreator.CreateWithSchemas([typeBSchema, typeABefore]);

        void AddBefore(bool a1, string a2, long b1, float b2)
        {
            HollowObjectWriteRecord b = new(typeBSchema);
            b.SetLong("b1", b1);
            b.SetFloat("b2", b2);
            int bOrdinal = producer.Add("TypeB", b);

            HollowObjectWriteRecord a = new(typeABefore);
            a.SetBoolean("a1", a1);
            a.SetString("a2", a2);
            a.SetReference("a3", bOrdinal);
            producer.Add("TypeA", a);
        }

        AddBefore(true, "zero", 0, 0.1f);
        AddBefore(false, "one", 1, 1.1f);
        AddBefore(true, "two", 1, 1.1f);

        HollowReadStateEngine consumer = StateEngineRoundTripper.RoundTripSnapshot(producer);

        AddBefore(true, "zero", 0, 0.1f);
        AddBefore(false, "one", 1, 1.1f);
        AddBefore(false, "two", 2, 2.2f);

        StateEngineRoundTripper.RoundTripDelta(producer, consumer);

        producer = Restore(consumer, typeCSchema, typeAAfter);

        void AddAfter(bool a1, string a2, string c1)
        {
            HollowObjectWriteRecord c = new(typeCSchema);
            c.SetString("c1", c1);
            int cOrdinal = producer.Add("TypeC", c);

            HollowObjectWriteRecord a = new(typeAAfter);
            a.SetBoolean("a1", a1);
            a.SetString("a2", a2);
            a.SetReference("a4", cOrdinal);
            producer.Add("TypeA", a);
        }

        AddAfter(false, "one", "c1");
        AddAfter(false, "two", "c2");
        AddAfter(true, "zero", "c0");
        AddAfter(true, "wxyz", "c3");

        StateEngineRoundTripper.RoundTripDelta(producer, consumer);

        HollowObjectTypeReadState typeA = Assert.IsType<HollowObjectTypeReadState>(consumer.GetTypeState("TypeA"));
        int a1Position = typeA.Schema.GetPosition("a1");
        int a2Position = typeA.Schema.GetPosition("a2");

        // (a1, a2) is all the two schemas share, and matching on it is what kept these ordinals.
        Assert.True(typeA.ReadBoolean(0, a1Position));
        Assert.Equal("zero", typeA.ReadString(0, a2Position));
        Assert.False(typeA.ReadBoolean(1, a1Position));
        Assert.Equal("one", typeA.ReadString(1, a2Position));
        Assert.False(typeA.ReadBoolean(3, a1Position));
        Assert.Equal("two", typeA.ReadString(3, a2Position));

        // Only the genuinely new record took a new ordinal, and it filled the hole left by the old one.
        Assert.True(typeA.ReadBoolean(2, a1Position));
        Assert.Equal("wxyz", typeA.ReadString(2, a2Position));

        // A delta cannot change a consumer's data model: the consumer still holds the old schema, and
        // TypeC — which it has never had a snapshot of — was skipped over rather than rejected.
        Assert.Equal(2, typeA.Schema.GetPosition("a3"));
        Assert.Equal(-1, typeA.Schema.GetPosition("a4"));
        Assert.Null(consumer.GetTypeState("TypeC"));

        // The new data model reaches the consumer with the next snapshot, ordinals intact.
        AddAfter(false, "one", "c1");
        AddAfter(false, "two", "c2");
        AddAfter(true, "zero", "c0");
        AddAfter(true, "wxyz", "c3");

        consumer = StateEngineRoundTripper.RoundTripSnapshot(producer);

        typeA = Assert.IsType<HollowObjectTypeReadState>(consumer.GetTypeState("TypeA"));
        HollowObjectTypeReadState typeC = Assert.IsType<HollowObjectTypeReadState>(consumer.GetTypeState("TypeC"));

        Assert.Equal(-1, typeA.Schema.GetPosition("a3"));

        int a4Position = typeA.Schema.GetPosition("a4");
        int c1Position = typeC.Schema.GetPosition("c1");

        Assert.Equal("c0", typeC.ReadString(typeA.ReadOrdinal(0, a4Position), c1Position));
        Assert.Equal("c1", typeC.ReadString(typeA.ReadOrdinal(1, a4Position), c1Position));
        Assert.Equal("c3", typeC.ReadString(typeA.ReadOrdinal(2, a4Position), c1Position));
        Assert.Equal("c2", typeC.ReadString(typeA.ReadOrdinal(3, a4Position), c1Position));
    }

    /// <summary>
    /// The <c>Decimal</c> extension goes through the copiers like any other field; see PORTING.md.
    /// </summary>
    [Fact]
    public void DecimalFieldsSurviveARestore()
    {
        HollowObjectSchema schema = new("Priced", 2);
        schema.AddField("name", FieldType.String);
        schema.AddField("amount", FieldType.Decimal);

        HollowWriteStateEngine producer = HollowWriteStateCreator.CreateWithSchemas([schema]);

        void Add(string name, decimal? amount)
        {
            HollowObjectWriteRecord record = new(schema);
            record.SetString("name", name);

            if (amount is { } value)
            {
                record.SetDecimal("amount", value);
            }

            producer.Add("Priced", record);
        }

        Add("a", 12.3400m);
        Add("b", -0.0000000001m);
        Add("c", null);

        HollowReadStateEngine consumer = StateEngineRoundTripper.RoundTripSnapshot(producer);

        producer = Restore(consumer, schema);

        Add("a", 12.3400m);
        Add("b", -0.0000000001m);
        Add("c", null);
        Add("d", decimal.MaxValue);

        StateEngineRoundTripper.RoundTripDelta(producer, consumer);

        HollowObjectTypeReadState state = Assert.IsType<HollowObjectTypeReadState>(consumer.GetTypeState("Priced"));
        int amountPosition = state.Schema.GetPosition("amount");

        // Ordinals 0..2 came through the restore untouched.
        Assert.Equal(12.3400m, state.ReadDecimal(0, amountPosition));
        Assert.Equal(-0.0000000001m, state.ReadDecimal(1, amountPosition));
        Assert.Null(state.ReadDecimal(2, amountPosition));
        Assert.Equal(decimal.MaxValue, state.ReadDecimal(3, amountPosition));

        // Decimal equality ignores scale, so check the trailing zeros separately: a restore that
        // normalised them would have changed the record and cost it its ordinal.
        Assert.Equal("12.3400", state.ReadDecimal(0, amountPosition)!.Value.Invariant());
    }

    [Fact]
    public void RecreatingAWriteStateReproducesTheReadState()
    {
        HollowObjectSchema objectSchema = ObjectSchema();
        HollowListSchema listSchema = ListSchema();
        HollowWriteStateEngine producer = HollowWriteStateCreator.CreateWithSchemas([objectSchema, listSchema]);

        AddObject(producer, objectSchema, 1, "one");
        AddObject(producer, objectSchema, 2, "two");
        AddList(producer, 0, 1);
        AddList(producer, 1, 0, 1);

        HollowReadStateEngine consumer = StateEngineRoundTripper.RoundTripSnapshot(producer);

        HollowWriteStateEngine recreated = HollowWriteStateCreator.RecreateAndPopulateUsingReadEngine(consumer);
        HollowReadStateEngine reread = StateEngineRoundTripper.RoundTripSnapshot(recreated);

        AssertObject(reread, 0, 1, "one");
        AssertObject(reread, 1, 2, "two");
        AssertList(reread, 0, 0, 1);
        AssertList(reread, 1, 1, 0, 1);

        Assert.Equal(
            consumer.GetTypeState("TestObject")!.MaxOrdinal, reread.GetTypeState("TestObject")!.MaxOrdinal);
        Assert.Equal(consumer.GetTypeState("TestList")!.MaxOrdinal, reread.GetTypeState("TestList")!.MaxOrdinal);
    }

    /// <summary>
    /// A type has to be registered after everything it references, because the object mapper and the
    /// blob format both assume a referenced type already exists.
    /// </summary>
    [Fact]
    public void CreatingFromSchemasOrdersThemByDependency()
    {
        HollowObjectSchema movie = new("Movie", 1);
        movie.AddField("cast", FieldType.Reference, "ActorList");

        HollowListSchema actorList = new("ActorList", "Actor");

        HollowObjectSchema actor = new("Actor", 1);
        actor.AddField("name", FieldType.String);

        HollowWriteStateEngine engine =
            HollowWriteStateCreator.CreateWithSchemas([movie, actorList, actor]);

        Assert.Equal(
            ["Actor", "ActorList", "Movie"],
            engine.OrderedTypeStates.Select(state => state.Schema.Name));
    }

    /// <summary>
    /// A reference cycle cannot be ordered, but dropping the types involved would be worse than
    /// returning them in some fixed order.
    /// </summary>
    [Fact]
    public void SortingSchemasKeepsTypesCaughtInACycle()
    {
        HollowObjectSchema value = new("Value", 1);
        value.AddField("struct", FieldType.Reference, "Struct");

        HollowObjectSchema structType = new("Struct", 1);
        structType.AddField("value", FieldType.Reference, "Value");

        HollowObjectSchema leaf = new("Leaf", 1);
        leaf.AddField("name", FieldType.String);

        IReadOnlyList<HollowSchema> ordered =
            HollowSchemaSorter.DependencyOrderedSchemaList([value, structType, leaf]);

        Assert.Equal(["Leaf", "Struct", "Value"], ordered.Select(schema => schema.Name));
    }
}
