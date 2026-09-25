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
using Hollow.Core.Util;
using Hollow.Core.Write;

namespace Hollow.Tests.Core.Read.Object;

/// <summary>
/// The edges of an object record's fixed-length packing: the widest values, the boundary between two
/// segments of the bit string, a field nothing ever wrote, and a negative float.
/// </summary>
/// <remarks>
/// Ported from <c>HollowObjectLargeFieldTest</c>, <c>HollowObjectExactBitBoundaryEdgeCaseTest</c>,
/// <c>HollowObjectNullStringValueTest</c>, <c>HollowObjectStringEqualityTest</c> and
/// <c>NegativeFloatTest</c>. Every one of them is about the same thing: fields are packed to the bit
/// rather than to the byte, so a field's width, its sign and where it happens to fall are all things
/// the reader has to get exactly right, and getting one wrong corrupts the field beside it rather than
/// failing.
/// </remarks>
public sealed class ObjectFieldEdgeCaseTests
{
    // ── The widest values each type can hold ─────────────────────────────────────────────────────

    [Fact]
    public void TheWidestValueOfEveryNumericTypeSurvivesASnapshotAndTwoDeltas()
    {
        HollowObjectSchema schema = new("TestObject", 3);
        schema.AddField("longField", FieldType.Long);
        schema.AddField("intField", FieldType.Int);
        schema.AddField("doubleField", FieldType.Double);

        HollowWriteStateEngine writeEngine = new();
        writeEngine.AddTypeState(new HollowObjectTypeWriteState(schema));

        Add(writeEngine, schema, 1, 1, 2.53D);
        Add(writeEngine, schema, 100, 100, 3523456.3252352456346D);

        HollowReadStateEngine readEngine = StateEngineRoundTripper.RoundTripSnapshot(writeEngine);

        // A cycle that introduces the extremes. Every field's width grows, which is what makes the
        // delta rewrite rather than carry records across.
        Add(writeEngine, schema, 100, 100, 3523456.3252352456346D);
        Add(writeEngine, schema, long.MinValue, int.MinValue, double.Epsilon);
        Add(writeEngine, schema, 200, 200, 1.00003D);

        StateEngineRoundTripper.RoundTripDelta(writeEngine, readEngine);

        AssertRecord(readEngine, 1, 100, 100, 3523456.3252352456346D);
        AssertRecord(readEngine, 2, long.MinValue, int.MinValue, double.Epsilon);
        AssertRecord(readEngine, 3, 200, 200, 1.00003D);

        Add(writeEngine, schema, long.MaxValue, int.MaxValue, double.MaxValue);
        Add(writeEngine, schema, 100, 100, 3523456.3252352456346D);
        Add(writeEngine, schema, long.MinValue, int.MinValue, double.Epsilon);
        Add(writeEngine, schema, 200, 200, 1.00003D);

        StateEngineRoundTripper.RoundTripDelta(writeEngine, readEngine);

        AssertRecord(readEngine, 0, long.MaxValue, int.MaxValue, double.MaxValue);
        AssertRecord(readEngine, 1, 100, 100, 3523456.3252352456346D);
        AssertRecord(readEngine, 2, long.MinValue, int.MinValue, double.Epsilon);
        AssertRecord(readEngine, 3, 200, 200, 1.00003D);
    }

    // ── A field nothing wrote, at the exact end of a segment ─────────────────────────────────────

