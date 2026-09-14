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

using Hollow.Api.Client;
using Hollow.Api.Custom;
using Hollow.Core;
using Hollow.Core.Read.Engine;

namespace Hollow.Api.Consumer;

/// <summary>
/// Keeps a local in-memory copy of a Hollow dataset up to date from a blob store.
/// </summary>
/// <remarks>
/// <para>
/// A consumer needs a <see cref="IBlobRetriever"/>, which says where the blobs come from. Give it an
/// <see cref="IAnnouncementWatcher"/> as well and it follows whatever the producer announces; without
/// one, the caller drives it with <see cref="TriggerRefreshTo(long)"/>.
/// </para>
/// <para>
/// Build one with <see cref="HollowConsumerBuilder"/>:
/// </para>
/// <code>
/// HollowConsumer consumer = new HollowConsumerBuilder()
///     .WithBlobRetriever(blobRetriever)
///     .WithAnnouncementWatcher(announcementWatcher)
///     .Build();
///
/// consumer.TriggerRefresh();
/// </code>
/// <para>
/// <strong>Port note.</strong> Several things around the edges of Java's consumer are not ported: the
/// generated <c>HollowAPI</c> layer and its code generation, object longevity (serving reads of a prior
/// version from a live state), and metrics collection. The refresh itself, the update planning and the
/// blob store contracts are all here. Java's nested types — <c>Blob</c>, <c>BlobRetriever</c>,
/// <c>RefreshListener</c> and the rest — are flattened into this namespace.
/// </para>
/// </remarks>
public sealed class HollowConsumer : IDisposable
{
    private readonly ReaderWriterLockSlim _refreshLock = new(LockRecursionPolicy.SupportsRecursion);
    private readonly HollowClientUpdater _updater;
    private readonly IAnnouncementWatcher? _announcementWatcher;

    internal HollowConsumer(HollowConsumerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        _updater = new HollowClientUpdater(
            builder.BlobRetriever ?? throw new InvalidOperationException("A blob retriever is required."),
            builder.RefreshListeners,
            builder.DoubleSnapshotConfig,
            builder.UpdatePlanBlobVerifier,
            builder.TypeFilter,
            builder.MemoryMode,
            builder.ApiFactory,
            builder.ObjectLongevityConfig,
            builder.ObjectLongevityDetector,
            builder.TimeProvider);

        _announcementWatcher = builder.AnnouncementWatcher;
        _announcementWatcher?.SubscribeToUpdates(this);
    }

    /// <summary>
    /// The state engine holding the current data, or <see langword="null"/> before the first successful
    /// refresh.
    /// </summary>
    /// <remarks>
    /// This reference is replaced whenever a refresh loads a snapshot, so code that holds on to it
    /// across refreshes may be reading a state that is no longer current. Take
    /// <see cref="AcquireRefreshLock"/> around a read that must not see the data change underneath it.
    /// </remarks>
    public HollowReadStateEngine? StateEngine => _updater.StateEngine;

    /// <summary>
    /// Watches the states this consumer has superseded, or <see langword="null"/> when object
    /// longevity is off.
    /// </summary>
    /// <remarks>
    /// Exposed so that an application can drive the housekeeping itself, or ask what the last pass
    /// found. See <see cref="IObjectLongevityConfig"/>.
    /// </remarks>
    public StaleReferenceDetector? StaleReferenceDetector => _updater.StaleReferenceDetector;

    /// <summary>
    /// The typed API over the current data, or <see langword="null"/> before the first successful
    /// refresh.
    /// </summary>
    /// <remarks>
    /// A plain <see cref="HollowApi"/> unless the consumer was built with
    /// <see cref="HollowConsumerBuilder.WithApiFactory"/>. Like <see cref="StateEngine"/>, this
    /// reference is replaced whenever a refresh loads a snapshot.
    /// </remarks>
    public HollowApi? Api => _updater.Api;

    /// <summary>
    /// The version of the data currently held, or <see cref="HollowConstants.VersionNone"/> before the
    /// first successful refresh.
    /// </summary>
    public long CurrentVersionId => _updater.CurrentVersionId;

    /// <summary>
    /// Completes with the version of the first data this consumer loads.
    /// </summary>
    /// <remarks>
    /// A failed refresh does not fail this task: a producer that publishes often will give the consumer
    /// another chance, and a caller waiting for first data would rather keep waiting than be told the
    /// first attempt did not work.
    /// </remarks>
    public Task<long> InitialLoad => _updater.InitialLoad;

