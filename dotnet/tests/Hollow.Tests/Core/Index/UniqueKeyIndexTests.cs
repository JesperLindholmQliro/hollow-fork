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
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Schema;
using Hollow.Core.Write;

namespace Hollow.Tests.Core.Index;

/// <summary>
/// The unique-key index answers the same questions as <see cref="PrimaryKeyIndexTests"/> covers, so
/// most of these tests assert that the two agree. What differs is that this one binds its type accesses
/// when it is built rather than walking the schema's referenced type states on every lookup.
/// </summary>
public class UniqueKeyIndexTests
{
    private sealed record Movie(int Id, double Rating, string Title, int Variant = 0);

    private sealed class Model
    {
        internal Model()
        {
            StringSchema = new HollowObjectSchema("String", 1);
            StringSchema.AddField("value", FieldType.String);

            MovieSchema = new HollowObjectSchema(
                "Movie", 4, new PrimaryKey("Movie", "id", "rating", "title.value"));
            MovieSchema.AddField("id", FieldType.Int);
            MovieSchema.AddField("rating", FieldType.Double);
            MovieSchema.AddField("title", FieldType.Reference, "String");
            MovieSchema.AddField("variant", FieldType.Int);

            Engine = new HollowWriteStateEngine { RandomizedTag = 1 };
            Engine.AddTypeState(new HollowObjectTypeWriteState(StringSchema));
            Engine.AddTypeState(new HollowObjectTypeWriteState(MovieSchema));
        }

        internal HollowObjectSchema StringSchema { get; }

        internal HollowObjectSchema MovieSchema { get; }

        internal HollowWriteStateEngine Engine { get; }

        internal void Add(params Movie[] movies)
        {
            HollowObjectWriteRecord title = new(StringSchema);
            HollowObjectWriteRecord movie = new(MovieSchema);

            foreach (Movie m in movies)
            {
                title.Reset();
                title.SetString("value", m.Title);
                int titleOrdinal = Engine.Add("String", title);

                movie.Reset();
                movie.SetInt("id", m.Id);
                movie.SetDouble("rating", m.Rating);
                movie.SetReference("title", titleOrdinal);
                movie.SetInt("variant", m.Variant);
                Engine.Add("Movie", movie);
            }
        }

        internal HollowReadStateEngine ReadSnapshot()
        {
            using MemoryStream stream = new();
            new HollowBlobWriter(Engine).WriteSnapshot(stream);
            stream.Position = 0;

            HollowReadStateEngine readEngine = new();
            new HollowBlobReader(readEngine).ReadSnapshot(stream);
            return readEngine;
        }

        internal void PrepareNextCycle(long randomizedTag)
        {
            Engine.PrepareForNextCycle();
            Engine.RandomizedTag = randomizedTag;
        }

        internal void WriteDelta(HollowReadStateEngine consumer)
        {
            using MemoryStream stream = new();
            new HollowBlobWriter(Engine).WriteDelta(stream);
            stream.Position = 0;

            new HollowBlobReader(consumer).ApplyDelta(stream);
        }
    }

    [Fact]
    public void RecordsAreFoundByTheirKey()
    {
        Model model = new();
        model.Add(new Movie(1, 1.1d, "one"), new Movie(1, 1.1d, "1"), new Movie(2, 2.2d, "two"));

        using HollowUniqueKeyIndex index = new(model.ReadSnapshot(), "Movie");

        Assert.Equal(0, index.GetMatchingOrdinal(1, 1.1d, "one"));
        Assert.Equal(1, index.GetMatchingOrdinal(1, 1.1d, "1"));
        Assert.Equal(2, index.GetMatchingOrdinal(2, 2.2d, "two"));

        Assert.Equal([1, 1.1d, "one"], index.GetRecordKey(0));
        Assert.Equal([2, 2.2d, "two"], index.GetRecordKey(2));

        Assert.Equal(HollowConstants.OrdinalNone, index.GetMatchingOrdinal(9, 9.9d, "nine"));
    }

    /// <summary>
    /// The two indexes have to agree: they serve the same purpose and differ only in how they reach the
    /// data.
    /// </summary>
    [Fact]
    public void ItAgreesWithThePrimaryKeyIndex()
    {
        Model model = new();
        Movie[] movies = [.. Enumerable.Range(0, 500).Select(i => new Movie(i, i / 10d, $"movie-{i}"))];
        model.Add(movies);

        HollowReadStateEngine consumer = model.ReadSnapshot();

        using HollowUniqueKeyIndex unique = new(consumer, "Movie");
        using HollowPrimaryKeyIndex primary = new(consumer, "Movie");

        foreach (Movie movie in movies)
        {
            int uniqueOrdinal = unique.GetMatchingOrdinal(movie.Id, movie.Rating, movie.Title);

            Assert.NotEqual(HollowConstants.OrdinalNone, uniqueOrdinal);
            Assert.Equal(primary.GetMatchingOrdinal(movie.Id, movie.Rating, movie.Title), uniqueOrdinal);
            Assert.Equal(primary.GetRecordKey(uniqueOrdinal), unique.GetRecordKey(uniqueOrdinal));
        }

        Assert.Equal(
            primary.GetMatchingOrdinal(999, 99.9d, "absent"),
            unique.GetMatchingOrdinal(999, 99.9d, "absent"));

        Assert.Equal(primary.FieldTypes, unique.FieldTypes);
        Assert.True(unique.ApproxHeapFootprintInBytes > 0);
    }

