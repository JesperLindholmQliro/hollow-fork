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

using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Schema;
using Hollow.Core.Tools.Diff.Exact;
using Hollow.Core.Tools.History;
using Hollow.Core.Write;
using Hollow.Core.Write.Copy;

namespace Hollow.Tests.Core.Tools.History;

/// <summary>
/// The point of a historical state is that a record which is gone is still readable, and a record which
/// is not gone reads as it is now. These tests hold both ends of that against each of the three ways a
/// historical state gets built.
/// </summary>
public class HollowHistoricalStateCreatorTests
{
    private const string MovieType = "Movie";
    private const string ListType = "ListOfMovie";

    /// <summary>A movie by id, with the title it had.</summary>
    private sealed record Movie(int Id, string Title);

    [Fact]
    public void ADeltaLeavesTheRecordsItRemovedReadableAtTheirOldOrdinals()
    {
        Producer producer = new();

        producer.WriteCycle(FirstCycle);

        HollowReadStateEngine consumer = producer.ReadSnapshot();
        HollowReadStateEngine before = producer.ReadSnapshot();

        producer.NextCycle();
        producer.WriteCycle(SecondCycle);
        producer.ApplyDelta(consumer);

        HollowHistoricalStateDataAccess history =
            new HollowHistoricalStateCreator().CreateBasedOnNewDelta(1L, consumer);

        Assert.Equal(1L, history.Version);
        Assert.Same(consumer, history.NextState);

        HollowObjectTypeReadState liveMovies = Movies(consumer);
        HollowObjectTypeReadState beforeMovies = Movies(before);
        IHollowObjectTypeDataAccess historicalMovies = HistoricalMovies(history);

        int title = beforeMovies.Schema.GetPosition("title");

        int[] removed =
        [
            .. beforeMovies.PopulatedOrdinals.EnumerateSetBits()
                .Where(ordinal => !liveMovies.PopulatedOrdinals.Get(ordinal)),
        ];

        Assert.NotEmpty(removed);

        // Gone from the live state, still here.
        foreach (int ordinal in removed)
        {
            Assert.Equal(
                beforeMovies.ReadString(ordinal, title), historicalMovies.ReadString(ordinal, title));
        }

        // Still in the live state, so the historical state forwards rather than keeping a copy.
        foreach (int ordinal in liveMovies.PopulatedOrdinals.EnumerateSetBits())
        {
            Assert.Equal(
                liveMovies.ReadString(ordinal, title), historicalMovies.ReadString(ordinal, title));
        }
    }

    [Fact]
    public void ADeltaLeavesTheListsItRemovedReadable()
    {
        Producer producer = new();

        producer.WriteCycle(FirstCycle);

        HollowReadStateEngine consumer = producer.ReadSnapshot();
        HollowReadStateEngine before = producer.ReadSnapshot();

        producer.NextCycle();
        producer.WriteCycle(SecondCycle);
        producer.ApplyDelta(consumer);

        HollowHistoricalStateDataAccess history =
            new HollowHistoricalStateCreator().CreateBasedOnNewDelta(1L, consumer);

        HollowTypeReadState liveLists = consumer.GetTypeState(ListType)!;
        HollowTypeReadState beforeLists = before.GetTypeState(ListType)!;
        IHollowListTypeDataAccess historicalLists =
            Assert.IsAssignableFrom<IHollowListTypeDataAccess>(history.GetTypeDataAccess(ListType));

        int[] removed =
        [
            .. beforeLists.PopulatedOrdinals.EnumerateSetBits()
                .Where(ordinal => !liveLists.PopulatedOrdinals.Get(ordinal)),
        ];

        Assert.NotEmpty(removed);

        foreach (int ordinal in removed)
        {
            IHollowListTypeDataAccess beforeAccess =
                Assert.IsAssignableFrom<IHollowListTypeDataAccess>(beforeLists);

            Assert.Equal(beforeAccess.Size(ordinal), historicalLists.Size(ordinal));

            for (int index = 0; index < beforeAccess.Size(ordinal); index++)
            {
                Assert.Equal(
                    beforeAccess.GetElementOrdinal(ordinal, index),
                    historicalLists.GetElementOrdinal(ordinal, index));
            }
        }
    }

    [Fact]
    public void AReverseDeltaKeepsWhatTheTransitionAdded()
    {
        Producer producer = new();

        producer.WriteCycle(FirstCycle);

        HollowReadStateEngine consumer = producer.ReadSnapshot();

        producer.NextCycle();
        producer.WriteCycle(SecondCycle);
        producer.ApplyDelta(consumer);

        HollowObjectTypeReadState liveMovies = Movies(consumer);

        int[] added =
        [
            .. liveMovies.PopulatedOrdinals.EnumerateSetBits()
                .Where(ordinal => !liveMovies.PreviousOrdinals.Get(ordinal)),
        ];

        Assert.NotEmpty(added);

        HollowHistoricalStateDataAccess history =
            new HollowHistoricalStateCreator().CreateBasedOnNewDelta(1L, consumer, reverse: true);

        IHollowObjectTypeDataAccess historicalMovies = HistoricalMovies(history);
        int title = liveMovies.Schema.GetPosition("title");

        // Walking backwards, the records to keep are the ones this transition brought in — they are
        // what the state being moved *to* will not have.
        foreach (int ordinal in added)
        {
            Assert.Equal(liveMovies.ReadString(ordinal, title), historicalMovies.ReadString(ordinal, title));
        }
    }

