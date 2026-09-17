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

using Hollow.Api.Sampling;
using Hollow.Core.Read.Filter;
using Hollow.Core.Schema;

namespace Hollow.Tests.Api.Sampling;

/// <summary>
/// Counting which fields an application actually reads.
/// </summary>
/// <remarks>
/// Java has no tests for <c>api.sampling</c>. These cover the two things that decide whether the
/// numbers mean anything: which reads a director admits, and which counter a read lands in.
/// </remarks>
public class SamplingTests
{
    [Fact]
    public void TheDisabledDirectorCountsNothingAndStaysThatWay()
    {
        HollowSamplingDirector director = DisabledSamplingDirector.Instance;

        Assert.False(director.ShouldRecord());

        // An update scope is meaningless for a director that admits nothing.
        using (HollowSamplingScope.EnterUpdate())
        {
            Assert.False(director.ShouldRecord());
        }

        Assert.False(director.ShouldRecord());
    }

    [Fact]
    public void TheDatasetsOwnReadsDoNotCount()
    {
        EnabledSamplingDirector director = new();

        Assert.True(director.ShouldRecord());

        // A refresh reads records to build indexes and checksums; those are not the application's.
        using (HollowSamplingScope.EnterUpdate())
        {
            Assert.False(director.ShouldRecord());
        }

        // The scope restores what surrounded it, so the application counts again.
        Assert.True(director.ShouldRecord());
    }

    [Fact]
    public void ATimeSlicedDirectorAlternatesBetweenALongOffAndAShortOn()
    {
        FakeTimeProvider time = new();

        using TimeSliceSamplingDirector director =
            new(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(5), time);

        director.StartSampling();

        // Counting starts off, so the first flip is the one that turns it on.
        Assert.False(director.ShouldRecord());
        Assert.Equal(TimeSpan.FromSeconds(1), time.Timer!.DueTime);

        time.Timer.Fire();

        Assert.True(director.ShouldRecord());
        Assert.Equal(TimeSpan.FromMilliseconds(5), time.Timer.DueTime);

        time.Timer.Fire();

        Assert.False(director.ShouldRecord());
        Assert.Equal(TimeSpan.FromSeconds(1), time.Timer.DueTime);
    }

    [Fact]
    public void StoppingLeavesCountingOffAndReleasesTheTimer()
    {
        FakeTimeProvider time = new();

        TimeSliceSamplingDirector director =
            new(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(5), time);

        director.StartSampling();
        time.Timer!.Fire();

        Assert.True(director.ShouldRecord());

        director.StopSampling();

        Assert.False(director.ShouldRecord());
        Assert.True(time.Timer.Disposed);
    }

    [Fact]
    public void StartingAnAlreadyStartedDirectorDoesNothing()
    {
        FakeTimeProvider time = new();

        using TimeSliceSamplingDirector director = new(time);

        director.StartSampling();

        FakeTimeProvider.FakeTimer? first = time.Timer;

        director.StartSampling();

        Assert.Same(first, time.Timer);
    }

    [Fact]
    public void AListenerLearnsTheStateWhenItIsAddedAndWheneverItFlips()
    {
        FakeTimeProvider time = new();

        using TimeSliceSamplingDirector director = new(time);

        StatusRecorder recorder = new();

        director.AddSamplingStatusListener(recorder);

        // Told straight away, so a listener never starts out of step with the director.
        Assert.False(recorder.Last);

        director.StartSampling();
        time.Timer!.Fire();

        Assert.True(recorder.Last);

        time.Timer.Fire();

        Assert.False(recorder.Last);
    }

    [Fact]
    public void AListenerAddedWhileCountingIsOnIsToldSo()
    {
        FakeTimeProvider time = new();

        using TimeSliceSamplingDirector director = new(time);

        director.StartSampling();
        time.Timer!.Fire();

        StatusRecorder recorder = new();
        director.AddSamplingStatusListener(recorder);

        Assert.True(recorder.Last);
    }

    [Fact]
    public void AnObjectTypeCountsNothingUntilADirectorSaysSo()
    {
        HollowObjectSampler sampler = new(MovieSchema(), DisabledSamplingDirector.Instance);

        sampler.RecordFieldAccess(0);

        Assert.False(sampler.HasSampleResults);

        sampler.SetSamplingDirector(new EnabledSamplingDirector());
        sampler.RecordFieldAccess(0);

        Assert.True(sampler.HasSampleResults);
        Assert.Equal(1, Count(sampler, "Movie.Id"));
    }

    [Fact]
    public void AFieldSpecificDirectorCountsOnlyTheFieldsItNames()
    {
        HollowObjectSampler sampler = new(MovieSchema(), DisabledSamplingDirector.Instance);

        // Sampling two suspect fields at full rate costs far less than sampling everything.
        sampler.SetFieldSpecificSamplingDirector(
            Fields(("Movie", "Title")), new EnabledSamplingDirector());

        sampler.RecordFieldAccess(0);
        sampler.RecordFieldAccess(1);

        Assert.Equal(0, Count(sampler, "Movie.Id"));
        Assert.Equal(1, Count(sampler, "Movie.Title"));
    }

