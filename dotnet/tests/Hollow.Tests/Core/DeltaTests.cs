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
using Hollow.Core.Read;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Schema;
using Hollow.Core.Write;

namespace Hollow.Tests.Core;

/// <summary>
/// Applying a delta has to leave the consumer in exactly the state a snapshot of the same cycle would
/// have produced. Every test here checks that equivalence rather than just spot-checking fields: a
/// delta that merges records slightly wrong still looks plausible field by field.
/// </summary>
public class DeltaTests
{
    private static HollowObjectSchema MovieSchema()
    {
        HollowObjectSchema schema = new("Movie", 4);
        schema.AddField("id", FieldType.Int);
        schema.AddField("title", FieldType.String);
        schema.AddField("boxOffice", FieldType.Long);
        schema.AddField("tagline", FieldType.String);
        return schema;
    }

    private sealed record Movie(int Id, string? Title, long BoxOffice, string? Tagline);

    private static void AddMovies(HollowWriteStateEngine engine, HollowObjectSchema schema, IEnumerable<Movie> movies)
    {
        HollowObjectWriteRecord record = new(schema);
        foreach (Movie movie in movies)
        {
            record.Reset();
            record.SetInt("id", movie.Id);
            record.SetString("title", movie.Title);
            record.SetLong("boxOffice", movie.BoxOffice);
            record.SetString("tagline", movie.Tagline);
            engine.Add("Movie", record);
        }
    }

    private static HollowReadStateEngine ReadSnapshot(HollowWriteStateEngine engine)
    {
        using MemoryStream stream = new();
        new HollowBlobWriter(engine).WriteSnapshot(stream);
        stream.Position = 0;

        HollowReadStateEngine readEngine = new();
        new HollowBlobReader(readEngine).ReadSnapshot(stream);
        return readEngine;
    }

    private static void ApplyDelta(HollowWriteStateEngine engine, HollowReadStateEngine readEngine)
    {
        using MemoryStream stream = new();
        new HollowBlobWriter(engine).WriteDelta(stream);
        stream.Position = 0;

        new HollowBlobReader(readEngine).ApplyDelta(stream);
    }

    /// <summary>
    /// Reads every populated record back as a comparable value, so two states can be compared whole.
    /// </summary>
    private static Dictionary<int, Movie> ReadAll(HollowReadStateEngine engine)
    {
        HollowObjectTypeReadState state =
            Assert.IsType<HollowObjectTypeReadState>(engine.GetTypeState("Movie"));

        int id = state.Schema.GetPosition("id");
        int title = state.Schema.GetPosition("title");
        int boxOffice = state.Schema.GetPosition("boxOffice");
        int tagline = state.Schema.GetPosition("tagline");

        Dictionary<int, Movie> movies = [];
        foreach (int ordinal in state.PopulatedOrdinals.EnumerateSetBits())
        {
            movies[ordinal] = new Movie(
                state.ReadInt(ordinal, id),
                state.ReadString(ordinal, title),
                state.ReadLong(ordinal, boxOffice),
                state.ReadString(ordinal, tagline));
        }

        return movies;
    }

    /// <summary>
    /// Runs two cycles, then checks that a consumer which applied the delta holds exactly what a
    /// consumer which read the second snapshot outright would hold — ordinals included.
    /// </summary>
    private static void AssertDeltaMatchesSnapshot(Movie[] firstCycle, Movie[] secondCycle)
    {
        HollowObjectSchema schema = MovieSchema();

        HollowWriteStateEngine engine = new() { RandomizedTag = 111 };
        engine.AddTypeState(new HollowObjectTypeWriteState(schema));

        AddMovies(engine, schema, firstCycle);
        HollowReadStateEngine consumer = ReadSnapshot(engine);
        Dictionary<int, Movie> afterFirst = ReadAll(consumer);

        engine.PrepareForNextCycle();
        engine.RandomizedTag = 222;
        AddMovies(engine, schema, secondCycle);

        HollowReadStateEngine viaSnapshot = ReadSnapshot(engine);
        ApplyDelta(engine, consumer);

        Assert.Equal(ReadAll(viaSnapshot), ReadAll(consumer));
        Assert.Equal(viaSnapshot.GetTypeState("Movie")!.MaxOrdinal, consumer.GetTypeState("Movie")!.MaxOrdinal);

        // And the transition must actually have changed something, or the test proves nothing.
        Assert.NotEqual(afterFirst, ReadAll(consumer));
    }

