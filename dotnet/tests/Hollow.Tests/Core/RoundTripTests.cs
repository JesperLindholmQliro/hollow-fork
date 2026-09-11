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
using Hollow.Core.Read.Filter;
using Hollow.Core.Schema;
using Hollow.Core.Write;

namespace Hollow.Tests.Core;

/// <summary>
/// Writes a snapshot with the write state engine and reads it back with the read state engine. This is
/// the test that actually exercises the blob format: the bit packing, the variable-length ranges, the
/// null sentinels and the shard layout all have to agree for a record to survive the trip.
/// </summary>
public class RoundTripTests
{
    private static HollowObjectSchema MovieSchema()
    {
        HollowObjectSchema schema = new("Movie", 8);
        schema.AddField("id", FieldType.Int);
        schema.AddField("boxOffice", FieldType.Long);
        schema.AddField("rating", FieldType.Float);
        schema.AddField("score", FieldType.Double);
        schema.AddField("released", FieldType.Boolean);
        schema.AddField("title", FieldType.String);
        schema.AddField("poster", FieldType.Bytes);
        schema.AddField("country", FieldType.Reference, "Country");
        return schema;
    }

    private static HollowReadStateEngine RoundTrip(HollowWriteStateEngine writeEngine, ITypeFilter? filter = null)
    {
        using MemoryStream stream = new();

        new HollowBlobWriter(writeEngine).WriteSnapshot(stream);

        stream.Position = 0;
        HollowReadStateEngine readEngine = new();
        new HollowBlobReader(readEngine).ReadSnapshot(stream, filter);

        return readEngine;
    }

    [Fact]
    public void EveryFieldTypeSurvivesTheRoundTrip()
    {
        HollowObjectSchema schema = MovieSchema();
        HollowWriteStateEngine writeEngine = new();
        writeEngine.AddTypeState(new HollowObjectTypeWriteState(schema));

        HollowObjectWriteRecord record = new(schema);
        record.SetInt("id", 42);
        record.SetLong("boxOffice", 1_234_567_890_123L);
        record.SetFloat("rating", 8.5f);
        record.SetDouble("score", 0.123456789);
        record.SetBoolean("released", true);
        record.SetString("title", "The Matrix");
        record.SetBytes("poster", [1, 2, 3, 250, 255]);
        record.SetReference("country", 7);

        int ordinal = writeEngine.Add("Movie", record);

        HollowReadStateEngine readEngine = RoundTrip(writeEngine);
        HollowObjectTypeReadState readState = Assert.IsType<HollowObjectTypeReadState>(readEngine.GetTypeState("Movie"));

        Assert.Equal(schema, readState.Schema);
        Assert.Equal(ordinal, readState.MaxOrdinal);
        Assert.Equal([ordinal], readState.PopulatedOrdinals.EnumerateSetBits());

        Assert.Equal(42, readState.ReadInt(ordinal, schema.GetPosition("id")));
        Assert.Equal(1_234_567_890_123L, readState.ReadLong(ordinal, schema.GetPosition("boxOffice")));
        Assert.Equal(8.5f, readState.ReadFloat(ordinal, schema.GetPosition("rating")));
        Assert.Equal(0.123456789, readState.ReadDouble(ordinal, schema.GetPosition("score")));
        Assert.True(readState.ReadBoolean(ordinal, schema.GetPosition("released")));
        Assert.Equal("The Matrix", readState.ReadString(ordinal, schema.GetPosition("title")));
        Assert.Equal([1, 2, 3, 250, 255], readState.ReadBytes(ordinal, schema.GetPosition("poster")));
        Assert.Equal(7, readState.ReadOrdinal(ordinal, schema.GetPosition("country")));
    }

