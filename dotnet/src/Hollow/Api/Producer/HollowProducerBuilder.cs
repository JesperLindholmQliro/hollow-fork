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

using Hollow.Api.Consumer;
using Hollow.Api.Producer.Enforcer;
using Hollow.Api.Producer.Fs;
using Hollow.Api.Producer.Listener;
using Hollow.Api.Producer.Validation;
using Hollow.Core.Write;

namespace Hollow.Api.Producer;

/// <summary>
/// Configures and creates a <see cref="HollowProducer"/>.
/// </summary>
/// <remarks>
/// A publisher is required, and a blob stager defaults to an in-memory one. Without an announcer the
/// producer publishes but announces nothing, which is what a caller wants when something else decides
/// when consumers move.
/// </remarks>
public sealed class HollowProducerBuilder
{
    private readonly List<IHollowProducerEventListener> _listeners = [];

    /// <summary>Where blobs are written before publishing.</summary>
    internal IBlobStager? BlobStager { get; private set; }

    internal OptionalBlobPartConfig? OptionalPartConfig { get; private set; }

    internal TaskScheduler? SnapshotPublishScheduler { get; private set; }

    internal BlobStorageCleaner BlobStorageCleaner { get; private set; } = BlobStorageCleaner.None;

    /// <summary>Where published blobs go.</summary>
    internal IPublisher? Publisher { get; private set; }

    /// <summary>How consumers are told about a version, if at all.</summary>
    internal IAnnouncer? Announcer { get; private set; }

    /// <summary>How each cycle's version is chosen.</summary>
    internal IVersionMinter? VersionMinter { get; private set; }

    /// <summary>What decides whether this producer may run cycles.</summary>
    internal ISingleProducerEnforcer? SingleProducerEnforcer { get; private set; }

    /// <summary>Whether a restore should only accept announced snapshots.</summary>
    internal IUpdatePlanBlobVerifier? UpdatePlanBlobVerifier { get; private set; }

    /// <summary>The listeners to register before the first cycle.</summary>
    internal IReadOnlyList<IHollowProducerEventListener> Listeners => _listeners;

    /// <summary>Whether each cycle's blobs are checked against each other.</summary>
    internal bool DoIntegrityCheck { get; private set; } = true;

    /// <summary>How many deltas may go by before another snapshot is published.</summary>
    internal int NumStatesBetweenSnapshots { get; private set; }

    /// <summary>The size a type shard may reach before the type is split.</summary>
    internal long TargetMaxTypeShardSize { get; private set; } =
        HollowWriteStateEngine.DefaultTargetMaxTypeShardSize;

    /// <summary>Whether reclaimed ordinal holes are concentrated in as few shards as possible.</summary>
    internal bool FocusHoleFillInFewestShards { get; private set; }

    internal bool PartitionedOrdinalMap { get; private set; }

    internal bool AllowTypeResharding { get; private set; }

    /// <summary>
    /// Stages blobs through <paramref name="blobStager"/> before publishing them.
    /// </summary>
    public HollowProducerBuilder WithBlobStager(IBlobStager blobStager)
    {
        ArgumentNullException.ThrowIfNull(blobStager);

        BlobStager = blobStager;

        return this;
    }

    /// <summary>
    /// Writes the types <paramref name="optionalPartConfig"/> assigns into optional blob parts.
    /// </summary>
    /// <remarks>
    /// Takes effect only where the stager is one this builder creates. A stager supplied through
    /// <see cref="WithBlobStager"/> was already given its own configuration, and this does not reach
    /// back into it.
    /// </remarks>
    public HollowProducerBuilder WithOptionalPartConfig(OptionalBlobPartConfig optionalPartConfig)
    {
        ArgumentNullException.ThrowIfNull(optionalPartConfig);

        OptionalPartConfig = optionalPartConfig;

        return this;
    }

