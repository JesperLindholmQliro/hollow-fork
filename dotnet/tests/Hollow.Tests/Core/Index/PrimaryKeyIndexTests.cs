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
using Hollow.Core.Schema;
using Hollow.Core.Write;

namespace Hollow.Tests.Core.Index;

/// <summary>
/// Looks records up by key rather than by ordinal. The index is a hash table over the producer's
/// records, so the tests check the things a hash table gets wrong: collisions, keys that traverse
/// references, duplicate keys, and keeping the table right as records come and go across a delta.
/// </summary>
public class PrimaryKeyIndexTests
{
    /// <summary>
    /// A movie keyed by <c>(id, rating, title)</c>, where the title lives in a referenced record so the
    /// key has to be resolved through a path rather than read straight off the record.
    /// </summary>
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

        HollowReadStateEngine consumer = model.ReadSnapshot();
        using HollowPrimaryKeyIndex index = new(consumer, "Movie");

        Assert.Equal(0, index.GetMatchingOrdinal(1, 1.1d, "one"));
        Assert.Equal(1, index.GetMatchingOrdinal(1, 1.1d, "1"));
        Assert.Equal(2, index.GetMatchingOrdinal(2, 2.2d, "two"));

        Assert.Equal([1, 1.1d, "one"], index.GetRecordKey(0));
        Assert.Equal([1, 1.1d, "1"], index.GetRecordKey(1));
        Assert.Equal([2, 2.2d, "two"], index.GetRecordKey(2));
    }

    [Fact]
    public void AKeyThatMatchesNothingReturnsNoOrdinal()
    {
        Model model = new();
        model.Add(new Movie(1, 1.1d, "one"));

        using HollowPrimaryKeyIndex index = new(model.ReadSnapshot(), "Movie");

        Assert.Equal(HollowConstants.OrdinalNone, index.GetMatchingOrdinal(1, 1.1d, "two"));
        Assert.Equal(HollowConstants.OrdinalNone, index.GetMatchingOrdinal(9, 9.9d, "one"));

        // The wrong number of key fields is a miss rather than an error.
        Assert.Equal(HollowConstants.OrdinalNone, index.GetMatchingOrdinal(1));
    }

    /// <summary>
    /// An explicit key need not be the one the schema declares.
    /// </summary>
    [Fact]
    public void AnExplicitKeyOverridesTheDeclaredOne()
    {
        Model model = new();
        model.Add(new Movie(1, 1.1d, "one"), new Movie(2, 2.2d, "two"));

        using HollowPrimaryKeyIndex index = new(model.ReadSnapshot(), "Movie", "id");

        Assert.Equal(0, index.GetMatchingOrdinal(1));
        Assert.Equal(1, index.GetMatchingOrdinal(2));
    }

    /// <summary>
    /// A key field reached through a reference has to be followed to the record that actually holds the
    /// value, both when hashing and when comparing.
    /// </summary>
    [Fact]
    public void AKeyCanTraverseAReference()
    {
        Model model = new();
        model.Add(new Movie(1, 1.1d, "Persona"), new Movie(1, 1.1d, "Sweden"));

        using HollowPrimaryKeyIndex index = new(model.ReadSnapshot(), "Movie", "title.value");

        Assert.Equal(0, index.GetMatchingOrdinal("Persona"));
        Assert.Equal(1, index.GetMatchingOrdinal("Sweden"));
        Assert.Equal(HollowConstants.OrdinalNone, index.GetMatchingOrdinal("Wild Strawberries"));
    }

    /// <summary>
    /// A path that stops at a single-field record is auto-expanded, so naming the reference and naming
    /// the value behind it index the same thing.
    /// </summary>
    [Fact]
    public void AnUnexpandedPathIndexesTheSameRecords()
    {
        Model model = new();
        model.Add(new Movie(1, 1.1d, "Persona"), new Movie(2, 2.2d, "Sweden"));

        HollowReadStateEngine consumer = model.ReadSnapshot();

        using HollowPrimaryKeyIndex expanded = new(consumer, "Movie", "title");
        using HollowPrimaryKeyIndex spelledOut = new(consumer, "Movie", "title.value");

        Assert.Equal(spelledOut.GetMatchingOrdinal("Persona"), expanded.GetMatchingOrdinal("Persona"));
        Assert.Equal(0, expanded.GetMatchingOrdinal("Persona"));
    }

    /// <summary>
    /// Enough records that the table is densely packed and most lookups probe past occupied buckets.
    /// </summary>
    [Fact]
    public void ADenseIndexProbesCorrectly()
    {
        Model model = new();
        model.Add([.. Enumerable.Range(0, 2000).Select(i => new Movie(i, i / 10d, $"movie-{i}"))]);

        HollowReadStateEngine consumer = model.ReadSnapshot();
        using HollowPrimaryKeyIndex index = new(consumer, "Movie");

        for (int i = 0; i < 2000; i++)
        {
            int ordinal = index.GetMatchingOrdinal(i, i / 10d, $"movie-{i}");
            Assert.NotEqual(HollowConstants.OrdinalNone, ordinal);
            Assert.Equal([i, i / 10d, $"movie-{i}"], index.GetRecordKey(ordinal));
        }

        Assert.Equal(HollowConstants.OrdinalNone, index.GetMatchingOrdinal(2000, 200d, "movie-2000"));
        Assert.True(index.ApproxHeapFootprintInBytes > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheIndexTracksADelta(bool allowDeltaUpdate)
    {
        Model model = new();
        model.Add(new Movie(1, 1.1d, "one"), new Movie(1, 1.1d, "1"), new Movie(2, 2.2d, "two"));

        HollowReadStateEngine consumer = model.ReadSnapshot();

        using HollowPrimaryKeyIndex index = new(consumer, "Movie") { AllowDeltaUpdate = allowDeltaUpdate };
        index.ListenForDeltaUpdates();

        Assert.Equal(0, index.GetMatchingOrdinal(1, 1.1d, "one"));
        Assert.Equal(1, index.GetMatchingOrdinal(1, 1.1d, "1"));

        // The second cycle drops one record and adds another.
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
    /// Removing a record breaks the probe run that passed through its bucket, so the incremental update
    /// has to shuffle the following entries back. Enough churn to make that happen repeatedly.
    /// </summary>
    [Fact]
    public void TheIncrementalUpdateSurvivesRepeatedChurn()
    {
        Model model = new();
        Movie[] initial = [.. Enumerable.Range(0, 400).Select(i => new Movie(i, i / 10d, $"movie-{i}"))];
        model.Add(initial);

        HollowReadStateEngine consumer = model.ReadSnapshot();

        using HollowPrimaryKeyIndex index = new(consumer, "Movie") { AllowDeltaUpdate = true };
        index.ListenForDeltaUpdates();

        for (int generation = 1; generation <= 5; generation++)
        {
            model.PrepareNextCycle(generation + 1);

            // Drop a handful each cycle -- few enough that the incremental path is taken rather than a
            // full rebuild -- and add the same number of new ones.
            Movie[] kept = [.. initial.Where(m => m.Id % 100 >= generation)];
            Movie[] added =
                [.. Enumerable.Range(1000 * generation, 5).Select(i => new Movie(i, i / 10d, $"movie-{i}"))];

            model.Add([.. kept, .. added]);
            model.WriteDelta(consumer);

            foreach (Movie m in kept.Concat(added))
            {
                Assert.NotEqual(
                    HollowConstants.OrdinalNone, index.GetMatchingOrdinal(m.Id, m.Rating, m.Title));
            }

            foreach (Movie m in initial.Except(kept))
            {
                Assert.Equal(
                    HollowConstants.OrdinalNone, index.GetMatchingOrdinal(m.Id, m.Rating, m.Title));
            }

            initial = [.. kept, .. added];
        }
    }

    /// <summary>
    /// Once detached, the index stops following the data -- which is the point of detaching, but also
    /// means a stale index must not be mistaken for a live one.
    /// </summary>
    [Fact]
    public void ADetachedIndexStopsTrackingDeltas()
    {
        Model model = new();
        model.Add(new Movie(1, 1.1d, "one"));

        HollowReadStateEngine consumer = model.ReadSnapshot();
        using HollowPrimaryKeyIndex index = new(consumer, "Movie");
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

        // Two records with the same key, differing only in a field outside it.
        model.Add(
            new Movie(1, 1.1d, "one"),
            new Movie(1, 1.1d, "one", Variant: 1),
            new Movie(2, 2.2d, "two"));

        using HollowPrimaryKeyIndex index = new(model.ReadSnapshot(), "Movie");

        Assert.True(index.ContainsDuplicates());

        object?[] duplicate = Assert.Single(index.GetDuplicateKeys());
        Assert.Equal([1, 1.1d, "one"], duplicate);
    }

    [Fact]
    public void UniqueKeysAreNotReportedAsDuplicates()
    {
        Model model = new();
        model.Add(new Movie(1, 1.1d, "one"), new Movie(2, 2.2d, "two"));

        using HollowPrimaryKeyIndex index = new(model.ReadSnapshot(), "Movie");

        Assert.False(index.ContainsDuplicates());
        Assert.Empty(index.GetDuplicateKeys());
    }

    [Fact]
    public void DuplicateKeyCountsAreReportedUpToTheGivenLimit()
    {
        Model model = new();

        // Three keys held by three records each, plus one key held once.
        model.Add(
        [
            .. Enumerable.Range(1, 3).SelectMany(
                i => Enumerable.Range(0, 3).Select(
                    copy => new Movie(i, i * 1.1d, $"movie-{i}", Variant: copy))),
            new Movie(9, 9.9d, "unique"),
        ]);

        using HollowPrimaryKeyIndex index = new(model.ReadSnapshot(), "Movie");

        IReadOnlyList<HollowPrimaryKeyIndex.DuplicateKeyInfo> all = index.GetDuplicateKeys(10);
        Assert.Equal(3, all.Count);
        Assert.All(all, info => Assert.Equal(3, info.Count));
        Assert.Equal([1, 2, 3], all.Select(info => (int)info.Key[0]!).Order());

        Assert.Equal(2, index.GetDuplicateKeys(2).Count);
        Assert.Empty(index.GetDuplicateKeys(0));
    }

    /// <summary>
    /// Indexing only some ordinals is how a caller indexes a subset, and rules out delta tracking --
    /// there would be no way to tell which of the new records belong to the subset.
    /// </summary>
    [Fact]
    public void OnlyTheGivenOrdinalsAreIndexed()
    {
        Model model = new();
        model.Add(new Movie(1, 1.1d, "one"), new Movie(2, 2.2d, "two"), new Movie(3, 3.3d, "three"));

        HollowReadStateEngine consumer = model.ReadSnapshot();

        Hollow.Core.Util.BitSet subset = new();
        subset.Set(0);
        subset.Set(2);

        using HollowPrimaryKeyIndex index =
            new(consumer, new PrimaryKey("Movie", "id"), memoryRecycler: null, specificOrdinalsToIndex: subset);

        Assert.Equal(0, index.GetMatchingOrdinal(1));
        Assert.Equal(HollowConstants.OrdinalNone, index.GetMatchingOrdinal(2));
        Assert.Equal(2, index.GetMatchingOrdinal(3));

        Assert.Throws<InvalidOperationException>(index.ListenForDeltaUpdates);
    }

    /// <summary>
    /// An index over a type the state does not hold reads as empty rather than throwing, so a consumer
    /// that filtered the type out still starts up.
    /// </summary>
    [Fact]
    public void AnIndexOverAnAbsentTypeIsNotInitialized()
    {
        Model model = new();
        model.Add(new Movie(1, 1.1d, "one"));

        using HollowPrimaryKeyIndex index =
            new(model.ReadSnapshot(), new PrimaryKey("NoSuchType", "id"));

        Assert.False(index.IsInitialized);
        Assert.Null(index.TypeState);
        Assert.Throws<InvalidOperationException>(() => index.GetMatchingOrdinal(1));
    }

    /// <summary>
    /// A key path that names a type the state does not hold is the same situation, reached through a
    /// path rather than the root type.
    /// </summary>
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
        using HollowPrimaryKeyIndex index = new(consumer, "Movie", "title.value");

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

        Assert.Throws<ArgumentException>(() => new HollowPrimaryKeyIndex(consumer, "Unkeyed"));
    }
}
