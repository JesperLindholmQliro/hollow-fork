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
using Hollow.Reference.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hollow.Reference.Infrastructure.Adapters;

/// <summary>
/// Follows the announced version and nudges every consumer subscribed to it when it moves.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>DynamoDBAnnouncementWatcher</c> and <c>S3AnnouncementWatcher</c>, which are the same
/// class twice over: a thread, a one-second sleep, a read, and a refresh if the number changed.
/// </para>
/// <para>
/// This one asks the store to tell it instead, and only falls back to polling when the store cannot —
/// see <see cref="IHollowAnnouncementStore.Subscribe"/>. In the local mode that makes a new file in the
/// watching folder reach the consumer as soon as the operating system notices it, rather than up to a
/// second later. In the cloud modes it polls, because neither S3 nor DynamoDB has anything to push
/// with short of wiring up a notification topic, which is more infrastructure than a reference
/// implementation should ask anyone to provision.
/// </para>
/// <para>
/// A pin beats an announcement: while one is in force this reports the pinned version, so a consumer
/// that has caught up to a bad dataset is moved back to the good one by the same mechanism that moves
/// it forwards.
/// </para>
/// </remarks>
public sealed class HollowAnnouncementStoreWatcher : IAnnouncementWatcher, IDisposable
{
    private readonly IHollowAnnouncementStore _store;
    private readonly ILogger _logger;
    private readonly List<HollowConsumer> _subscribedConsumers = [];
    private readonly Lock _consumersLock = new();
    private readonly Lock _versionLock = new();

    // Polls are serialized rather than allowed to overlap: a FileSystemWatcher reports one file with
    // several events, and there is nothing to gain from three reads of the same folder at once.
    private readonly SemaphoreSlim _pollLock = new(1, 1);

    private readonly IDisposable? _subscription;
    private readonly Timer? _timer;

    private AnnouncedVersions _latest;
    private bool _disposed;

    /// <param name="store">Where the announcement is kept.</param>
    /// <param name="pollInterval">
    /// How often to read the store when it cannot say for itself that it changed. Zero turns polling
    /// off, which is only sensible for a store that can.
    /// </param>
    /// <param name="logger">Somewhere to put the failures that must not stop the watcher.</param>
    public HollowAnnouncementStoreWatcher(
        IHollowAnnouncementStore store,
        TimeSpan? pollInterval = null,
        ILogger<HollowAnnouncementStoreWatcher>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        _logger = logger ?? NullLogger<HollowAnnouncementStoreWatcher>.Instance;

        // Read once before anyone can subscribe, so that a consumer built against this watcher finds a
        // version to load rather than having to wait out the first poll.
        _latest = Synchronously.Run(() => store.ReadAsync());

        _subscription = store.Subscribe(OnStoreChanged);

        if (_subscription is not null)
        {
            return;
        }

        TimeSpan interval = pollInterval ?? TimeSpan.FromSeconds(1);

        if (interval > TimeSpan.Zero)
        {
            _timer = new Timer(_ => OnStoreChanged(), state: null, interval, interval);
        }
    }

    /// <summary>Whether this watcher is told about changes rather than looking for them.</summary>
    public bool IsPushBased => _subscription is not null;

    public long GetLatestVersion()
    {
        lock (_versionLock)
        {
            return _latest.EffectiveVersion;
        }
    }

    public VersionInfo GetLatestVersionInfo()
    {
        AnnouncedVersions latest;

        lock (_versionLock)
        {
            latest = _latest;
        }

        return new VersionInfo(
            latest.EffectiveVersion,
            latest.Metadata,
            latest.IsPinned,
            wasAnnounced: latest.AnnouncedVersion != IAnnouncementWatcher.NoAnnouncementAvailable);
    }

    public void SubscribeToUpdates(HollowConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        lock (_consumersLock)
        {
            _subscribedConsumers.Add(consumer);
        }
    }

    /// <summary>
    /// Reads the store now and refreshes the subscribed consumers if what it says has changed.
    /// </summary>
    /// <remarks>Public so that a test can drive it rather than waiting for an event or a tick.</remarks>
    public async Task PollAsync(CancellationToken cancellationToken = default)
    {
        await _pollLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            AnnouncedVersions current = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
            long previousVersion;

            lock (_versionLock)
            {
                previousVersion = _latest.EffectiveVersion;
                _latest = current;
            }

            if (previousVersion == current.EffectiveVersion)
            {
                return;
            }

            HollowConsumer[] consumers;

            lock (_consumersLock)
            {
                consumers = [.. _subscribedConsumers];
            }

            foreach (HollowConsumer consumer in consumers)
            {
                // Asynchronous on purpose: a refresh loads blobs, and the caller here is either a
                // timer callback or a FileSystemWatcher event, neither of which should be held up by it.
                _ = consumer.TriggerRefreshAsync(cancellationToken: cancellationToken);
            }
        }
        finally
        {
            _pollLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _subscription?.Dispose();
        _timer?.Dispose();
        _pollLock.Dispose();
    }

    private void OnStoreChanged()
    {
        if (_disposed)
        {
            return;
        }

        _ = PollAsync().ContinueWith(
            static (poll, state) =>
            {
                // Java prints the stack trace and carries on, and carrying on is right: a watcher that
                // stopped on one failed read would leave the consumer stuck on an old version with
                // nothing to say why.
                ((ILogger)state!).LogWarning(
                    poll.Exception?.GetBaseException(),
                    "Reading the announced version failed; the next change will try again.");
            },
            _logger,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