    [Fact]
    public void AFieldNothingEverWroteCanStillBeReadWhereItFallsOnASegmentBoundary()
    {
        // The fixed-length bit string is kept in segments, and a record whose last field ends exactly
        // at a segment's end is the case where a read of that field walks into the next segment. A
        // field nothing ever populated is one bit wide, which is what puts the boundary in reach.
        HollowObjectSchema floats = new("TestObject", 3);
        floats.AddField("float1", FieldType.Float);
        floats.AddField("float2", FieldType.Float);
        floats.AddField("unpopulatedField", FieldType.Int);

        HollowObjectSchema ints = new("TestObject2", 3);
        ints.AddField("int1", FieldType.Int);
        ints.AddField("int2", FieldType.Int);
        ints.AddField("unpopulatedField", FieldType.Int);

        HollowWriteStateEngine writeEngine = new();
        writeEngine.AddTypeState(new HollowObjectTypeWriteState(floats));
        writeEngine.AddTypeState(new HollowObjectTypeWriteState(ints));

        for (int i = 0; i < 4094; i++)
        {
            AddFloats(writeEngine, floats, i);
        }

        for (int i = 0; i < 992; i++)
        {
            AddInts(writeEngine, ints, i);
        }

        HollowReadStateEngine readEngine = StateEngineRoundTripper.RoundTripSnapshot(writeEngine);

        for (int i = 0; i < 4096; i++)
        {
            AddFloats(writeEngine, floats, i);
        }

        for (int i = 0; i < 993; i++)
        {
            AddInts(writeEngine, ints, i);
        }

        StateEngineRoundTripper.RoundTripDelta(writeEngine, readEngine);

        HollowObjectTypeReadState floatState =
            (HollowObjectTypeReadState)readEngine.GetTypeDataAccess("TestObject")!;

        HollowObjectTypeReadState intState =
            (HollowObjectTypeReadState)readEngine.GetTypeDataAccess("TestObject2")!;

        // These ordinals are one past the last the cycle populated, which is deliberate: it is the
        // read of the last field of the record after the last record that runs into the end of the
        // segment. The value means nothing — Java asserts nothing about it either — so what is
        // asserted is that the read completes rather than walking off the end of the bit string.
        Assert.Null(Record.Exception(() => floatState.ReadInt(4096, 2)));
        Assert.Null(Record.Exception(() => intState.ReadInt(992, 2)));

        // The records that do exist still read back what was written, on both sides of the boundary.
        Assert.Equal(4095, floatState.PopulatedOrdinals.Cardinality() - 1);

        for (int ordinal = 4090; ordinal < 4096; ordinal++)
        {
            Assert.Equal((float)ordinal, floatState.ReadFloat(ordinal, 0));
            Assert.Equal((float)ordinal, floatState.ReadFloat(ordinal, 1));
        }

        for (int ordinal = 986; ordinal < 992; ordinal++)
        {
            Assert.Equal(ordinal, intState.ReadInt(ordinal, 0));
            Assert.Equal(1_047_552, intState.ReadInt(ordinal, 1));
        }
    }

    // ── A negative float, which sets the top bit of the field beside it if it is packed wrongly ──

