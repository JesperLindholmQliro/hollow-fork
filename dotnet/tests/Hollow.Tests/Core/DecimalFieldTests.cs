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
using Hollow.Core.Index;
using Hollow.Core.Index.Key;
using Hollow.Core.Read;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Read.Engine.Set;
using Hollow.Core.Read.Filter;
using Hollow.Core.Schema;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Tests.Core;

/// <summary>
/// The <see cref="FieldType.Decimal"/> field type, which is an extension to the Hollow format rather
/// than part of Netflix Hollow — see <c>PORTING.md</c>.
/// </summary>
/// <remarks>
/// It is the only field wider than 64 bits, so it is the only one stored as two elements rather than
/// one. Everything that handles fields generically — the record writer, the field filter, the delta
/// applicator, the key hashers — has to carry both halves, which is what most of these tests check.
/// </remarks>
public class DecimalFieldTests
{
    private static readonly decimal[] InterestingValues =
    [
        0m,
        1m,
        -1m,
        0.01m,
        -0.01m,
        1.50m,
        decimal.MaxValue,
        decimal.MinValue,
        0.0000000000000000000000000001m,
        123456789.123456789m,
        -98765.4321m,
    ];

    private static HollowObjectSchema PriceSchema(PrimaryKey? primaryKey = null)
    {
        HollowObjectSchema schema = new("Price", 3, primaryKey);
        schema.AddField("id", FieldType.Int);
        schema.AddField("amount", FieldType.Decimal);
        schema.AddField("label", FieldType.String);
        return schema;
    }

    private static HollowWriteStateEngine Engine(HollowObjectSchema schema, int numShards = 1) =>
        NewEngine(schema, numShards);

    private static HollowWriteStateEngine NewEngine(HollowObjectSchema schema, int numShards)
    {
        HollowWriteStateEngine engine = new() { RandomizedTag = 1 };
        engine.AddTypeState(new HollowObjectTypeWriteState(schema, numShards));
        return engine;
    }

    private static void Add(
        HollowWriteStateEngine engine, HollowObjectSchema schema, int id, decimal? amount, string? label = null)
    {
        HollowObjectWriteRecord record = new(schema);
        record.SetInt("id", id);

        if (amount is { } value)
        {
            record.SetDecimal("amount", value);
        }

        record.SetString("label", label ?? $"price-{id}");
        engine.Add("Price", record);
    }

    private static HollowReadStateEngine ReadSnapshot(HollowWriteStateEngine engine, ITypeFilter? filter = null)
    {
        using MemoryStream stream = new();
        new HollowBlobWriter(engine).WriteSnapshot(stream);
        stream.Position = 0;

        HollowReadStateEngine readEngine = new();
        new HollowBlobReader(readEngine).ReadSnapshot(stream, filter);
        return readEngine;
    }

    private static void ApplyDelta(HollowWriteStateEngine engine, HollowReadStateEngine consumer)
    {
        using MemoryStream stream = new();
        new HollowBlobWriter(engine).WriteDelta(stream);
        stream.Position = 0;

        new HollowBlobReader(consumer).ApplyDelta(stream);
    }

