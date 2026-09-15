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
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;
using Hollow.Core.Write.ObjectMapper.FlatRecords;

namespace Hollow.Tests.Core.Write.FlatRecords;

/// <summary>
/// Moves a record from one dataset to another as a flat record, which is what the extractor and the
/// dumper exist for.
/// </summary>
/// <remarks>
/// Ported from <c>FlatRecordExtractorTests</c> and <c>FlatRecordDumperTests</c>, which check the same
/// round trip. Java supplies a fake schema identifier mapper; this port has a real one and uses it, so
/// each test has to build the destination's mapper over the destination's own schemas.
/// </remarks>
public class FlatRecordTransferTests
{
    [Fact]
    public void ARecordLeavesOneDatasetAndArrivesInAnother()
    {
        HollowReadStateEngine source = Catalogue(
            new Movie { Id = 1, Title = "Heat", Cast = ["Pacino", "De Niro"] });

        HollowWriteStateEngine destination = Empty();
        int ordinal = new FlatRecordDumper(destination).Dump(Extract(source, destination, "Movie", 0));

        HollowReadStateEngine arrived = RoundTrip(destination);
        HollowObjectTypeReadState movies = (HollowObjectTypeReadState)arrived.GetTypeState("Movie")!;

        Assert.Equal(1, movies.ReadInt(ordinal, movies.Schema.GetPosition("Id")));
        Assert.Equal("Heat", ReadString(arrived, movies, ordinal, "Title"));
    }

    [Fact]
    public void OnlyWhatTheRecordReachesTravels()
    {
        HollowReadStateEngine source = Catalogue(
            new Movie { Id = 1, Title = "Heat" },
            new Movie { Id = 2, Title = "Ronin" });

        FlatRecord record = Extract(source, Empty(), "Movie", OrdinalOf(source, 2));

        FlatRecordOrdinalReader reader = new(record);

        // The other film, and its title, are in the same dataset and are no part of this record.
        Assert.DoesNotContain(
            "Heat",
            Enumerable.Range(0, reader.OrdinalCount)
                .Where(i => reader.ReadSchema(i).Name == "String")
                .Select(i => reader.ReadFieldString(i, "value")));
    }

    [Fact]
    public void TheKeyTravelsWithIt()
    {
        HollowReadStateEngine source = Catalogue(new Movie { Id = 7, Title = "Ronin" });

        FlatRecord record = Extract(source, Empty(), "Movie", 0);

        // Keyed without a model class at the far end: the writer noted where the key's fields sit.
        Assert.Equal("Movie", record.RecordPrimaryKey!.Type);
        Assert.Equal([7], record.RecordPrimaryKey.Key);
    }

    [Fact]
    public void TheBytesAreTheWholeOfIt()
    {
        HollowReadStateEngine source = Catalogue(
            new Movie
            {
                Id = 1,
                Title = "Heat",
                Cast = ["Pacino"],
                Genres = ["Crime"],
                Ratings = new Dictionary<string, int> { ["imdb"] = 81 },
            });

        HollowWriteStateEngine destination = Empty();
        FlatRecord extracted = Extract(source, destination, "Movie", 0);

        // Through a byte array and back, as it would cross a wire: nothing of the source dataset comes
        // with it but the schema identifiers, which both ends agree on because both models match.
        FlatRecord received = new(
            new Hollow.Core.Memory.ArrayByteData(extracted.ToArray()),
            new HollowDatasetSchemaIdentifierMapper(destination));

        int ordinal = new FlatRecordDumper(destination).Dump(received);

        HollowReadStateEngine arrived = RoundTrip(destination);
        HollowObjectTypeReadState movies = (HollowObjectTypeReadState)arrived.GetTypeState("Movie")!;

        Assert.Equal("Heat", ReadString(arrived, movies, ordinal, "Title"));
        Assert.Equal(1, arrived.GetTypeState("SetOfString")!.PopulatedOrdinals.Cardinality());
        Assert.Equal(1, arrived.GetTypeState("MapOfStringToInteger")!.PopulatedOrdinals.Cardinality());
    }

