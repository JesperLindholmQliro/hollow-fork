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
using Hollow.Core.Index.Key;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Schema;
using Hollow.Core.Tools.History;
using Hollow.Core.Tools.History.KeyIndex;
using Hollow.Core.Util;
using Hollow.Core.Write;

namespace Hollow.Tests.Core.Tools.History;

/// <summary>
/// A history is only worth keeping if a record that changed three versions ago can still be found and
/// read. These tests follow records through forward deltas, reverse deltas and a double snapshot, and
/// check that both halves of that hold: the key index finds the record, and the state it points at
/// still has it.
/// </summary>
public class HollowHistoryTests
{
    private const string MovieType = "Movie";

    /// <summary>A movie by id, with the title it had at some version.</summary>
    private sealed record Movie(int Id, string Title);

    [Fact]
    public void EachDeltaBecomesAStateHoldingWhatThatTransitionDropped()
    {
        Producer producer = new();

        producer.WriteCycle(V1);

        HollowReadStateEngine consumer = producer.ReadSnapshot();
        HollowReadStateEngine atV1 = producer.ReadSnapshot();

        HollowHistory history = new(consumer, 1L, 10);

        producer.NextCycle();
        producer.WriteCycle(V2);
        producer.ApplyDelta(consumer);
        history.DeltaOccurred(2L);

        Assert.Equal(2L, history.LatestVersion);
        Assert.Equal(1, history.NumberOfHistoricalStates);

        HollowHistoricalState state = history.HistoricalStates[0];

        Assert.Equal(2L, state.Version);
        Assert.Same(state, history.GetHistoricalState(2L));

        HollowHistoricalStateTypeKeyOrdinalMapping movies = state.KeyOrdinalMapping.GetTypeMapping(MovieType)!;

        // Cube is gone, Existenz is new, and Dune has a different title — the same key removed and
        // added again, which is what modified means here.
        Assert.Equal(1, movies.NumberOfRemovedRecords);
        Assert.Equal(1, movies.NumberOfNewRecords);
        Assert.Equal(1, movies.NumberOfModifiedRecords);

        // The record the delta dropped is still readable, at the ordinal it had before it went.
        int cubeOrdinal = OrdinalOf(atV1, id: 3);

        Assert.Equal("Cube", Title(state, cubeOrdinal));

        // And a record that did not go is read from the live state through the same data access.
        Assert.Equal("Alien", Title(state, OrdinalOf(consumer, id: 1)));
    }

    [Fact]
    public void AChangedRecordIsFoundByKeyAndReadOnBothSidesOfTheChange()
    {
        Producer producer = new();

        producer.WriteCycle(V1);

        HollowReadStateEngine consumer = producer.ReadSnapshot();
        HollowHistory history = new(consumer, 1L, 10);

        producer.NextCycle();
        producer.WriteCycle(V2);
        producer.ApplyDelta(consumer);
        history.DeltaOccurred(2L);

        HollowHistoricalState state = history.HistoricalStates[0];
        HollowHistoricalStateTypeKeyOrdinalMapping movies = state.KeyOrdinalMapping.GetTypeMapping(MovieType)!;

        // This is the path the UI takes: a key goes in, a key ordinal comes back, and that names the
        // record's place on either side of the transition.
        IntList keyOrdinals = history.KeyIndex.TypeKeyIndexes[MovieType].QueryIndexedFields("4");

        Assert.Equal(1, keyOrdinals.Count);

        int keyOrdinal = keyOrdinals.Get(0);

        Assert.Equal("Dune", Title(state, movies.FindRemovedOrdinal(keyOrdinal)));
        Assert.Equal("Dune: Part Two", Title(state, movies.FindAddedOrdinal(keyOrdinal)));
    }

    [Fact]
    public void StatesAccumulateAndTheOldestFallOffTheEnd()
    {
        Producer producer = new();

        producer.WriteCycle(V1);

        HollowReadStateEngine consumer = producer.ReadSnapshot();
        HollowHistory history = new(consumer, 1L, maxHistoricalStatesToKeep: 2);

        foreach ((Movie[] cycle, long version) in new[] { (V2, 2L), (V3, 3L), (V4, 4L) })
        {
            producer.NextCycle();
            producer.WriteCycle(cycle);
            producer.ApplyDelta(consumer);
            history.DeltaOccurred(version);
        }

        Assert.Equal(2, history.NumberOfHistoricalStates);
        Assert.Equal([4L, 3L], history.HistoricalStates.Select(state => state.Version));

        // The dropped state is no longer reachable by version either.
        Assert.Null(history.GetHistoricalState(2L));

        // Newest first in the list, oldest first along the chain.
        Assert.Equal(4L, history.HistoricalStates[1].NextState!.Version);
    }

