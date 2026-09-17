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

using System.Globalization;
using Hollow.Core.Index;
using Hollow.Core.Index.Key;
using Hollow.Core.Read;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Schema;
using Hollow.Core.Util;
using Hollow.Core.Write;

namespace Hollow.Tests.Core;

/// <summary>
/// Everything this library renders as text reads the same on every machine.
/// </summary>
/// <remarks>
/// <para>
/// The whole suite runs under a culture that formats numbers differently from the invariant one — see
/// <see cref="HostileCulture"/> — so any test comparing formatted output against a literal would fail
/// if the code under test used the current culture. These tests pin the cases that matter directly, so
/// the intent survives even if that guard is ever removed.
/// </para>
/// <para>
/// See "Culture-invariant formatting and parsing" in <c>PORTING.md</c>.
/// </para>
/// </remarks>
public class CultureInvarianceTests
{
    /// <summary>
    /// If this fails, the suite is no longer running under a hostile culture and every other test in
    /// this file has stopped proving anything.
    /// </summary>
    [Fact]
    public void TheSuiteRunsUnderACultureThatFormatsNumbersDifferently()
    {
        Assert.Equal(",", CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator);
        Assert.NotEqual("-", CultureInfo.CurrentCulture.NumberFormat.NegativeSign);

        // And the framework really does honour it, so a bare ToString would differ.
        Assert.NotEqual("1.5", 1.5d.ToString(CultureInfo.CurrentCulture));
    }

    private static HollowObjectSchema ValueSchema()
    {
        HollowObjectSchema schema = new("Value", 6);
        schema.AddField("i", FieldType.Int);
        schema.AddField("l", FieldType.Long);
        schema.AddField("f", FieldType.Float);
        schema.AddField("d", FieldType.Double);
        schema.AddField("m", FieldType.Decimal);
        schema.AddField("b", FieldType.Boolean);
        return schema;
    }

    private static HollowObjectTypeReadState ReadOneRecord()
    {
        HollowObjectSchema schema = ValueSchema();

        HollowWriteStateEngine engine = new();
        engine.AddTypeState(new HollowObjectTypeWriteState(schema));

        HollowObjectWriteRecord record = new(schema);
        record.SetInt("i", -1234);
        record.SetLong("l", -9876543210L);
        record.SetFloat("f", -1.5f);
        record.SetDouble("d", -2.25d);
        record.SetDecimal("m", -3.750m);
        record.SetBoolean("b", true);
        engine.Add("Value", record);

        using MemoryStream stream = new();
        new HollowBlobWriter(engine).WriteSnapshot(stream);
        stream.Position = 0;

        HollowReadStateEngine readEngine = new();
        new HollowBlobReader(readEngine).ReadSnapshot(stream);

        return Assert.IsType<HollowObjectTypeReadState>(readEngine.GetTypeState("Value"));
    }

    /// <summary>
    /// The case that prompted all of this: a decimal field displayed with a comma on a machine whose
    /// culture uses one.
    /// </summary>
    [Fact]
    public void DisplayStringUsesTheInvariantCulture()
    {
        HollowObjectTypeReadState state = ReadOneRecord();
        HollowObjectSchema schema = state.Schema;

        Assert.Equal("-1234", HollowReadFieldUtils.DisplayString(state, 0, schema.GetPosition("i")));
        Assert.Equal("-9876543210", HollowReadFieldUtils.DisplayString(state, 0, schema.GetPosition("l")));
        Assert.Equal("-1.5", HollowReadFieldUtils.DisplayString(state, 0, schema.GetPosition("f")));
        Assert.Equal("-2.25", HollowReadFieldUtils.DisplayString(state, 0, schema.GetPosition("d")));
        Assert.Equal("-3.75", HollowReadFieldUtils.DisplayString(state, 0, schema.GetPosition("m")));
    }