    [Fact]
    public void AFieldTheDestinationDoesNotHaveIsDropped()
    {
        // Two models of one type: the record is written against the wider one and dumped into the
        // narrower, which is a producer that has grown a field ahead of a consumer.
        HollowObjectSchema wide = new("Movie", 2, "Id");
        wide.AddField("Id", FieldType.Int);
        wide.AddField("Year", FieldType.Int);

        HollowObjectSchema narrow = new("Movie", 1, "Id");
        narrow.AddField("Id", FieldType.Int);

        HollowWriteStateEngine wider = new();
        wider.AddTypeState(new HollowObjectTypeWriteState(wide));

        HollowObjectWriteRecord movie = new(wide);
        movie.SetInt("Id", 1);
        movie.SetInt("Year", 1995);

        HollowWriteStateEngine narrower = new();
        narrower.AddTypeState(new HollowObjectTypeWriteState(narrow));

        FlatRecordWriter writer = new(wider, new HollowDatasetSchemaIdentifierMapper(wider));
        writer.Write(wide, movie);

        // The record is read back through the writer's mapper, not the destination's: a schema
        // identifier names the schema the record was *written* against, and has to, or the reader
        // would lose its place in the bytes at the first field the two ends disagree about. What the
        // destination declares decides only what is kept.
        FlatRecord record = new(
            new Hollow.Core.Memory.ArrayByteData(writer.GenerateFlatRecord().ToArray()),
            new HollowDatasetSchemaIdentifierMapper(wider));

        int ordinal = new FlatRecordDumper(narrower).Dump(record);

        HollowReadStateEngine arrived = RoundTrip(narrower);
        HollowObjectTypeReadState movies = (HollowObjectTypeReadState)arrived.GetTypeState("Movie")!;

        Assert.Equal(1, movies.Schema.FieldCount);
        Assert.Equal(1, movies.ReadInt(ordinal, 0));
    }

    [Fact]
    public void AReferenceWithNothingToPointAtIsRefused()
    {
        // A record whose Title is a reference to a String, dumped into an engine that has Movie but no
        // String. Dropping the field would leave a reference pointing at nothing, so it fails instead.
        HollowObjectSchema stringSchema = new("String", 1);
        stringSchema.AddField("value", FieldType.String);

        HollowObjectSchema movieSchema = new("Movie", 2, "Id");
        movieSchema.AddField("Id", FieldType.Int);
        movieSchema.AddField("Title", FieldType.Reference, "String");

        HollowWriteStateEngine complete = new();
        complete.AddTypeState(new HollowObjectTypeWriteState(stringSchema));
        complete.AddTypeState(new HollowObjectTypeWriteState(movieSchema));

        HollowObjectWriteRecord title = new(stringSchema);
        title.SetString("value", "Heat");

        FlatRecordWriter writer = new(complete, new HollowDatasetSchemaIdentifierMapper(complete));

        HollowObjectWriteRecord movie = new(movieSchema);
        movie.SetInt("Id", 1);
        movie.SetReference("Title", writer.Write(stringSchema, title));
        writer.Write(movieSchema, movie);

        HollowWriteStateEngine withoutStrings = new();
        withoutStrings.AddTypeState(new HollowObjectTypeWriteState(stringSchema));
        withoutStrings.AddTypeState(new HollowObjectTypeWriteState(movieSchema));

        FlatRecord record = new(
            new Hollow.Core.Memory.ArrayByteData(writer.GenerateFlatRecord().ToArray()),
            new HollowDatasetSchemaIdentifierMapper(withoutStrings));

        // The mapper still knows both schemas, so the record decodes; the engine dumped into does not
        // have the String type, so the record the reference points at goes nowhere.
        HollowWriteStateEngine destination = new();
        destination.AddTypeState(new HollowObjectTypeWriteState(movieSchema));

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => new FlatRecordDumper(destination).Dump(record));

