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

using Hollow.Core.Read.Engine;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Api.Producer;

/// <summary>
/// Assigns a version to each cycle.
/// </summary>
/// <remarks>
/// Versions must ascend: a later state has a greater version. A consumer plans its route through the
/// delta chain by comparing them.
/// </remarks>
public interface IVersionMinter
{
    /// <summary>Returns a version for a new state.</summary>
    long Mint();
}

/// <summary>
/// Stages the blobs of a cycle, so that a publisher never sees a half-written one.
/// </summary>
public interface IBlobStager
{
    /// <summary>Opens a blob to write a snapshot of <paramref name="version"/> into.</summary>
    Blob OpenSnapshot(long version);

    /// <summary>Opens a blob to write the header of <paramref name="version"/> into.</summary>
    HeaderBlob OpenHeader(long version);

    /// <summary>
    /// Opens a blob to write the delta from <paramref name="fromVersion"/> to
    /// <paramref name="toVersion"/> into, where <paramref name="fromVersion"/> is the older.
    /// </summary>
    Blob OpenDelta(long fromVersion, long toVersion);

    /// <summary>
    /// Opens a blob to write the reverse delta from <paramref name="fromVersion"/> to
    /// <paramref name="toVersion"/> into, where <paramref name="fromVersion"/> is the newer.
    /// </summary>
    Blob OpenReverseDelta(long fromVersion, long toVersion);
}

/// <summary>
/// Moves a staged artifact into the blob store consumers read from.
/// </summary>
public interface IPublisher
{
    /// <summary>
    /// Publishes <paramref name="publishArtifact"/>, which this producer's <see cref="IBlobStager"/>
    /// staged.
    /// </summary>
    void Publish(IPublishArtifact publishArtifact);
}

/// <summary>
/// Tells consumers that a version is ready to read.
/// </summary>
public interface IAnnouncer
{
    /// <summary>
    /// Announces <paramref name="stateVersion"/>.
    /// </summary>
    /// <param name="stateVersion">The version consumers should move to.</param>
    /// <param name="metadata">
    /// The producer's header tags for that version, plus the producer's own announcement tags. An
    /// announcement mechanism that can carry metadata should pass it to consumers, which is what makes
    /// a consumer's schema-change double snapshot possible.
    /// </param>
    void Announce(long stateVersion, IReadOnlyDictionary<string, string> metadata);
}

/// <summary>
/// Wraps the streams a <em>staged</em> blob is written to and read back from.
/// </summary>
/// <remarks>
/// <para>
/// This compresses staging, not the blob store. A publisher reads a staged blob through
/// <see cref="Decompress"/>, so what it copies out — and what consumers download — is the plain bytes.
/// The gain is that a large cycle's staging files stay small while the producer is holding all four of
/// them at once.
/// </para>
/// <para>
/// A blob store that wants compression at rest does it in its own <see cref="IPublisher"/>, where the
/// consumer side can be made to match.
/// </para>
/// </remarks>
public interface IBlobCompressor
{
    /// <summary>Wraps a stream a staged blob is being written to.</summary>
    Stream Compress(Stream stream);

    /// <summary>Wraps a stream a staged blob is being read back from.</summary>
    Stream Decompress(Stream stream);
}

/// <summary>
/// The state a producer's populate stage adds records to.
/// </summary>
/// <remarks>
/// Valid only during the populate stage. Every member throws once the stage has finished, so a
/// reference captured by a task that outlives the stage fails loudly rather than corrupting the next
/// cycle.
/// </remarks>
public interface IWriteState
{
    /// <summary>
    /// Adds a record mapped from <paramref name="value"/>, returning the ordinal it was assigned.
    /// </summary>
    int Add(object value);

    /// <summary>The mapper behind <see cref="Add"/>, for callers that need it directly.</summary>
    HollowObjectMapper ObjectMapper { get; }

    /// <summary>The write state engine, for callers that need to build records by hand.</summary>
    HollowWriteStateEngine StateEngine { get; }

    /// <summary>
    /// The state the previous successful cycle produced, or <see langword="null"/> when this is the
    /// first cycle of a delta chain.
    /// </summary>
    IReadState? PriorState { get; }

    /// <summary>The version being populated.</summary>
    long Version { get; }
}

/// <summary>
/// A version and the read state engine holding it.
/// </summary>
public interface IReadState
{
    /// <summary>The version.</summary>
    long Version { get; }

    /// <summary>The records of that version.</summary>
    HollowReadStateEngine StateEngine { get; }
}

/// <summary>
/// Fills a cycle's write state with the records that version should hold.
/// </summary>
/// <remarks>
/// <para>
/// A populator describes the whole state, not the change since last time: add every record the version
/// should contain. The producer works out what changed.
/// </para>
/// <para>
/// Java declares this as the functional interface <c>HollowProducer.Populator</c>; a delegate is the
/// C# equivalent and is what a lambda binds to without ceremony.
/// </para>
/// </remarks>
public delegate void Populator(IWriteState writeState);
