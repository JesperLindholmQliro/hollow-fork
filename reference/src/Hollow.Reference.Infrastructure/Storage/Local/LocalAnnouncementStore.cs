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

using System.Globalization;
using System.Text.Json;
using Hollow.Api.Consumer;

namespace Hollow.Reference.Infrastructure.Storage.Local;

/// <summary>
/// An announcement store that is a folder full of empty-ish files, one per announced version.
/// </summary>
/// <remarks>
/// <para>
/// This is what stands in for the Java reference implementation's DynamoDB table. Announcing a version
/// creates a file in the watching folder named after it; the announced version is the highest-numbered
/// file there. A consumer does not poll for it — a <see cref="FileSystemWatcher"/> on the folder tells
/// it a file appeared, which is as close as a directory gets to the push notification a real
/// announcement bus would give.
/// </para>
/// <para>
/// A pin is a file called <c>pinned.version</c> holding a version number, matching the DynamoDB row's
/// <c>pin_version</c> attribute: while it is there, consumers stay on that version however much the
/// producer announces. Delete it and they catch up.
/// </para>
/// <para>
/// The folder is pruned as it grows, keeping the newest few announcements. A real announcement store
/// holds one row; this one holds a history because the history is the notification mechanism, and a
/// producer cycling every ten seconds would otherwise fill a directory nobody can list.
/// </para>
/// </remarks>
public sealed class LocalAnnouncementStore : IHollowAnnouncementStore
{
    /// <summary>The file a pin is written to, sitting beside the announcements it overrides.</summary>
    public const string PinFileName = "pinned.version";

    private static readonly JsonSerializerOptions MetadataJson = new() { WriteIndented = false };

    private readonly string _rootPath;
    private readonly string _watchingDirectory;
    private readonly SimulatedLatency _latency;
    private readonly int _announcementsToKeep;

    public LocalAnnouncementStore(
        string rootPath, SimulatedLatency? latency = null, int announcementsToKeep = 50)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(announcementsToKeep, 1);

        _rootPath = rootPath;
        _watchingDirectory = Path.Combine(rootPath, "watching");
        _latency = latency ?? SimulatedLatency.None;
        _announcementsToKeep = announcementsToKeep;

        Directory.CreateDirectory(_watchingDirectory);
    }

    /// <summary>The folder announcements appear in, and the one the watcher is pointed at.</summary>
    public string WatchingDirectory => _watchingDirectory;

    public async Task AnnounceAsync(
        long version,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        await _latency.DelayAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        // Written outside the watching folder and moved in, so that the watcher sees one completed file
        // appear rather than a temporary one being filled in.
        string staged = Path.Combine(_rootPath, $"announce.{Guid.NewGuid():n}.tmp");

        try
        {
            await File.WriteAllTextAsync(
                staged,
                JsonSerializer.Serialize(metadata, MetadataJson),
                cancellationToken).ConfigureAwait(false);

            File.Move(staged, Path.Combine(_watchingDirectory, FileNameFor(version)), overwrite: true);
        }
        catch
        {
            TryDelete(staged);
            throw;
        }

        Prune();
    }

    public async Task<AnnouncedVersions> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _latency.DelayAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        long announced = IAnnouncementWatcher.NoAnnouncementAvailable;
        string? announcedPath = null;

        foreach ((long version, string path) in EnumerateAnnouncements())
        {
            if (version > announced)
            {
                announced = version;
                announcedPath = path;
            }
        }

        IReadOnlyDictionary<string, string> metadata = announcedPath is null
            ? AnnouncedVersions.None.Metadata
            : await ReadMetadataAsync(announcedPath, cancellationToken).ConfigureAwait(false);

        return new AnnouncedVersions(announced, ReadPin(), metadata);
    }

    public IDisposable? Subscribe(Action onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);

        FileSystemWatcher watcher = new(_watchingDirectory)
        {
            // A new file is the announcement itself; the rest are here for the pin, which is written
            // over and deleted rather than created afresh.
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            IncludeSubdirectories = false,
        };

        void Raise(object? sender, FileSystemEventArgs e) => onChanged();

        watcher.Created += Raise;
        watcher.Changed += Raise;
        watcher.Deleted += Raise;
        watcher.Renamed += Raise;

        // The watcher gives up if the OS drops events on it — too many at once, or the folder going
        // away. Re-reading is what the callback does anyway, so telling the caller to look is both the
        // recovery and the notification.
        watcher.Error += (_, _) => onChanged();

        watcher.EnableRaisingEvents = true;

        return watcher;
    }

    /// <summary>Pins consumers to <paramref name="version"/>, or lifts the pin if it is null.</summary>
    /// <remarks>
    /// Nothing in the producer or the consumer calls this — a pin is an operator's decision, made by
    /// writing the file. It is here so that a test can make one, and so that the file's format has a
    /// single definition.
    /// </remarks>
    public void Pin(long? version)
    {
        string path = Path.Combine(_watchingDirectory, PinFileName);

        if (version is not { } pinned)
        {
            TryDelete(path);
            return;
        }

        string staged = Path.Combine(_rootPath, $"pin.{Guid.NewGuid():n}.tmp");
        File.WriteAllText(staged, pinned.ToString(CultureInfo.InvariantCulture));
        File.Move(staged, path, overwrite: true);
    }

    private static string FileNameFor(long version) => version.ToString(CultureInfo.InvariantCulture);

    private IEnumerable<(long Version, string Path)> EnumerateAnnouncements()
    {
        if (!Directory.Exists(_watchingDirectory))
        {
            yield break;
        }

        foreach (string path in Directory.EnumerateFiles(_watchingDirectory))
        {
            string name = Path.GetFileName(path);

            // Only the plain numbers are announcements; the pin file and anything else in the folder
            // is deliberately not one.
            if (long.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out long version))
            {
                yield return (version, path);
            }
        }
    }

    private long ReadPin()
    {
        try
        {
            string text = File.ReadAllText(Path.Combine(_watchingDirectory, PinFileName)).Trim();

            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long pinned)
                ? pinned
                : IAnnouncementWatcher.NoAnnouncementAvailable;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or IOException)
        {
            return IAnnouncementWatcher.NoAnnouncementAvailable;
        }
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadMetadataAsync(
        string path, CancellationToken cancellationToken)
    {
        try
        {
            string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, MetadataJson)
                ?? AnnouncedVersions.None.Metadata;
        }
        catch (Exception e) when (e is FileNotFoundException or IOException or JsonException)
        {
            // Read while it was being moved into place, or written by something else. The version is
            // what matters; the metadata is decoration.
            return AnnouncedVersions.None.Metadata;
        }
    }

    private void Prune()
    {
        List<(long Version, string Path)> announcements = [.. EnumerateAnnouncements()];

        if (announcements.Count <= _announcementsToKeep)
        {
            return;
        }

        announcements.Sort(static (left, right) => right.Version.CompareTo(left.Version));

        foreach ((_, string path) in announcements.Skip(_announcementsToKeep))
        {
            TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Another process reading it, or already gone. Either way there is nothing to do about it.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