    [Fact]
    public void ARecordDroppedSeveralVersionsBackIsStillFoundByKey()
    {
        Producer producer = new();

        producer.WriteCycle(V1);

        HollowReadStateEngine consumer = producer.ReadSnapshot();
        HollowHistory history = new(consumer, 1L, 10);

        foreach ((Movie[] cycle, long version) in new[] { (V2, 2L), (V3, 3L), (V4, 4L) })
        {
            producer.NextCycle();
            producer.WriteCycle(cycle);
            producer.ApplyDelta(consumer);
            history.DeltaOccurred(version);
        }

        // Cube went at v2 and never came back. Three versions later its key still has an ordinal, and
        // the state that dropped it still has the record.
        int cubeKey = history.KeyIndex.TypeKeyIndexes[MovieType].QueryIndexedFields("3").Get(0);

        HollowHistoricalState v2 = history.GetHistoricalState(2L)!;
        int cubeOrdinal = v2.KeyOrdinalMapping.GetTypeMapping(MovieType)!.FindRemovedOrdinal(cubeKey);

        Assert.NotEqual(HollowConstants.OrdinalNone, cubeOrdinal);
        Assert.Equal("Cube", Title(v2, cubeOrdinal));

        // No later state removed it, because by then it was already gone.
        foreach (HollowHistoricalState state in history.HistoricalStates.Where(state => state.Version > 2L))
        {
            Assert.Equal(
                HollowConstants.OrdinalNone,
                state.KeyOrdinalMapping.GetTypeMapping(MovieType)!.FindRemovedOrdinal(cubeKey));
        }
    }

    [Fact]
    public void AReverseDeltaExtendsTheHistoryBackwards()
    {
        Producer producer = new();

        // v0 comes before the history starts; it is what the reverse delta will walk back to.
        producer.WriteCycle(V0);

        HollowReadStateEngine forward = producer.ReadSnapshot();
        HollowReadStateEngine backward = producer.ReadSnapshot();

        producer.NextCycle();
        producer.WriteCycle(V1);

        byte[] toV1 = producer.Delta();
        byte[] backToV0 = producer.ReverseDelta();

        producer.Apply(forward, toV1);
        producer.Apply(backward, toV1);

        // Both engines now sit at v1; the forward one takes the delta to v2, the backward one the
        // reverse delta back to v0.
        HollowHistory history = new(forward, backward, 1L, 1L, 10);

        producer.NextCycle();
        producer.WriteCycle(V2);
        producer.Apply(forward, producer.Delta());
        history.DeltaOccurred(2L);

        producer.Apply(backward, backToV0);
        history.ReverseDeltaOccurred(0L);

        Assert.Equal(0L, history.OldestVersion);
        Assert.Equal([2L, 1L], history.HistoricalStates.Select(state => state.Version));

        // A state records what changed on the way into its version, so v1's records the v0 -> v1
        // transition: Cube arrived, Gattaca went. Walking backwards those come from the reverse
        // delta's additions and removals the other way round, which is what makes Gattaca — gone from
        // every later state — the record v1 has to keep.
        HollowHistoricalState v1 = history.GetHistoricalState(1L)!;
        HollowHistoricalStateTypeKeyOrdinalMapping movies = v1.KeyOrdinalMapping.GetTypeMapping(MovieType)!;

        int gattacaKey = history.KeyIndex.TypeKeyIndexes[MovieType].QueryIndexedFields("7").Get(0);
        int gattacaOrdinal = movies.FindRemovedOrdinal(gattacaKey);

        Assert.NotEqual(HollowConstants.OrdinalNone, gattacaOrdinal);
        Assert.Equal("Gattaca", Title(v1, gattacaOrdinal));

        int cubeKey = history.KeyIndex.TypeKeyIndexes[MovieType].QueryIndexedFields("3").Get(0);

        Assert.NotEqual(HollowConstants.OrdinalNone, movies.FindAddedOrdinal(cubeKey));

        // And its chain runs on to the newer state.
        Assert.Equal(2L, v1.NextState!.Version);
    }