    /// <summary>
    /// Publishes the snapshot on <paramref name="scheduler"/> rather than on the cycle's own thread.
    /// </summary>
    /// <remarks>
    /// A snapshot is the largest artifact a cycle produces and the least urgent: consumers already on
    /// the chain move by delta, and only a consumer starting fresh needs it. Moving its upload off the
    /// cycle shortens the time between a version being ready and being announced.
    /// <para>
    /// The cycle still waits for the upload before cleaning up its staged files, so a slow scheduler
    /// delays the cycle's end rather than corrupting what it published.
    /// </para>
    /// </remarks>
    public HollowProducerBuilder WithSnapshotPublishScheduler(TaskScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);

        SnapshotPublishScheduler = scheduler;

        return this;
    }

    /// <summary>
    /// Gives <paramref name="blobStorageCleaner"/> the chance to remove old blobs after each publish.
    /// </summary>
    public HollowProducerBuilder WithBlobStorageCleaner(BlobStorageCleaner blobStorageCleaner)
    {
        ArgumentNullException.ThrowIfNull(blobStorageCleaner);

        BlobStorageCleaner = blobStorageCleaner;

        return this;
    }

    /// <summary>
    /// Stages blobs to <paramref name="stagingDirectory"/>, which keeps a large cycle's blobs off the
    /// heap.
    /// </summary>
    public HollowProducerBuilder WithBlobStagingDirectory(string stagingDirectory)
    {
        ArgumentNullException.ThrowIfNull(stagingDirectory);

        BlobStager = new HollowFilesystemBlobStager(stagingDirectory, optionalPartConfig: OptionalPartConfig);

        return this;
    }

    /// <summary>
    /// Publishes through <paramref name="publisher"/>.
    /// </summary>
    public HollowProducerBuilder WithPublisher(IPublisher publisher)
    {
        ArgumentNullException.ThrowIfNull(publisher);

        Publisher = publisher;

        return this;
    }

    /// <summary>
    /// Announces through <paramref name="announcer"/>.
    /// </summary>
    public HollowProducerBuilder WithAnnouncer(IAnnouncer announcer)
    {
        ArgumentNullException.ThrowIfNull(announcer);

        Announcer = announcer;

        return this;
    }

    /// <summary>
    /// Chooses each cycle's version with <paramref name="versionMinter"/>, rather than with
    /// <see cref="VersionMinterWithCounter"/>.
    /// </summary>
    public HollowProducerBuilder WithVersionMinter(IVersionMinter versionMinter)
    {
        ArgumentNullException.ThrowIfNull(versionMinter);

        VersionMinter = versionMinter;

        return this;
    }

    /// <summary>
    /// Registers listeners before the first cycle.
    /// </summary>
    public HollowProducerBuilder WithListeners(params IHollowProducerEventListener[] listeners)
    {
        ArgumentNullException.ThrowIfNull(listeners);

        _listeners.AddRange(listeners);

        return this;
    }

    /// <summary>
    /// Registers validators, which decide whether each cycle's data may be announced.
    /// </summary>
    public HollowProducerBuilder WithValidators(params IValidatorListener[] validators)
    {
        ArgumentNullException.ThrowIfNull(validators);

        _listeners.AddRange(validators);

        return this;
    }

    /// <summary>
    /// Decides whether this producer may run cycles with <paramref name="singleProducerEnforcer"/>.
    /// </summary>
    public HollowProducerBuilder WithSingleProducerEnforcer(ISingleProducerEnforcer singleProducerEnforcer)
    {
        ArgumentNullException.ThrowIfNull(singleProducerEnforcer);

        SingleProducerEnforcer = singleProducerEnforcer;

        return this;
    }

    /// <summary>
    /// Requires a restore to land on a snapshot that was actually announced.
    /// </summary>
    public HollowProducerBuilder WithUpdatePlanBlobVerifier(IUpdatePlanBlobVerifier updatePlanBlobVerifier)
    {
        ArgumentNullException.ThrowIfNull(updatePlanBlobVerifier);

        UpdatePlanBlobVerifier = updatePlanBlobVerifier;

        return this;
    }

    /// <summary>
    /// Publishes a snapshot only every <paramref name="numStatesBetweenSnapshots"/> cycles, rather than
    /// on every one.
    /// </summary>
    /// <remarks>
    /// A snapshot is what a new consumer starts from, so publishing fewer of them saves storage and
    /// costs a new consumer more deltas to replay. Only takes effect when the integrity check is off:
    /// the check reads each cycle's snapshot, so a cycle that skipped writing one could not be checked.
    /// </remarks>
    public HollowProducerBuilder WithNumStatesBetweenSnapshots(int numStatesBetweenSnapshots)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(numStatesBetweenSnapshots);

        NumStatesBetweenSnapshots = numStatesBetweenSnapshots;

        return this;
    }

    /// <summary>
    /// Turns off the check that each cycle's snapshot, delta and reverse delta describe the same data.
    /// </summary>
    /// <remarks>
    /// The check reads back everything the cycle wrote, so it roughly doubles a cycle's work. Turning
    /// it off means a delta that is subtly wrong reaches consumers, and the first sign of it is a
    /// consumer holding data that never existed.
    /// </remarks>
    public HollowProducerBuilder WithoutIntegrityCheck()
    {
        DoIntegrityCheck = false;

        return this;
    }

    /// <summary>
    /// Splits a type across more shards once a single shard would exceed
    /// <paramref name="targetMaxTypeShardSize"/> bytes.
    /// </summary>
    public HollowProducerBuilder WithTargetMaxTypeShardSize(long targetMaxTypeShardSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetMaxTypeShardSize);

        TargetMaxTypeShardSize = targetMaxTypeShardSize;

        return this;
    }

    /// <summary>
    /// Concentrates reclaimed ordinal holes in as few shards as possible, which keeps deltas smaller
    /// at the cost of a less even distribution.
    /// </summary>
    public HollowProducerBuilder WithFocusHoleFillInFewestShards(bool focusHoleFillInFewestShards = true)
    {
        FocusHoleFillInFewestShards = focusHoleFillInFewestShards;

        return this;
    }

    /// <summary>
    /// Spreads each type's records across four ordinal maps rather than one, so that populating a
    /// cycle from several threads contends on four write locks instead of one.
    /// </summary>
    /// <remarks>
    /// Worth it for a producer whose populator is genuinely parallel and whose records are cheap to
    /// serialise, which is when that one lock is the bottleneck. It is not free: the ordinals handed
    /// out are interleaved rather than consecutive, which spends two bits of the ordinal space and
    /// some of the locality a delta relies on. Leave it off unless a profile says otherwise.
    /// </remarks>
    public HollowProducerBuilder WithPartitionedOrdinalMap(bool partitionedOrdinalMap = true)
    {
        PartitionedOrdinalMap = partitionedOrdinalMap;

        return this;
    }

    /// <summary>
    /// Lets a type change its shard count between cycles as its data grows or shrinks past
    /// <see cref="WithTargetMaxTypeShardSize"/>.
    /// </summary>
    /// <remarks>
    /// Off by default, and only safe to turn on once every consumer of the delta chain can rearrange
    /// its records to a new count — an older consumer applies the delta at the count it already has and
    /// misreads every ordinal in it. A count changes by at most a factor of two per cycle.
    /// </remarks>
    public HollowProducerBuilder WithTypeResharding(bool allowTypeResharding = true)
    {
        AllowTypeResharding = allowTypeResharding;

        return this;
    }

    /// <summary>
    /// Creates the producer.
    /// </summary>
    /// <remarks>
    /// Register the data model with <see cref="HollowProducer.InitializeDataModel(Type[])"/> before the
    /// first cycle, and restore from the announced version if one exists.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No publisher was configured.</exception>
    public HollowProducer Build()
    {
        BlobStager ??= new HollowInMemoryBlobStager(OptionalPartConfig);

        return new HollowProducer(this);
    }
}
