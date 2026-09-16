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
using Hollow.Tools.Filter;

namespace Hollow.Tests.Tools;

/// <summary>
/// Filters blobs at the producer rather than at the consumer.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>FilteredHollowBlobWriterTest</c>. Every test reads the filtered blob back into a
/// state engine and asserts on the records, because that is the only thing that proves the byte
/// walking is right: a blob whose framing is off by one byte does not load at all, and one whose bit
/// packing is wrong loads and answers with nonsense.
/// </para>
/// <para>
/// The dataset uses two shards and a null variable-length field on purpose — both are places where
/// getting the walk wrong is easy and silent.
/// </para>
/// </remarks>
public class FilteredHollowBlobWriterTests
{
    [Fact]
    public void ADroppedTypeIsGoneFromTheBlob()
    {
        HollowReadStateEngine filtered = Filter(Snapshot(), Types("Movie", "String"));

        Assert.Null(filtered.GetTypeState("Actor"));
        Assert.NotNull(filtered.GetTypeState("Movie"));
    }

    [Fact]
    public void ADroppedFieldIsGoneFromTheBlob()
    {
        HollowReadStateEngine filtered = Filter(
            Snapshot(), Fields(("Movie", "Id"), ("Movie", "Title"), ("String", "value")));

        HollowObjectTypeReadState movies = Movies(filtered);

        Assert.Equal(["Id", "Title"], FieldNames(movies.Schema));

        // The records still read correctly, which is the part byte-level filtering gets wrong.
        Assert.Equal([1, 2, 3, 4, 5], Ids(movies).Order());
        Assert.Equal("Heat", TitleOf(filtered, movies, 1));
    }

    [Fact]
    public void WhatIsKeptStillReadsCorrectly()
    {
        HollowReadStateEngine unfiltered = Read(Snapshot());
        HollowReadStateEngine filtered = Filter(Snapshot(), TypeFilter.IncludeAll);

        // Nothing filtered, so every record has to survive the rewrite untouched — including the
        // null-valued fields and the second shard.
        Assert.Equal(Ids(Movies(unfiltered)).Order(), Ids(Movies(filtered)).Order());

        Assert.Equal(
            Enumerable.Range(1, 5).Select(id => TitleOf(unfiltered, Movies(unfiltered), id)),
            Enumerable.Range(1, 5).Select(id => TitleOf(filtered, Movies(filtered), id)));
    }

    [Fact]
    public void ANullFieldSurvivesTheRewrite()
    {
        HollowReadStateEngine filtered = Filter(Snapshot(), TypeFilter.IncludeAll);

        HollowObjectTypeReadState movies = Movies(filtered);
        int field = movies.Schema.GetPosition("Title");

        // Two of the five films have no title, which exercises the null flag in the range pointers.
        Assert.Contains(
            movies.PopulatedOrdinals.EnumerateSetBits(),
            ordinal => movies.ReadOrdinal(ordinal, field) == -1);
    }

    [Fact]
    public void OneReadProducesSeveralDifferentlyFilteredBlobs()
    {
        byte[] snapshot = Snapshot();

        using MemoryStream everything = new();
        using MemoryStream idsOnly = new();

        new FilteredHollowBlobWriter(TypeFilter.IncludeAll, Fields(("Movie", "Id")))
            .FilterSnapshot(new MemoryStream(snapshot), everything, idsOnly);

        HollowObjectTypeReadState wide = Movies(Read(everything.ToArray()));
        HollowObjectTypeReadState narrow = Movies(Read(idsOnly.ToArray()));

        Assert.Equal(["Id", "Title", "Year"], FieldNames(wide.Schema));
        Assert.Equal(["Id"], FieldNames(narrow.Schema));

        // The same records either way; only the fields differ.
        Assert.Equal(Ids(wide).Order(), Ids(narrow).Order());
    }

    [Fact]
    public void ADeltaIsFilteredToo()
    {
        Catalogue catalogue = new();
        byte[] snapshot = catalogue.WriteSnapshot();
        byte[] delta = catalogue.WriteSecondCycleDelta();

        ITypeFilter filter = Fields(("Movie", "Id"), ("Movie", "Title"), ("String", "value"));

        using MemoryStream filteredSnapshot = new();
        using MemoryStream filteredDelta = new();

        FilteredHollowBlobWriter writer = new(filter);
        writer.FilterSnapshot(new MemoryStream(snapshot), filteredSnapshot);
        writer.FilterDelta(new MemoryStream(delta), filteredDelta);

        HollowReadStateEngine engine = new();
        HollowBlobReader reader = new(engine);

        reader.ReadSnapshot(new MemoryStream(filteredSnapshot.ToArray()));
        reader.ApplyDelta(new MemoryStream(filteredDelta.ToArray()));

        // A delta carries its removals and additions where a snapshot carries its populated set, so
        // applying one on top of a filtered snapshot is the real test of the delta path.
        Assert.Equal([1, 2, 6], Ids(Movies(engine)).Order());
        Assert.Equal("Ali", TitleOf(engine, Movies(engine), 6));
    }

    [Fact]
    public void TheWrongNumberOfOutputsIsRefused()
    {
        FilteredHollowBlobWriter writer = new(TypeFilter.IncludeAll, TypeFilter.IncludeAll);

        using MemoryStream only = new();

        Assert.Throws<ArgumentException>(
            () => writer.FilterSnapshot(new MemoryStream(Snapshot()), only));
    }