    [Fact]
    public void NullsSurviveTheRoundTrip()
    {
        HollowObjectSchema schema = MovieSchema();
        HollowWriteStateEngine writeEngine = new();
        writeEngine.AddTypeState(new HollowObjectTypeWriteState(schema));

        // A record with nothing set at all: every field should read back as null.
        HollowObjectWriteRecord record = new(schema);
        int ordinal = writeEngine.Add("Movie", record);

        HollowReadStateEngine readEngine = RoundTrip(writeEngine);
        HollowObjectTypeReadState readState = Assert.IsType<HollowObjectTypeReadState>(readEngine.GetTypeState("Movie"));

        for (int fieldIndex = 0; fieldIndex < schema.FieldCount; fieldIndex++)
        {
            Assert.True(
                readState.IsNull(ordinal, fieldIndex),
                $"field {schema.GetFieldName(fieldIndex)} should be null");
        }

        Assert.Equal(int.MinValue, readState.ReadInt(ordinal, schema.GetPosition("id")));
        Assert.Equal(long.MinValue, readState.ReadLong(ordinal, schema.GetPosition("boxOffice")));
        Assert.True(float.IsNaN(readState.ReadFloat(ordinal, schema.GetPosition("rating"))));
        Assert.True(double.IsNaN(readState.ReadDouble(ordinal, schema.GetPosition("score"))));
        Assert.Null(readState.ReadBoolean(ordinal, schema.GetPosition("released")));
        Assert.Null(readState.ReadString(ordinal, schema.GetPosition("title")));
        Assert.Null(readState.ReadBytes(ordinal, schema.GetPosition("poster")));
        Assert.Equal(-1, readState.ReadOrdinal(ordinal, schema.GetPosition("country")));
    }