    [Fact]
    public void AnExplicitKeyOverridesTheDeclaredOne()
    {
        Model model = new();
        model.Add(new Movie(1, 1.1d, "one"), new Movie(2, 2.2d, "two"));

        using HollowUniqueKeyIndex index = new(model.ReadSnapshot(), "Movie", "id");

        Assert.Equal(0, index.GetMatchingOrdinal(1));
        Assert.Equal(1, index.GetMatchingOrdinal(2));
        Assert.Equal([FieldType.Int], index.FieldTypes);
    }

    /// <summary>
    /// A key field reached through a reference is the case this index resolves differently: the step
    /// into <c>String</c> is bound once rather than looked up per query.
    /// </summary>
    [Fact]
    public void AKeyCanTraverseAReference()
    {
        Model model = new();
        model.Add(new Movie(1, 1.1d, "Persona"), new Movie(1, 1.1d, "Sweden"));

        using HollowUniqueKeyIndex index = new(model.ReadSnapshot(), "Movie", "title.value");

        Assert.Equal(0, index.GetMatchingOrdinal("Persona"));
        Assert.Equal(1, index.GetMatchingOrdinal("Sweden"));
        Assert.Equal(HollowConstants.OrdinalNone, index.GetMatchingOrdinal("Wild Strawberries"));

        Assert.Equal(["Persona"], index.GetRecordKey(0));
    }

    /// <summary>
    /// An unexpanded path means the same as the expanded one, and both must resolve the same steps.
    /// </summary>
    [Fact]
    public void AnUnexpandedPathIndexesTheSameRecords()
    {
        Model model = new();
        model.Add(new Movie(1, 1.1d, "Persona"), new Movie(2, 2.2d, "Sweden"));

        HollowReadStateEngine consumer = model.ReadSnapshot();

        using HollowUniqueKeyIndex expanded = new(consumer, "Movie", "title");
        using HollowUniqueKeyIndex spelledOut = new(consumer, "Movie", "title.value");

        Assert.Equal(spelledOut.GetMatchingOrdinal("Persona"), expanded.GetMatchingOrdinal("Persona"));
        Assert.Equal(0, expanded.GetMatchingOrdinal("Persona"));
    }

