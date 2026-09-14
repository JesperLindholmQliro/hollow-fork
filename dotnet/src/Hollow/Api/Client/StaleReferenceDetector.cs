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
using Hollow.Api.Custom;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.DataAccess.Proxy;

namespace Hollow.Api.Client;

/// <summary>
/// Told how many stale references a consumer can see, so that an application can find the code
/// holding them.
/// </summary>
/// <remarks>
/// Named <c>HollowConsumer.ObjectLongevityDetector</c> in Java; the <c>I</c> prefix follows the .NET
/// interface naming convention.
/// </remarks>
public interface IObjectLongevityDetector
{
    /// <summary>
    /// How many superseded states something still holds a reference into.
    /// </summary>
    /// <remarks>
    /// A hint rather than a fault: an application legitimately holds a record for the length of a
    /// request. A count that stays above zero across many reports is the signal worth chasing.
    /// </remarks>
    void StaleReferenceExistenceDetected(int count)
    {
    }

    /// <summary>
    /// How many superseded states were actually <em>read</em> during the last usage detection window.
    /// </summary>
    /// <remarks>
    /// Stronger than existence: something is not merely holding old data, it is serving from it.
    /// </remarks>
    void StaleReferenceUsageDetected(int count)
    {
    }
}

/// <summary>
/// Watches the states a consumer has superseded, and eventually drops the data behind them.
/// </summary>
/// <remarks>
/// <para>
/// Object longevity keeps a reference readable after the state it came from has moved on, which means
/// the consumer holds on to a historical state for as long as anything might read it. Left alone that
/// is unbounded: one leaked reference pins every state taken since. This is what bounds it. Each
/// superseded state is watched weakly, and once it has outlived the grace period and a usage detection
/// window in which nothing read it, the data behind it is dropped and further reads throw.
/// </para>
/// <para>
/// Named <c>StaleHollowReferenceDetector</c> in Java, which runs a daemon thread and asks the
/// <c>api.sampling</c> framework whether a reference has been used. This port has no sampling; it asks
/// <see cref="HollowProxyDataAccess.WasRead"/> instead, and runs the housekeeping on a
/// <see cref="TimeProvider"/> timer. Java's expired-usage stack trace recorder is not ported — it
/// exists to attribute a read to the code that made it, which a .NET caller gets from the exception a
/// disabled access throws.
/// </para>
/// </remarks>
public sealed class StaleReferenceDetector : IDisposable
{
    /// <summary>How often the handles are looked over, as in Java.</summary>
    private static readonly TimeSpan DefaultHousekeepingInterval = TimeSpan.FromSeconds(30);

    private readonly List<ApiHandle> _handles = [];
    private readonly IObjectLongevityConfig _config;
    private readonly IObjectLongevityDetector? _detector;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _lock = new();

    private ITimer? _timer;
    private bool _disposed;

    /// <summary>
    /// Watches superseded states as <paramref name="config"/> says.
    /// </summary>
    /// <param name="config">The grace and usage detection periods, and whether to drop.</param>
    /// <param name="detector">Told the counts each time the handles are looked over.</param>
    /// <param name="timeProvider">
    /// Where the clock and the housekeeping timer come from. Injected so that a test can make an hour
    /// pass without waiting one.
    /// </param>
    /// <param name="housekeepingInterval">How often to look the handles over.</param>
    public StaleReferenceDetector(
        IObjectLongevityConfig config,
        IObjectLongevityDetector? detector = null,
        TimeProvider? timeProvider = null,
        TimeSpan? housekeepingInterval = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        _config = config;
        _detector = detector;
        _timeProvider = timeProvider ?? TimeProvider.System;
        HousekeepingInterval = housekeepingInterval ?? DefaultHousekeepingInterval;
    }

    /// <summary>How often the handles are looked over.</summary>
    public TimeSpan HousekeepingInterval { get; }