    [Fact]
    public void ANegativeFloatDoesNotReachIntoTheFieldAfterIt()
    {
        // A float is stored as its raw bits, and a negative one has the top bit of those thirty-two
        // set. If the width or the sign extension is wrong, the field that follows reads as garbage —
        // so the assertion is on the int, not on the float.
        HollowObjectSchema schema = new("TypeWithFloat", 2);
        schema.AddField("f", FieldType.Float);
        schema.AddField("i", FieldType.Int);

        HollowWriteStateEngine writeEngine = new();
        writeEngine.AddTypeState(new HollowObjectTypeWriteState(schema));

        HollowObjectWriteRecord record = new(schema);

        for (int i = 0; i < 10; i++)
        {
            record.Reset();
            record.SetFloat("f", -200f);
            record.SetInt("i", i);
            writeEngine.Add("TypeWithFloat", record);
        }

        HollowReadStateEngine readEngine = StateEngineRoundTripper.RoundTripSnapshot(writeEngine);

        HollowObjectTypeReadState typeState =
            (HollowObjectTypeReadState)readEngine.GetTypeDataAccess("TypeWithFloat")!;

        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(i, typeState.ReadInt(i, 1));
            Assert.Equal(-200f, typeState.ReadFloat(i, 0));
        }
    }

    [Fact]
    public void ANegativeZeroFloatIsNotTheSameRecordAsAPositiveZero()
    {
        // -0f and 0f compare equal but have different bits, and Hollow deduplicates records by their
        // bytes. Two records here, not one.
        HollowObjectSchema schema = new("TypeWithFloat", 1);
        schema.AddField("f", FieldType.Float);

        HollowWriteStateEngine writeEngine = new();
        writeEngine.AddTypeState(new HollowObjectTypeWriteState(schema));

        HollowObjectWriteRecord record = new(schema);

        record.Reset();
        record.SetFloat("f", 0f);
        writeEngine.Add("TypeWithFloat", record);

        record.Reset();
        record.SetFloat("f", -0f);
        writeEngine.Add("TypeWithFloat", record);

        HollowReadStateEngine readEngine = StateEngineRoundTripper.RoundTripSnapshot(writeEngine);

        HollowObjectTypeReadState typeState =
            (HollowObjectTypeReadState)readEngine.GetTypeDataAccess("TypeWithFloat")!;

        Assert.Equal(2, typeState.PopulatedOrdinals.Cardinality());
        Assert.Equal(
            float.IsNegative(typeState.ReadFloat(0, 0)),
            !float.IsNegative(typeState.ReadFloat(1, 0)));
    }

    // ── Strings ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ANullStringIsNullRatherThanEmptyAndDoesNotDisturbTheRecordAroundIt()
    {
        HollowObjectSchema schema = new("TestObject", 2);
        schema.AddField("f1", FieldType.Int);
        schema.AddField("f2", FieldType.String);

        HollowWriteStateEngine writeEngine = new();
        writeEngine.AddTypeState(new HollowObjectTypeWriteState(schema));

        HollowObjectWriteRecord record = new(schema);

        foreach ((int number, string? text) in ((int, string?)[])[(0, "zero"), (1, null), (2, "two")])
        {
            record.Reset();
            record.SetInt("f1", number);
            record.SetString("f2", text);
            writeEngine.Add("TestObject", record);
        }

        HollowReadStateEngine readEngine = StateEngineRoundTripper.RoundTripSnapshot(writeEngine);

        HollowObjectTypeReadState typeState =
            (HollowObjectTypeReadState)readEngine.GetTypeDataAccess("TestObject")!;

        Assert.Equal(0, typeState.ReadInt(0, 0));
        Assert.Equal("zero", typeState.ReadString(0, 1));

        Assert.Equal(1, typeState.ReadInt(1, 0));
        Assert.Null(typeState.ReadString(1, 1));

        Assert.Equal(2, typeState.ReadInt(2, 0));
        Assert.Equal("two", typeState.ReadString(2, 1));
    }

    [Fact]
    public void ComparingAStringInPlaceAgreesWithReadingItOut()
    {
        // The comparison walks the stored characters rather than building a string, so a prefix and an
        // extension of the stored value are the two ways of getting the length wrong.
        HollowObjectSchema schema = new("TestObject", 1);
        schema.AddField("str", FieldType.String);

        HollowWriteStateEngine writeEngine = new();
        writeEngine.AddTypeState(new HollowObjectTypeWriteState(schema));

        HollowObjectWriteRecord record = new(schema);
        record.SetString("str", "test");
        writeEngine.Add("TestObject", record);

        HollowReadStateEngine readEngine = StateEngineRoundTripper.RoundTripSnapshot(writeEngine);

        HollowObjectTypeReadState typeState =
            (HollowObjectTypeReadState)readEngine.GetTypeDataAccess("TestObject")!;

        Assert.False(typeState.IsStringFieldEqual(0, 0, "tes"));
        Assert.True(typeState.IsStringFieldEqual(0, 0, "test"));
        Assert.False(typeState.IsStringFieldEqual(0, 0, "testt"));
        Assert.False(typeState.IsStringFieldEqual(0, 0, string.Empty));
        Assert.False(typeState.IsStringFieldEqual(0, 0, null));
    }

    [Fact]
    public void AnEmptyStringIsNotANullOne()
    {
        HollowObjectSchema schema = new("TestObject", 1);
        schema.AddField("str", FieldType.String);

        HollowWriteStateEngine writeEngine = new();
        writeEngine.AddTypeState(new HollowObjectTypeWriteState(schema));

        HollowObjectWriteRecord record = new(schema);

        record.Reset();
        record.SetString("str", string.Empty);
        writeEngine.Add("TestObject", record);

        record.Reset();
        record.SetString("str", null);
        writeEngine.Add("TestObject", record);

        HollowReadStateEngine readEngine = StateEngineRoundTripper.RoundTripSnapshot(writeEngine);

        HollowObjectTypeReadState typeState =
            (HollowObjectTypeReadState)readEngine.GetTypeDataAccess("TestObject")!;

        Assert.Equal(2, typeState.PopulatedOrdinals.Cardinality());
        Assert.Equal(string.Empty, typeState.ReadString(0, 0));
        Assert.Null(typeState.ReadString(1, 0));
        Assert.True(typeState.IsStringFieldEqual(0, 0, string.Empty));
        Assert.True(typeState.IsStringFieldEqual(1, 0, null));
    }

    private static void Add(
        HollowWriteStateEngine engine, HollowObjectSchema schema, long l, int i, double d)
    {
        HollowObjectWriteRecord record = new(schema);
        record.SetLong("longField", l);
        record.SetInt("intField", i);
        record.SetDouble("doubleField", d);
        engine.Add("TestObject", record);
    }

    private static void AddFloats(HollowWriteStateEngine engine, HollowObjectSchema schema, float value)
    {
        HollowObjectWriteRecord record = new(schema);
        record.SetFloat("float1", value);
        record.SetFloat("float2", value);
        engine.Add("TestObject", record);
    }

    private static void AddInts(HollowWriteStateEngine engine, HollowObjectSchema schema, int value)
    {
        HollowObjectWriteRecord record = new(schema);
        record.SetInt("int1", value);
        record.SetInt("int2", 1_047_552);
        engine.Add("TestObject2", record);
    }

    private static void AssertRecord(
        HollowReadStateEngine readEngine, int ordinal, long l, int i, double d)
    {
        HollowObjectTypeReadState typeState =
            (HollowObjectTypeReadState)readEngine.GetTypeDataAccess("TestObject")!;

        Assert.Equal(l, typeState.ReadLong(ordinal, 0));
        Assert.Equal(i, typeState.ReadInt(ordinal, 1));
        Assert.Equal(d, typeState.ReadDouble(ordinal, 2));
    }
}