    [Fact]
    public void ADoubleSnapshotKeepsEveryRecordTheNewStateDoesNotHave()
    {
        Producer producer = new();

        producer.WriteCycle(FirstCycle);
        HollowReadStateEngine previous = producer.ReadSnapshot();

        producer.NextCycle();
        producer.WriteCycle(SecondCycle);
        HollowReadStateEngine current = producer.ReadSnapshot();

        DiffEqualityMapping mapping = new(previous, current);
        DiffEqualityMappingOrdinalRemapper remapper = new(mapping);

        HollowHistoricalStateDataAccess history = new HollowHistoricalStateCreator()
            .CreateHistoricalStateFromDoubleSnapshot(1L, previous, current, remapper);

        history.NextState = current;

        HollowObjectTypeReadState previousMovies = Movies(previous);
        IHollowObjectTypeDataAccess historicalMovies = HistoricalMovies(history);

        int title = previousMovies.Schema.GetPosition("title");

        // Every record the old state had is readable, at whatever ordinal the shared space gave it —
        // the ones the new state still has by forwarding, the rest from the copy that was kept.
        foreach (int ordinal in previousMovies.PopulatedOrdinals.EnumerateSetBits())
        {
            Assert.Equal(
                previousMovies.ReadString(ordinal, title),
                historicalMovies.ReadString(remapper.GetMappedOrdinal(MovieType, ordinal), title));
        }
    }

    [Fact]
    public void ADoubleSnapshotRecordsTheTypesWhoseSchemaChanged()
    {
        Producer previousProducer = new();
        previousProducer.WriteCycle(FirstCycle);
        HollowReadStateEngine previous = previousProducer.ReadSnapshot();

        // The new model has dropped the list type altogether and grown a field on Movie.
        HollowObjectSchema widerMovie = new(MovieType, 3);
        widerMovie.AddField("id", FieldType.Int);
        widerMovie.AddField("title", FieldType.String);
        widerMovie.AddField("year", FieldType.Int);

        HollowWriteStateEngine writeEngine = new() { RandomizedTag = 7 };
        writeEngine.AddTypeState(new HollowObjectTypeWriteState(widerMovie));

        HollowObjectWriteRecord movie = new(widerMovie);

        foreach (Movie value in SecondCycle)
        {
            movie.Reset();
            movie.SetInt("id", value.Id);
            movie.SetString("title", value.Title);
            movie.SetInt("year", 2000 + value.Id);

            writeEngine.Add(MovieType, movie);
        }

        HollowReadStateEngine current = Producer.ReadSnapshotOf(writeEngine);

        DiffEqualityMapping mapping = new(previous, current);
        DiffEqualityMappingOrdinalRemapper remapper = new(mapping);

        HollowHistoricalStateDataAccess history = new HollowHistoricalStateCreator()
            .CreateHistoricalStateFromDoubleSnapshot(1L, previous, current, remapper);

        HollowHistoricalSchemaChange listChange = history.SchemaChanges[ListType];

        Assert.NotNull(listChange.BeforeSchema);
        Assert.Null(listChange.AfterSchema);

        // Movie kept its name but not its shape, so both sides are recorded.
        HollowHistoricalSchemaChange movieChange = history.SchemaChanges[MovieType];

        Assert.Equal(2, Assert.IsType<HollowObjectSchema>(movieChange.BeforeSchema).FieldCount);
        Assert.Equal(3, Assert.IsType<HollowObjectSchema>(movieChange.AfterSchema).FieldCount);
    }

    [Fact]
    public void AConsistentOrdinalStateKeepsThePreviousStateWhole()
    {
        Producer producer = new();
        producer.WriteCycle(FirstCycle);

        HollowReadStateEngine previous = producer.ReadSnapshot();

        HollowHistoricalStateDataAccess history = new HollowHistoricalStateCreator()
            .CreateConsistentOrdinalHistoricalStateFromDoubleSnapshot(1L, previous);

        HollowObjectTypeReadState previousMovies = Movies(previous);
        IHollowObjectTypeDataAccess historicalMovies = HistoricalMovies(history);

        int title = previousMovies.Schema.GetPosition("title");

        // Nothing was copied and nothing was remapped, so every ordinal reads exactly as it did.
        foreach (int ordinal in previousMovies.PopulatedOrdinals.EnumerateSetBits())
        {
            Assert.Equal(previousMovies.ReadString(ordinal, title), historicalMovies.ReadString(ordinal, title));
        }
    }