    [Fact]
    public void AddingRecords()
    {
        Movie[] first = [new(1, "A", 10, "one"), new(2, "B", 20, "two")];
        AssertDeltaMatchesSnapshot(first, [.. first, new Movie(3, "C", 30, "three")]);
    }

    [Fact]
    public void RemovingRecords()
    {
        Movie[] first = [new(1, "A", 10, "one"), new(2, "B", 20, "two"), new(3, "C", 30, "three")];
        AssertDeltaMatchesSnapshot(first, [first[0], first[2]]);
    }

    [Fact]
    public void AddingAndRemovingTogether()
    {
        Movie[] first = [new(1, "A", 10, "one"), new(2, "B", 20, "two"), new(3, "C", 30, "three")];
        Movie[] second = [first[0], new Movie(4, "D", 40, "four"), new Movie(5, "E", 50, "five")];
        AssertDeltaMatchesSnapshot(first, second);
    }

    /// <summary>
    /// A field's bit width is recomputed each cycle, so a record that grows past the old width forces
    /// every carried-over record to be re-encoded at the new width.
    /// </summary>
    [Fact]
    public void WideningAFieldRewritesCarriedOverRecords()
    {
        Movie[] first = [new(1, "A", 1, "one"), new(2, "B", 2, "two")];
        Movie[] second = [.. first, new Movie(3, "C", long.MaxValue / 2, "three")];
        AssertDeltaMatchesSnapshot(first, second);
    }

    [Fact]
    public void NullsSurviveADelta()
    {
        Movie[] first = [new(1, null, 10, "one"), new(2, "B", 20, null)];
        Movie[] second = [first[0], new Movie(3, null, 30, null)];
        AssertDeltaMatchesSnapshot(first, second);
    }

    [Fact]
    public void VariableLengthDataStaysAlignedAcrossADelta()
    {
        // Strings of differing length exercise the per-record range offsets, which are the easiest
        // thing for a delta merge to get subtly wrong.
        Movie[] first = [.. Enumerable.Range(0, 40).Select(i => new Movie(i, new string('x', i), i, $"tag-{i}"))];
        Movie[] second =
        [
            .. first.Where(m => m.Id % 3 != 0),
            .. Enumerable.Range(100, 10).Select(i => new Movie(i, new string('y', i - 60), i, $"new-{i}")),
        ];

        AssertDeltaMatchesSnapshot(first, second);
    }

    /// <summary>
    /// Runs the cycles in order against one consumer, checking after each that applying the delta left
    /// it holding exactly what reading that cycle's snapshot outright would have.
    /// </summary>
    /// <returns>How many records the bulk path carried across, over the whole sequence.</returns>
    private static long RunCyclesCountingBulkCopies(params Movie[][] cycles)
    {
        HollowObjectSchema schema = MovieSchema();

        HollowWriteStateEngine engine = new() { RandomizedTag = 1 };
        engine.AddTypeState(new HollowObjectTypeWriteState(schema));

        AddMovies(engine, schema, cycles[0]);
        HollowReadStateEngine consumer = ReadSnapshot(engine);

        long before = DeltaDiagnostics.BulkCopiedObjects;

        foreach (Movie[] cycle in cycles.Skip(1))
        {
            engine.PrepareForNextCycle();
            AddMovies(engine, schema, cycle);

            HollowReadStateEngine viaSnapshot = ReadSnapshot(engine);
            ApplyDelta(engine, consumer);

            Assert.Equal(ReadAll(viaSnapshot), ReadAll(consumer));
            Assert.Equal(
                viaSnapshot.GetTypeState("Movie")!.MaxOrdinal, consumer.GetTypeState("Movie")!.MaxOrdinal);
        }

        return DeltaDiagnostics.BulkCopiedObjects - before;
    }

