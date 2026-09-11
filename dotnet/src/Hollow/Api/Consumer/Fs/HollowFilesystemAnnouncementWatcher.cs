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

using System.Globalization;

namespace Hollow.Api.Consumer.Fs;

/// <summary>
/// Announces versions through a file in the blob store directory, and polls it for changes.
/// </summary>
/// <remarks>
/// The file is <c>announced.version</c>, holding the version as decimal text — the same convention a
/// filesystem-backed Java producer uses.
/// </remarks>
public sealed class HollowFilesystemAnnouncementWatcher : IAnnouncementWatcher, IDisposable
{
    /// <summary>The name of the file the announced version is written to.</summary>
    public const string AnnouncementFileName = "announced.version";

    private readonly string _announcePath;
    private readonly List<HollowConsumer> _subscribedConsumers = [];
    private readonly Lock _consumersLock = new();
    private readonly Timer? _timer;

    private long _latestVersion;

    /// <summary>
    /// Watches the announcement file in <paramref name="publishDirectory"/>.
    /// </summary>
    /// <param name="publishDirectory">The directory the producer publishes to.</param>
    /// <param name="pollInterval">
    /// How often to re-read the announcement file, or <see cref="TimeSpan.Zero"/> to not poll at all —
    /// in which case a caller drives refreshes itself and this watcher only reports the version.
    /// </param>
    public HollowFilesystemAnnouncementWatcher(string publishDirectory, TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(publishDirectory);

        _announcePath = Path.Combine(publishDirectory, AnnouncementFileName);
        _latestVersion = ReadLatestVersion();

        TimeSpan interval = pollInterval ?? TimeSpan.FromSeconds(1);
        if (interval > TimeSpan.Zero)
        {
            _timer = new Timer(_ => Poll(), state: null, interval, interval);
        }
    }

    /// <inheritdoc />
    public long GetLatestVersion() => Interlocked.Read(ref _latestVersion);

    /// <inheritdoc />
    public void SubscribeToUpdates(HollowConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        lock (_consumersLock)
        {
            _subscribedConsumers.Add(consumer);
        }
    }

    /// <summary>
    /// Re-reads the announcement file and refreshes every subscribed consumer if the version changed.
    /// </summary>
    /// <remarks>
    /// Called on a timer when polling is enabled. Exposed so that a test, or a caller driving the
    /// watcher from a file-change notification of its own, can trigger the check directly rather than
    /// waiting out the interval.
    /// </remarks>
    public void Poll()
    {
        long currentVersion = ReadLatestVersion();

        if (Interlocked.Exchange(ref _latestVersion, currentVersion) == currentVersion)
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
            // Fire and forget: a failed refresh reaches the consumer's own refresh listeners, and a
            // watcher that threw here would only kill the timer.
            _ = consumer.TriggerRefreshAsync();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _timer?.Dispose();

    private long ReadLatestVersion()
    {
        try
        {
            string text = File.ReadAllText(_announcePath).Trim();

            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long version)
                ? version
                : IAnnouncementWatcher.NoAnnouncementAvailable;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or IOException)
        {
            // Nothing announced yet, or the producer is mid-write. Either way the next poll will see it.
            return IAnnouncementWatcher.NoAnnouncementAvailable;
        }
    }
}