    [Fact]
    public void CopyingAStateUnderTheIdentityRemapperChangesNothing()
    {
        Producer producer = new();

        producer.WriteCycle(FirstCycle);

        HollowReadStateEngine consumer = producer.ReadSnapshot();
        HollowReadStateEngine before = producer.ReadSnapshot();

        producer.NextCycle();
        producer.WriteCycle(SecondCycle);
        producer.ApplyDelta(consumer);

        HollowHistoricalStateCreator creator = new();
        HollowHistoricalStateDataAccess history = creator.CreateBasedOnNewDelta(1L, consumer);
        HollowHistoricalStateDataAccess copy =
            creator.CopyButRemapOrdinals(history, IdentityOrdinalRemapper.Instance);

        copy.NextState = consumer;

        Assert.Equal(history.Version, copy.Version);

        HollowObjectTypeReadState liveMovies = Movies(consumer);
        HollowObjectTypeReadState beforeMovies = Movies(before);

        IHollowObjectTypeDataAccess copiedMovies = HistoricalMovies(copy);
        int title = beforeMovies.Schema.GetPosition("title");

        foreach (int ordinal in beforeMovies.PopulatedOrdinals.EnumerateSetBits())
        {
            HollowObjectTypeReadState expected =
                liveMovies.PopulatedOrdinals.Get(ordinal) ? liveMovies : beforeMovies;

            Assert.Equal(expected.ReadString(ordinal, title), copiedMovies.ReadString(ordinal, title));
        }
    }

    private static Movie[] FirstCycle { get; } =
        [new(1, "Alien"), new(2, "Blade Runner"), new(3, "Cube"), new(4, "Dune")];

    private static Movie[] SecondCycle { get; } =
        [new(1, "Alien"), new(3, "Cube: The Director's Cut"), new(5, "Existenz")];

    private static HollowObjectTypeReadState Movies(HollowReadStateEngine engine) =>
        Assert.IsType<HollowObjectTypeReadState>(engine.GetTypeState(MovieType));

    private static IHollowObjectTypeDataAccess HistoricalMovies(HollowHistoricalStateDataAccess history) =>
        Assert.IsAssignableFrom<IHollowObjectTypeDataAccess>(history.GetTypeDataAccess(MovieType));

    /// <summary>
    /// A producer of movies and lists of them, held across cycles so that a delta can be written.
    /// </summary>
    private sealed class Producer
    {
        private readonly HollowObjectSchema _movieSchema;
        private readonly HollowWriteStateEngine _writeEngine;

        internal Producer()
        {
            _movieSchema = new HollowObjectSchema(MovieType, 2);
            _movieSchema.AddField("id", FieldType.Int);
            _movieSchema.AddField("title", FieldType.String);

            _writeEngine = new HollowWriteStateEngine { RandomizedTag = 1 };
            _writeEngine.AddTypeState(new HollowObjectTypeWriteState(_movieSchema));
            _writeEngine.AddTypeState(
                new HollowListTypeWriteState(new HollowListSchema(ListType, MovieType)));
        }

        internal void WriteCycle(Movie[] movies)
        {
            HollowObjectWriteRecord record = new(_movieSchema);
            List<int> ordinals = [];

            foreach (Movie movie in movies)
            {
                record.Reset();
                record.SetInt("id", movie.Id);
                record.SetString("title", movie.Title);

                ordinals.Add(_writeEngine.Add(MovieType, record));
            }

            // One list per adjacent pair, so that a change to either end takes the list with it.
            HollowListWriteRecord list = new();

            for (int i = 0; i + 1 < ordinals.Count; i++)
            {
                list.Reset();
                list.AddElement(ordinals[i]);
                list.AddElement(ordinals[i + 1]);

                _writeEngine.Add(ListType, list);
            }
        }

        internal void NextCycle()
        {
            _writeEngine.PrepareForNextCycle();
            _writeEngine.RandomizedTag++;
        }

        internal HollowReadStateEngine ReadSnapshot() => ReadSnapshotOf(_writeEngine);

        internal static HollowReadStateEngine ReadSnapshotOf(HollowWriteStateEngine writeEngine)
        {
            using MemoryStream blob = new();

            new HollowBlobWriter(writeEngine).WriteSnapshot(blob);
            blob.Position = 0;

            HollowReadStateEngine engine = new();
            new HollowBlobReader(engine).ReadSnapshot(blob);

            return engine;
        }

        internal void ApplyDelta(HollowReadStateEngine consumer)
        {
            using MemoryStream blob = new();

            new HollowBlobWriter(_writeEngine).WriteDelta(blob);
            blob.Position = 0;

            new HollowBlobReader(consumer).ApplyDelta(blob);
        }
    }
}