        Assert.Contains("reference", failure.Message, StringComparison.Ordinal);

        // And the message carries the record as this end decoded it, because a schema-identifier
        // disagreement is otherwise undiagnosable.
        Assert.Contains("As decoded at this end:", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFlatRecordCanBeReadByAPerson()
    {
        HollowReadStateEngine source = Catalogue(
            new Movie { Id = 1, Title = "Heat", Cast = ["Pacino", "De Niro"] });

        string text = new FlatRecordStringifier().Stringify(Extract(source, Empty(), "Movie", 0));

        Assert.Contains("(Movie)", text, StringComparison.Ordinal);
        Assert.Contains("Id: 1", text, StringComparison.Ordinal);

        // A one-field wrapper prints as its value rather than as a record holding a field.
        Assert.Contains("Title: Heat", text, StringComparison.Ordinal);
        Assert.Contains("e0: Pacino", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExcludedTypePrintsAsNothing()
    {
        HollowReadStateEngine source = Catalogue(new Movie { Id = 1, Title = "Heat" });

        string text = new FlatRecordStringifier()
            .ExcludeObjectTypes("String")
            .Stringify(Extract(source, Empty(), "Movie", 0));

        Assert.Contains("Id: 1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Heat", text, StringComparison.Ordinal);
    }

    private static FlatRecord Extract(
        HollowReadStateEngine source, HollowWriteStateEngine destination, string type, int ordinal) =>
        new FlatRecordExtractor(source, new HollowDatasetSchemaIdentifierMapper(destination))
            .Extract(type, ordinal);

    /// <summary>A populated read state holding the given films.</summary>
    private static HollowReadStateEngine Catalogue(params Movie[] films)
    {
        HollowWriteStateEngine engine = Empty();
        HollowObjectMapper mapper = new(engine);

        foreach (Movie film in films)
        {
            mapper.Add(film);
        }

        return RoundTrip(engine);
    }

    /// <summary>A write state that knows the model but holds nothing.</summary>
    private static HollowWriteStateEngine Empty()
    {
        HollowWriteStateEngine engine = new();
        new HollowObjectMapper(engine).InitializeTypeState<Movie>();

        return engine;
    }

    private static HollowReadStateEngine RoundTrip(HollowWriteStateEngine writeEngine)
    {
        using MemoryStream stream = new();
        new HollowBlobWriter(writeEngine).WriteSnapshot(stream);

        stream.Position = 0;
        HollowReadStateEngine readEngine = new();
        new HollowBlobReader(readEngine).ReadSnapshot(stream);

        return readEngine;
    }

    /// <summary>The ordinal of the film with the given identifier.</summary>
    private static int OrdinalOf(HollowReadStateEngine engine, int id)
    {
        HollowObjectTypeReadState movies = (HollowObjectTypeReadState)engine.GetTypeState("Movie")!;
        int field = movies.Schema.GetPosition("Id");

        return movies.PopulatedOrdinals.EnumerateSetBits().First(ordinal => movies.ReadInt(ordinal, field) == id);
    }

    /// <summary>Follows a reference to a String record and reads its value.</summary>
    private static string? ReadString(
        HollowReadStateEngine engine, HollowObjectTypeReadState state, int ordinal, string field)
    {
        int referenced = state.ReadOrdinal(ordinal, state.Schema.GetPosition(field));
        HollowObjectTypeReadState strings = (HollowObjectTypeReadState)engine.GetTypeState("String")!;

        return referenced == -1 ? null : strings.ReadString(referenced, 0);
    }

    [HollowPrimaryKey("Id")]
    private sealed class Movie
    {
        public int Id { get; init; }

        public string? Title { get; init; }

        public List<string>? Cast { get; init; }

        public HashSet<string>? Genres { get; init; }

        public Dictionary<string, int>? Ratings { get; init; }
    }
}