    /// <summary>The number of distinct snapshots that have failed to apply.</summary>
    public int NumFailedSnapshotTransitions => _updater.NumFailedSnapshotTransitions;

    /// <summary>The number of distinct deltas that have failed to apply.</summary>
    public int NumFailedDeltaTransitions => _updater.NumFailedDeltaTransitions;

    /// <summary>
    /// Refreshes to the latest announced version, or — without an announcement watcher — to the latest
    /// version the blob store holds.
    /// </summary>
    /// <remarks>Blocks until the refresh finishes, and does nothing if already on that version.</remarks>
    public void TriggerRefresh()
    {
        _refreshLock.EnterWriteLock();
        try
        {
            _updater.UpdateTo(
                _announcementWatcher?.GetLatestVersionInfo() ?? new VersionInfo(HollowConstants.VersionLatest));
        }
        finally
        {
            _refreshLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Refreshes on a background thread, optionally after a delay.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what an announcement watcher calls when it sees a new version. Awaiting the returned
    /// task surfaces a refresh failure; dropping it does not, so a caller that does drop it should
    /// register an <see cref="IRefreshListener"/> to hear about failures.
    /// </para>
    /// <para>
    /// Java takes an <c>Executor</c> for this. A .NET caller controls scheduling through the task
    /// itself, so there is nothing to configure.
    /// </para>
    /// </remarks>
    public async Task TriggerRefreshAsync(TimeSpan delay = default, CancellationToken cancellationToken = default)
    {
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        await Task.Run(TriggerRefresh, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Refreshes to <paramref name="version"/>, or as close to it as the blob store allows.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// This consumer has an <see cref="IAnnouncementWatcher"/>, which decides its version.
    /// </exception>
    public void TriggerRefreshTo(long version) => TriggerRefreshTo(new VersionInfo(version));

    /// <summary>
    /// Refreshes to <paramref name="versionInfo"/>, or as close to it as the blob store allows.
    /// </summary>
    /// <inheritdoc cref="TriggerRefreshTo(long)" path="/exception"/>
    public void TriggerRefreshTo(VersionInfo versionInfo)
    {
        ArgumentNullException.ThrowIfNull(versionInfo);

        if (_announcementWatcher is not null)
        {
            throw new NotSupportedException(
                "Cannot refresh to a specified version when the consumer has an announcement watcher.");
        }

        _refreshLock.EnterWriteLock();
        try
        {
            _updater.UpdateTo(versionInfo);
        }
        finally
        {
            _refreshLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Holds off any refresh until the returned value is disposed.
    /// </summary>
    /// <remarks>
    /// Take this around a read that spans more than one call and must see a single consistent version:
    /// <code>
    /// using (consumer.AcquireRefreshLock())
    /// {
    ///     // …read from consumer.StateEngine…
    /// }
    /// </code>
    /// Java exposes the underlying read lock instead; this shape makes it hard to forget the release.
    /// <para>
    /// The lock belongs to the thread that took it, so do not hold it across an <c>await</c>: the
    /// continuation may resume on another thread, which then cannot release it.
    /// </para>
    /// </remarks>
    public IDisposable AcquireRefreshLock()
    {
        _refreshLock.EnterReadLock();

        return new RefreshLockHandle(_refreshLock);
    }

    /// <summary>
    /// Makes the next refresh load a snapshot rather than follow deltas.
    /// </summary>
    public void ForceDoubleSnapshotNextUpdate() => _updater.ForceDoubleSnapshotNextUpdate();

    /// <summary>
    /// Forgets the recorded transition failures, so they may be tried again on the next refresh.
    /// </summary>
    public void ClearFailedTransitions() => _updater.ClearFailedTransitions();

    /// <summary>
    /// Adds a refresh listener, which takes effect on the next refresh.
    /// </summary>
    public void AddRefreshListener(IRefreshListener refreshListener) =>
        _updater.AddRefreshListener(refreshListener, this);

    /// <summary>
    /// Removes a refresh listener, which takes effect on the next refresh.
    /// </summary>
    public void RemoveRefreshListener(IRefreshListener refreshListener) =>
        _updater.RemoveRefreshListener(refreshListener, this);

    /// <inheritdoc />
    public void Dispose() => _refreshLock.Dispose();

    private sealed class RefreshLockHandle(ReaderWriterLockSlim refreshLock) : IDisposable
    {
        private ReaderWriterLockSlim? _refreshLock = refreshLock;

        public void Dispose()
        {
            Interlocked.Exchange(ref _refreshLock, null)?.ExitReadLock();
        }
    }
}
