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

using Hollow.Api.Objects.Generic;
using Hollow.Api.Producer;
using Hollow.Api.Producer.Validation;
using Hollow.Core;
using Hollow.Core.Read.Engine;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Tests.Api;

/// <summary>
/// The validators that judge a cycle by what it did rather than by what it holds.
/// </summary>
/// <remarks>
/// A producer runs these against the read state it has just built and refuses to announce the version if
/// one of them fails, so each test either drives a real cycle or hands a validator a real read state —
/// never a stub, since what these validators are getting right is how they read a dataset.
/// </remarks>
public class ValidatorTests
{
    [HollowPrimaryKey("Id")]
    public sealed record Movie(int Id, string Title);

    [HollowPrimaryKey("Code")]
    public sealed record Coupon(string? Code, int Value);

    public sealed record Unkeyed(int Id);

    /// <summary>Mints 1, 2, 3… so a test can name the versions it expects.</summary>
    private sealed class CountingVersionMinter : IVersionMinter
    {
        private long _version;

        public long Mint() => ++_version;
    }

    /// <summary>A version and its records, which is all a validator is given.</summary>
    private sealed record TestReadState(long Version, HollowReadStateEngine StateEngine) : IReadState;

    private static HollowProducer Producer(
        InMemoryPublisher blobStore, Type modelType, params IValidatorListener[] validators)
    {
        HollowProducer producer = new HollowProducerBuilder()
            .WithPublisher(blobStore)
            .WithAnnouncer(blobStore)
            .WithVersionMinter(new CountingVersionMinter())
            .WithValidators(validators)
            .Build();

        producer.InitializeDataModel(modelType);

        return producer;
    }

    private static Populator Movies(params Movie[] movies) =>
        state =>
        {
            foreach (Movie movie in movies)
            {
                state.Add(movie);
            }
        };

    private static Populator Coupons(params Coupon[] coupons) =>
        state =>
        {
            foreach (Coupon coupon in coupons)
            {
                state.Add(coupon);
            }
        };

    private static Movie[] Catalogue(int count, string titlePrefix = "movie") =>
        [.. Enumerable.Range(1, count).Select(i => new Movie(i, $"{titlePrefix} {i}"))];

    /// <summary>
    /// The state a validator would be handed after <paramref name="cycles"/> were published, read the
    /// way a consumer reads it: a snapshot and then the deltas, so that a validator asking what the last
    /// cycle changed has a previous cycle to find.
    /// </summary>
    private static IReadState Published(Type modelType, params Populator[] cycles)
    {
        InMemoryPublisher blobStore = new();
        HollowProducer producer = Producer(blobStore, modelType);

        long version = HollowConstants.VersionNone;
        HollowReadStateEngine readEngine = new();
        HollowBlobReader reader = new(readEngine);

        foreach (Populator cycle in cycles)
        {
            long previous = version;
            version = producer.RunCycle(cycle);

            using Stream blob = previous == HollowConstants.VersionNone
                ? blobStore.RetrieveSnapshotBlob(version)!.OpenStream()
                : blobStore.RetrieveDeltaBlob(previous)!.OpenStream();

            if (previous == HollowConstants.VersionNone)
            {
                reader.ReadSnapshot(blob);
            }
            else
            {
                reader.ApplyDelta(blob);
            }
        }

        return new TestReadState(version, readEngine);
    }

    /// <summary>The single result of the single validator that failed.</summary>
    private static ValidationResult Refusal(ValidationStatusException e) =>
        Assert.Single(e.ValidationStatus.Results, result => !result.IsPassed);

    [Fact]
    public void TheMinimumCountValidatorCatchesATypeFallingBelowItsFloor()
    {
        InMemoryPublisher blobStore = new();
        HollowProducer producer =
            Producer(blobStore, typeof(Movie), new MinimumRecordCountValidator("Movie", 10));

        producer.RunCycle(Movies(Catalogue(20)));
        Assert.Equal(1, blobStore.AnnouncedVersion);

        // Ten is the floor, not the first value below it.
        producer.RunCycle(Movies(Catalogue(10)));
        Assert.Equal(2, blobStore.AnnouncedVersion);

        ValidationStatusException e =
            Assert.Throws<ValidationStatusException>(() => producer.RunCycle(Movies(Catalogue(9))));

        Assert.Contains("fewer than the required 10", Refusal(e).Message!, StringComparison.Ordinal);
        Assert.Equal(2, blobStore.AnnouncedVersion);
    }

    /// <summary>
    /// The floor is read afresh each cycle, so a producer that takes it from configuration picks up a
    /// change without being restarted.
    /// </summary>
    [Fact]
    public void TheMinimumCountValidatorRereadsAThresholdThatMoves()
    {
        int floor = 100;

        InMemoryPublisher blobStore = new();
        HollowProducer producer =
            Producer(blobStore, typeof(Movie), new MinimumRecordCountValidator("Movie", () => floor));

        Assert.Throws<ValidationStatusException>(() => producer.RunCycle(Movies(Catalogue(5))));

        floor = 5;
        producer.RunCycle(Movies(Catalogue(5)));

        Assert.Equal(2, blobStore.AnnouncedVersion);
    }

