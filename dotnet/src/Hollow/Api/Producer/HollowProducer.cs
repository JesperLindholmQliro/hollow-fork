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

using System.Diagnostics;
using Hollow.Api.Consumer;
using Hollow.Api.Producer.Enforcer;
using Hollow.Api.Producer.Listener;
using Hollow.Api.Producer.Validation;
using Hollow.Core;
using Hollow.Core.Read;
using Hollow.Core.Read.Engine;
using Hollow.Core.Schema;
using Hollow.Core.Tools.Checksum;
using Hollow.Core.Util;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Api.Producer;

/// <summary>
/// Publishes a dataset one version at a time, as a delta chain consumers can follow.
/// </summary>
/// <remarks>
/// <para>
/// Each call to <see cref="RunCycle"/> describes the whole dataset as it should now be; the producer
/// works out what changed, writes a snapshot, a delta and a reverse delta, checks that the three agree,
/// runs whatever validators are registered, and only then announces the version to consumers.
/// </para>
/// <code>
/// HollowProducer producer = new HollowProducerBuilder()
///     .WithBlobStager(new HollowFilesystemBlobStager(stagingDirectory))
///     .WithPublisher(new HollowFilesystemPublisher(blobStoreDirectory))
///     .WithAnnouncer(new HollowFilesystemAnnouncer(blobStoreDirectory))
///     .Build();
///
/// producer.InitializeDataModel(typeof(Movie));
///
/// producer.RunCycle(state =>
/// {
///     foreach (Movie movie in QueryEverything())
///     {
///         state.Add(movie);
///     }
/// });
/// </code>
/// <para>
/// Nothing is announced unless the cycle gets all the way through. A cycle that fails, or that turns
/// out to have nothing to publish, leaves the write state exactly as it was, so the next cycle
/// produces a delta from the last version consumers actually saw.
/// </para>
/// <para>
/// <strong>Port note.</strong> Metrics collection and optional blob parts are not ported — see
/// <c>PORTING.md</c>.
/// </para>
/// </remarks>
public sealed class HollowProducer
{
    private readonly ProducerListenerSupport _listeners = new();
    private readonly IBlobStager _blobStager;
    private readonly IPublisher _publisher;
    private readonly IAnnouncer? _announcer;
    private readonly IVersionMinter _versionMinter;
    private readonly ISingleProducerEnforcer _singleProducerEnforcer;
    private readonly IUpdatePlanBlobVerifier? _updatePlanBlobVerifier;
    private readonly bool _doIntegrityCheck;
    private readonly int _numStatesBetweenSnapshots;
    private readonly long _targetMaxTypeShardSize;
    private readonly bool _focusHoleFillInFewestShards;
    private readonly bool _allowTypeResharding;

    private readonly Lock _cycleLock = new();

    private HollowObjectMapper _objectMapper;
    private ReadStateHelper _readStates = ReadStateHelper.NewDeltaChain();
    private int _numStatesUntilNextSnapshot;
    private bool _isInitialized;

    internal HollowProducer(HollowProducerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        _blobStager = builder.BlobStager
            ?? throw new InvalidOperationException("A blob stager is required.");
        _publisher = builder.Publisher
            ?? throw new InvalidOperationException("A publisher is required.");
        _announcer = builder.Announcer;
        _versionMinter = builder.VersionMinter ?? new VersionMinterWithCounter();
        _singleProducerEnforcer = builder.SingleProducerEnforcer ?? new BasicSingleProducerEnforcer();
        _updatePlanBlobVerifier = builder.UpdatePlanBlobVerifier;
        _doIntegrityCheck = builder.DoIntegrityCheck;
        _numStatesBetweenSnapshots = builder.NumStatesBetweenSnapshots;
        _targetMaxTypeShardSize = builder.TargetMaxTypeShardSize;
        _focusHoleFillInFewestShards = builder.FocusHoleFillInFewestShards;
        _allowTypeResharding = builder.AllowTypeResharding;

        _objectMapper = new HollowObjectMapper(NewWriteEngine());

        foreach (IHollowProducerEventListener listener in builder.Listeners)
        {
            _listeners.AddListener(listener);
        }
    }

