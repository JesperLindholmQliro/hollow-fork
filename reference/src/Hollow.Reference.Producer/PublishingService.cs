/*
 *  Copyright 2016 Netflix, Inc.
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

using Hollow.Api.Consumer;
using Hollow.Api.Producer;
using Hollow.Api.Producer.Listener;
using Hollow.Api.Producer.Validation;
using Hollow.Reference.Infrastructure;
using Hollow.Reference.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Both halves of Hollow name a type Blob, and this file needs the consumer's IBlobRetriever and the
// producer's Blob at once.
using Blob = Hollow.Api.Producer.Blob;
using HollowProducer = Hollow.Api.Producer.HollowProducer;

namespace Hollow.Reference.Producer;

/// <summary>
/// Publishes the whole catalogue, over and over, until the host is stopped.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>how.hollow.producer.Producer</c>'s <c>main</c>, <c>restoreIfAvailable</c> and
/// <c>cycleForever</c>. The shape is the same — restore, then cycle on a timer — and the differences
/// are all about being a service rather than a script: the infrastructure is injected, the wait is
/// cancellable, and a failed cycle is logged and retried instead of ending the process.
/// </para>
/// <para>
/// A producer hands Hollow the entire dataset every cycle, not the changes. Working out what changed is
/// Hollow's job, and doing it centrally is what makes a consumer's update a delta rather than a
/// reload.
/// </para>
/// </remarks>
internal sealed class PublishingService : BackgroundService
{
    private readonly IPublisher _publisher;
    private readonly IAnnouncer _announcer;
    private readonly IBlobRetriever _blobRetriever;
    private readonly IAnnouncementWatcher _announcementWatcher;
    private readonly HollowReferenceOptions _options;
    private readonly ILogger<PublishingService> _logger;

    public PublishingService(
        IPublisher publisher,
        IAnnouncer announcer,
        IBlobRetriever blobRetriever,
        IAnnouncementWatcher announcementWatcher,
        HollowReferenceOptions options,
        ILogger<PublishingService> logger)
    {
        _publisher = publisher;
        _announcer = announcer;
        _blobRetriever = blobRetriever;
        _announcementWatcher = announcementWatcher;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Producing into the {Mode} infrastructure, under the namespace {Namespace}.",
            _options.Mode,
            _options.Namespace);

        HollowProducer producer = new HollowProducerBuilder()
            .WithPublisher(_publisher)
            .WithAnnouncer(_announcer)

            // Blobs are written here before they are published, and the staging file is what the
            // publisher reads. Somewhere local and disposable is right in every mode: in the cloud
            // modes it is the file that gets uploaded, and in the local mode it is one copy away from
            // where it is going anyway.
            .WithBlobStagingDirectory(Path.Combine(Path.GetTempPath(), "hollow-reference-staging"))

            .WithListeners(new CycleLogger(_logger))

            // The Java reference implementation runs no validators. These two are the cheapest way to
            // catch the failure a producer actually has, which is not a corrupt blob but a bad upstream
            // read: an empty catalogue, or one that lost most of itself between cycles.
            .WithValidators(
                new MinimumRecordCountValidator("Movie", 1),
                new RecordCountPercentChangeValidator(
                    "Movie", ChangeThreshold.Create().WithRemoved(0.5f).Build()))
            .Build();

        // Declared rather than inferred from the first cycle's records, so that the schema is the same
        // even in a cycle that happens to contain no films.
        producer.InitializeDataModel(typeof(Movie));

        if (_options.Producer.RestoreOnStartup)
        {
            RestoreIfAvailable(producer);
        }

        await CycleUntilStoppedAsync(producer, stoppingToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Picks up where the last run left off, so the first blob published is a delta rather than the
    /// start of an unrelated chain.
    /// </summary>
    /// <remarks>
    /// Without this a restarted producer has no previous state to compare against, so it publishes a
    /// snapshot every consumer has to reload from — the data is the same, but every reader pays for the
    /// restart. Ported from <c>Producer.restoreIfAvailable</c>.
    /// </remarks>
    private void RestoreIfAvailable(HollowProducer producer)
    {
        long latestVersion = _announcementWatcher.GetLatestVersion();

        if (latestVersion == IAnnouncementWatcher.NoAnnouncementAvailable)
        {
            _logger.LogInformation("Nothing has been announced yet; starting a new delta chain.");
            return;
        }

        try
        {
            producer.Restore(latestVersion, _blobRetriever);

            _logger.LogInformation("Restored version {Version}.", latestVersion);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A restore that fails is not a reason to stop producing: the next cycle publishes a
            // snapshot and every consumer reloads, which is worse than a delta and better than nothing.
            _logger.LogWarning(
                e,
                "Restoring version {Version} failed; the next cycle starts a new delta chain.",
                latestVersion);
        }
    }

    /// <summary>
    /// Runs a cycle, waits out the rest of the interval, and does it again.
    /// </summary>
    /// <remarks>
    /// The interval is measured from the start of one cycle to the start of the next, as in the Java
    /// original: a cycle that takes longer than the interval is followed immediately by the next one
    /// rather than by an interval of idleness.
    /// </remarks>
    private async Task CycleUntilStoppedAsync(HollowProducer producer, CancellationToken stoppingToken)
    {
        SourceDataRetriever source = new(
            _options.Producer.Source.ActorCount,
            _options.Producer.Source.MovieCount,
            _options.Producer.Source.Seed);

        int maxCycles = _options.Producer.MaxCycles;

        for (int cycle = 0; maxCycles <= 0 || cycle < maxCycles; cycle++)
        {
            long startedAt = Environment.TickCount64;

            try
            {
                producer.RunCycle(writeState =>
                {
                    foreach (Movie movie in source.RetrieveAllMovies())
                    {
                        // Thread-safe, and parallelisable where the source of truth can be read that
                        // way. This one cannot: it hands back one list it mutates in place.
                        writeState.Add(movie);
                    }
                });
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // One bad cycle is not a reason to stop. A validator refusing a version, or the store
                // being briefly unreachable, is exactly what the next cycle is for.
                _logger.LogError(e, "A cycle failed. Trying again after the interval.");
            }

            TimeSpan remaining =
                _options.Producer.CycleInterval
                - TimeSpan.FromMilliseconds(Environment.TickCount64 - startedAt);

            if (remaining <= TimeSpan.Zero)
            {
                continue;
            }

            try
            {
                await Task.Delay(remaining, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopping between cycles rather than in the middle of one, which is the only place a
                // producer can be interrupted without leaving a half-published version behind.
                return;
            }
        }

        _logger.LogInformation("Ran {Cycles} cycles, which is all that was asked for.", maxCycles);
    }

    /// <summary>Says what each cycle did, which is the whole of the producer's output.</summary>
    private sealed class CycleLogger : HollowProducerListener
    {
        private readonly ILogger _logger;

        internal CycleLogger(ILogger logger) => _logger = logger;

        public override void OnCycleStart(long version) =>
            _logger.LogInformation("Cycle {Version} started.", version);

        public override void OnNoDeltaAvailable(long version) =>
            _logger.LogInformation(
                "Nothing changed in version {Version}; no blobs were published.", version);

        public override void OnBlobPublish(Status status, Blob blob, TimeSpan elapsed) =>
            _logger.LogInformation(
                "Published the {BlobType} blob for version {Version} in {Elapsed}.",
                blob.BlobType,
                blob.ToVersion,
                elapsed);

        public override void OnValidationStatusComplete(
            ValidationStatus status, long version, TimeSpan elapsed) =>
            _logger.LogInformation(
                "{Count} validators ran on version {Version}: {Outcome}.",
                status.Results.Count,
                version,
                status.Passed ? "all passed" : "the version was refused");

        public override void OnAnnouncementComplete(
            Status status, IReadState? readState, long version, TimeSpan elapsed) =>
            _logger.LogInformation("Announced version {Version}.", version);

        public override void OnCycleComplete(
            Status status, IReadState? readState, long version, TimeSpan elapsed)
        {
            if (status.Type != StatusType.Success)
            {
                _logger.LogWarning(
                    status.Cause, "Cycle {Version} did not complete after {Elapsed}.", version, elapsed);
            }
        }
    }
}