    /// <summary>
    /// A threshold no dataset could ever meet is a mistake in the validator's own configuration rather
    /// than a fault in the data, so it is reported as an error rather than as a failed dataset.
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData((1 << 29) + 1)]
    public void TheMinimumCountValidatorRejectsAnImpossibleThreshold(int floor)
    {
        ValidationResult result =
            new MinimumRecordCountValidator("Movie", floor).OnValidate(
                Published(typeof(Movie), Movies(Catalogue(5))));

        Assert.Equal(ValidationResultType.Error, result.ResultType);
        Assert.IsType<ArgumentOutOfRangeException>(result.Exception);
    }

    /// <summary>
    /// A validator watching a type the dataset does not have cannot vouch for anything, so it says so
    /// rather than quietly passing.
    /// </summary>
    [Fact]
    public void TheMinimumCountValidatorFailsForATypeThatIsNotThere()
    {
        ValidationResult result =
            new MinimumRecordCountValidator("Actor", 1).OnValidate(
                Published(typeof(Movie), Movies(Catalogue(5))));

        Assert.False(result.IsPassed);
        Assert.Contains("which is not present", result.Message!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A null key field makes a record unreachable by the very thing the model says identifies it, which
    /// none of the count or duplicate validators notice.
    /// </summary>
    [Fact]
    public void TheNullKeyValidatorCatchesARecordWithNoKey()
    {
        InMemoryPublisher blobStore = new();
        HollowProducer producer =
            Producer(blobStore, typeof(Coupon), new NullPrimaryKeyFieldValidator(typeof(Coupon)));

        producer.RunCycle(Coupons(new Coupon("SPRING", 10), new Coupon("SUMMER", 20)));
        Assert.Equal(1, blobStore.AnnouncedVersion);

        ValidationStatusException e = Assert.Throws<ValidationStatusException>(
            () => producer.RunCycle(Coupons(new Coupon("SPRING", 10), new Coupon(null, 30))));

        ValidationResult result = Refusal(e);

        Assert.Contains(
            "has records with a null in its primary key", result.Message!, StringComparison.Ordinal);
        Assert.Equal("1", result.Details["NullKeyRecordCount"]);
        Assert.Equal(1, blobStore.AnnouncedVersion);
    }

    /// <summary>
    /// Which fields to check comes from the model, so a type that declares no key is a mistake the
    /// caller can be told about before a cycle ever runs.
    /// </summary>
    [Fact]
    public void TheNullKeyValidatorNeedsAModelThatDeclaresAKey()
    {
        ArgumentException e =
            Assert.Throws<ArgumentException>(() => new NullPrimaryKeyFieldValidator(typeof(Unkeyed)));

        Assert.Contains(nameof(HollowPrimaryKeyAttribute), e.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Named by type name instead, the key is a property of the data rather than of a CLR type, so it
    /// can only be discovered once there is data — and then it is a failed cycle, not a throw.
    /// </summary>
    [Fact]
    public void TheNullKeyValidatorFailsForDataWithNoKey()
    {
        ValidationResult result =
            new NullPrimaryKeyFieldValidator("Unkeyed").OnValidate(
                Published(typeof(Unkeyed), state => state.Add(new Unkeyed(1))));

        Assert.False(result.IsPassed);
        Assert.Contains("declares no primary key", result.Message!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A generated API would give the predicate <c>before.Title</c>; the generic wrapper has to follow
    /// the reference into the shared String type by hand, which is what a title is in the blob.
    /// </summary>
    private static string? Title(GenericHollowObject movie) =>
        movie.GetObject("Title")?.GetString("value");

    private static ObjectModificationValidator<GenericHollowObject> TitlesMayNotChange() =>
        new(
            "Movie",
            (before, after) => Title(before) == Title(after),
            (dataAccess, ordinal) => new GenericHollowObject(dataAccess, "Movie", ordinal));

    /// <summary>
    /// The class of bug no count can catch: a field that was never supposed to change, changing, in a
    /// cycle that left the record count exactly where it was.
    /// </summary>
    [Fact]
    public void TheModificationValidatorCatchesAChangeTheCountsCannotSee()
    {
        InMemoryPublisher blobStore = new();
        HollowProducer producer = Producer(blobStore, typeof(Movie), TitlesMayNotChange());

        producer.RunCycle(Movies(new Movie(1, "one"), new Movie(2, "two")));

        // A record leaving and another arriving is not a modification, whatever the titles are.
        producer.RunCycle(Movies(new Movie(1, "one"), new Movie(3, "three")));
        Assert.Equal(2, blobStore.AnnouncedVersion);

        ValidationStatusException e = Assert.Throws<ValidationStatusException>(
            () => producer.RunCycle(Movies(new Movie(1, "uno"), new Movie(3, "three"))));

        ValidationResult result = Refusal(e);

        Assert.Contains(
            "changed in a way this validator does not allow", result.Message!, StringComparison.Ordinal);
        Assert.Equal("1", result.Details["key"]);
        Assert.Equal(2, blobStore.AnnouncedVersion);
    }

    /// <summary>
    /// The first cycle of a chain replaced nothing, so there is no pair to judge and nothing to refuse.
    /// </summary>
    [Fact]
    public void TheModificationValidatorHasNothingToJudgeOnTheFirstCycle()
    {
        InMemoryPublisher blobStore = new();
        HollowProducer producer = Producer(blobStore, typeof(Movie), TitlesMayNotChange());

        producer.RunCycle(Movies(new Movie(1, "one")));

        Assert.Equal(1, blobStore.AnnouncedVersion);
    }

    /// <summary>
    /// Exactly what <see cref="RecordCountVarianceValidator"/> is blind to: every record replaced, the
    /// count untouched.
    /// </summary>
    [Fact]
    public void ThePercentChangeValidatorCatchesAWholesaleRewriteTheCountValidatorMisses()
    {
        InMemoryPublisher blobStore = new();
        InMemoryPublisher byVariance = new();

        ChangeThreshold threshold = ChangeThreshold.Create().WithUpdated(0.1f).Build();

        HollowProducer producer = Producer(
            blobStore, typeof(Movie), new RecordCountPercentChangeValidator("Movie", threshold));
        HollowProducer counting = Producer(
            byVariance, typeof(Movie), new RecordCountVarianceValidator("Movie", 1f));

        foreach (HollowProducer each in (HollowProducer[])[producer, counting])
        {
            each.RunCycle(Movies(Catalogue(100)));
        }

        // The net count does not move, so the variance validator sees nothing at all.
        counting.RunCycle(Movies(Catalogue(100, "rewritten")));
        Assert.Equal(2, byVariance.AnnouncedVersion);

        ValidationStatusException e = Assert.Throws<ValidationStatusException>(
            () => producer.RunCycle(Movies(Catalogue(100, "rewritten"))));

        ValidationResult result = Refusal(e);

        Assert.Contains("updated 100%", result.Message!, StringComparison.Ordinal);
        Assert.Equal("100", result.Details["UpdatedRecordCount"]);
        Assert.Equal("0", result.Details["AddedRecordCount"]);
        Assert.Equal(1, blobStore.AnnouncedVersion);
    }

    /// <summary>
    /// A threshold that was never set is not a threshold of zero: it is not checked, so the other two
    /// can be bounded without also having to name a bound for the third.
    /// </summary>
    [Fact]
    public void AThresholdLeftUnsetIsNotChecked()
    {
        InMemoryPublisher blobStore = new();
        HollowProducer producer = Producer(
            blobStore,
            typeof(Movie),
            new RecordCountPercentChangeValidator(
                "Movie", ChangeThreshold.Create().WithRemoved(0.5f).Build()));

        producer.RunCycle(Movies(Catalogue(10)));

        // Every record replaced, which would breach an updated threshold had one been given.
        producer.RunCycle(Movies(Catalogue(10, "rewritten")));
        Assert.Equal(2, blobStore.AnnouncedVersion);

        // And the threshold that was given still holds.
        Assert.Throws<ValidationStatusException>(() => producer.RunCycle(Movies(Catalogue(3, "rewritten"))));
        Assert.Equal(2, blobStore.AnnouncedVersion);
    }

    /// <summary>
    /// Removing more than everything is not a lenient bound, it is a typo — most likely a percentage
    /// where a fraction was meant. It is caught where it is written rather than cycles later, since a
    /// constant is checkable the moment it is given.
    /// </summary>
    [Fact]
    public void ARemovalThresholdIsAFractionRatherThanAPercentage()
    {
        ArgumentOutOfRangeException e = Assert.Throws<ArgumentOutOfRangeException>(
            () => ChangeThreshold.Create().WithRemoved(50f));

        Assert.Contains("one per cent is 0.01f", e.Message, StringComparison.Ordinal);

        // A threshold read afresh each cycle has nothing to check until it is read, so it is checked
        // then instead — and a broken one becomes an error rather than escaping OnValidate.
        float removable = 50f;

        ValidationResult result = new RecordCountPercentChangeValidator(
                "Movie", ChangeThreshold.Create().WithRemoved(() => removable).Build())
            .OnValidate(Published(typeof(Movie), Movies(Catalogue(5)), Movies(Catalogue(4))));

        Assert.Equal(ValidationResultType.Error, result.ResultType);
        Assert.IsType<ArgumentOutOfRangeException>(result.Exception);
    }
}