    [Fact]
    public void NoFilterAtAllIsRefused() =>
        Assert.Throws<ArgumentException>(() => new FilteredHollowBlobWriter());

    /// <summary>A filter that keeps the named types whole.</summary>
    private static ITypeFilter Types(params string[] types) => TypeFilter.Include(types);

    /// <summary>A filter that keeps only the named fields, of only the types they name.</summary>
    private static ITypeFilter Fields(params (string Type, string Field)[] fields) =>
        TypeFilter.Include(
            fields.Select(field => field.Type).Distinct(StringComparer.Ordinal),
            fields.GroupBy(field => field.Type, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlySet<string>)group
                        .Select(field => field.Field)
                        .ToHashSet(StringComparer.Ordinal),
                    StringComparer.Ordinal));

    private static HollowObjectTypeReadState Movies(HollowReadStateEngine engine) =>
        (HollowObjectTypeReadState)engine.GetTypeState("Movie")!;

    private static string[] FieldNames(HollowObjectSchema schema) =>
        [.. Enumerable.Range(0, schema.FieldCount).Select(schema.GetFieldName)];

    private static IEnumerable<int> Ids(HollowObjectTypeReadState movies)
    {
        int field = movies.Schema.GetPosition("Id");

        return [.. movies.PopulatedOrdinals.EnumerateSetBits()
            .Select(ordinal => movies.ReadInt(ordinal, field))];
    }

    /// <summary>The title of the film with the given identifier, or null where it has none.</summary>
    private static string? TitleOf(
        HollowReadStateEngine engine, HollowObjectTypeReadState movies, int id)
    {
        int idField = movies.Schema.GetPosition("Id");
        int titleField = movies.Schema.GetPosition("Title");

        HollowObjectTypeReadState strings = (HollowObjectTypeReadState)engine.GetTypeState("String")!;

        foreach (int ordinal in movies.PopulatedOrdinals.EnumerateSetBits())
        {
            if (movies.ReadInt(ordinal, idField) != id)
            {
                continue;
            }

            int title = movies.ReadOrdinal(ordinal, titleField);

            return title == -1 ? null : strings.ReadString(title, 0);
        }

        return null;
    }

    private static byte[] Snapshot() => new Catalogue().WriteSnapshot();

    private static HollowReadStateEngine Filter(byte[] snapshot, ITypeFilter filter)
    {
        using MemoryStream filtered = new();

        new FilteredHollowBlobWriter(filter).FilterSnapshot(new MemoryStream(snapshot), filtered);

        return Read(filtered.ToArray());
    }

    private static HollowReadStateEngine Read(byte[] snapshot)
    {
        HollowReadStateEngine engine = new();

        new HollowBlobReader(engine).ReadSnapshot(new MemoryStream(snapshot));

        return engine;
    }

    /// <summary>A film catalogue with a sharded type, a reference field and a null.</summary>
    private sealed class Catalogue
    {
        private readonly HollowWriteStateEngine _engine = new();
        private readonly HollowObjectSchema _movie;
        private readonly HollowObjectSchema _actor;
        private readonly HollowObjectSchema _string;

        internal Catalogue()
        {
            _string = new HollowObjectSchema("String", 1, "value");
            _string.AddField("value", FieldType.String);

            _movie = new HollowObjectSchema("Movie", 3, "Id");
            _movie.AddField("Id", FieldType.Int);
            _movie.AddField("Title", FieldType.Reference, "String");
            _movie.AddField("Year", FieldType.Int);

            _actor = new HollowObjectSchema("Actor", 1, "Name");
            _actor.AddField("Name", FieldType.String);

            _engine.AddTypeState(new HollowObjectTypeWriteState(_string));

            // Two shards, because a sharded type writes a shard count the walk has to read.
            _engine.AddTypeState(new HollowObjectTypeWriteState(_movie, numShards: 2));
            _engine.AddTypeState(new HollowObjectTypeWriteState(_actor));

            AddFilms((1, "Heat"), (2, "Ronin"), (3, null), (4, "Collateral"), (5, null));

            AddActor();
        }

        internal byte[] WriteSnapshot()
        {
            _engine.PrepareForWrite();

            using MemoryStream stream = new();
            new HollowBlobWriter(_engine).WriteSnapshot(stream);

            return stream.ToArray();
        }

        /// <summary>Keeps two films, drops three, adds one, and writes the delta.</summary>
        internal byte[] WriteSecondCycleDelta()
        {
            _engine.PrepareForNextCycle();

            AddFilms((1, "Heat"), (2, "Ronin"), (6, "Ali"));
            AddActor();

            _engine.PrepareForWrite();

            using MemoryStream stream = new();
            new HollowBlobWriter(_engine).WriteDelta(stream);

            return stream.ToArray();
        }

        private void AddActor()
        {
            HollowObjectWriteRecord actor = new(_actor);
            actor.SetString("Name", "Pacino");

            _engine.Add("Actor", actor);
        }

        private void AddFilms(params (int Id, string? Title)[] films)
        {
            HollowObjectWriteRecord movie = new(_movie);

            foreach ((int id, string? title) in films)
            {
                movie.Reset();
                movie.SetInt("Id", id);
                movie.SetInt("Year", 1990 + id);

                if (title is not null)
                {
                    HollowObjectWriteRecord titleRecord = new(_string);
                    titleRecord.SetString("value", title);

                    movie.SetReference("Title", _engine.Add("String", titleRecord));
                }

                _engine.Add("Movie", movie);
            }
        }
    }
}
