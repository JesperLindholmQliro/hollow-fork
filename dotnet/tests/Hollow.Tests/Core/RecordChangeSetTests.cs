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

using Hollow.Core.Index.Key;
using Hollow.Core.Read.Engine;
using Hollow.Core.Util;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Tests.Core;

/// <summary>
/// The blob format only ever adds and removes records, so "this record was updated" is an interpretation
/// laid over it by the primary key. These tests are about that interpretation being the one a caller
/// means.
/// </summary>
public class RecordChangeSetTests
{
    [HollowPrimaryKey("Id")]
    private sealed record Movie(int Id, string Title);

    private sealed record Cycle(HollowWriteStateEngine WriteEngine, HollowReadStateEngine ReadEngine)
    {
        /// <summary>Publishes <paramref name="movies"/> as the next cycle and reads it back.</summary>
        internal Cycle Then(params Movie[] movies)
        {
            HollowObjectMapper mapper = new(WriteEngine);

            foreach (Movie movie in movies)
            {
                mapper.Add(movie);
            }

            StateEngineRoundTripper.RoundTripDelta(WriteEngine, ReadEngine);

            return this;
        }

        internal RecordChangeSet Changes(PrimaryKey? primaryKey = null) =>
            RecordChangeSet.Compute(ReadEngine, "Movie", primaryKey);
    }

    /// <summary>Publishes <paramref name="movies"/> as the first cycle of a chain.</summary>
    private static Cycle First(params Movie[] movies)
    {
        HollowWriteStateEngine writeEngine = new();
        HollowObjectMapper mapper = new(writeEngine);

        foreach (Movie movie in movies)
        {
            mapper.Add(movie);
        }

        HollowReadStateEngine readEngine = new();
        StateEngineRoundTripper.RoundTripSnapshot(writeEngine, readEngine);

        return new Cycle(writeEngine, readEngine);
    }

    private static IReadOnlyList<int> Ordinals(BitSet set) => [.. set.EnumerateSetBits()];

    /// <summary>
    /// A record whose key is unchanged but whose other fields are not is one change, not an unrelated
    /// removal and addition — which is the whole reason the key is needed.
    /// </summary>
    [Fact]
    public void ARecordRewrittenUnderTheSameKeyCountsAsUpdated()
    {
        RecordChangeSet changes = First(new Movie(1, "one"), new Movie(2, "two"))
            .Then(new Movie(1, "one"), new Movie(2, "dos"))
            .Changes();

        UpdatedRecord updated = Assert.Single(changes.Updated);

        Assert.Empty(Ordinals(changes.Added));
        Assert.Empty(Ordinals(changes.Removed));
        Assert.NotEqual(updated.FromOrdinal, updated.ToOrdinal);
        Assert.True(changes.HasPriorState);
    }

    [Fact]
    public void AddedAndRemovedRecordsAreReportedSeparately()
    {
        RecordChangeSet changes = First(new Movie(1, "one"), new Movie(2, "two"))
            .Then(new Movie(2, "two"), new Movie(3, "three"))
            .Changes();

        Assert.Empty(changes.Updated);
        Assert.Single(Ordinals(changes.Added));
        Assert.Single(Ordinals(changes.Removed));
    }

    /// <summary>
    /// All three at once, since the interesting mistake is a computation that gets one kind right by
    /// letting it swallow another.
    /// </summary>
    [Fact]
    public void AllThreeKindsOfChangeAreToldApartInOneCycle()
    {
        Cycle cycle = First(new Movie(1, "one"), new Movie(2, "two"), new Movie(3, "three"))
            .Then(new Movie(1, "one"), new Movie(2, "dos"), new Movie(4, "four"));

        RecordChangeSet changes = cycle.Changes();

        Assert.Single(changes.Updated);
        Assert.Single(Ordinals(changes.Added));
        Assert.Single(Ordinals(changes.Removed));

        HollowPrimaryKeyValueDeriver deriver = new(changes.PrimaryKey, cycle.ReadEngine);

        Assert.Equal(4, deriver.GetRecordKey(Ordinals(changes.Added)[0])[0]);
        Assert.Equal(2, deriver.GetRecordKey(changes.Updated[0].ToOrdinal)[0]);
    }