    /// <summary>
    /// Marking a string field null explicitly must read back as null, not as a one-byte value. This is
    /// the case Java's <c>setNull</c> gets wrong for variable-length fields.
    /// </summary>
    [Fact]
    public void ExplicitlyNulledStringReadsBackAsNull()
    {
        HollowObjectSchema schema = new("Movie", 2);
        schema.AddField("id", FieldType.Int);
        schema.AddField("title", FieldType.String);

        HollowWriteStateEngine writeEngine = new();
        writeEngine.AddTypeState(new HollowObjectTypeWriteState(schema));

        HollowObjectWriteRecord record = new(schema);
        record.SetInt("id", 1);
        record.SetString("title", "something");
        record.SetNull("title");

        int ordinal = writeEngine.Add("Movie", record);

        HollowReadStateEngine readEngine = RoundTrip(writeEngine);
        HollowObjectTypeReadState readState = Assert.IsType<HollowObjectTypeReadState>(readEngine.GetTypeState("Movie"));

        Assert.True(readState.IsNull(ordinal, schema.GetPosition("title")));
        Assert.Null(readState.ReadString(ordinal, schema.GetPosition("title")));
        Assert.Equal(1, readState.ReadInt(ordinal, schema.GetPosition("id")));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void ManyRecordsSurviveTheRoundTripAcrossShards(int numShards)
    {
        HollowObjectSchema schema = new("Movie", 3);
        schema.AddField("id", FieldType.Int);
        schema.AddField("title", FieldType.String);
        schema.AddField("boxOffice", FieldType.Long);

        HollowWriteStateEngine writeEngine = new();
        writeEngine.AddTypeState(new HollowObjectTypeWriteState(schema, numShards));

        const int RecordCount = 500;
        Dictionary<int, int> ordinalsById = [];

        HollowObjectWriteRecord record = new(schema);
        for (int i = 0; i < RecordCount; i++)
        {
            record.Reset();
            record.SetInt("id", i);
            record.SetString("title", $"movie-{i}");
            record.SetLong("boxOffice", (long)i * 1_000_000_007L);
            ordinalsById[i] = writeEngine.Add("Movie", record);
        }

        HollowReadStateEngine readEngine = RoundTrip(writeEngine);
        HollowObjectTypeReadState readState = Assert.IsType<HollowObjectTypeReadState>(readEngine.GetTypeState("Movie"));

        Assert.Equal(RecordCount, readState.PopulatedOrdinals.Cardinality());

        foreach ((int id, int ordinal) in ordinalsById)
        {
            Assert.Equal(id, readState.ReadInt(ordinal, schema.GetPosition("id")));
            Assert.Equal($"movie-{id}", readState.ReadString(ordinal, schema.GetPosition("title")));
            Assert.Equal((long)id * 1_000_000_007L, readState.ReadLong(ordinal, schema.GetPosition("boxOffice")));
        }
    }

    [Fact]
    public void IdenticalRecordsShareAnOrdinal()
    {
        HollowObjectSchema schema = new("Movie", 1);
        schema.AddField("id", FieldType.Int);

        HollowWriteStateEngine writeEngine = new();
        writeEngine.AddTypeState(new HollowObjectTypeWriteState(schema));

        HollowObjectWriteRecord record = new(schema);
        record.SetInt("id", 1);
        int first = writeEngine.Add("Movie", record);

        record.Reset();
        record.SetInt("id", 1);
        int duplicate = writeEngine.Add("Movie", record);

        record.Reset();
        record.SetInt("id", 2);
        int other = writeEngine.Add("Movie", record);

        Assert.Equal(first, duplicate);
        Assert.NotEqual(first, other);

        HollowReadStateEngine readEngine = RoundTrip(writeEngine);
        HollowObjectTypeReadState readState = Assert.IsType<HollowObjectTypeReadState>(readEngine.GetTypeState("Movie"));

        Assert.Equal(2, readState.PopulatedOrdinals.Cardinality());
    }

    [Fact]
    public void MultipleTypesSurviveTheRoundTripAndReferencesAreWired()
    {
        HollowObjectSchema countrySchema = new("Country", 1);
        countrySchema.AddField("name", FieldType.String);

        HollowObjectSchema movieSchema = new("Movie", 2);
        movieSchema.AddField("title", FieldType.String);
        movieSchema.AddField("country", FieldType.Reference, "Country");

        HollowWriteStateEngine writeEngine = new();
        writeEngine.AddTypeState(new HollowObjectTypeWriteState(countrySchema));
        writeEngine.AddTypeState(new HollowObjectTypeWriteState(movieSchema));

        HollowObjectWriteRecord country = new(countrySchema);
        country.SetString("name", "Sweden");
        int countryOrdinal = writeEngine.Add("Country", country);

        HollowObjectWriteRecord movie = new(movieSchema);
        movie.SetString("title", "Persona");
        movie.SetReference("country", countryOrdinal);
        int movieOrdinal = writeEngine.Add("Movie", movie);

        HollowReadStateEngine readEngine = RoundTrip(writeEngine);

        HollowObjectTypeReadState movieState = Assert.IsType<HollowObjectTypeReadState>(readEngine.GetTypeState("Movie"));
        HollowObjectTypeReadState countryState = Assert.IsType<HollowObjectTypeReadState>(readEngine.GetTypeState("Country"));

        int referencedOrdinal = movieState.ReadOrdinal(movieOrdinal, movieSchema.GetPosition("country"));
        Assert.Equal(countryOrdinal, referencedOrdinal);
        Assert.Equal("Sweden", countryState.ReadString(referencedOrdinal, countrySchema.GetPosition("name")));

        // The reference field's schema should have been wired to the referenced type's read state.
        Assert.Same(
            countryState,
            movieState.Schema.GetReferencedTypeState(movieSchema.GetPosition("country")));
    }

    [Fact]
    public void HeaderTagsSurviveTheRoundTrip()
    {
        HollowObjectSchema schema = new("Movie", 1);
        schema.AddField("id", FieldType.Int);

        HollowWriteStateEngine writeEngine = new();
        writeEngine.AddTypeState(new HollowObjectTypeWriteState(schema));
        writeEngine.AddHeaderTag("source.version", "2026-09-11");
        writeEngine.AddHeaderTag("producer", "hollow-dotnet");

        HollowObjectWriteRecord record = new(schema);
        record.SetInt("id", 1);
        writeEngine.Add("Movie", record);

        HollowReadStateEngine readEngine = RoundTrip(writeEngine);

        Assert.Equal("2026-09-11", readEngine.HeaderTags["source.version"]);
        Assert.Equal("hollow-dotnet", readEngine.HeaderTags["producer"]);
    }

    [Fact]
    public void FilteredFieldsAreDroppedOnRead()
    {
        HollowObjectSchema schema = new("Movie", 3);
        schema.AddField("id", FieldType.Int);
        schema.AddField("title", FieldType.String);
        schema.AddField("boxOffice", FieldType.Long);

        HollowWriteStateEngine writeEngine = new();
        writeEngine.AddTypeState(new HollowObjectTypeWriteState(schema));

        HollowObjectWriteRecord record = new(schema);
        record.SetInt("id", 7);
        record.SetString("title", "Solaris");
        record.SetLong("boxOffice", 99L);
        int ordinal = writeEngine.Add("Movie", record);

        ITypeFilter filter = TypeFilter.Include(
            ["Movie"],
            new Dictionary<string, IReadOnlySet<string>> { ["Movie"] = new HashSet<string> { "id", "boxOffice" } });

        HollowReadStateEngine readEngine = RoundTrip(writeEngine, filter);
        HollowObjectTypeReadState readState = Assert.IsType<HollowObjectTypeReadState>(readEngine.GetTypeState("Movie"));

        Assert.Equal(2, readState.Schema.FieldCount);
        Assert.Equal(-1, readState.Schema.GetPosition("title"));

        // The retained fields must still decode correctly after the excluded one was squeezed out.
        Assert.Equal(7, readState.ReadInt(ordinal, readState.Schema.GetPosition("id")));
        Assert.Equal(99L, readState.ReadLong(ordinal, readState.Schema.GetPosition("boxOffice")));
    }

    [Fact]
    public void StringComparisonAndHashingMatchTheStoredValue()
    {
        HollowObjectSchema schema = new("Movie", 1);
        schema.AddField("title", FieldType.String);

        HollowWriteStateEngine writeEngine = new();
        writeEngine.AddTypeState(new HollowObjectTypeWriteState(schema));

        HollowObjectWriteRecord record = new(schema);
        record.SetString("title", "Låt den rätte komma in");
        int ordinal = writeEngine.Add("Movie", record);

        HollowReadStateEngine readEngine = RoundTrip(writeEngine);
        HollowObjectTypeReadState readState = Assert.IsType<HollowObjectTypeReadState>(readEngine.GetTypeState("Movie"));

        int fieldIndex = schema.GetPosition("title");

        Assert.Equal("Låt den rätte komma in", readState.ReadString(ordinal, fieldIndex));
        Assert.True(readState.IsStringFieldEqual(ordinal, fieldIndex, "Låt den rätte komma in"));
        Assert.False(readState.IsStringFieldEqual(ordinal, fieldIndex, "Låt den rätte komma i"));
        Assert.False(readState.IsStringFieldEqual(ordinal, fieldIndex, null));

        Assert.Equal(
            Hollow.Core.Memory.Encoding.HashCodes.Compute("Låt den rätte komma in"),
            readState.FindVarLengthFieldHashCode(ordinal, fieldIndex));
    }

    [Fact]
    public void EmptyTypeSurvivesTheRoundTrip()
    {
        HollowObjectSchema schema = new("Movie", 1);
        schema.AddField("id", FieldType.Int);

        HollowWriteStateEngine writeEngine = new();
        writeEngine.AddTypeState(new HollowObjectTypeWriteState(schema));

        HollowReadStateEngine readEngine = RoundTrip(writeEngine);
        HollowObjectTypeReadState readState = Assert.IsType<HollowObjectTypeReadState>(readEngine.GetTypeState("Movie"));

        Assert.Equal(-1, readState.MaxOrdinal);
        Assert.Equal(0, readState.PopulatedOrdinals.Cardinality());
    }

    [Fact]
    public void OrdinalsAreReusedAcrossCyclesWhenRecordsAreDropped()
    {
        HollowObjectSchema schema = new("Movie", 1);
        schema.AddField("id", FieldType.Int);

        HollowWriteStateEngine writeEngine = new();
        writeEngine.AddTypeState(new HollowObjectTypeWriteState(schema));

        HollowObjectWriteRecord record = new(schema);
        for (int i = 0; i < 3; i++)
        {
            record.Reset();
            record.SetInt("id", i);
            writeEngine.Add("Movie", record);
        }

        Assert.Equal(3, RoundTrip(writeEngine).GetTypeState("Movie")!.PopulatedOrdinals.Cardinality());

        // Second cycle keeps only one of the three records; the other two ordinals become free.
        writeEngine.PrepareForNextCycle();
        record.Reset();
        record.SetInt("id", 1);
        int keptOrdinal = writeEngine.Add("Movie", record);

        HollowReadStateEngine readEngine = RoundTrip(writeEngine);
        HollowObjectTypeReadState readState = Assert.IsType<HollowObjectTypeReadState>(readEngine.GetTypeState("Movie"));

        Assert.Equal([keptOrdinal], readState.PopulatedOrdinals.EnumerateSetBits());
        Assert.Equal(1, readState.ReadInt(keptOrdinal, schema.GetPosition("id")));
    }
}