    [Fact]
    public void AnObjectTypeReportsOneResultPerFieldAndResetsToZero()
    {
        HollowObjectSampler sampler = new(MovieSchema(), new EnabledSamplingDirector());

        sampler.RecordFieldAccess(1);
        sampler.RecordFieldAccess(1);

        Assert.Equal(["Movie.Id", "Movie.Title", "Movie.Year"], sampler.GetSampleResults().Select(result => result.Identifier));
        Assert.Equal(2, Count(sampler, "Movie.Title"));

        sampler.Reset();

        Assert.False(sampler.HasSampleResults);
        Assert.Equal(0, Count(sampler, "Movie.Title"));
    }

    [Fact]
    public void TheNullSamplersCannotBeTurnedOn()
    {
        // A type state with no type holds one of these, and turning sampling on globally must not
        // start it counting against a type name that does not exist.
        HollowObjectSampler.Null.SetSamplingDirector(new EnabledSamplingDirector());
        HollowListSampler.Null.SetSamplingDirector(new EnabledSamplingDirector());
        HollowListSampler.Null.RecordGet();

        Assert.Empty(HollowObjectSampler.Null.GetSampleResults());
        Assert.False(HollowObjectSampler.Null.HasSampleResults);
        Assert.False(HollowListSampler.Null.HasSampleResults);
    }

    [Fact]
    public void TheThreeCollectionOperationsAreCountedSeparately()
    {
        HollowListSampler sampler = new("ListOfMovie", new EnabledSamplingDirector());

        sampler.RecordSize();
        sampler.RecordGet();
        sampler.RecordGet();
        sampler.RecordIterator();

        Assert.Equal(1, Count(sampler, "ListOfMovie.Count"));
        Assert.Equal(2, Count(sampler, "ListOfMovie.Get()"));
        Assert.Equal(1, Count(sampler, "ListOfMovie.Enumerate()"));
    }

    [Fact]
    public void AMapAlsoCountsTheBucketsAKeyLookupReads()
    {
        HollowMapSampler sampler = new("MapOfStringToMovie", new EnabledSamplingDirector());

        sampler.RecordBucketRetrieval();
        sampler.RecordBucketRetrieval();
        sampler.RecordBucketRetrieval();

        // Many buckets read per lookup is a hash key doing badly, which the lookup count alone hides.
        Assert.True(sampler.HasSampleResults);
        Assert.Equal(3, Count(sampler, "MapOfStringToMovie.BucketValue()"));
        Assert.Equal(0, Count(sampler, "MapOfStringToMovie.Get()"));

        sampler.Reset();

        Assert.False(sampler.HasSampleResults);
    }

    [Fact]
    public void ATypeSpecificDirectorSelectsWholeCollectionTypes()
    {
        HollowListSampler selected = new("ListOfMovie", DisabledSamplingDirector.Instance);
        HollowListSampler other = new("ListOfActor", DisabledSamplingDirector.Instance);

        ITypeFilter filter = TypeFilter.Include(["ListOfMovie"]);

        selected.SetFieldSpecificSamplingDirector(filter, new EnabledSamplingDirector());
        other.SetFieldSpecificSamplingDirector(filter, new EnabledSamplingDirector());

        selected.RecordGet();
        other.RecordGet();

        Assert.True(selected.HasSampleResults);
        Assert.False(other.HasSampleResults);
    }

    [Fact]
    public void CreationsAreReportedWithTheMostMaterialisedTypeFirst()
    {
        HollowObjectCreationSampler sampler = new("Movie", "Actor", "String");

        sampler.SetSamplingDirector(new EnabledSamplingDirector());

        sampler.RecordCreation(0);
        sampler.RecordCreation(0);
        sampler.RecordCreation(2);

        Assert.Equal(
            ["Movie", "String", "Actor"],
            sampler.GetSampleResults().Select(result => result.Identifier));

        Assert.Equal([2, 1, 0], sampler.GetSampleResults().Select(result => result.SampleCount));
    }

    [Fact]
    public void ResultsSortHottestFirstAndTiesByName()
    {
        SampleResult[] results = [new("b", 1), new("a", 5), new("a", 1)];

        Array.Sort(results);

        Assert.Equal(["a", "a", "b"], results.Select(result => result.Identifier));
        Assert.Equal([5, 1, 1], results.Select(result => result.SampleCount));
        Assert.Equal("a: 5", results[0].ToString());
    }

    private static long Count(IHollowSampler sampler, string identifier) =>
        sampler.GetSampleResults()
            .Single(result => string.Equals(result.Identifier, identifier, StringComparison.Ordinal))
            .SampleCount;

    private static HollowObjectSchema MovieSchema()
    {
        HollowObjectSchema schema = new("Movie", 3, "Id");

        schema.AddField("Id", FieldType.Int);
        schema.AddField("Title", FieldType.String);
        schema.AddField("Year", FieldType.Int);

        return schema;
    }

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

    private sealed class StatusRecorder : ISamplingStatusListener
    {
        internal bool Last { get; private set; }

        public void SamplingStatusChanged(bool samplingOn) => Last = samplingOn;
    }

    /// <summary>A clock whose single timer a test fires by hand.</summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        internal FakeTimer? Timer { get; private set; }

        public override ITimer CreateTimer(
            TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            Timer = new FakeTimer(callback, state, dueTime);

        internal sealed class FakeTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
        {
            internal TimeSpan DueTime { get; private set; } = dueTime;

            internal bool Disposed { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                DueTime = dueTime;

                return true;
            }

            public void Dispose() => Disposed = true;

            public ValueTask DisposeAsync()
            {
                Disposed = true;

                return ValueTask.CompletedTask;
            }

            internal void Fire() => callback(state);
        }
    }
}
