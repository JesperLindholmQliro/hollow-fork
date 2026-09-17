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

using Hollow.Core.Memory.Encoding;

namespace Hollow.Reference.Infrastructure.Adapters;

/// <summary>
/// The list of versions a snapshot exists for, encoded small enough to fetch on every cold start.
/// </summary>
/// <remarks>
/// <para>
/// A consumer starting up asks for a snapshot of the announced version, and there usually is not one:
/// snapshots are written every cycle but kept only occasionally, and the announced version is more
/// often reachable as a snapshot plus a run of deltas. A blob store cannot answer "the greatest
/// version at or below this one", so the publisher maintains this index and the retriever reads it.
/// </para>
/// <para>
/// Ported from the snapshot index half of <c>S3Publisher</c> and <c>S3BlobRetriever</c>. The encoding
/// is the Java one exactly: the first version in full, then the gap to each version after it, all as
/// variable-length longs. Versions are minted from the clock, so the gaps are small and the whole
/// index for years of cycles fits in a few kilobytes.
/// </para>
/// </remarks>
public static class SnapshotIndex
{
    /// <summary>Encodes <paramref name="versions"/>, which have to be sorted ascending.</summary>
    public static byte[] Encode(IReadOnlyList<long> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);

        if (versions.Count == 0)
        {
            // Java indexes an empty list and throws; a producer publishing its first snapshot has an
            // empty one, so the case is real rather than defensive.
            return [];
        }

        using MemoryStream encoded = new();
        Span<byte> scratch = stackalloc byte[10];

        long previous = 0;

        foreach (long version in versions)
        {
            long gap = version - previous;

            if (gap < 0)
            {
                throw new ArgumentException("the versions have to be sorted ascending", nameof(versions));
            }

            encoded.Write(scratch[..VarInt.WriteVLong(scratch, gap)]);
            previous = version;
        }

        return encoded.ToArray();
    }

    /// <summary>Reads back what <see cref="Encode"/> wrote.</summary>
    public static IReadOnlyList<long> Decode(ReadOnlySpan<byte> encoded)
    {
        List<long> versions = [];
        long current = 0;
        int position = 0;

        while (position < encoded.Length)
        {
            current += VarInt.ReadVLong(encoded[position..], out int length);
            position += length;
            versions.Add(current);
        }

        return versions;
    }

    /// <summary>
    /// The greatest indexed version at or below <paramref name="desiredVersion"/>, or
    /// <see langword="null"/> when every indexed version is newer than it.
    /// </summary>
    /// <remarks>
    /// This is the question the retriever actually asks. Everything older than the desired version can
    /// be reached from that snapshot by replaying deltas forwards; nothing newer can be reached at all,
    /// because a consumer will not walk a delta chain backwards to get to where it was told to be.
    /// </remarks>
    public static long? GreatestAtMost(ReadOnlySpan<byte> encoded, long desiredVersion)
    {
        long current = 0;
        long best = 0;
        int position = 0;

        while (position < encoded.Length)
        {
            current += VarInt.ReadVLong(encoded[position..], out int length);
            position += length;

            if (current > desiredVersion)
            {
                break;
            }

            best = current;
        }

        return best == 0 ? null : best;
    }
}