    /// <summary>
    /// A catalogue large enough that one edit leaves every other record untouched, and stable enough
    /// that no field has to be widened — which is when the applicator stops re-encoding record by
    /// record and copies the whole run.
    /// </summary>
    /// <remarks>
    /// This is what the fast path is for, and the shape of a real delta: a small change to a large
    /// dataset. Without a case like it the bulk path is never reached, so this test asserts that it was
    /// as well as that the result is right.
    /// </remarks>
    [Fact]
    public void AnUnchangedRunIsCarriedAcrossInBulk()
    {
        Movie[] first =
            [.. Enumerable.Range(0, 200).Select(i => new Movie(i, $"title-{i:D4}", i, $"tag-{i:D4}"))];

        // One record rewritten, at the same widths: same id range, same string lengths.
        Movie[] second = [.. first.Select(m => m.Id == 100 ? m with { Title = "title-XXXX" } : m)];

        long bulkCopied = RunCyclesCountingBulkCopies(first, second);

        Assert.Equal(200, bulkCopied);
    }

    /// <summary>
    /// The same, but with the run starting after an addition rather than at ordinal zero, so the bytes
    /// it carries land at a different offset than they had — which is what the strided pointer fix-up
    /// exists to correct, and the one part of the bulk path a run starting at zero never exercises.
    /// </summary>
    /// <remarks>
    /// Every record is the same width on purpose. A cycle that changes a length can widen a field, and
    /// a widened field sends the whole delta down the re-encoding path, where this proves nothing.
    /// </remarks>
    [Fact]
    public void ARunAfterAnAdditionHasItsPointersCorrected()
    {
        // A null tagline every seventh record, so the run being copied contains the null flag that
        // shares a var-length pointer's top bit and has to survive the correction.
        Movie[] first =
        [
            .. Enumerable.Range(0, 200).Select(i =>
                new Movie(i, $"title-{i:D4}", i, i % 7 == 0 ? null : $"tag-{i:D4}")),
        ];

        // Dropping a record leaves a hole low in the ordinal space…
        Movie[] second = [.. first.Where(m => m.Id != 5)];

        // …which this cycle's addition reuses. A removed ordinal keeps its bytes until something takes
        // its place, so the run after ordinal 5 only moves if what replaces it is a different length —
        // which is the whole point here. Different by a few bytes, not enough to widen anything.
        Movie[] third = [.. second, new Movie(5, "title-0005-and-then-some", 5, null)];

        long bulkCopied = RunCyclesCountingBulkCopies(first, second, third);

        // 200 carried across the second cycle, then 199 across the third: the addition at ordinal 5
        // splits that one into the run before it and the run after.
        Assert.Equal(399, bulkCopied);
    }

    [Fact]
    public void SeveralDeltasInSequence()
    {
        HollowObjectSchema schema = MovieSchema();

        HollowWriteStateEngine engine = new() { RandomizedTag = 1 };
        engine.AddTypeState(new HollowObjectTypeWriteState(schema));

        AddMovies(engine, schema, [new Movie(0, "gen-0", 0, "t0")]);
        HollowReadStateEngine consumer = ReadSnapshot(engine);

        for (int generation = 1; generation <= 5; generation++)
        {
            engine.PrepareForNextCycle();
            engine.RandomizedTag = generation + 1;

            // Each cycle keeps the previous generation's record and adds a new one.
            AddMovies(
                engine,
                schema,
                [
                    new Movie(generation - 1, $"gen-{generation - 1}", generation - 1, $"t{generation - 1}"),
                    new Movie(generation, $"gen-{generation}", generation, $"t{generation}"),
                ]);

            HollowReadStateEngine viaSnapshot = ReadSnapshot(engine);
            ApplyDelta(engine, consumer);

            Assert.Equal(ReadAll(viaSnapshot), ReadAll(consumer));
        }
    }

