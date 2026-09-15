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

using Hollow.Core.Memory;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;
using Hollow.Core.Write.ObjectMapper.FlatRecords;

namespace Hollow.Tests.Core.Write.FlatRecords;

/// <summary>
/// Takes a CLR object to a flat record and back, which is the whole point of the format from a
/// caller's side: everything in between is bytes.
/// </summary>
/// <remarks>
/// Ported from <c>HollowObjectMapperFlatRecordTest</c>. Java's version reflects fields straight back
/// onto the object and lets the JVM's widening rules make them fit; .NET will not assign an
/// <c>int</c> to a <c>short</c> member, so these check the narrower types too.
/// </remarks>
public class FlatRecordMappingTests
{
    [Fact]
    public void AnObjectGoesOutAndComesBack()
    {
        Movie original = new()
        {
            Id = 1,
            Title = "Heat",
            Cast = ["Pacino", "De Niro"],
            Genres = ["Crime", "Drama"],
            Ratings = new Dictionary<string, int> { ["imdb"] = 81, ["rt"] = 86 },
        };

        Movie read = RoundTrip(original);

        Assert.Equal(1, read.Id);
        Assert.Equal("Heat", read.Title);
        Assert.Equal(["Pacino", "De Niro"], read.Cast);
        Assert.Equal(["Crime", "Drama"], read.Genres!.Order());
        Assert.Equal(81, read.Ratings!["imdb"]);
        Assert.Equal(86, read.Ratings["rt"]);
    }

    [Fact]
    public void ANullMemberStaysNull()
    {
        Movie read = RoundTrip(new Movie { Id = 2 });

        Assert.Equal(2, read.Id);
        Assert.Null(read.Title);
        Assert.Null(read.Cast);
        Assert.Null(read.Genres);
        Assert.Null(read.Ratings);
    }

    [Fact]
    public void AnEmptyCollectionIsNotTheSameAsAMissingOne()
    {
        Movie read = RoundTrip(new Movie { Id = 3, Cast = [] });

        Assert.NotNull(read.Cast);
        Assert.Empty(read.Cast);
    }

    [Fact]
    public void EveryScalarTypeSurvivesTheRoundTrip()
    {
        Everything original = new()
        {
            Narrow = -7,
            Wide = long.MaxValue / 3,
            Unsigned = 4_000_000_000,
            Single = 1.5f,
            Wider = -2.25d,
            Exact = 12.3456m,
            Flag = true,
            Letter = 'ሾ',
            Text = "a string with a ሾ in it",
            Raw = [1, 2, 3, 250],
            Rating = Certificate.Fifteen,
            Maybe = 42,
        };

        Everything read = RoundTrip(original);

        // A short and a uint both go out as int fields and have to be narrowed back on the way in.
        Assert.Equal((short)-7, read.Narrow);
        Assert.Equal(long.MaxValue / 3, read.Wide);

        // Larger than int.MaxValue, so it travels by its bits rather than by its value.
        Assert.Equal(4_000_000_000u, read.Unsigned);

        Assert.Equal(1.5f, read.Single);
        Assert.Equal(-2.25d, read.Wider);
        Assert.Equal(12.3456m, read.Exact);
        Assert.True(read.Flag);
        Assert.Equal('ሾ', read.Letter);
        Assert.Equal("a string with a ሾ in it", read.Text);
        Assert.Equal<byte[]>([1, 2, 3, 250], read.Raw!);

        // An enum travels as its member name, so renumbering the enum cannot silently change it.
        Assert.Equal(Certificate.Fifteen, read.Rating);

        Assert.Equal(42, read.Maybe);
    }