    /// <summary>
    /// Raised when a listener throws without meaning to veto the cycle.
    /// </summary>
    /// <remarks>
    /// The port takes no logging dependency, where Java logs a warning. Subscribing is the way to see
    /// that a listener is broken; a broken listener does not stop the cycle.
    /// </remarks>
    public event EventHandler<Exception>? ListenerFailed
    {
        add => _listeners.ListenerFailed += value;
        remove => _listeners.ListenerFailed -= value;
    }

    /// <summary>The write state engine this producer populates.</summary>
    public HollowWriteStateEngine WriteEngine => _objectMapper.StateEngine;

    /// <summary>The mapper that turns CLR objects into records.</summary>
    public HollowObjectMapper ObjectMapper => _objectMapper;

    /// <summary>
    /// The version of the last cycle that published something, or
    /// <see cref="HollowConstants.VersionNone"/> before any has.
    /// </summary>
    public long LastSuccessfulCycle { get; private set; } = HollowConstants.VersionNone;

    /// <summary>
    /// Registers the data model, by mapping each of <paramref name="types"/> without adding records.
    /// </summary>
    /// <remarks>
    /// Doing this up front rather than letting the first cycle discover the types means a cycle that
    /// happens to hold no records of some type still declares it, so a consumer's data model does not
    /// change under it.
    /// </remarks>
    public void InitializeDataModel(params Type[] types)
    {
        ArgumentNullException.ThrowIfNull(types);

        long start = Stopwatch.GetTimestamp();

        foreach (Type type in types)
        {
            _objectMapper.InitializeTypeState(type);
        }

        _isInitialized = true;
        _listeners.Listeners().Fire<IDataModelInitializationListener>(
            listener => listener.OnProducerInit(Stopwatch.GetElapsedTime(start)));
    }

    /// <summary>
    /// Registers the data model from <paramref name="schemas"/> rather than from CLR types.
    /// </summary>
    public void InitializeDataModel(params HollowSchema[] schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);

        long start = Stopwatch.GetTimestamp();

        HollowWriteStateCreator.PopulateStateEngineWithTypeWriteStates(WriteEngine, schemas);