    /// <summary>
    /// A negative number has to start with an ASCII hyphen, not with whatever the current culture's
    /// negative sign happens to be.
    /// </summary>
    [Fact]
    public void NegativeNumbersUseAnAsciiHyphen()
    {
        HollowObjectTypeReadState state = ReadOneRecord();

        foreach (string fieldName in (string[])["i", "l", "f", "d", "m"])
        {
            string? text = HollowReadFieldUtils.DisplayString(state, 0, state.Schema.GetPosition(fieldName));

            Assert.NotNull(text);
            Assert.StartsWith("-", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BitSetsRenderInvariantly()
    {
        BitSet bitSet = new();
        bitSet.Set(1);
        bitSet.Set(1000);

        Assert.Equal("{1, 1000}", bitSet.ToString());

        Hollow.Core.Memory.ThreadSafeBitSet threadSafe = new();
        threadSafe.Set(1);
        threadSafe.Set(1000);

        Assert.Equal("{1, 1000}", threadSafe.ToString());
    }

    /// <summary>
    /// A duplicate key report holds boxed field values, so rendering it goes through whatever those
    /// values' types do by default.
    /// </summary>
    [Fact]
    public void DuplicateKeyInfoRendersInvariantly()
    {
        HollowPrimaryKeyIndex.DuplicateKeyInfo info = new([1, -2.5d, -3.750m, "text"], 7);

        Assert.Equal("[1, -2.5, -3.750, text] (count=7)", info.ToString());
    }

    /// <summary>
    /// The schema text syntax is a real format, so it cannot depend on where it was written.
    /// </summary>
    [Fact]
    public void SchemaTextRendersInvariantly()
    {
        HollowObjectSchema schema = ValueSchema();

        Assert.Equal(
            "Value {\n\tint i;\n\tlong l;\n\tfloat f;\n\tdouble d;\n\tdecimal m;\n\tboolean b;\n}",
            schema.ToString());

        Assert.Equal(
            "PrimaryKey [type=Value, fieldPaths=[i, l]]",
            new PrimaryKey("Value", "i", "l").ToString());
    }

    /// <summary>
    /// The values themselves have to survive a round trip regardless of culture — this is about the
    /// binary encoding rather than about text, but it is the thing that would matter most if a culture
    /// leaked into the write path.
    /// </summary>
    [Fact]
    public void ValuesRoundTripUnchangedUnderAHostileCulture()
    {
        HollowObjectTypeReadState state = ReadOneRecord();
        HollowObjectSchema schema = state.Schema;

        Assert.Equal(-1234, state.ReadInt(0, schema.GetPosition("i")));
        Assert.Equal(-9876543210L, state.ReadLong(0, schema.GetPosition("l")));
        Assert.Equal(-1.5f, state.ReadFloat(0, schema.GetPosition("f")));
        Assert.Equal(-2.25d, state.ReadDouble(0, schema.GetPosition("d")));
        Assert.Equal(-3.750m, state.ReadDecimal(0, schema.GetPosition("m")));
        Assert.True(state.ReadBoolean(0, schema.GetPosition("b")));
    }

    /// <summary>
    /// An exception message carrying a number is text too, and reads the same everywhere.
    /// </summary>
    [Fact]
    public void ExceptionMessagesRenderNumbersInvariantly()
    {
        HollowObjectSchema schema = ValueSchema();

        HollowWriteStateEngine first = new() { RandomizedTag = -1234 };
        first.AddTypeState(new HollowObjectTypeWriteState(schema));

        HollowObjectWriteRecord record = new(schema);
        record.SetInt("i", 1);
        first.Add("Value", record);

        using MemoryStream snapshot = new();
        new HollowBlobWriter(first).WriteSnapshot(snapshot);
        snapshot.Position = 0;

        HollowReadStateEngine consumer = new();
        new HollowBlobReader(consumer).ReadSnapshot(snapshot);

        first.PrepareForNextCycle();
        first.RandomizedTag = -5678;
        record.Reset();
        record.SetInt("i", 2);
        first.Add("Value", record);

        using MemoryStream delta = new();
        new HollowBlobWriter(first).WriteDelta(delta);

        consumer.RandomizedTag = 999;
        delta.Position = 0;

        InvalidDataException e = Assert.Throws<InvalidDataException>(
            () => new HollowBlobReader(consumer).ApplyDelta(delta));

        Assert.Contains("-1234", e.Message, StringComparison.Ordinal);
        Assert.Contains("999", e.Message, StringComparison.Ordinal);
    }
}
