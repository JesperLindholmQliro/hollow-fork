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
using Hollow.Api.Producer;
using Hollow.Core;
using ConsumerBlob = Hollow.Api.Consumer.Blob;
using ConsumerHeaderBlob = Hollow.Api.Consumer.HeaderBlob;
using ProducerBlob = Hollow.Api.Producer.Blob;
using ProducerHeaderBlob = Hollow.Api.Producer.HeaderBlob;

namespace Hollow.Tests.Api;

/// <summary>
/// A blob store that is both an <see cref="IPublisher"/> and an <see cref="IBlobRetriever"/>, so a
/// producer and a consumer can be pointed at the same thing.
/// </summary>
internal sealed class InMemoryPublisher : IPublisher, IBlobRetriever, IAnnouncer
{
    private readonly Dictionary<long, PublishedBlob> _snapshots = [];
    private readonly Dictionary<long, PublishedBlob> _deltas = [];
    private readonly Dictionary<long, PublishedBlob> _reverseDeltas = [];
    private readonly Dictionary<long, PublishedHeaderBlob> _headers = [];

    internal long AnnouncedVersion { get; private set; } = HollowConstants.VersionNone;

    internal IReadOnlyDictionary<string, string> AnnouncedMetadata { get; private set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    internal int PublishedSnapshotCount => _snapshots.Count;

    internal int PublishedDeltaCount => _deltas.Count;

    internal int PublishedHeaderCount => _headers.Count;

    public void Publish(IPublishArtifact publishArtifact)
    {
        switch (publishArtifact)
        {
            case ProducerHeaderBlob header:
                _headers[header.Version] = new PublishedHeaderBlob(ReadAll(header), header.Version);
                break;

            case ProducerBlob { BlobType: BlobType.Snapshot } snapshot:
                _snapshots[snapshot.ToVersion] = new PublishedBlob(ReadAll(snapshot), snapshot.ToVersion);
                break;

            case ProducerBlob { BlobType: BlobType.Delta } delta:
                _deltas[delta.FromVersion] =
                    new PublishedBlob(ReadAll(delta), delta.FromVersion, delta.ToVersion);
                break;

            case ProducerBlob reverseDelta:
                _reverseDeltas[reverseDelta.FromVersion] =
                    new PublishedBlob(ReadAll(reverseDelta), reverseDelta.FromVersion, reverseDelta.ToVersion);
                break;
        }
    }

    public void Announce(long stateVersion, IReadOnlyDictionary<string, string> metadata)
    {
        AnnouncedVersion = stateVersion;
        AnnouncedMetadata = metadata;
    }

    public ConsumerBlob? RetrieveSnapshotBlob(long desiredVersion)
    {
        if (_snapshots.TryGetValue(desiredVersion, out PublishedBlob? exact))
        {
            return exact;
        }

        long nearest = _snapshots.Keys
            .Where(version => version < desiredVersion)
            .DefaultIfEmpty(HollowConstants.VersionNone)
            .Max();

        return nearest == HollowConstants.VersionNone ? null : _snapshots[nearest];
    }

    public ConsumerBlob? RetrieveDeltaBlob(long currentVersion) => _deltas.GetValueOrDefault(currentVersion);

    public ConsumerBlob? RetrieveReverseDeltaBlob(long currentVersion) =>
        _reverseDeltas.GetValueOrDefault(currentVersion);

    public ConsumerHeaderBlob? RetrieveHeaderBlob(long currentVersion) =>
        _headers.GetValueOrDefault(currentVersion);

    private static byte[] ReadAll(IPublishArtifact artifact)
    {
        using Stream stream = artifact.OpenStream();
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);

        return buffer.ToArray();
    }

    private sealed class PublishedBlob : ConsumerBlob
    {
        private readonly byte[] _bytes;

        internal PublishedBlob(byte[] bytes, long toVersion)
            : base(toVersion) => _bytes = bytes;

        internal PublishedBlob(byte[] bytes, long fromVersion, long toVersion)
            : base(fromVersion, toVersion) => _bytes = bytes;

        public override Stream OpenStream() => new MemoryStream(_bytes, writable: false);
    }

    private sealed class PublishedHeaderBlob(byte[] bytes, long version) : ConsumerHeaderBlob(version)
    {
        public override Stream OpenStream() => new MemoryStream(bytes, writable: false);
    }
}