    /// <summary>Starts the housekeeping timer, if it is not already running.</summary>
    public void StartMonitoring()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _timer ??= _timeProvider.CreateTimer(
                static state => ((StaleReferenceDetector)state!).Housekeeping(),
                this,
                HousekeepingInterval,
                HousekeepingInterval);
        }
    }

    /// <summary>Whether <paramref name="api"/> is one of the APIs being watched.</summary>
    public bool IsKnownApiHandle(HollowApi api)
    {
        lock (_lock)
        {
            return _handles.Any(handle => handle.Handles(api));
        }
    }

    /// <summary>
    /// Takes note of a newly built API, and starts the clock on any state it supersedes.
    /// </summary>
    public void NewApiHandle(HollowApi api)
    {
        ArgumentNullException.ThrowIfNull(api);

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            foreach (ApiHandle handle in _handles)
            {
                handle.NewApiAvailable(api, _timeProvider.GetUtcNow());
            }

            _handles.Add(new ApiHandle(api));
        }
    }

    /// <summary>
    /// Looks every handle over: opens the usage detection window on the expired ones, drops what is
    /// droppable, and reports the counts.
    /// </summary>
    /// <remarks>Public so that a test can drive it without waiting for the timer.</remarks>
    public void Housekeeping()
    {
        int existenceSignals;
        int usageSignals;

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();

            for (int i = _handles.Count - 1; i >= 0; i--)
            {
                _handles[i].Housekeeping(now, _config);

                if (_handles[i].IsFinished)
                {
                    _handles.RemoveAt(i);
                }
            }

            existenceSignals = _handles.Count(handle => handle.IsExistingStaleReferenceHint);
            usageSignals = _handles.Count(handle => handle.HasBeenUsedSinceReset);
        }

        // Outside the lock: a detector is application code, and holding the lock across it would let
        // it deadlock the consumer's refresh.
        _detector?.StaleReferenceExistenceDetected(existenceSignals);
        _detector?.StaleReferenceUsageDetected(usageSignals);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        ITimer? timer;

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            timer = _timer;
            _timer = null;

            _handles.Clear();
        }

        timer?.Dispose();
    }

    /// <summary>
    /// One superseded state, watched weakly through both the API and the proxy it read.
    /// </summary>
    /// <remarks>
    /// Weakly, so that watching does not keep anything alive — the whole point is to notice when
    /// something <em>else</em> is keeping it alive.
    /// </remarks>
    private sealed class ApiHandle
    {
        private readonly WeakReference<HollowApi> _api;

        /// <summary>
        /// The proxy this API read through, watched alongside it.
        /// </summary>
        /// <remarks>
        /// Java watches only the API, which leaves a hole: a caller holds <em>records</em>, and a
        /// record holds the proxy, not the API. So the API is routinely collected while the data is
        /// still perfectly reachable — and Java's detach, which starts by dereferencing the API, then
        /// does nothing and the historical state is pinned for good. Watching the proxy as well is
        /// what makes the drop happen in that case, which is the common one.
        /// </remarks>
        private readonly WeakReference<HollowProxyDataAccess>? _proxy;

        /// <summary>
        /// A second object, allocated with the handle and held both strongly and weakly.
        /// </summary>
        /// <remarks>
        /// Java's trick for telling "someone is holding this" from "it simply has not been collected
        /// yet", without forcing a collection. The strong reference is dropped when the usage
        /// detection window opens; after that, if the sentinel has been collected but the state has
        /// not, a collection has run and something is still holding it.
        /// </remarks>
        private readonly WeakReference<object> _weakSentinel;

        private object? _strongSentinel;
        private DateTimeOffset? _gracePeriodBegan;
        private bool _usageDetected;
        private bool _detached;

        internal ApiHandle(HollowApi api)
        {
            object sentinel = new();

            _api = new WeakReference<HollowApi>(api);
            _strongSentinel = sentinel;
            _weakSentinel = new WeakReference<object>(sentinel);

            if (api.DataAccess is HollowProxyDataAccess proxy)
            {
                _proxy = new WeakReference<HollowProxyDataAccess>(proxy);
            }
        }

        /// <summary>
        /// Whether nothing can read this state any more, so the handle can be forgotten.
        /// </summary>
        /// <remarks>
        /// The proxy outliving the API is the ordinary case, since that is what a held record points
        /// at. Only once both are gone is there nothing left to drop.
        /// </remarks>
        internal bool IsFinished => !_api.TryGetTarget(out _) && Proxy is null;

        /// <summary>Whether something is holding this state past its grace period.</summary>
        internal bool IsExistingStaleReferenceHint => !IsFinished && !_weakSentinel.TryGetTarget(out _);

        /// <summary>Whether this state has been read since the usage detection window opened.</summary>
        internal bool HasBeenUsedSinceReset => _strongSentinel is null && Proxy is { WasRead: true };

        /// <summary>The proxy this API read through, if it is still reachable.</summary>
        private HollowProxyDataAccess? Proxy =>
            _proxy is not null && _proxy.TryGetTarget(out HollowProxyDataAccess? proxy) ? proxy : null;

        /// <summary>Whether this handle watches <paramref name="other"/>.</summary>
        internal bool Handles(HollowApi other) =>
            _api.TryGetTarget(out HollowApi? current) && ReferenceEquals(current, other);

        /// <summary>
        /// Starts this handle's clock if <paramref name="newApi"/> genuinely supersedes it.
        /// </summary>
        internal void NewApiAvailable(HollowApi newApi, DateTimeOffset now)
        {
            if (ShouldBeginGracePeriod(newApi))
            {
                _gracePeriodBegan = now;
            }
        }

        /// <summary>Moves this handle through its periods, and drops its data once they have passed.</summary>
        internal void Housekeeping(DateTimeOffset now, IObjectLongevityConfig config)
        {
            if (_gracePeriodBegan is not { } began)
            {
                return;
            }

            if (_strongSentinel is not null && now > began + config.GracePeriod)
            {
                BeginUsageDetectionPeriod();
            }

            if (ShouldDetach(now, began, config))
            {
                Detach();
            }
        }

        /// <summary>
        /// Opens the usage detection window: lets the sentinel go, and starts counting reads afresh.
        /// </summary>
        private void BeginUsageDetectionPeriod()
        {
            _strongSentinel = null;

            Proxy?.ResetWasRead();
        }

        private bool ShouldDetach(DateTimeOffset now, DateTimeOffset began, IObjectLongevityConfig config)
        {
            if (_detached || now <= began + config.GracePeriod + config.UsageDetectionPeriod)
            {
                return false;
            }

            if (config.ForceDropData)
            {
                return true;
            }

            if (!config.DropDataAutomatically || _usageDetected)
            {
                return false;
            }

            // A read during the window means the reference is genuinely in use. Remembered, so that a
            // later window finding no reads does not then drop data something is still serving from.
            if (Proxy is { WasRead: true })
            {
                _usageDetected = true;

                return false;
            }

            return true;
        }

        /// <summary>Drops the data behind this state, so that any further read through it throws.</summary>
        private void Detach()
        {
            // Through the proxy, which is what a held record points at and what holds the data. The
            // API is usually gone by now; where it is not, its caches are released too.
            Proxy?.DisableDataAccess();

            if (_api.TryGetTarget(out HollowApi? current))
            {
                current.DetachCaches();
            }

            _detached = true;
        }

        /// <summary>
        /// Whether <paramref name="newApi"/> supersedes this one, rather than being another view of the
        /// same data.
        /// </summary>
        /// <remarks>
        /// The comparison unwraps proxies on both sides. A new API built over a proxy onto the very
        /// data this handle already reads is not a supersession — nothing has moved on, so there is
        /// nothing to start a clock on.
        /// </remarks>
        private bool ShouldBeginGracePeriod(HollowApi newApi)
        {
            if (_gracePeriodBegan is not null
                || !_api.TryGetTarget(out HollowApi? current)
                || ReferenceEquals(current, newApi))
            {
                return false;
            }

            return !ReferenceEquals(Unwrap(current.DataAccess), Unwrap(newApi.DataAccess));
        }

        private static IHollowDataAccess Unwrap(IHollowDataAccess dataAccess) =>
            dataAccess is HollowProxyDataAccess proxy ? proxy.ProxiedDataAccess : dataAccess;
    }
}
