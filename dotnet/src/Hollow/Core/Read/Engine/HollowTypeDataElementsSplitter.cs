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

using System.Numerics;
using Hollow.Core.Memory.Encoding;
using Hollow.Core.Util;

namespace Hollow.Core.Read.Engine;

/// <summary>
/// Divides one shard's records into several, which is half of what resharding a type does.
/// </summary>
/// <remarks>
/// <para>
/// A record's shard is the low bits of its ordinal, so doubling a type's shard count sends every
/// second record of a shard to a new one. Splitting by <c>n</c> sends ordinal <c>o</c> of the source to
/// split <c>o &amp; (n - 1)</c> at ordinal <c>o &gt;&gt; log2(n)</c>.
/// </para>
/// <para>
/// The records cannot be copied bit for bit: a variable-length field's width follows how many bytes its
/// shard holds, so the splits are narrower than the source and each record has to be re-encoded.
/// </para>
/// </remarks>
/// <typeparam name="TElements">The data elements of the record kind being split.</typeparam>
public abstract class HollowTypeDataElementsSplitter<TElements>
    where TElements : HollowTypeDataElements
{
    /// <summary>
    /// Initialises a splitter dividing <paramref name="from"/> into <paramref name="numSplits"/> ways.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="numSplits"/> is not a positive power of two.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="from"/> is a delta's data elements, which are never split.
    /// </exception>
    protected HollowTypeDataElementsSplitter(TElements from, int numSplits)
    {
        ArgumentNullException.ThrowIfNull(from);

        if (numSplits <= 0 || (numSplits & (numSplits - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(numSplits), numSplits, "A shard can only be split by a power of two.");
        }

        if (from.EncodedAdditions is not null)
        {
            throw new ArgumentException(
                "These data elements came from a delta blob. A delta's data elements are consumed as they are "
                + "applied and never reshard.",
                nameof(from));
        }

        From = from;
        NumSplits = numSplits;
        ToMask = numSplits - 1;
        ToOrdinalShift = BitOperations.TrailingZeroCount((uint)numSplits);
    }

    /// <summary>The shard being divided.</summary>
    protected TElements From { get; }

    /// <summary>How many shards it is divided into.</summary>
    protected int NumSplits { get; }

    /// <summary>Selects a split from a source ordinal.</summary>
    protected int ToMask { get; }

    /// <summary>Turns a source ordinal into an ordinal within its split.</summary>
    protected int ToOrdinalShift { get; }

    /// <summary>The splits, once <see cref="Split"/> has built them.</summary>
    protected TElements[] To { get; private set; } = [];

    /// <summary>
    /// Divides the source shard.
    /// </summary>
    public TElements[] Split()
    {
        To = CreateSplits();

        foreach (TElements split in To)
        {
            split.MaxOrdinal = -1;
        }

        PopulateStats();
        CopyRecords();

        // A delta's removal list is per shard, so it has to follow the records into the new shards.
        if (From.EncodedRemovals is { } removals)
        {
            GapEncodedVariableLengthIntegerReader[] splitRemovals = removals.Split(NumSplits);

            for (int i = 0; i < To.Length; i++)
            {
                To[i].EncodedRemovals = splitRemovals[i];
            }
        }

        return To;
    }

    /// <summary>Creates the empty splits.</summary>
    protected abstract TElements[] CreateSplits();

    /// <summary>Works out each split's maximum ordinal and field widths, and allocates its storage.</summary>
    protected abstract void PopulateStats();

    /// <summary>Copies each source record into the split it belongs to.</summary>
    protected abstract void CopyRecords();
}

/// <summary>
/// Merges several shards' records into one, which is the other half of what resharding a type does.
/// </summary>
/// <remarks>
/// The inverse of <see cref="HollowTypeDataElementsSplitter{TElements}"/>: joining <c>n</c> shards puts
/// ordinal <c>o</c> of source <c>i</c> at ordinal <c>(o * n) + i</c>, which is what keeps a record's
/// shard the low bits of its ordinal at the new shard count.
/// </remarks>
/// <typeparam name="TElements">The data elements of the record kind being joined.</typeparam>
public abstract class HollowTypeDataElementsJoiner<TElements>
    where TElements : HollowTypeDataElements
{
    /// <summary>
    /// Initialises a joiner merging <paramref name="from"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The number of shards is not a power of two, the result would exceed the ordinal space, or one of
    /// them is a delta's data elements.
    /// </exception>
    protected HollowTypeDataElementsJoiner(TElements[] from)
    {
        ArgumentNullException.ThrowIfNull(from);

        if (from.Length <= 0 || (from.Length & (from.Length - 1)) != 0)
        {
            throw new ArgumentException(
                "Shards can only be joined a power of two at a time.", nameof(from));
        }

        const int maxOrdinal = 1 << 29;

        foreach (TElements elements in from)
        {
            if (elements.EncodedAdditions is not null)
            {
                throw new ArgumentException(
                    "These data elements came from a delta blob. A delta's data elements are consumed as they "
                    + "are applied and never reshard.",
                    nameof(from));
            }

            if (elements.MaxOrdinal != -1
                && ((long)elements.MaxOrdinal * from.Length) + from.Length - 1 >= maxOrdinal)
            {
                throw new ArgumentException(
                    "Joining these shards would push an ordinal past the 2^29 limit.", nameof(from));
            }
        }

        From = from;
        FromMask = from.Length - 1;
        FromOrdinalShift = BitOperations.TrailingZeroCount((uint)from.Length);
    }

    /// <summary>The shards being merged.</summary>
    protected TElements[] From { get; }

    /// <summary>Selects a source from a joined ordinal.</summary>
    protected int FromMask { get; }

    /// <summary>Turns a joined ordinal into an ordinal within its source.</summary>
    protected int FromOrdinalShift { get; }

    /// <summary>The merged shard, once <see cref="Join"/> has built it.</summary>
    protected TElements To { get; private set; } = null!;

    /// <summary>
    /// Merges the source shards.
    /// </summary>
    public TElements Join()
    {
        To = CreateJoined();
        To.MaxOrdinal = -1;

        PopulateStats();
        CopyRecords();

        To.EncodedRemovals = GapEncodedVariableLengthIntegerReader.Join(
            [.. From.Select(elements => elements.EncodedRemovals)]);

        return To;
    }

    /// <summary>The highest ordinal the join will reach.</summary>
    protected int JoinedMaxOrdinal()
    {
        int maxOrdinal = -1;

        for (int i = 0; i < From.Length; i++)
        {
            if (From[i].MaxOrdinal != -1)
            {
                maxOrdinal = Math.Max(maxOrdinal, (From[i].MaxOrdinal * From.Length) + i);
            }
        }

        return maxOrdinal;
    }

    /// <summary>Creates the empty merged shard.</summary>
    protected abstract TElements CreateJoined();

    /// <summary>Works out the merged shard's maximum ordinal and field widths, and allocates storage.</summary>
    protected abstract void PopulateStats();

    /// <summary>Copies each source record into the merged shard.</summary>
    protected abstract void CopyRecords();
}