        _isInitialized = true;
        _listeners.Listeners().Fire<IDataModelInitializationListener>(
            listener => listener.OnProducerInit(Stopwatch.GetElapsedTime(start)));
    }

    /// <summary>
    /// Picks up the delta chain that <paramref name="versionDesired"/> belongs to, so that this
    /// producer continues it instead of starting a new one.
    /// </summary>
    /// <param name="versionDesired">
    /// The version to restore from, normally the one currently announced.
    /// </param>
    /// <param name="blobRetriever">Where to read that version from.</param>
    /// <returns>
    /// The state restored, or <see langword="null"/> when <paramref name="versionDesired"/> is
    /// <see cref="HollowConstants.VersionNone"/> and there was nothing to restore.
    /// </returns>
    /// <remarks>
    /// This is what a producer does on startup. Without it the first cycle after a restart would
    /// publish a snapshot and a delta chain unrelated to the previous one, forcing every consumer to
    /// take a double snapshot.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The data model has not been initialised.</exception>
    public IReadState? Restore(long versionDesired, IBlobRetriever blobRetriever) =>
        Restore(new VersionInfo(versionDesired), blobRetriever);

    /// <inheritdoc cref="Restore(long, IBlobRetriever)"/>
    public IReadState? Restore(VersionInfo versionDesired, IBlobRetriever blobRetriever)
    {
        ArgumentNullException.ThrowIfNull(versionDesired);
        ArgumentNullException.ThrowIfNull(blobRetriever);

        if (!_isInitialized)
        {
            throw new InvalidOperationException(
                $"Call {nameof(InitializeDataModel)} before restoring: a restore has to know the data model to "
                + "match the published records against.");
        }

        if (versionDesired.Version == HollowConstants.VersionNone)
        {
            return null;
        }

        ProducerListenerSupport.Snapshot listeners = _listeners.Listeners();
        listeners.Fire<IRestoreListener>(listener => listener.OnProducerRestoreStart(versionDesired.Version));

        long start = Stopwatch.GetTimestamp();
        long versionReached = HollowConstants.VersionNone;
        Status status = Status.Success;

        try
        {
            HollowConsumerBuilder consumerBuilder = new HollowConsumerBuilder().WithBlobRetriever(blobRetriever);

            if (_updatePlanBlobVerifier is { } verifier)
            {
                consumerBuilder.WithUpdatePlanBlobVerifier(verifier);
            }

            using HollowConsumer consumer = consumerBuilder.Build();
            consumer.TriggerRefreshTo(versionDesired);

            IReadState readState = new ReadState(consumer.CurrentVersionId, consumer.StateEngine!);
            versionReached = readState.Version;

            // A restore cannot go into a write state that already holds records, so the data model is
            // rebuilt into a fresh engine and only swapped in once the restore has worked.
            HollowWriteStateEngine writeEngine = NewWriteEngine();
            HollowWriteStateCreator.PopulateStateEngineWithTypeWriteStates(writeEngine, WriteEngine.Schemas);
            HollowObjectMapper newObjectMapper = new(writeEngine);

            writeEngine.RestoreFrom(readState.StateEngine);

            _readStates = ReadStateHelper.Restored(readState);
            _objectMapper = newObjectMapper;

            return readState;
        }
        catch (Exception e)
        {
            status = Status.Fail(e);
            throw;
        }
        finally
        {
            Status finalStatus = status;
            long reached = versionReached;
            TimeSpan elapsed = Stopwatch.GetElapsedTime(start);

            listeners.Fire<IRestoreListener>(listener =>
                listener.OnProducerRestoreComplete(finalStatus, versionDesired.Version, reached, elapsed));
        }
    }

    /// <summary>
    /// Runs one cycle: populates a new state, publishes it if anything changed, and announces it.
    /// </summary>
    /// <param name="populator">
    /// Adds every record the new version should hold. This describes the whole dataset, not the change
    /// since last time.
    /// </param>
    /// <returns>
    /// The version consumers should now be on — the version this cycle produced, or the previous one
    /// when this cycle published nothing.
    /// </returns>
    /// <remarks>
    /// Cycles are serialised: a concurrent call waits. A cycle that throws leaves the producer able to
    /// run another one against the last published version.
    /// </remarks>
    public long RunCycle(Populator populator)
    {
        ArgumentNullException.ThrowIfNull(populator);

        lock (_cycleLock)
        {
            return RunCycleUnderLock(populator);
        }
    }

    /// <summary>
    /// Registers a listener, which takes effect on the next cycle or restore.
    /// </summary>
    public void AddListener(IHollowProducerEventListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);

        _listeners.AddListener(listener);
    }

    /// <summary>
    /// Unregisters a listener, which takes effect on the next cycle or restore.
    /// </summary>
    public void RemoveListener(IHollowProducerEventListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);

        _listeners.RemoveListener(listener);
    }

    /// <summary>
    /// Takes or gives up primary-producer status.
    /// </summary>
    /// <returns>Whether this producer is primary afterwards.</returns>
    public bool EnablePrimaryProducer(bool enable)
    {
        if (enable)
        {
            _singleProducerEnforcer.Enable();
        }
        else
        {
            _singleProducerEnforcer.Disable();
        }

        return _singleProducerEnforcer.IsPrimary;
    }

    private HollowWriteStateEngine NewWriteEngine() => new()
    {
        TargetMaxTypeShardSize = _targetMaxTypeShardSize,
        FocusHoleFillInFewestShards = _focusHoleFillInFewestShards,
        AllowTypeResharding = _allowTypeResharding,
    };

    private long RunCycleUnderLock(Populator populator)
    {
        ProducerListenerSupport.Snapshot listeners = _listeners.Listeners();

        if (!_singleProducerEnforcer.IsPrimary)
        {
            listeners.Fire<ICycleListener>(
                listener => listener.OnCycleSkip(CycleSkipReason.NotPrimaryProducer));

            return LastSuccessfulCycle;
        }

        long toVersion = _versionMinter.Mint();

        if (!_readStates.HasCurrent)
        {
            listeners.Fire<ICycleListener>(listener => listener.OnNewDeltaChain(toVersion));
        }

        listeners.Fire<ICycleListener>(listener => listener.OnCycleStart(toVersion));

        long start = Stopwatch.GetTimestamp();
        Status status = Status.Success;
        IReadState? cycleReadState = null;

        try
        {
            cycleReadState = RunCycleStages(listeners, populator, toVersion);
            return LastSuccessfulCycle;
        }
        catch (Exception e)
        {
            status = Status.Fail(e);
            throw;
        }
        finally
        {
            Status finalStatus = status;
            IReadState? finalReadState = cycleReadState;
            TimeSpan elapsed = Stopwatch.GetElapsedTime(start);

            listeners.Fire<ICycleListener>(listener =>
                listener.OnCycleComplete(finalStatus, finalReadState, toVersion, elapsed));
        }
    }

    private IReadState? RunCycleStages(
        ProducerListenerSupport.Snapshot listeners, Populator populator, long toVersion)
    {
        Artifacts artifacts = new();
        HollowWriteStateEngine writeEngine = WriteEngine;

        try
        {
            writeEngine.PrepareForNextCycle();
            writeEngine.AddHeaderTag(
                HollowHeaderTags.MetricCycleStart, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().Invariant());

            Populate(listeners, populator, toVersion);

            if (!writeEngine.HasChangedSinceLastCycle())
            {
                // Nothing to publish. Wind the write state back so that the next cycle produces a delta
                // from the version consumers are actually on, and report the version they should stay on.
                writeEngine.ResetToLastPrepareForNextCycle();
                listeners.Fire<IPublishListener>(listener => listener.OnNoDeltaAvailable(toVersion));

                return _readStates.Current;
            }

            bool schemaChanged = _readStates.HasCurrent
                && !writeEngine.HasIdenticalSchemas(_readStates.Current!.StateEngine);

            UpdateHeaderTags(writeEngine, toVersion, schemaChanged);

            Publish(listeners, toVersion, artifacts);

            ReadStateHelper candidate = _readStates.RoundTrip(toVersion);
            candidate = _doIntegrityCheck
                ? CheckIntegrity(listeners, candidate, artifacts, schemaChanged)
                : SkipIntegrityCheck(candidate, artifacts);

            try
            {
                Validate(listeners, candidate.Pending!);
                Announce(listeners, candidate.Pending!);

                _readStates = candidate.Commit();
                LastSuccessfulCycle = toVersion;

                return _readStates.Current;
            }
            catch
            {
                // The blobs are published but nothing has been told to read them. Wind the pending read
                // state back to the current version so that the producer's own view matches what
                // consumers still see.
                if (artifacts.ReverseDelta is { } reverseDelta)
                {
                    ApplyDelta(reverseDelta, candidate.Pending!.StateEngine);
                    _readStates = candidate.Rollback();
                }

                throw;
            }
        }
        catch
        {
            writeEngine.ResetToLastPrepareForNextCycle();
            throw;
        }
        finally
        {
            artifacts.Cleanup();
        }
    }

    private void Populate(ProducerListenerSupport.Snapshot listeners, Populator populator, long toVersion)
    {
        listeners.Fire<IPopulateListener>(listener => listener.OnPopulateStart(toVersion));

        long start = Stopwatch.GetTimestamp();
        Status status = Status.Success;

        try
        {
            WriteStateForCycle writeState = new(this, toVersion, _readStates.Current);
            try
            {
                populator(writeState);
            }
            finally
            {
                // Closing the write state turns a reference captured by an asynchronous populator into
                // a loud failure rather than records leaking into the next cycle.
                writeState.Close();
            }
        }
        catch (Exception e)
        {
            status = Status.Fail(e);
            throw;
        }
        finally
        {
            Status finalStatus = status;
            TimeSpan elapsed = Stopwatch.GetElapsedTime(start);

            listeners.Fire<IPopulateListener>(listener =>
                listener.OnPopulateComplete(finalStatus, toVersion, elapsed));
        }
    }

    private void UpdateHeaderTags(HollowWriteStateEngine writeEngine, long toVersion, bool schemaChanged)
    {
        writeEngine.AddHeaderTag(HollowHeaderTags.ProducerToVersion, toVersion.Invariant());
        writeEngine.AddHeaderTag(HollowHeaderTags.SchemaHash, new HollowSchemaHash(writeEngine).Hash);
        writeEngine.AddHeaderTag(
            HollowHeaderTags.SchemaChange, schemaChanged ? bool.TrueString : bool.FalseString);

        // The resharding tag describes this version only. Clear whatever the last cycle left; the shard
        // decision made while writing the blobs will put it back if anything moves.
        writeEngine.HeaderTags.Remove(HollowHeaderTags.TypeReshardingInvoked);

        long previousCounter =
            writeEngine.GetHeaderTag(HollowHeaderTags.DeltaChainVersionCounter) is { } counter
            && long.TryParse(counter, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out long parsed)
                ? parsed
                : 0;

        writeEngine.AddHeaderTag(HollowHeaderTags.DeltaChainVersionCounter, (previousCounter + 1).Invariant());
    }

    private void Publish(ProducerListenerSupport.Snapshot listeners, long toVersion, Artifacts artifacts)
    {
        listeners.Fire<IPublishListener>(listener => listener.OnPublishStart(toVersion));

        long start = Stopwatch.GetTimestamp();
        Status status = Status.Success;

        try
        {
            artifacts.Header = _blobStager.OpenHeader(toVersion);
            artifacts.Header.Write(new HollowBlobWriter(WriteEngine));
            _publisher.Publish(artifacts.Header);

            // A snapshot is always written when the integrity check needs one to read, or when enough
            // deltas have gone by that a new consumer would otherwise have a long chain to replay.
            bool needSnapshot =
                !_readStates.HasCurrent || _doIntegrityCheck || _numStatesUntilNextSnapshot <= 0;

            if (needSnapshot)
            {
                artifacts.Snapshot = StageBlob(listeners, _blobStager.OpenSnapshot(toVersion));
            }

            if (_readStates.HasCurrent)
            {
                long fromVersion = _readStates.Current!.Version;

                artifacts.Delta = StageBlob(listeners, _blobStager.OpenDelta(fromVersion, toVersion));
                artifacts.ReverseDelta = StageBlob(
                    listeners, _blobStager.OpenReverseDelta(toVersion, fromVersion));

                PublishBlob(listeners, artifacts.Delta);
                PublishBlob(listeners, artifacts.ReverseDelta);

                if (--_numStatesUntilNextSnapshot < 0)
                {
                    PublishBlob(listeners, artifacts.Snapshot!);
                    _numStatesUntilNextSnapshot = _numStatesBetweenSnapshots;
                }
            }
            else
            {
                PublishBlob(listeners, artifacts.Snapshot!);
                _numStatesUntilNextSnapshot = _numStatesBetweenSnapshots;
            }
        }
        catch (Exception e)
        {
            status = Status.Fail(e);
            throw;
        }
        finally
        {
            Status finalStatus = status;
            TimeSpan elapsed = Stopwatch.GetElapsedTime(start);

            listeners.Fire<IPublishListener>(listener =>
                listener.OnPublishComplete(finalStatus, toVersion, elapsed));
        }
    }

    private Blob StageBlob(ProducerListenerSupport.Snapshot listeners, Blob blob)
    {
        long start = Stopwatch.GetTimestamp();
        Status status = Status.Success;

        try
        {
            blob.Write(new HollowBlobWriter(WriteEngine));

            return blob;
        }
        catch (Exception e)
        {
            status = Status.Fail(e);
            throw;
        }
        finally
        {
            Status finalStatus = status;
            TimeSpan elapsed = Stopwatch.GetElapsedTime(start);

            listeners.Fire<IPublishListener>(listener => listener.OnBlobStage(finalStatus, blob, elapsed));
        }
    }

    private void PublishBlob(ProducerListenerSupport.Snapshot listeners, Blob blob)
    {
        long start = Stopwatch.GetTimestamp();
        Status status = Status.Success;

        try
        {
            _publisher.Publish(blob);
        }
        catch (Exception e)
        {
            status = Status.Fail(e);
            throw;
        }
        finally
        {
            Status finalStatus = status;
            TimeSpan elapsed = Stopwatch.GetElapsedTime(start);

            listeners.Fire<IPublishListener>(listener => listener.OnBlobPublish(finalStatus, blob, elapsed));
        }
    }

    /// <summary>
    /// Reads the blobs just written and checks that the snapshot, the delta and the reverse delta all
    /// describe the same state.
    /// </summary>
    /// <remarks>
    /// This is the check that earns the producer its keep. A delta is derived from the write state's
    /// ordinal bookkeeping rather than from the data, so a mistake in that bookkeeping produces a
    /// delta that applies cleanly and leaves the consumer holding subtly wrong data. Comparing
    /// checksums catches it before anything is announced.
    /// </remarks>
    private ReadStateHelper CheckIntegrity(
        ProducerListenerSupport.Snapshot listeners,
        ReadStateHelper readStates,
        Artifacts artifacts,
        bool schemaChanged)
    {
        listeners.Fire<IIntegrityCheckListener>(
            listener => listener.OnIntegrityCheckStart(readStates.PendingVersion));

        long start = Stopwatch.GetTimestamp();
        Status status = Status.Success;

        try
        {
            ReadStateHelper result = readStates;
            HollowReadStateEngine pending = readStates.Pending!.StateEngine;

            ReadSnapshot(artifacts.Snapshot!, pending);

            if (readStates.HasCurrent && artifacts.Delta is { } delta)
            {
                if (artifacts.ReverseDelta is not { } reverseDelta)
                {
                    throw new InvalidOperationException("A delta cannot be checked without its reverse delta.");
                }

                HollowReadStateEngine current = readStates.Current!.StateEngine;

                HollowChecksum currentChecksum =
                    HollowChecksum.ForStateEngineWithCommonSchemas(current, pending);
                HollowChecksum pendingChecksum =
                    HollowChecksum.ForStateEngineWithCommonSchemas(pending, current);

                ApplyDelta(delta, current);
                HollowChecksum forwardChecksum =
                    HollowChecksum.ForStateEngineWithCommonSchemas(current, pending);

                if (!forwardChecksum.Equals(pendingChecksum))
                {
                    throw new ChecksumValidationException(BlobType.Delta, forwardChecksum, pendingChecksum);
                }

                ApplyDelta(reverseDelta, pending);
                HollowChecksum reverseChecksum =
                    HollowChecksum.ForStateEngineWithCommonSchemas(pending, current);

                if (!reverseChecksum.Equals(currentChecksum))
                {
                    throw new ChecksumValidationException(
                        BlobType.ReverseDelta, reverseChecksum, currentChecksum);
                }

                // Both engines now hold the other's data. With identical schemas that is a free result:
                // relabel them rather than moving the data back.
                if (!schemaChanged)
                {
                    result = readStates.Swap();
                }
                else
                {
                    ApplyDelta(reverseDelta, current);
                    ApplyDelta(delta, pending);
                }
            }

            return result;
        }
        catch (Exception e)
        {
            status = Status.Fail(e);
            throw;
        }
        finally
        {
            Status finalStatus = status;
            TimeSpan elapsed = Stopwatch.GetElapsedTime(start);

            listeners.Fire<IIntegrityCheckListener>(listener =>
                listener.OnIntegrityCheckComplete(
                    finalStatus, readStates.Pending, readStates.PendingVersion, elapsed));
        }
    }

    /// <summary>
    /// Brings the producer's read state up to the new version without checking the blobs against each
    /// other, for a producer that has turned the integrity check off.
    /// </summary>
    private ReadStateHelper SkipIntegrityCheck(ReadStateHelper readStates, Artifacts artifacts)
    {
        bool canFollowDelta = readStates.HasCurrent
            && readStates.Current!.StateEngine.HasIdenticalSchemas(WriteEngine);

        if (!canFollowDelta || artifacts.Delta is null)
        {
            ReadSnapshot(
                artifacts.Snapshot
                    ?? throw new InvalidOperationException(
                        "No snapshot was published, so the producer cannot read the new state."),
                readStates.Pending!.StateEngine);

            return readStates;
        }

        ApplyDelta(artifacts.Delta, readStates.Current!.StateEngine);

        return readStates.Swap();
    }

    private static void ReadSnapshot(Blob blob, HollowReadStateEngine stateEngine)
    {
        using Stream stream = blob.OpenStream();
        using HollowBlobInput input = HollowBlobInput.Serial(stream, leaveOpen: true);

        new HollowBlobReader(stateEngine).ReadSnapshot(input);
    }

    private static void ApplyDelta(Blob blob, HollowReadStateEngine stateEngine)
    {
        using Stream stream = blob.OpenStream();
        using HollowBlobInput input = HollowBlobInput.Serial(stream, leaveOpen: true);

        new HollowBlobReader(stateEngine).ApplyDelta(input);
    }

    private void Validate(ProducerListenerSupport.Snapshot listeners, IReadState readState)
    {
        listeners.Fire<IValidationStatusListener>(
            listener => listener.OnValidationStatusStart(readState.Version));

        long start = Stopwatch.GetTimestamp();
        ValidationStatus? validationStatus = null;

        try
        {
            List<ValidationResult> results = [];

            foreach (IValidatorListener validator in listeners.OfType<IValidatorListener>())
            {
                try
                {
                    results.Add(validator.OnValidate(readState));
                }
                catch (Exception e)
                {
                    // A broken validator cannot vouch for the data, so it counts as a failure — but the
                    // other validators still run, so the report says everything that is wrong at once.
                    results.Add(ValidationResult.From(validator).Error(e));
                }
            }

            validationStatus = new ValidationStatus(results);

            if (validationStatus.Failed)
            {
                throw new ValidationStatusException(
                    validationStatus, "One or more validations failed; see the individual results.");
            }
        }
        finally
        {
            ValidationStatus finalStatus = validationStatus ?? new ValidationStatus([]);
            TimeSpan elapsed = Stopwatch.GetElapsedTime(start);

            listeners.Fire<IValidationStatusListener>(listener =>
                listener.OnValidationStatusComplete(finalStatus, readState.Version, elapsed));
        }
    }

    private void Announce(ProducerListenerSupport.Snapshot listeners, IReadState readState)
    {
        if (_announcer is not { } announcer)
        {
            return;
        }

        listeners.Fire<IAnnouncementListener>(listener => listener.OnAnnouncementStart(readState.Version));

        long start = Stopwatch.GetTimestamp();
        Status status = Status.Success;

        try
        {
            Dictionary<string, string> metadata = new(StringComparer.Ordinal)
            {
                [HollowHeaderTags.MetricAnnouncement] =
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().Invariant(),
            };

            foreach ((string name, string value) in readState.StateEngine.HeaderTags)
            {
                metadata[name] = value;
            }

            // Hold primary status across the announcement: losing it half way would leave consumers
            // pointed at a version this producer is no longer responsible for.
            _singleProducerEnforcer.Lock();
            try
            {
                if (!_singleProducerEnforcer.IsPrimary)
                {
                    throw new InvalidOperationException(
                        "This producer is no longer the primary, so it will not announce the version it just "
                        + "published.");
                }

                announcer.Announce(readState.Version, metadata);
            }
            finally
            {
                _singleProducerEnforcer.Unlock();
            }
        }
        catch (Exception e)
        {
            status = Status.Fail(e);
            throw;
        }
        finally
        {
            Status finalStatus = status;
            TimeSpan elapsed = Stopwatch.GetElapsedTime(start);

            listeners.Fire<IAnnouncementListener>(listener =>
                listener.OnAnnouncementComplete(finalStatus, readState, readState.Version, elapsed));
        }
    }

    /// <summary>
    /// The blobs one cycle staged, and their cleanup.
    /// </summary>
    private sealed class Artifacts
    {
        internal Blob? Snapshot { get; set; }

        internal Blob? Delta { get; set; }

        internal Blob? ReverseDelta { get; set; }

        internal HeaderBlob? Header { get; set; }

        internal void Cleanup()
        {
            Snapshot?.Cleanup();
            Delta?.Cleanup();
            ReverseDelta?.Cleanup();
            Header?.Cleanup();

            Snapshot = null;
            Delta = null;
            ReverseDelta = null;
            Header = null;
        }
    }

    /// <summary>
    /// The write state handed to a populator, valid only while the populate stage is running.
    /// </summary>
    private sealed class WriteStateForCycle(HollowProducer producer, long version, IReadState? priorState)
        : IWriteState
    {
        private bool _closed;

        public int Add(object value)
        {
            ArgumentNullException.ThrowIfNull(value);

            return ObjectMapper.Add(value);
        }

        public HollowObjectMapper ObjectMapper => Open(producer._objectMapper);

        public HollowWriteStateEngine StateEngine => Open(producer.WriteEngine);

        public IReadState? PriorState => Open(priorState);

        public long Version => Open(version);

        internal void Close() => _closed = true;

        private T Open<T>(T value) => _closed
            ? throw new InvalidOperationException(
                "The populate stage has finished; this write state can no longer be used. A populator must not "
                + "keep a reference to it, or hand one to work that outlives the call.")
            : value;
    }
}

/// <summary>
/// Thrown when a cycle's delta and the snapshot of the same cycle do not describe the same data.
/// </summary>
/// <remarks>
/// This is a bug in the producer or in Hollow itself, not in the caller's data — which is exactly why
/// the check exists.
/// </remarks>
public sealed class ChecksumValidationException : Exception
{
    internal ChecksumValidationException(BlobType blobType, HollowChecksum actual, HollowChecksum expected)
        : base(
            $"The {blobType.GetPrefix()} produced a state with checksum {actual} where {expected} was expected, "
            + "so it does not describe the same data as the snapshot of the same cycle.")
    {
        BlobType = blobType;
        Actual = actual;
        Expected = expected;
    }

    /// <summary>Initialises an exception with no detail, for serialisation compatibility.</summary>
    public ChecksumValidationException()
    {
    }

    /// <summary>Initialises an exception with no detail.</summary>
    public ChecksumValidationException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises an exception with no detail.</summary>
    public ChecksumValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Which blob disagreed.</summary>
    public BlobType BlobType { get; }

    /// <summary>The checksum the blob produced.</summary>
    public HollowChecksum? Actual { get; }

    /// <summary>The checksum it should have produced.</summary>
    public HollowChecksum? Expected { get; }
}