    [Fact]
    public void ADoubleSnapshotStitchesTheExistingHistoryOnToTheNewState()
    {
        Producer producer = new();

        producer.WriteCycle(V1);

        HollowReadStateEngine consumer = producer.ReadSnapshot();
        HollowReadStateEngine atV1 = producer.ReadSnapshot();

        HollowHistory history = new(consumer, 1L, 10);

        producer.NextCycle();
        producer.WriteCycle(V2);
        producer.ApplyDelta(consumer);
        history.DeltaOccurred(2L);

        // A separate producer, so the new state's ordinals bear no relation to the old ones.
        Producer afresh = new();
        afresh.WriteCycle(V3);

        HollowReadStateEngine reloaded = afresh.ReadSnapshot();

        history.DoubleSnapshotOccurred(reloaded, 3L);

        Assert.Equal(3L, history.LatestVersion);
        Assert.Same(reloaded, history.LatestState);
        Assert.Equal([3L, 2L], history.HistoricalStates.Select(state => state.Version));

        HollowHistoricalStateTypeKeyOrdinalMapping movies =
            history.HistoricalStates[0].KeyOrdinalMapping.GetTypeMapping(MovieType)!;

        // Between v2 and v3 Alien went and Fargo arrived; Dune: Part Two and Existenz carried over.
        IntList alien = history.KeyIndex.TypeKeyIndexes[MovieType].QueryIndexedFields("1");
        IntList fargo = history.KeyIndex.TypeKeyIndexes[MovieType].QueryIndexedFields("6");

        Assert.NotEqual(
            HollowConstants.OrdinalNone, movies.FindRemovedOrdinal(alien.Get(0)));
        Assert.NotEqual(
            HollowConstants.OrdinalNone, movies.FindAddedOrdinal(fargo.Get(0)));

        // The state that was already in the history has been rebuilt against the new ordinal space, and
        // still answers for the record it kept — at the ordinal that space gave it.
        HollowHistoricalState v2 = history.GetHistoricalState(2L)!;
        int cubeKey = history.KeyIndex.TypeKeyIndexes[MovieType].QueryIndexedFields("3").Get(0);
        int cubeOrdinal = v2.KeyOrdinalMapping.GetTypeMapping(MovieType)!.FindRemovedOrdinal(cubeKey);

        Assert.NotEqual(HollowConstants.OrdinalNone, cubeOrdinal);
        Assert.Equal("Cube", Title(v2, cubeOrdinal));
    }

    [Fact]
    public void ADoubleSnapshotHasToMoveTheHistoryForwards()
    {
        Producer producer = new();
        producer.WriteCycle(V1);

        HollowReadStateEngine consumer = producer.ReadSnapshot();
        HollowHistory history = new(consumer, 5L, 10);

        Assert.Throws<ArgumentException>(() => history.DoubleSnapshotOccurred(consumer, 5L));
    }

    [Fact]
    public void AReverseStateEngineHasToMeetTheForwardOne()
    {
        Producer producer = new();
        producer.WriteCycle(V1);

        HollowReadStateEngine consumer = producer.ReadSnapshot();
        HollowReadStateEngine other = producer.ReadSnapshot();

        HollowHistory history = new(consumer, 1L, 10);

        // A reverse state at a different version would leave a gap in the middle of the history.
        Assert.Throws<InvalidOperationException>(() => history.InitializeReverseStateEngine(other, 7L));

        history.InitializeReverseStateEngine(other, 1L);

        Assert.Same(other, history.OldestState);
        Assert.Throws<InvalidOperationException>(() => history.InitializeReverseStateEngine(other, 1L));
    }

    [Fact]
    public void AHistoryWithoutAReverseStateEngineRefusesAReverseDelta()
    {
        Producer producer = new();
        producer.WriteCycle(V1);

        HollowHistory history = new(producer.ReadSnapshot(), 1L, 10);

        Assert.Throws<InvalidOperationException>(() => history.ReverseDeltaOccurred(0L));
    }

