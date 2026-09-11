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
/// What a blob does to a consumer's state.
/// </summary>
public enum BlobType
{
    /// <summary>A complete copy of a state, applicable from nothing.</summary>
    Snapshot,

    /// <summary>The change from one state to the next.</summary>
    Delta,

    /// <summary>The change from one state back to the one before it.</summary>
    ReverseDelta,
}

/// <summary>
/// Names for the blob types, as they appear in a blob store's file names.
/// </summary>
public static class BlobTypeExtensions
{
    /// <summary>
    /// The name a blob store uses for <paramref name="blobType"/>.
    /// </summary>
    /// <remarks>
    /// These strings are part of the on-disk layout a Java Hollow producer writes, so they are not
    /// derived from the enum member names.
    /// </remarks>
    public static string GetPrefix(this BlobType blobType) => blobType switch
    {
        BlobType.Snapshot => "snapshot",
        BlobType.Delta => "delta",
        BlobType.ReverseDelta => "reversedelta",
        _ => throw new ArgumentOutOfRangeException(nameof(blobType), blobType, null),
    };
}

/// <summary>
/// A published artifact identified by the version it produces, which a consumer reads to move its
/// state.
/// </summary>
/// <remarks>
/// Java nests this as <c>HollowConsumer.VersionedBlob</c>; this port flattens the consumer's nested
/// types into the <c>Hollow.Api.Consumer</c> namespace, where the names read the same.
/// </remarks>
public interface IVersionedBlob
{
    /// <summary>
    /// Opens a stream over this blob's bytes.
    /// </summary>
    /// <remarks>
    /// The caller reads the stream to the end and disposes it. Reading must not be interrupted
    /// mid-blob, so an implementation backed by a remote store should fetch the whole artifact — to
    /// disk, say — before returning a stream over it.
    /// </remarks>
    Stream OpenStream();
}

/// <summary>
/// The header of a published state, which carries its schemas without its records.
/// </summary>
public abstract class HeaderBlob : IVersionedBlob
{
    /// <summary>
    /// Initialises a header blob for <paramref name="version"/>.
    /// </summary>
    protected HeaderBlob(long version) => Version = version;

    /// <summary>The version this header describes.</summary>
    public long Version { get; }

    /// <inheritdoc />
    public abstract Stream OpenStream();
}

/// <summary>
/// A snapshot, delta or reverse delta, identified by the versions it moves a consumer between.
/// </summary>
/// <remarks>
/// A blob is defined by three things: the version it applies to (<see cref="FromVersion"/>, which is
/// <see cref="HollowConstants.VersionNone"/> for a snapshot), the version it produces
/// (<see cref="ToVersion"/>), and the bytes themselves.
/// </remarks>
public abstract class Blob : IVersionedBlob
{
    /// <summary>
    /// Initialises a snapshot producing <paramref name="toVersion"/>.
    /// </summary>
    protected Blob(long toVersion)
        : this(HollowConstants.VersionNone, toVersion)
    {
    }

    /// <summary>
    /// Initialises a transition from <paramref name="fromVersion"/> to <paramref name="toVersion"/>.
    /// </summary>
    protected Blob(long fromVersion, long toVersion)
    {
        FromVersion = fromVersion;
        ToVersion = toVersion;

        BlobType = Classify(fromVersion, toVersion);

        static BlobType Classify(long fromVersion, long toVersion) =>
            fromVersion == HollowConstants.VersionNone ? Consumer.BlobType.Snapshot
                : toVersion < fromVersion ? Consumer.BlobType.ReverseDelta
                : Consumer.BlobType.Delta;
    }

    /// <summary>
    /// The version this blob applies to, or <see cref="HollowConstants.VersionNone"/> for a snapshot.
    /// </summary>
    public long FromVersion { get; }

    /// <summary>The version a consumer arrives at once this blob is applied.</summary>
    public long ToVersion { get; }

    /// <summary>What this blob does to a consumer's state.</summary>
    public BlobType BlobType { get; }

    /// <summary>Whether this blob is a complete state rather than a transition.</summary>
    public bool IsSnapshot => FromVersion == HollowConstants.VersionNone;

    /// <summary>Whether this blob moves a consumer back to an earlier version.</summary>
    public bool IsReverseDelta => ToVersion < FromVersion;

    /// <summary>Whether this blob moves a consumer forward to a later version.</summary>
    public bool IsDelta => !IsSnapshot && !IsReverseDelta;

    /// <inheritdoc />
    public abstract Stream OpenStream();

    /// <inheritdoc />
    public override string ToString() =>
        IsSnapshot
            ? $"{BlobType.GetPrefix()} to {ToVersion.Invariant()}"
            : $"{BlobType.GetPrefix()} from {FromVersion.Invariant()} to {ToVersion.Invariant()}";
}
