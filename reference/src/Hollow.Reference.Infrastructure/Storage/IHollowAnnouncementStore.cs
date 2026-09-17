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

namespace Hollow.Reference.Infrastructure.Storage;

/// <summary>
/// What a producer has announced, and what a reader has been pinned to.
/// </summary>
/// <param name="AnnouncedVersion">
/// The newest version the producer has announced, or
/// <see cref="IAnnouncementWatcher.NoAnnouncementAvailable"/> if it has never announced one.
/// </param>
/// <param name="PinnedVersion">
/// A version consumers are held at regardless of what has been announced since, or
/// <see cref="IAnnouncementWatcher.NoAnnouncementAvailable"/> when nothing is pinned. This is how a
/// bad dataset is rolled back without rolling back the producer.
/// </param>
/// <param name="Metadata">Whatever the producer said about the announcement, which may be empty.</param>
public readonly record struct AnnouncedVersions(
    long AnnouncedVersion,
    long PinnedVersion,
    IReadOnlyDictionary<string, string> Metadata)
{
    /// <summary>Nothing has ever been announced.</summary>
    public static AnnouncedVersions None { get; } = new(
        IAnnouncementWatcher.NoAnnouncementAvailable,
        IAnnouncementWatcher.NoAnnouncementAvailable,
        new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>Whether a pin is in force.</summary>
    public bool IsPinned => PinnedVersion != IAnnouncementWatcher.NoAnnouncementAvailable;

    /// <summary>The version a consumer should actually be on: the pin if there is one, else the announcement.</summary>
    public long EffectiveVersion => IsPinned ? PinnedVersion : AnnouncedVersion;
}

/// <summary>
/// Where the announced version is kept, and how a consumer finds out it has changed.
/// </summary>
/// <remarks>
/// <para>
/// The Java reference implementation offers two of these — one over an S3 object and one over a
/// DynamoDB table — and both poll once a second. The abstraction here is the same idea with room for a
/// store that can say when it changed rather than being asked: see <see cref="Subscribe"/>.
/// </para>
/// </remarks>
public interface IHollowAnnouncementStore
{
    /// <summary>Records <paramref name="version"/> as the announced one.</summary>
    Task AnnounceAsync(
        long version,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default);

    /// <summary>Reads what is currently announced and pinned.</summary>
    Task<AnnouncedVersions> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks to be told when the announcement changes, returning something to dispose to stop, or
    /// <see langword="null"/> if this store cannot say and has to be polled.
    /// </summary>
    /// <remarks>
    /// <paramref name="onChanged"/> may be called from any thread, more than once for a single change,
    /// and occasionally when nothing has changed at all; the watcher re-reads and compares rather than
    /// trusting it. Only the local mode implements this — a <c>FileSystemWatcher</c> is the nearest a
    /// directory gets to a push notification. S3, DynamoDB, Azure Blob Storage and Azure Table Storage
    /// all return <see langword="null"/> and are polled, as in the Java original.
    /// </remarks>
    IDisposable? Subscribe(Action onChanged);
}