    [Fact]
    public void PopulatedOrdinalsTrackTheDelta()
    {
        HollowObjectSchema schema = MovieSchema();

        HollowWriteStateEngine engine = new() { RandomizedTag = 1 };
        engine.AddTypeState(new HollowObjectTypeWriteState(schema));

        AddMovies(engine, schema, [new Movie(1, "A", 1, "a"), new Movie(2, "B", 2, "b")]);
        HollowReadStateEngine consumer = ReadSnapshot(engine);

        Assert.Equal(2, consumer.GetTypeState("Movie")!.PopulatedOrdinals.Cardinality());

        engine.PrepareForNextCycle();
        engine.RandomizedTag = 2;
        AddMovies(engine, schema, [new Movie(1, "A", 1, "a"), new Movie(3, "C", 3, "c")]);

        ApplyDelta(engine, consumer);

        HollowObjectTypeReadState state =
            Assert.IsType<HollowObjectTypeReadState>(consumer.GetTypeState("Movie"));

        Assert.Equal(2, state.PopulatedOrdinals.Cardinality());

        // The previous cycle's ordinals are what the listener saw before this transition.
        Assert.Equal(2, state.PreviousOrdinals.Cardinality());
        Assert.NotEqual(state.PopulatedOrdinals, state.PreviousOrdinals);
    }

    /// <summary>
    /// A delta names the state it applies to. Applying it anywhere else would silently corrupt the
    /// data, so it must be refused.
    /// </summary>
    [Fact]
    public void ADeltaCannotBeAppliedToTheWrongState()
    {
        HollowObjectSchema schema = MovieSchema();

        HollowWriteStateEngine engine = new() { RandomizedTag = 1 };
        engine.AddTypeState(new HollowObjectTypeWriteState(schema));
        AddMovies(engine, schema, [new Movie(1, "A", 1, "a")]);

        HollowReadStateEngine consumer = ReadSnapshot(engine);

        engine.PrepareForNextCycle();
        engine.RandomizedTag = 2;
        AddMovies(engine, schema, [new Movie(2, "B", 2, "b")]);

        using MemoryStream deltaStream = new();
        new HollowBlobWriter(engine).WriteDelta(deltaStream);

        // A consumer holding some other state must reject it.
        consumer.RandomizedTag = 999;
        deltaStream.Position = 0;

        Assert.Throws<InvalidDataException>(() => new HollowBlobReader(consumer).ApplyDelta(deltaStream));
    }

    /// <summary>
    /// A delta only carries the types whose records changed, so a type left alone in a cycle must not
    /// appear in the blob at all — and must survive the transition untouched.
    /// </summary>
    [Fact]
    public void UnchangedTypesAreLeftOutOfTheDelta()
    {
        HollowObjectSchema movieSchema = MovieSchema();
        HollowObjectSchema studioSchema = new("Studio", 1);
        studioSchema.AddField("name", FieldType.String);

        HollowWriteStateEngine engine = new() { RandomizedTag = 1 };
        engine.AddTypeState(new HollowObjectTypeWriteState(movieSchema));
        engine.AddTypeState(new HollowObjectTypeWriteState(studioSchema));

        HollowObjectWriteRecord studio = new(studioSchema);
        studio.SetString("name", "Svensk Filmindustri");

        AddMovies(engine, movieSchema, [new Movie(1, "A", 1, "a")]);
        engine.Add("Studio", studio);

        HollowReadStateEngine consumer = ReadSnapshot(engine);

        engine.PrepareForNextCycle();
        engine.RandomizedTag = 2;
        AddMovies(engine, movieSchema, [new Movie(2, "B", 2, "b")]);
        studio.Reset();
        studio.SetString("name", "Svensk Filmindustri");
        engine.Add("Studio", studio);

        using MemoryStream deltaStream = new();
        new HollowBlobWriter(engine).WriteDelta(deltaStream);
        deltaStream.Position = 0;

        using (HollowBlobInput input = HollowBlobInput.Serial(deltaStream, leaveOpen: true))
        {
            HollowBlobHeader header = new HollowBlobHeaderReader().ReadHeader(input);
            Assert.Equal(["Movie"], header.Schemas.Select(s => s.Name));
        }

        deltaStream.Position = 0;
        new HollowBlobReader(consumer).ApplyDelta(deltaStream);

        HollowObjectTypeReadState studioState =
            Assert.IsType<HollowObjectTypeReadState>(consumer.GetTypeState("Studio"));

        Assert.Equal("Svensk Filmindustri", studioState.ReadString(0, studioSchema.GetPosition("name")));
        Assert.Equal(0, studioState.MaxOrdinal);
        Assert.Equal(new Movie(2, "B", 2, "b"), Assert.Single(ReadAll(consumer)).Value);
    }
}
