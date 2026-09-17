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

namespace Hollow.Api.Sampling;

/// <summary>
/// Decides, read by read, whether a sampler should count this one.
/// </summary>
/// <remarks>
/// Sampling exists to answer "which fields does this application actually read", which is what says
/// whether a field is worth its bytes. Counting every read would cost more than the reads, so a
/// director says when to count — always, never, or for a slice of each second.
/// </remarks>
public abstract class HollowSamplingDirector
{
    /// <summary>Whether the read about to happen should be counted.</summary>
    public abstract bool ShouldRecord();

    /// <summary>
    /// Whether the calling work is the dataset applying a transition, whose reads are not the
    /// application's.
    /// </summary>
    /// <remarks>
    /// A refresh reads records to build indexes and checksums. Counting those would report fields as
    /// hot that no caller ever asked for, so the work doing it marks itself with
    /// <see cref="HollowSamplingScope.EnterUpdate"/> — which is where Java's
    /// <c>setUpdateThread(Thread)</c> went, and why.
    /// </remarks>
    protected static bool IsUpdate => HollowSamplingScope.IsUpdate;
}

/// <summary>A director that counts nothing, which is what every sampler starts with.</summary>
public sealed class DisabledSamplingDirector : HollowSamplingDirector
{
    private DisabledSamplingDirector()
    {
    }

    /// <summary>The one instance, which samplers compare against to skip their work entirely.</summary>
    public static DisabledSamplingDirector Instance { get; } = new();

    /// <inheritdoc />
    public override bool ShouldRecord() => false;
}

/// <summary>A director that counts every read but the dataset's own.</summary>
public sealed class EnabledSamplingDirector : HollowSamplingDirector
{
    /// <inheritdoc />
    public override bool ShouldRecord() => !IsUpdate;
}

/// <summary>Notified when a time-sliced director turns counting on or off.</summary>
/// <remarks>Named <c>SamplingStatusListener</c> in Java.</remarks>
public interface ISamplingStatusListener
{
    /// <summary>Called with the new state whenever it changes.</summary>
    void SamplingStatusChanged(bool samplingOn);
}

/// <summary>
/// A director that counts for a short slice out of each interval, so that sampling costs a fraction of
/// what counting everything would.
/// </summary>
/// <remarks>
/// <para>
/// One millisecond in every second, by default. That is enough to tell a field nothing reads from one
/// read a million times, which is the question sampling is for, and cheap enough to leave on.
/// </para>
/// <para>
/// Java runs the toggle on a daemon thread that sleeps between flips. This uses a
/// <see cref="TimeProvider"/> timer, as the rest of the port's housekeeping does — no thread parked
/// doing nothing, and a test can make an hour pass without waiting for it.
/// </para>
/// <para>
/// Java's toggler writes the flag and the listener list without synchronisation. Here the flag is
/// volatile and the list is guarded, because the timer callback and the caller of
/// <see cref="StartSampling"/> are genuinely different threads.
/// </para>
/// </remarks>
public sealed class TimeSliceSamplingDirector : HollowSamplingDirector, IDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly List<ISamplingStatusListener> _listeners = [];
    private readonly Lock _lock = new();

    private ITimer? _timer;
    private TimeSpan _off;
    private TimeSpan _on;
    private volatile bool _record;
    private bool _isInPlay;

    /// <summary>Counts for one millisecond in every second.</summary>
    public TimeSliceSamplingDirector(TimeProvider? timeProvider = null)
        : this(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1), timeProvider)
    {
    }

    /// <summary>Counts for <paramref name="on"/> out of every <paramref name="off"/> plus it.</summary>
    public TimeSliceSamplingDirector(TimeSpan off, TimeSpan on, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(off, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(on, TimeSpan.Zero);

        _off = off;
        _on = on;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public override bool ShouldRecord() => _record && !IsUpdate;

    /// <summary>Changes the timing, taking effect at the next flip.</summary>
    public void SetTiming(TimeSpan off, TimeSpan on)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(off, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(on, TimeSpan.Zero);

        lock (_lock)
        {
            _off = off;
            _on = on;
        }
    }

    /// <summary>Starts flipping. Starting an already-started director does nothing.</summary>
    public void StartSampling()
    {
        lock (_lock)
        {
            if (_isInPlay)
            {
                return;
            }

            _isInPlay = true;

            // Counting starts off, so the first flip is the one that turns it on.
            _timer = _timeProvider.CreateTimer(_ => Flip(), state: null, _off, Timeout.InfiniteTimeSpan);
        }

        NotifyListeners();
    }

    /// <summary>Stops flipping and leaves counting off.</summary>
    public void StopSampling()
    {
        lock (_lock)
        {
            _isInPlay = false;
            _record = false;

            _timer?.Dispose();
            _timer = null;
        }

        NotifyListeners();
    }

    /// <summary>
    /// Adds a listener, telling it the current state straight away so that it never starts out of step.
    /// </summary>
    public void AddSamplingStatusListener(ISamplingStatusListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);

        listener.SamplingStatusChanged(_record);

        lock (_lock)
        {
            _listeners.Add(listener);
        }
    }

    /// <inheritdoc />
    public void Dispose() => StopSampling();

    private void Flip()
    {
        lock (_lock)
        {
            if (!_isInPlay)
            {
                return;
            }

            _record = !_record;

            // Re-armed one interval at a time rather than run on a period, because the two intervals
            // differ and which one is next depends on which way the flag just went.
            _timer?.Change(_record ? _on : _off, Timeout.InfiniteTimeSpan);
        }

        NotifyListeners();
    }

    private void NotifyListeners()
    {
        ISamplingStatusListener[] listeners;

        lock (_lock)
        {
            listeners = [.. _listeners];
        }

        bool samplingOn = _record;

        foreach (ISamplingStatusListener listener in listeners)
        {
            listener.SamplingStatusChanged(samplingOn);
        }
    }
}