    [Fact]
    public void ADenseIndexProbesCorrectly()
    {
        Model model = new();
        model.Add([.. Enumerable.Range(0, 2000).Select(i => new Movie(i, i / 10d, $"movie-{i}"))]);

        using HollowUniqueKeyIndex index = new(model.ReadSnapshot(), "Movie");

        for (int i = 0; i < 2000; i++)
        {
            int ordinal = index.GetMatchingOrdinal(i, i / 10d, $"movie-{i}");
            Assert.NotEqual(HollowConstants.OrdinalNone, ordinal);
            Assert.Equal([i, i / 10d, $"movie-{i}"], index.GetRecordKey(ordinal));
        }

        Assert.Equal(HollowConstants.OrdinalNone, index.GetMatchingOrdinal(2000, 200d, "movie-2000"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheIndexTracksADelta(bool allowDeltaUpdate)
    {
        Model model = new();
        model.Add(new Movie(1, 1.1d, "one"), new Movie(1, 1.1d, "1"), new Movie(2, 2.2d, "two"));

        HollowReadStateEngine consumer = model.ReadSnapshot();

        using HollowUniqueKeyIndex index = new(consumer, "Movie") { AllowDeltaUpdate = allowDeltaUpdate };
        index.ListenForDeltaUpdates();

        Assert.Equal(1, index.GetMatchingOrdinal(1, 1.1d, "1"));

        model.PrepareNextCycle(2);
        model.Add(new Movie(1, 1.1d, "one"), new Movie(2, 2.2d, "two"), new Movie(3, 3.3d, "three"));
        model.WriteDelta(consumer);

        Assert.Equal(0, index.GetMatchingOrdinal(1, 1.1d, "one"));
        Assert.Equal(HollowConstants.OrdinalNone, index.GetMatchingOrdinal(1, 1.1d, "1"));
        Assert.Equal(2, index.GetMatchingOrdinal(2, 2.2d, "two"));

        int threeOrdinal = index.GetMatchingOrdinal(3, 3.3d, "three");
        Assert.NotEqual(HollowConstants.OrdinalNone, threeOrdinal);
        Assert.Equal([3, 3.3d, "three"], index.GetRecordKey(threeOrdinal));
    }

    /// <summary>
    /// This index is the one Java reaches for when an index has to outlive several deltas without being
    /// rebuilt, so a long run of transitions is the case worth pinning.
    /// </summary>
    [Fact]
    public void TheIndexSurvivesManyDeltas()
    {
        Model model = new();
        Movie[] current = [.. Enumerable.Range(0, 200).Select(i => new Movie(i, i / 10d, $"movie-{i}"))];
        model.Add(current);

        HollowReadStateEngine consumer = model.ReadSnapshot();

        using HollowUniqueKeyIndex index = new(consumer, "Movie") { AllowDeltaUpdate = true };
        index.ListenForDeltaUpdates();

        for (int generation = 1; generation <= 10; generation++)
        {
            model.PrepareNextCycle(generation + 1);

            Movie[] kept = [.. current.Where(m => m.Id % 50 >= generation)];
            Movie[] added =
                [.. Enumerable.Range(1000 * generation, 5).Select(i => new Movie(i, i / 10d, $"movie-{i}"))];

            current = [.. kept, .. added];
            model.Add(current);
            model.WriteDelta(consumer);

            foreach (Movie movie in current)
            {
                Assert.NotEqual(
                    HollowConstants.OrdinalNone,
                    index.GetMatchingOrdinal(movie.Id, movie.Rating, movie.Title));
            }
        }

        // And a record dropped along the way is really gone.
        Assert.Equal(HollowConstants.OrdinalNone, index.GetMatchingOrdinal(0, 0d, "movie-0"));
    }

    [Fact]
    public void ADetachedIndexStopsTrackingDeltas()
    {
        Model model = new();
        model.Add(new Movie(1, 1.1d, "one"));

        HollowReadStateEngine consumer = model.ReadSnapshot();
        using HollowUniqueKeyIndex index = new(consumer, "Movie");
        index.ListenForDeltaUpdates();
        index.DetachFromDeltaUpdates();

        model.PrepareNextCycle(2);
        model.Add(new Movie(2, 2.2d, "two"));
        model.WriteDelta(consumer);

        Assert.Equal(HollowConstants.OrdinalNone, index.GetMatchingOrdinal(2, 2.2d, "two"));
    }

    [Fact]
    public void DuplicateKeysAreReported()
    {
        Model model = new();
        model.Add(
            new Movie(1, 1.1d, "one"),
            new Movie(1, 1.1d, "one", Variant: 1),
            new Movie(2, 2.2d, "two"));

        using HollowUniqueKeyIndex index = new(model.ReadSnapshot(), "Movie");

        Assert.True(index.ContainsDuplicates());
        Assert.Equal([1, 1.1d, "one"], Assert.Single(index.GetDuplicateKeys()));

        HollowPrimaryKeyIndex.DuplicateKeyInfo info = Assert.Single(index.GetDuplicateKeys(10));
        Assert.Equal(2, info.Count);
        Assert.Equal([1, 1.1d, "one"], info.Key);
    }

    [Fact]
    public void UniqueKeysAreNotReportedAsDuplicates()
    {
        Model model = new();
        model.Add(new Movie(1, 1.1d, "one"), new Movie(2, 2.2d, "two"));

        using HollowUniqueKeyIndex index = new(model.ReadSnapshot(), "Movie");

        Assert.False(index.ContainsDuplicates());
        Assert.Empty(index.GetDuplicateKeys());
        Assert.Empty(index.GetDuplicateKeys(10));
    }

    [Fact]
    public void OnlyTheGivenOrdinalsAreIndexed()
    {
        Model model = new();
        model.Add(new Movie(1, 1.1d, "one"), new Movie(2, 2.2d, "two"), new Movie(3, 3.3d, "three"));

        Hollow.Core.Util.BitSet subset = new();
        subset.Set(0);
        subset.Set(2);

        using HollowUniqueKeyIndex index = new(
            model.ReadSnapshot(),
            new PrimaryKey("Movie", "id"),
            memoryRecycler: null,
            specificOrdinalsToIndex: subset);

        Assert.Equal(0, index.GetMatchingOrdinal(1));
        Assert.Equal(HollowConstants.OrdinalNone, index.GetMatchingOrdinal(2));
        Assert.Equal(2, index.GetMatchingOrdinal(3));

        Assert.Throws<InvalidOperationException>(index.ListenForDeltaUpdates);
    }

    [Fact]
    public void AnIndexOverAnAbsentTypeIsNotInitialized()
    {
        Model model = new();
        model.Add(new Movie(1, 1.1d, "one"));

        using HollowUniqueKeyIndex index = new(model.ReadSnapshot(), new PrimaryKey("NoSuchType", "id"));

        Assert.False(index.IsInitialized);
        Assert.Null(index.ObjectTypeDataAccess);
        Assert.Equal(0, index.ApproxHeapFootprintInBytes);
        Assert.Throws<InvalidOperationException>(() => index.GetMatchingOrdinal(1));
    }

    [Fact]
    public void AnIndexWhoseKeyPathCannotBeBoundIsNotInitialized()
    {
        HollowObjectSchema movieSchema = new("Movie", 2);
        movieSchema.AddField("id", FieldType.Int);
        movieSchema.AddField("title", FieldType.Reference, "String");

        HollowWriteStateEngine engine = new();
        engine.AddTypeState(new HollowObjectTypeWriteState(movieSchema));

        HollowObjectWriteRecord movie = new(movieSchema);
        movie.SetInt("id", 1);
        engine.Add("Movie", movie);

        using MemoryStream stream = new();
        new HollowBlobWriter(engine).WriteSnapshot(stream);
        stream.Position = 0;

        HollowReadStateEngine consumer = new();
        new HollowBlobReader(consumer).ReadSnapshot(stream);

        // "String" was never written, so the path cannot be bound past "title".
        using HollowUniqueKeyIndex index = new(consumer, "Movie", "title.value");

        Assert.False(index.IsInitialized);
    }

    [Fact]
    public void AnIndexWithNoKeyToFallBackOnIsRejected()
    {
        HollowObjectSchema schema = new("Unkeyed", 1);
        schema.AddField("id", FieldType.Int);

        HollowWriteStateEngine engine = new();
        engine.AddTypeState(new HollowObjectTypeWriteState(schema));

        HollowObjectWriteRecord record = new(schema);
        record.SetInt("id", 1);
        engine.Add("Unkeyed", record);

        using MemoryStream stream = new();
        new HollowBlobWriter(engine).WriteSnapshot(stream);
        stream.Position = 0;

        HollowReadStateEngine consumer = new();
        new HollowBlobReader(consumer).ReadSnapshot(stream);

        Assert.Throws<ArgumentException>(() => new HollowUniqueKeyIndex(consumer, "Unkeyed"));
    }

    /// <summary>
    /// Querying with the wrong number of values is a miss rather than an error, matching the primary key
    /// index.
    /// </summary>
    [Fact]
    public void TheWrongNumberOfKeyValuesIsAMiss()
    {
        Model model = new();
        model.Add(new Movie(1, 1.1d, "one"));

        using HollowUniqueKeyIndex index = new(model.ReadSnapshot(), "Movie");

        Assert.Equal(HollowConstants.OrdinalNone, index.GetMatchingOrdinal(1));
        Assert.Equal(HollowConstants.OrdinalNone, index.GetMatchingOrdinal(1, 1.1d, "one", "extra"));
    }

    /// <summary>
    /// The port's own field type extension has to work here too, since a decimal can be a key field.
    /// </summary>
    [Fact]
    public void ADecimalCanBeAKeyField()
    {
        HollowObjectSchema schema = new("Price", 2, new PrimaryKey("Price", "amount"));
        schema.AddField("id", FieldType.Int);
        schema.AddField("amount", FieldType.Decimal);

        HollowWriteStateEngine engine = new();
        engine.AddTypeState(new HollowObjectTypeWriteState(schema));

        decimal[] amounts = [0m, 1.50m, -0.01m, decimal.MaxValue];
        HollowObjectWriteRecord record = new(schema);

        for (int i = 0; i < amounts.Length; i++)
        {
            record.Reset();
            record.SetInt("id", i);
            record.SetDecimal("amount", amounts[i]);
            engine.Add("Price", record);
        }

        using MemoryStream stream = new();
        new HollowBlobWriter(engine).WriteSnapshot(stream);
        stream.Position = 0;

        HollowReadStateEngine consumer = new();
        new HollowBlobReader(consumer).ReadSnapshot(stream);

        using HollowUniqueKeyIndex index = new(consumer, "Price");

        HollowObjectTypeReadState prices =
            Assert.IsType<HollowObjectTypeReadState>(consumer.GetTypeState("Price"));
        int id = prices.Schema.GetPosition("id");

        for (int i = 0; i < amounts.Length; i++)
        {
            int ordinal = index.GetMatchingOrdinal(amounts[i]);
            Assert.NotEqual(HollowConstants.OrdinalNone, ordinal);
            Assert.Equal(i, prices.ReadInt(ordinal, id));
        }

        // Scale does not change which record a value finds.
        Assert.Equal(index.GetMatchingOrdinal(1.50m), index.GetMatchingOrdinal(1.5m));
        Assert.Equal(HollowConstants.OrdinalNone, index.GetMatchingOrdinal(99.99m));
    }
}