    [Fact]
    public void TheOneUnsignedValueThatCannotBeToldFromNullIsRefused()
    {
        // 2147483648 has the bits of int.MinValue, which is how the format says "null int". Storing it
        // would read back as null, so it fails instead of quietly losing the value.
        HollowWriteStateEngine engine = Engine<Everything>();

        HollowMappingException failure = Assert.Throws<HollowMappingException>(
            () => new HollowObjectMapper(engine).WriteFlat(
                new Everything { Unsigned = 2_147_483_648 },
                new HollowDatasetSchemaIdentifierMapper(engine)));

        Assert.Contains("null int", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnImmutableRecordIsBuiltThroughItsConstructor()
    {
        // Nothing on this type is settable, so the only way back in is the primary constructor.
        Screening read = RoundTrip(new Screening(7, "Heat", new Venue("Odeon")));

        Assert.Equal(7, read.Id);
        Assert.Equal("Heat", read.Title);
        Assert.Equal("Odeon", read.Venue.Name);
    }

    [Fact]
    public void TheRecordCarriesOnlyWhatTheObjectReaches()
    {
        HollowWriteStateEngine engine = Engine<Movie>();
        HollowObjectMapper mapper = new(engine);

        // Two films through one mapper, but each flat record is its own: one film, one title.
        mapper.Add(new Movie { Id = 1, Title = "Heat" });

        FlatRecord record = mapper.WriteFlat(
            new Movie { Id = 2, Title = "Ronin" }, new HollowDatasetSchemaIdentifierMapper(engine));

        FlatRecordOrdinalReader reader = new(record);

        Assert.DoesNotContain(
            "Heat",
            Enumerable.Range(0, reader.OrdinalCount)
                .Where(i => reader.ReadSchema(i).Name == "String")
                .Select(i => reader.ReadFieldString(i, "value")));
    }

    [Fact]
    public void TheEngineIsLeftAlone()
    {
        HollowWriteStateEngine engine = Engine<Movie>();

        new HollowObjectMapper(engine).WriteFlat(
            new Movie { Id = 1, Title = "Heat" }, new HollowDatasetSchemaIdentifierMapper(engine));

        // A flat record is written beside the dataset, not into it.
        Assert.False(engine.HasChangedSinceLastCycle());
    }

    [Fact]
    public void ReadingARecordAsTheWrongTypeIsRefused()
    {
        HollowWriteStateEngine engine = Engine<Movie>();
        HollowObjectMapper mapper = new(engine);

        FlatRecord record = mapper.WriteFlat(
            new Movie { Id = 1, Title = "Heat" }, new HollowDatasetSchemaIdentifierMapper(engine));

        // Left to itself this would answer with a Venue whose every field is null, because the fields
        // it looks for are simply not there.
        HollowMappingException failure =
            Assert.Throws<HollowMappingException>(() => mapper.ReadFlat<Venue>(record));

        Assert.Contains("Movie", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Writes an object out as bytes and reads it back as a new one.</summary>
    private static T RoundTrip<T>(T value)
        where T : notnull
    {
        HollowWriteStateEngine engine = Engine<T>();

        FlatRecord written = new HollowObjectMapper(engine).WriteFlat(
            value, new HollowDatasetSchemaIdentifierMapper(engine));

        // Through a byte array, as it would cross a wire. The far end is a second mapper over a second
        // engine: nothing of the first survives but the bytes and the model.
        HollowWriteStateEngine far = Engine<T>();

        return new HollowObjectMapper(far).ReadFlat<T>(
            new FlatRecord(
                new ArrayByteData(written.ToArray()), new HollowDatasetSchemaIdentifierMapper(far)))!;
    }

    private static HollowWriteStateEngine Engine<T>()
    {
        HollowWriteStateEngine engine = new();
        new HollowObjectMapper(engine).InitializeTypeState<T>();

        return engine;
    }

    private enum Certificate
    {
        Universal,
        Twelve,
        Fifteen,
        Eighteen,
    }

    [HollowPrimaryKey("Id")]
    private sealed class Movie
    {
        public int Id { get; set; }

        public string? Title { get; set; }

        public List<string>? Cast { get; set; }

        public HashSet<string>? Genres { get; set; }

        public Dictionary<string, int>? Ratings { get; set; }
    }

    private sealed class Everything
    {
        public short Narrow { get; set; }

        public long Wide { get; set; }

        public uint Unsigned { get; set; }

        public float Single { get; set; }

        public double Wider { get; set; }

        public decimal Exact { get; set; }

        public bool Flag { get; set; }

        public char Letter { get; set; }

        public string? Text { get; set; }

        public byte[]? Raw { get; set; }

        public Certificate Rating { get; set; }

        public int? Maybe { get; set; }
    }

    private sealed record Venue(string Name);

    [HollowPrimaryKey("Id")]
    private sealed record Screening(int Id, string Title, Venue Venue);
}
