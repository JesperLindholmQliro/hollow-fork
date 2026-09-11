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
using Hollow.Core;
using Hollow.Core.Write;

namespace Hollow.Tests;

/// <summary>
/// A blob store held in memory, standing in for the producer half of a publish/consume round trip.
/// </summary>
/// <remarks>
/// Java ships an equivalent in its <c>hollow-test</c> artifact. The producer API is not ported, so this
/// takes a <see cref="HollowWriteStateEngine"/> directly and writes the blobs itself.
/// </remarks>
internal sealed class InMemoryBlobStore : IBlobRetriever
{
    private readonly Dictionary<long, Blob> _snapshots = [];
    private readonly Dictionary<long, Blob> _deltas = [];
    private readonly Dictionary<long, Blob> _reverseDeltas = [];

    /// <summary>The version the producer last published, in the order the tests publish them.</summary>
    internal long LatestVersion { get; private set; } = HollowConstants.VersionNone;

    /// <summary>The announcement metadata attached to <see cref="LatestVersion"/>.</summary>
    internal Dictionary<string, string> LatestAnnouncementMetadata { get; } = [];

    /// <inheritdoc />
    public Blob? RetrieveSnapshotBlob(long desiredVersion)
    {
        if (_snapshots.TryGetValue(desiredVersion, out Blob? exact))
        {
            return exact;
        }

        long nearest = HollowConstants.VersionNone;
        foreach (long version in _snapshots.Keys)
        {
            if (version < desiredVersion && version > nearest)
            {
                nearest = version;
            }
        }

        return nearest == HollowConstants.VersionNone ? null : _snapshots[nearest];
    }

    /// <inheritdoc />
    public Blob? RetrieveDeltaBlob(long currentVersion) => _deltas.GetValueOrDefault(currentVersion);

    /// <inheritdoc />
    public Blob? RetrieveReverseDeltaBlob(long currentVersion) => _reverseDeltas.GetValueOrDefault(currentVersion);

    /// <summary>Removes a snapshot, so that a consumer has to fall back to an older one.</summary>
    internal void RemoveSnapshot(long version) => _snapshots.Remove(version);

    /// <summary>
    /// Writes a snapshot of <paramref name="writeEngine"/>, and — when the engine has a previous cycle
    /// — the delta and reverse delta connecting it to that cycle. Rolls the engine on to the next
    /// cycle afterwards, so the caller can add the following cycle's records straight away.
    /// </summary>
    internal void Publish(HollowWriteStateEngine writeEngine, long version, bool withDeltas = true)
    {
        // Give each cycle its own randomized tag, as a real producer does, so that the consumer's check
        // that a delta belongs to the state it is being applied to is actually exercised.
        writeEngine.RandomizedTag = version;

        HollowBlobWriter writer = new(writeEngine);

        _snapshots[version] = new ByteArrayBlob(Write(writer.WriteSnapshot), version);

        if (withDeltas && LatestVersion != HollowConstants.VersionNone)
        {
            _deltas[LatestVersion] = new ByteArrayBlob(Write(writer.WriteDelta), LatestVersion, version);
            _reverseDeltas[version] = new ByteArrayBlob(Write(writer.WriteReverseDelta), version, LatestVersion);
        }

        LatestVersion = version;
        LatestAnnouncementMetadata.Clear();
        foreach ((string name, string value) in writeEngine.HeaderTags)
        {
            LatestAnnouncementMetadata[name] = value;
        }

        writeEngine.PrepareForNextCycle();
    }

    private static byte[] Write(Action<Stream> write)
    {
        using MemoryStream stream = new();
        write(stream);

        return stream.ToArray();
    }

    private sealed class ByteArrayBlob : Blob
    {
        private readonly byte[] _bytes;

        internal ByteArrayBlob(byte[] bytes, long toVersion)
            : base(toVersion) => _bytes = bytes;

        internal ByteArrayBlob(byte[] bytes, long fromVersion, long toVersion)
            : base(fromVersion, toVersion) => _bytes = bytes;

        public override Stream OpenStream() => new MemoryStream(_bytes, writable: false);
    }
}

/// <summary>
/// A blob that throws instead of producing bytes, for testing what a consumer does when a transition
/// cannot be applied.
/// </summary>
internal sealed class UnreadableBlob : Blob
{
    internal UnreadableBlob(long toVersion)
        : base(toVersion)
    {
    }

    internal UnreadableBlob(long fromVersion, long toVersion)
        : base(fromVersion, toVersion)
    {
    }

    public override Stream OpenStream() => throw new IOException("This blob cannot be read.");
}