    [Fact]
    public void AutoDiscoveryCanBeTurnedOff()
    {
        Producer producer = new();
        producer.WriteCycle(V1);

        HollowReadStateEngine consumer = producer.ReadSnapshot();

        HollowHistory discovered = new(consumer, 1L, 10);
        HollowHistory bare = new(consumer, 1L, 10, autoDiscoverTypeIndex: false);

        Assert.Contains(MovieType, discovered.KeyIndex.TypeKeyIndexes.Keys);
        Assert.Empty(bare.KeyIndex.TypeKeyIndexes);

        // The types to follow can still be named by hand afterwards.
        bare.KeyIndex.IndexTypeField(new PrimaryKey(MovieType, "id"), consumer);

        Assert.Contains(MovieType, bare.KeyIndex.TypeKeyIndexes.Keys);
    }

    private static Movie[] V0 { get; } = [new(1, "Alien"), new(4, "Dune"), new(7, "Gattaca")];

    private static Movie[] V1 { get; } = [new(1, "Alien"), new(3, "Cube"), new(4, "Dune")];

    private static Movie[] V2 { get; } =
        [new(1, "Alien"), new(4, "Dune: Part Two"), new(5, "Existenz")];

    private static Movie[] V3 { get; } =
        [new(4, "Dune: Part Two"), new(5, "Existenz"), new(6, "Fargo")];

    private static Movie[] V4 { get; } = [new(5, "Existenz"), new(6, "Fargo")];

    private static string? Title(HollowHistoricalState state, int ordinal)
    {
        IHollowObjectTypeDataAccess movies =
            Assert.IsAssignableFrom<IHollowObjectTypeDataAccess>(state.DataAccess.GetTypeDataAccess(MovieType));

        return movies.ReadString(ordinal, movies.Schema.GetPosition("title"));
    }

    /// <summary>The ordinal the movie with <paramref name="id"/> sits at in <paramref name="engine"/>.</summary>
    private static int OrdinalOf(HollowReadStateEngine engine, int id)
    {
        HollowObjectTypeReadState movies =
            Assert.IsType<HollowObjectTypeReadState>(engine.GetTypeState(MovieType));

        int idPosition = movies.Schema.GetPosition("id");

        return movies.PopulatedOrdinals.EnumerateSetBits().Single(ordinal =>
            movies.ReadInt(ordinal, idPosition) == id);
    }

    /// <summary>A producer of movies keyed by id, held across cycles so deltas can be written.</summary>
    private sealed class Producer
    {
        private readonly HollowObjectSchema _movieSchema;
        private readonly HollowWriteStateEngine _writeEngine;

        internal Producer()
        {
            _movieSchema = new HollowObjectSchema(MovieType, 2, new PrimaryKey(MovieType, "id"));
            _movieSchema.AddField("id", FieldType.Int);
            _movieSchema.AddField("title", FieldType.String);

            _writeEngine = new HollowWriteStateEngine { RandomizedTag = 1 };
            _writeEngine.AddTypeState(new HollowObjectTypeWriteState(_movieSchema));
        }

        internal void WriteCycle(Movie[] movies)
        {
            HollowObjectWriteRecord record = new(_movieSchema);

            foreach (Movie movie in movies)
            {
                record.Reset();
                record.SetInt("id", movie.Id);
                record.SetString("title", movie.Title);

                _writeEngine.Add(MovieType, record);
            }
        }

        internal void NextCycle()
        {
            _writeEngine.PrepareForNextCycle();
            _writeEngine.RandomizedTag++;
        }

        internal HollowReadStateEngine ReadSnapshot()
        {
            using MemoryStream blob = new();

            new HollowBlobWriter(_writeEngine).WriteSnapshot(blob);
            blob.Position = 0;

            HollowReadStateEngine engine = new();
            new HollowBlobReader(engine).ReadSnapshot(blob);

            return engine;
        }

        internal void ApplyDelta(HollowReadStateEngine consumer) => Apply(consumer, Delta());

        /// <summary>The delta from the previous cycle to this one.</summary>
        internal byte[] Delta()
        {
            using MemoryStream blob = new();

            new HollowBlobWriter(_writeEngine).WriteDelta(blob);

            return blob.ToArray();
        }

        /// <summary>The delta from this cycle back to the previous one.</summary>
        internal byte[] ReverseDelta()
        {
            using MemoryStream blob = new();

            new HollowBlobWriter(_writeEngine).WriteReverseDelta(blob);

            return blob.ToArray();
        }

        internal void Apply(HollowReadStateEngine consumer, byte[] delta)
        {
            using MemoryStream blob = new(delta);

            new HollowBlobReader(consumer).ApplyDelta(blob);
        }
    }
}