    /// <summary>
    /// A record left alone is neither added nor removed, so it is in none of the three sets — a caller
    /// iterating them is iterating the change, not the dataset.
    /// </summary>
    [Fact]
    public void AnUntouchedRecordIsInNoneOfTheSets()
    {
        RecordChangeSet changes = First(new Movie(1, "one"), new Movie(2, "two"))
            .Then(new Movie(1, "one"), new Movie(2, "two"), new Movie(3, "three"))
            .Changes();

        Assert.Single(Ordinals(changes.Added));
        Assert.Empty(Ordinals(changes.Removed));
        Assert.Empty(changes.Updated);
    }

    /// <summary>
    /// A snapshot carries no memory of what came before, so every record would otherwise look added —
    /// which is why the caller is told there was nothing to compare against rather than being handed a
    /// change set that says the whole dataset appeared.
    /// </summary>
    [Fact]
    public void ASnapshotReportsNoPriorState()
    {
        RecordChangeSet changes = First(new Movie(1, "one")).Changes();

        Assert.False(changes.HasPriorState);
        Assert.Single(Ordinals(changes.Added));
    }

    /// <summary>
    /// A cycle that changed nothing is a delta over no records at all, not a rewrite of everything.
    /// </summary>
    [Fact]
    public void ACycleThatChangedNothingReportsNoChanges()
    {
        RecordChangeSet changes =
            First(new Movie(1, "one")).Then(new Movie(1, "one")).Changes();

        Assert.Empty(Ordinals(changes.Added));
        Assert.Empty(Ordinals(changes.Removed));
        Assert.Empty(changes.Updated);
        Assert.True(changes.HasPriorState);
    }

    /// <summary>
    /// The key decides what counts as the same record, so a caller can ask a different question of the
    /// same cycle by supplying one.
    /// </summary>
    [Fact]
    public void AnOverridingKeyChangesWhatCountsAsTheSameRecord()
    {
        Cycle cycle = First(new Movie(1, "one"), new Movie(2, "two"))
            .Then(new Movie(1, "uno"), new Movie(2, "two"));

        // By id the first movie was rewritten; by title it went away and an unrelated one arrived.
        Assert.Single(cycle.Changes().Updated);

        RecordChangeSet byTitle = cycle.Changes(new PrimaryKey("Movie", "Title"));

        Assert.Empty(byTitle.Updated);
        Assert.Single(Ordinals(byTitle.Added));
        Assert.Single(Ordinals(byTitle.Removed));
    }

    [Fact]
    public void AKeylessTypeCannotBeCompared()
    {
        HollowWriteStateEngine writeEngine = new();
        new HollowObjectMapper(writeEngine).Add(new Unkeyed(1));

        HollowReadStateEngine readEngine = StateEngineRoundTripper.RoundTripSnapshot(writeEngine);

        ArgumentException e = Assert.Throws<ArgumentException>(
            () => RecordChangeSet.Compute(readEngine, "Unkeyed"));

        Assert.Contains("declares no primary key", e.Message, StringComparison.Ordinal);
    }

    private sealed record Unkeyed(int Id);

    [Fact]
    public void AnAbsentTypeCannotBeCompared()
    {
        HollowReadStateEngine readEngine = StateEngineRoundTripper.RoundTripSnapshot(new HollowWriteStateEngine());

        ArgumentException e = Assert.Throws<ArgumentException>(
            () => RecordChangeSet.Compute(readEngine, "Movie"));

        Assert.Contains("is not an object type", e.Message, StringComparison.Ordinal);
    }
}
