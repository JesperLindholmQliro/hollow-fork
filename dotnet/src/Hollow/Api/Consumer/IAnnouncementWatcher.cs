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

using Hollow.Core;
using Hollow.Core.Util;

namespace Hollow.Api.Consumer;

/// <summary>
/// Whether a version was ever announced.
/// </summary>
public enum AnnouncementStatus
{
    /// <summary>The watcher cannot say — either it does not track this, or there is no watcher.</summary>
    Unknown,

    /// <summary>The version was announced.</summary>
    Announced,

    /// <summary>No announcement was found for the version.</summary>
    NotAnnounced,
}

/// <summary>
/// A version, together with whatever the announcement mechanism knows about it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IsPinned"/> tells a consumer that a producer moving backwards is deliberate rather than a
/// forked delta chain. <see cref="AnnouncementMetadata"/> carries producer-set tags, which is how the
/// schema-change double snapshot detects a changed data model. <see cref="WasAnnounced"/> keeps the
/// update plan from falling back onto a snapshot nobody announced.
/// </para>
/// <para>
/// Each is optional because an announcement mechanism need not track it; <see langword="null"/> means
/// "not known", which is different from <see langword="false"/>.
/// </para>
/// </remarks>
public sealed class VersionInfo
{
    /// <summary>
    /// Initialises version information carrying nothing but the version itself.
    /// </summary>
    public VersionInfo(long version) => Version = version;

    /// <summary>
    /// Initialises version information.
    /// </summary>
    public VersionInfo(
        long version,
        IReadOnlyDictionary<string, string>? announcementMetadata,
        bool? isPinned,
        bool? wasAnnounced = null)
    {
        Version = version;
        AnnouncementMetadata = announcementMetadata;
        IsPinned = isPinned;
        WasAnnounced = wasAnnounced;
    }

    /// <summary>The version.</summary>
    public long Version { get; }

    /// <summary>The tags the producer attached to the announcement, if any are known.</summary>
    public IReadOnlyDictionary<string, string>? AnnouncementMetadata { get; }

    /// <summary>Whether the version is pinned, or <see langword="null"/> when not known.</summary>
    public bool? IsPinned { get; }

    /// <summary>Whether the version was announced, or <see langword="null"/> when not known.</summary>
    public bool? WasAnnounced { get; }

    /// <inheritdoc />
    public override string ToString() => Version.Invariant();
}

/// <summary>
/// Tracks the latest announced version and pokes subscribed consumers when it changes.
/// </summary>
/// <remarks>
/// Named <c>HollowConsumer.AnnouncementWatcher</c> in Java; the <c>I</c> prefix follows the .NET
/// interface naming convention. A consumer built with a watcher refreshes to whatever the watcher
/// announces, so <see cref="HollowConsumer.TriggerRefreshTo(long)"/> is unavailable on it.
/// </remarks>
public interface IAnnouncementWatcher
{
    /// <summary>The version reported when nothing has been announced.</summary>
    const long NoAnnouncementAvailable = HollowConstants.VersionNone;

    /// <summary>The latest announced version.</summary>
    long GetLatestVersion();

    /// <summary>
    /// Subscribes <paramref name="consumer"/> to announcements.
    /// </summary>
    /// <remarks>
    /// An implementation calls <see cref="HollowConsumer.TriggerRefreshAsync"/> on the subscribed
    /// consumer whenever it learns of a new version, whether by push or by polling.
    /// </remarks>
    void SubscribeToUpdates(HollowConsumer consumer);

    /// <summary>
    /// The latest announced version together with whatever else is known about it.
    /// </summary>
    VersionInfo GetLatestVersionInfo() =>
        new(GetLatestVersion(), announcementMetadata: null, isPinned: null, wasAnnounced: true);

    /// <summary>
    /// Whether <paramref name="version"/> was announced.
    /// </summary>
    AnnouncementStatus GetVersionAnnouncementStatus(long version) => AnnouncementStatus.Unknown;
}
