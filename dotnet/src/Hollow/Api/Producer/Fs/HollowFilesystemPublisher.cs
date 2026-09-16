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
using Hollow.Api.Consumer;
using Hollow.Api.Consumer.Fs;
using Hollow.Core.Util;

namespace Hollow.Api.Producer.Fs;

/// <summary>
/// Publishes blobs as files in a directory, which is what
/// <see cref="HollowFilesystemBlobRetriever"/> reads.
/// </summary>
/// <remarks>
/// <para>
/// The layout is shared with Java: <c>snapshot-{version}</c>, <c>delta-{from}-{to}</c>,
/// <c>reversedelta-{from}-{to}</c> and <c>header-{version}</c>.
/// </para>
/// <para>
/// Each blob is written to a temporary name and then moved into place, so a consumer scanning the
/// directory never sees a half-written one.
/// </para>
/// </remarks>
public sealed class HollowFilesystemPublisher : IPublisher
{
    private readonly string _blobStoreDirectory;

    /// <summary>
    /// Publishes to <paramref name="blobStoreDirectory"/>, creating it if it does not exist.
    /// </summary>
    public HollowFilesystemPublisher(string blobStoreDirectory)
    {
        ArgumentNullException.ThrowIfNull(blobStoreDirectory);

        Directory.CreateDirectory(blobStoreDirectory);

        _blobStoreDirectory = blobStoreDirectory;
    }

    /// <inheritdoc />
    public void Publish(IPublishArtifact publishArtifact)
    {
        ArgumentNullException.ThrowIfNull(publishArtifact);

        string fileName = publishArtifact switch
        {
            HeaderBlob header => $"header-{header.Version.Invariant()}",

            Blob { BlobType: BlobType.Snapshot } snapshot =>
                $"snapshot-{snapshot.ToVersion.Invariant()}",

            Blob blob =>
                $"{blob.BlobType.GetPrefix()}-{blob.FromVersion.Invariant()}-{blob.ToVersion.Invariant()}",

            _ => throw new ArgumentException(
                $"Cannot publish an artifact of type {publishArtifact.GetType().Name}.", nameof(publishArtifact)),
        };

        string destination = Path.Combine(_blobStoreDirectory, fileName);

        PublishTo(publishArtifact.OpenStream, destination);

        if (publishArtifact is Blob { OptionalPartNames.Count: > 0 } blobWithParts)
        {
            foreach (string partName in blobWithParts.OptionalPartNames)
            {
                // The name a consumer will look for: the blob's own name with the part spliced in
                // after the prefix, which is the layout a Java blob store uses.
                string partFileName = blobWithParts.BlobType == BlobType.Snapshot
                    ? $"snapshot_{partName}-{blobWithParts.ToVersion.Invariant()}"
                    : $"{blobWithParts.BlobType.GetPrefix()}_{partName}-"
                        + $"{blobWithParts.FromVersion.Invariant()}-{blobWithParts.ToVersion.Invariant()}";

                PublishTo(
                    () => blobWithParts.OpenOptionalPartStream(partName),
                    Path.Combine(_blobStoreDirectory, partFileName));
            }
        }
    }

    /// <summary>
    /// Copies one artifact into place, through a temporary name so that a reader never sees a partial
    /// file under the name it is looking for.
    /// </summary>
    private static void PublishTo(Func<Stream> openSource, string destination)
    {

        // A ".tmp" suffix rather than a temporary directory, so that the move is within one filesystem
        // and therefore atomic. The retriever ignores a name whose trailing segment is not a number.
        string temporary = $"{destination}.{Random.Shared.Next(int.MaxValue).ToString(CultureInfo.InvariantCulture)}.tmp";

        try
        {
            using (Stream source = openSource())
            using (FileStream target = File.Create(temporary))
            {
                source.CopyTo(target);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }
}

/// <summary>
/// Announces a version by writing it to <c>announced.version</c> in the blob store directory, which is
/// what <see cref="HollowFilesystemAnnouncementWatcher"/> polls.
/// </summary>
public sealed class HollowFilesystemAnnouncer : IAnnouncer
{
    private readonly string _publishDirectory;

    /// <summary>
    /// Announces into <paramref name="publishDirectory"/>, creating it if it does not exist.
    /// </summary>
    public HollowFilesystemAnnouncer(string publishDirectory)
    {
        ArgumentNullException.ThrowIfNull(publishDirectory);

        Directory.CreateDirectory(publishDirectory);

        _publishDirectory = publishDirectory;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A file cannot carry the metadata, so it is dropped. A consumer reading a filesystem
    /// announcement therefore cannot detect a schema change from the announcement alone — it would
    /// have to read the header blob instead.
    /// </remarks>
    public void Announce(long stateVersion, IReadOnlyDictionary<string, string> metadata)
    {
        string announcePath = Path.Combine(
            _publishDirectory, HollowFilesystemAnnouncementWatcher.AnnouncementFileName);

        // Write then move, so a consumer polling the file never reads a partial version.
        string temporary = $"{announcePath}.tmp";

        File.WriteAllText(temporary, stateVersion.ToString(CultureInfo.InvariantCulture));
        File.Move(temporary, announcePath, overwrite: true);
    }
}