    private static Dictionary<int, decimal?> ReadAll(HollowReadStateEngine consumer)
    {
        HollowObjectTypeReadState state =
            Assert.IsType<HollowObjectTypeReadState>(consumer.GetTypeState("Price"));

        int id = state.Schema.GetPosition("id");
        int amount = state.Schema.GetPosition("amount");

        Dictionary<int, decimal?> values = [];
        foreach (int ordinal in state.PopulatedOrdinals.EnumerateSetBits())
        {
            values[state.ReadInt(ordinal, id)] = state.ReadDecimal(ordinal, amount);
        }

        return values;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void DecimalsSurviveTheRoundTrip(int numShards)
    {
        HollowObjectSchema schema = PriceSchema();
        HollowWriteStateEngine engine = Engine(schema, numShards);

        for (int i = 0; i < InterestingValues.Length; i++)
        {
            Add(engine, schema, i, InterestingValues[i]);
        }

        Dictionary<int, decimal?> read = ReadAll(ReadSnapshot(engine));

        for (int i = 0; i < InterestingValues.Length; i++)
        {
            Assert.Equal(InterestingValues[i], read[i]);
        }
    }

    /// <summary>
    /// The stored form keeps the scale, so a value read back prints as it was written. Converting
    /// through a double would not manage this, which is the reason the field type exists.
    /// </summary>
    [Fact]
    public void ScaleIsNormalisedOnTheRoundTrip()
    {
        HollowObjectSchema schema = PriceSchema();
        HollowWriteStateEngine engine = Engine(schema);

        Add(engine, schema, 0, 1.50m);
        Add(engine, schema, 1, 1.5m);
        Add(engine, schema, 2, 1.500000m);

        HollowObjectTypeReadState state =
            Assert.IsType<HollowObjectTypeReadState>(ReadSnapshot(engine).GetTypeState("Price"));
        int amount = state.Schema.GetPosition("amount");

        // The encoding preserves the value, not the scale it arrived with: all three spellings of 1.5
        // are written as one byte and read back identically.
        Assert.Equal("1.5", Format(state.ReadDecimal(0, amount)));
        Assert.Equal("1.5", Format(state.ReadDecimal(1, amount)));
        Assert.Equal("1.5", Format(state.ReadDecimal(2, amount)));
    }

    /// <summary>
    /// A value that needs the full 96-bit mantissa exercises both stored words, so a merge or filter
    /// that dropped the high half would show up here.
    /// </summary>
    [Fact]
    public void ValuesUsingTheFullMantissaSurvive()
    {
        HollowObjectSchema schema = PriceSchema();
        HollowWriteStateEngine engine = Engine(schema);

        Add(engine, schema, 0, decimal.MaxValue);
        Add(engine, schema, 1, decimal.MinValue);
        Add(engine, schema, 2, 79228162514264337593543950335m);

        Dictionary<int, decimal?> read = ReadAll(ReadSnapshot(engine));

        Assert.Equal(decimal.MaxValue, read[0]);
        Assert.Equal(decimal.MinValue, read[1]);
    }

    [Fact]
    public void ANullDecimalReadsBackAsNull()
    {
        HollowObjectSchema schema = PriceSchema();
        HollowWriteStateEngine engine = Engine(schema);

        Add(engine, schema, 0, null);
        Add(engine, schema, 1, 0m);
        Add(engine, schema, 2, null);

        HollowObjectTypeReadState state =
            Assert.IsType<HollowObjectTypeReadState>(ReadSnapshot(engine).GetTypeState("Price"));
        int amount = state.Schema.GetPosition("amount");

        Assert.True(state.IsNull(0, amount));
        Assert.Null(state.ReadDecimal(0, amount));

        // Zero is a value, not a null.
        Assert.False(state.IsNull(1, amount));
        Assert.Equal(0m, state.ReadDecimal(1, amount));

        Assert.True(state.IsNull(2, amount));
    }

    /// <summary>
    /// Two records with the same field values are one record, which means the write record's serialised
    /// form has to be identical for identical decimals.
    /// </summary>
    [Fact]
    public void IdenticalDecimalsDeduplicate()
    {
        HollowObjectSchema schema = PriceSchema();
        HollowWriteStateEngine engine = Engine(schema);

        HollowObjectWriteRecord record = new(schema);
        record.SetInt("id", 1);
        record.SetDecimal("amount", 12.34m);
        record.SetString("label", "a");
        int first = engine.Add("Price", record);

        record.Reset();
        record.SetInt("id", 1);
        record.SetDecimal("amount", 12.34m);
        record.SetString("label", "a");
        int duplicate = engine.Add("Price", record);

        // A value is normalised before it is written, so a different scale is the same stored form
        // and therefore the same record.
        record.Reset();
        record.SetInt("id", 1);
        record.SetDecimal("amount", 12.340m);
        record.SetString("label", "a");
        int rescaled = engine.Add("Price", record);

        Assert.Equal(first, duplicate);
        Assert.Equal(first, rescaled);
    }

    /// <summary>
    /// A delta has to carry both halves of every decimal, both for the records it adds and for those it
    /// leaves in place.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void DecimalsSurviveADelta(int numShards)
    {
        HollowObjectSchema schema = PriceSchema();
        HollowWriteStateEngine engine = Engine(schema, numShards);

        for (int i = 0; i < InterestingValues.Length; i++)
        {
            Add(engine, schema, i, InterestingValues[i]);
        }

        HollowReadStateEngine consumer = ReadSnapshot(engine);

        engine.PrepareForNextCycle();
        engine.RandomizedTag = 2;

        // Keep the even records, drop the odd ones, and add some new ones -- including a null.
        for (int i = 0; i < InterestingValues.Length; i += 2)
        {
            Add(engine, schema, i, InterestingValues[i]);
        }

        Add(engine, schema, 100, -0.0000000000000000000000000001m);
        Add(engine, schema, 101, null);
        Add(engine, schema, 102, decimal.MaxValue);

        HollowReadStateEngine viaSnapshot = ReadSnapshot(engine);
        ApplyDelta(engine, consumer);

        Assert.Equal(ReadAll(viaSnapshot), ReadAll(consumer));
        Assert.Equal(decimal.MaxValue, ReadAll(consumer)[102]);
        Assert.Null(ReadAll(consumer)[101]);
    }

    /// <summary>
    /// Filtering rewrites the fixed-length bit string with the excluded fields removed, so a field
    /// spanning two elements has to be carried across whole — and the fields after it have to end up at
    /// the right offset.
    /// </summary>
    [Fact]
    public void ADecimalSurvivesFieldFiltering()
    {
        HollowObjectSchema schema = PriceSchema();
        HollowWriteStateEngine engine = Engine(schema);

        for (int i = 0; i < InterestingValues.Length; i++)
        {
            Add(engine, schema, i, InterestingValues[i], $"label-{i}");
        }

        // Drop the int field that precedes the decimal, so the decimal moves within the record.
        ITypeFilter filter = TypeFilter.Include(
            ["Price"],
            new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
            {
                ["Price"] = new HashSet<string>(StringComparer.Ordinal) { "amount", "label" },
            });

        HollowObjectTypeReadState state =
            Assert.IsType<HollowObjectTypeReadState>(ReadSnapshot(engine, filter).GetTypeState("Price"));

        Assert.Equal(2, state.Schema.FieldCount);

        int amount = state.Schema.GetPosition("amount");
        int label = state.Schema.GetPosition("label");

        for (int i = 0; i < InterestingValues.Length; i++)
        {
            Assert.Equal(InterestingValues[i], state.ReadDecimal(i, amount));
            Assert.Equal($"label-{i}", state.ReadString(i, label));
        }
    }

    /// <summary>
    /// And the other direction: excluding the decimal has to skip 128 bits, not 64, or every field
    /// after it in the record would be read from the wrong offset.
    /// </summary>
    [Fact]
    public void ExcludingADecimalLeavesTheOtherFieldsReadable()
    {
        HollowObjectSchema schema = PriceSchema();
        HollowWriteStateEngine engine = Engine(schema);

        for (int i = 0; i < InterestingValues.Length; i++)
        {
            Add(engine, schema, i, InterestingValues[i], $"label-{i}");
        }

        ITypeFilter filter = TypeFilter.Include(
            ["Price"],
            new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
            {
                ["Price"] = new HashSet<string>(StringComparer.Ordinal) { "id", "label" },
            });

        HollowObjectTypeReadState state =
            Assert.IsType<HollowObjectTypeReadState>(ReadSnapshot(engine, filter).GetTypeState("Price"));

        Assert.Equal(2, state.Schema.FieldCount);
        Assert.Equal(-1, state.Schema.GetPosition("amount"));

        int id = state.Schema.GetPosition("id");
        int label = state.Schema.GetPosition("label");

        for (int i = 0; i < InterestingValues.Length; i++)
        {
            Assert.Equal(i, state.ReadInt(i, id));
            Assert.Equal($"label-{i}", state.ReadString(i, label));
        }
    }

    /// <summary>
    /// A decimal can be a primary key field, which means the index has to hash and compare it. It also
    /// has to treat equal values with different scales as one key, because .NET does.
    /// </summary>
    [Fact]
    public void ADecimalCanBeAPrimaryKeyField()
    {
        HollowObjectSchema schema = PriceSchema(new PrimaryKey("Price", "amount"));
        HollowWriteStateEngine engine = Engine(schema);

        for (int i = 0; i < InterestingValues.Length; i++)
        {
            Add(engine, schema, i, InterestingValues[i]);
        }

        HollowReadStateEngine consumer = ReadSnapshot(engine);
        using HollowPrimaryKeyIndex index = new(consumer, "Price");

        HollowObjectTypeReadState state =
            Assert.IsType<HollowObjectTypeReadState>(consumer.GetTypeState("Price"));
        int id = state.Schema.GetPosition("id");

        for (int i = 0; i < InterestingValues.Length; i++)
        {
            int ordinal = index.GetMatchingOrdinal(InterestingValues[i]);
            Assert.NotEqual(HollowConstants.OrdinalNone, ordinal);
            Assert.Equal(i, state.ReadInt(ordinal, id));
        }

        Assert.Equal(HollowConstants.OrdinalNone, index.GetMatchingOrdinal(999.99m));

        // 1.50m was written; 1.5m is the same value and must find the same record.
        Assert.Equal(index.GetMatchingOrdinal(1.50m), index.GetMatchingOrdinal(1.5m));
        Assert.NotEqual(HollowConstants.OrdinalNone, index.GetMatchingOrdinal(1.5m));
    }

    /// <summary>
    /// A decimal can also be a set's declared hash key, which is the one place where the producer and
    /// the consumer have to agree on a hash computed from two entirely different representations: the
    /// producer works from the serialised record bytes, the consumer from a boxed decimal.
    /// </summary>
    [Fact]
    public void ADecimalCanBeASetsHashKey()
    {
        HollowObjectSchema schema = PriceSchema();
        HollowSetSchema setSchema = new("SetOfPrice", "Price", "amount");

        HollowWriteStateEngine engine = new() { RandomizedTag = 1 };
        engine.AddTypeState(new HollowObjectTypeWriteState(schema));
        engine.AddTypeState(new HollowSetTypeWriteState(setSchema));

        HollowSetWriteRecord set = new();
        HollowObjectWriteRecord record = new(schema);

        for (int i = 0; i < InterestingValues.Length; i++)
        {
            record.Reset();
            record.SetInt("id", i);
            record.SetDecimal("amount", InterestingValues[i]);
            record.SetString("label", $"price-{i}");
            set.AddElement(engine.Add("Price", record));
        }

        int setOrdinal = engine.Add("SetOfPrice", set);

        HollowReadStateEngine consumer = ReadSnapshot(engine);
        HollowSetTypeReadState sets = Assert.IsType<HollowSetTypeReadState>(consumer.GetTypeState("SetOfPrice"));
        HollowObjectTypeReadState prices =
            Assert.IsType<HollowObjectTypeReadState>(consumer.GetTypeState("Price"));
        int id = prices.Schema.GetPosition("id");

        for (int i = 0; i < InterestingValues.Length; i++)
        {
            int element = sets.FindElement(setOrdinal, InterestingValues[i]);
            Assert.NotEqual(HollowConstants.OrdinalNone, element);
            Assert.Equal(i, prices.ReadInt(element, id));
        }

        Assert.Equal(HollowConstants.OrdinalNone, sets.FindElement(setOrdinal, 999.99m));

        // And again, scale must not change where a value is looked for.
        Assert.Equal(sets.FindElement(setOrdinal, 1.50m), sets.FindElement(setOrdinal, 1.5m));
    }

    [Fact]
    public void FieldUtilitiesHandleDecimals()
    {
        HollowObjectSchema schema = PriceSchema();
        HollowWriteStateEngine engine = Engine(schema);

        Add(engine, schema, 0, 1.50m);
        Add(engine, schema, 1, 1.5m);
        Add(engine, schema, 2, 2m);
        Add(engine, schema, 3, null);

        HollowObjectTypeReadState state =
            Assert.IsType<HollowObjectTypeReadState>(ReadSnapshot(engine).GetTypeState("Price"));
        int amount = state.Schema.GetPosition("amount");

        Assert.Equal(1.50m, HollowReadFieldUtils.FieldValueObject(state, 0, amount));
        Assert.Null(HollowReadFieldUtils.FieldValueObject(state, 3, amount));

        // Equal values, different scales: equal, and equally hashed.
        Assert.True(HollowReadFieldUtils.FieldsAreEqual(state, 0, amount, state, 1, amount));
        Assert.Equal(
            HollowReadFieldUtils.FieldHashCode(state, 0, amount),
            HollowReadFieldUtils.FieldHashCode(state, 1, amount));

        Assert.False(HollowReadFieldUtils.FieldsAreEqual(state, 0, amount, state, 2, amount));

        Assert.True(HollowReadFieldUtils.FieldValueEquals(state, 0, amount, 1.5m));
        Assert.False(HollowReadFieldUtils.FieldValueEquals(state, 0, amount, 2m));
        Assert.True(HollowReadFieldUtils.FieldValueEquals(state, 3, amount, null));

        Assert.Equal("1.5", HollowReadFieldUtils.DisplayString(state, 0, amount));
        Assert.Equal(
            HollowReadFieldUtils.FieldHashCode(state, 0, amount),
            HollowReadFieldUtils.HashObject(1.5m));
    }

    /// <summary>
    /// Writing a decimal into a field of some other type is a schema mistake, and saying so beats
    /// producing a blob that reads back as nonsense.
    /// </summary>
    [Fact]
    public void WritingADecimalIntoANonDecimalFieldIsRejected()
    {
        HollowObjectSchema schema = PriceSchema();
        HollowObjectWriteRecord record = new(schema);

        ArgumentException e = Assert.Throws<ArgumentException>(() => record.SetDecimal("label", 1m));
        Assert.Contains("DECIMAL", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSchemaRoundTripsThroughTheBlob()
    {
        HollowObjectSchema schema = PriceSchema();
        HollowWriteStateEngine engine = Engine(schema);
        Add(engine, schema, 0, 1m);

        HollowObjectTypeReadState state =
            Assert.IsType<HollowObjectTypeReadState>(ReadSnapshot(engine).GetTypeState("Price"));

        Assert.Equal(FieldType.Decimal, state.Schema.GetFieldType(state.Schema.GetPosition("amount")));
        Assert.Equal(schema, state.Schema);

        // The textual form names the extension explicitly, so a schema dump says what it is.
        Assert.Contains("decimal amount;", schema.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A decimal field is 128 bits, unlike every other fixed-length field, which is the thing most
    /// likely to be assumed away by code that handles fields generically.
    /// </summary>
    [Fact]
    public void ADecimalFieldIsVariableLength()
    {
        HollowObjectSchema schema = PriceSchema();
        HollowWriteStateEngine engine = Engine(schema);

        Add(engine, schema, 0, 1m);
        Add(engine, schema, 1, decimal.MaxValue);

        HollowObjectTypeReadState state =
            Assert.IsType<HollowObjectTypeReadState>(ReadSnapshot(engine).GetTypeState("Price"));
        int amount = state.Schema.GetPosition("amount");

        Assert.Equal(-1, FieldType.Decimal.GetFixedLength());
        Assert.True(FieldType.Decimal.IsVariableLength());

        // The record holds a pointer into the byte store rather than the value, so the field is only as
        // wide as that store is long -- nothing like the 128 bits a decimal used to cost every record.
        Assert.True(state.BitsRequiredForField("amount") <= 64);

        // 1m is the single byte of form A; decimal.MaxValue needs all seventeen of form D.
        Assert.Equal(1, state.VarLengthFieldByteLength(0, amount));
        Assert.Equal(17, state.VarLengthFieldByteLength(1, amount));
    }

    private sealed class Money
    {
        public int Id { get; set; }

        public decimal Amount { get; set; }

        public decimal? Optional { get; set; }
    }

    /// <summary>
    /// A CLR decimal maps onto the field type without the caller having to say anything, which is the
    /// point of adding it.
    /// </summary>
    [Fact]
    public void TheObjectMapperMapsClrDecimals()
    {
        HollowWriteStateEngine engine = new();
        HollowObjectMapper mapper = new(engine);

        int ordinal = mapper.Add(new Money { Id = 1, Amount = 42.42m, Optional = null });
        mapper.Add(new Money { Id = 2, Amount = -0.01m, Optional = 7.7m });

        HollowReadStateEngine consumer = ReadSnapshotOf(engine);

        HollowObjectTypeReadState money =
            Assert.IsType<HollowObjectTypeReadState>(consumer.GetTypeState("Money"));

        int amount = money.Schema.GetPosition("Amount");
        Assert.Equal(FieldType.Decimal, money.Schema.GetFieldType(amount));
        Assert.Equal(42.42m, money.ReadDecimal(ordinal, amount));

        // A nullable decimal is a reference to a wrapper type, as other nullable scalars are.
        int optional = money.Schema.GetPosition("Optional");
        Assert.Equal(FieldType.Reference, money.Schema.GetFieldType(optional));
        Assert.Equal("Decimal", money.Schema.GetReferencedType(optional));
        Assert.Equal(HollowConstants.OrdinalNone, money.ReadOrdinal(ordinal, optional));

        HollowObjectTypeReadState wrapper =
            Assert.IsType<HollowObjectTypeReadState>(consumer.GetTypeState("Decimal"));
        Assert.Equal(
            7.7m,
            wrapper.ReadDecimal(
                money.ReadOrdinal(1, optional), wrapper.Schema.GetPosition("value")));
    }

    private static HollowReadStateEngine ReadSnapshotOf(HollowWriteStateEngine engine)
    {
        using MemoryStream stream = new();
        new HollowBlobWriter(engine).WriteSnapshot(stream);
        stream.Position = 0;

        HollowReadStateEngine readEngine = new();
        new HollowBlobReader(readEngine).ReadSnapshot(stream);
        return readEngine;
    }

    private static string Format(decimal? value) =>
        value?.ToString(null, System.Globalization.CultureInfo.InvariantCulture) ?? "null";
}
