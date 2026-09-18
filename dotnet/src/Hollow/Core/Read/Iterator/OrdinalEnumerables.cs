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

using Hollow.Core.Memory.Encoding;
using Hollow.Core.Read.DataAccess;

namespace Hollow.Core.Read.Iterator;

/// <summary>
/// One entry of a map record: the ordinals of its key and value, and the hash bucket it occupies.
/// </summary>
/// <remarks>
/// <para>
/// Java exposes this as a cursor — <c>HollowMapEntryOrdinalIterator</c>, whose <c>next()</c> advances
/// and whose <c>getKey()</c> and <c>getValue()</c> read the position it stopped at. Here an entry is a
/// value, so a map record enumerates as an ordinary sequence.
/// </para>
/// <para>
/// <see cref="Bucket"/> is what Java reads back off the iterator as <c>getCurrentBucket()</c>. Only the
/// record copiers want it, and only when they are preserving hash positions, but carrying it costs
/// nothing in a struct and it is the one piece of cursor state a sequence would otherwise lose.
/// </para>
/// </remarks>
/// <param name="KeyOrdinal">The ordinal of the entry's key.</param>
/// <param name="ValueOrdinal">The ordinal of the entry's value.</param>
/// <param name="Bucket">The bucket of the record's hash table this entry sits in.</param>
public readonly record struct HollowMapEntry(int KeyOrdinal, int ValueOrdinal, int Bucket);

/// <summary>
/// One element of a set record: its ordinal and the hash bucket it occupies.
/// </summary>
/// <remarks>
/// Almost every caller wants the ordinals alone and takes them as an <see cref="IEnumerable{T}"/> of
/// <see cref="int"/>. This exists for the one that does not — the set copier preserving hash positions.
/// </remarks>
/// <param name="Ordinal">The ordinal of the element.</param>
/// <param name="Bucket">The bucket of the record's hash table this element sits in.</param>
public readonly record struct HollowSetElement(int Ordinal, int Bucket);

/// <summary>
/// Enumerates the elements and entries of collection records.
/// </summary>
/// <remarks>
/// <para>
/// Java models each of these as a class implementing <c>HollowOrdinalIterator</c>, a cursor whose
/// <c>next()</c> returns a sentinel — <c>NO_MORE_ORDINALS</c> — once it is spent. .NET already has a
/// sequence abstraction that <c>foreach</c> and LINQ both understand, so the cursors are gone and
/// these are <see cref="IEnumerable{T}"/>-returning methods instead. The iteration logic is unchanged;
/// what goes is the interface, the sentinel, and the classes that implemented it.
/// </para>
/// <para>
/// Each is written against a data-access interface rather than a concrete read state, so the same walk
/// serves a live state, a historical one and a proxy alike — which is what Java's classes did by taking
/// the data access as a constructor argument.
/// </para>
/// <para>
/// These are lazy, as an iterator method is. Java reads the record's size when the cursor is
/// constructed; here it is read when enumeration begins. Nothing in the library holds one across a
/// refresh, and reading the size at the point of use is if anything the safer of the two.
/// </para>
/// </remarks>
public static class OrdinalEnumerables
{
    /// <summary>
    /// The element ordinals of a list record, in order.
    /// </summary>
    public static IEnumerable<int> ListElements(IHollowListTypeDataAccess dataAccess, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(dataAccess);

        int size = dataAccess.Size(ordinal);

        for (int index = 0; index < size; index++)
        {
            yield return dataAccess.GetElementOrdinal(ordinal, index);
        }
    }

    /// <summary>
    /// The element ordinals of a set record, in hash-table order.
    /// </summary>
    public static IEnumerable<int> SetElements(IHollowSetTypeDataAccess dataAccess, int ordinal)
    {
        foreach (HollowSetElement element in SetElementsWithBuckets(dataAccess, ordinal))
        {
            yield return element.Ordinal;
        }
    }

    /// <summary>
    /// The elements of a set record with the bucket each occupies, skipping the empty buckets.
    /// </summary>
    public static IEnumerable<HollowSetElement> SetElementsWithBuckets(
        IHollowSetTypeDataAccess dataAccess, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(dataAccess);

        int numBuckets = HashCodes.HashTableSize(dataAccess.Size(ordinal));

        for (int bucket = 0; bucket < numBuckets; bucket++)
        {
            int bucketValue = dataAccess.RelativeBucketValue(ordinal, bucket);

            if (bucketValue != HollowConstants.OrdinalNone)
            {
                yield return new HollowSetElement(bucketValue, bucket);
            }
        }
    }

    /// <summary>
    /// The elements of a set record that could match <paramref name="hashCode"/>.
    /// </summary>
    /// <remarks>
    /// This is what a key-based lookup walks: elements that collided on the same hash land in
    /// consecutive buckets, so the run from the hashed bucket to the first empty one holds every
    /// candidate.
    /// </remarks>
    public static IEnumerable<int> PotentialMatchSetElements(
        IHollowSetTypeDataAccess dataAccess, int ordinal, int hashCode)
    {
        ArgumentNullException.ThrowIfNull(dataAccess);

        int numBuckets = HashCodes.HashTableSize(dataAccess.Size(ordinal));

        if (numBuckets == 0)
        {
            yield break;
        }

        int bucket = HashCodes.HashInt(hashCode) & (numBuckets - 1);

        // A table Hollow wrote always has room to spare, so the run from the hashed bucket reaches an
        // empty one before it gets back to where it started. Counting the buckets rather than trusting
        // that turns a table with no empty bucket in it — which only corrupt data produces — into an
        // exception instead of a walk round the ring for ever, yielding the same elements each time.
        for (int probed = 0; probed < numBuckets; probed++)
        {
            int bucketValue = dataAccess.RelativeBucketValue(ordinal, bucket);

            if (bucketValue == HollowConstants.OrdinalNone)
            {
                yield break;
            }

            yield return bucketValue;

            bucket = (bucket + 1) & (numBuckets - 1);
        }

        throw new InvalidDataException(
            $"the set at ordinal {ordinal} has no empty bucket in its {numBuckets}, so it is not a hash "
            + "table this wrote");
    }

    /// <summary>
    /// The entries of a map record, in hash-table order.
    /// </summary>
    public static IEnumerable<HollowMapEntry> MapEntries(IHollowMapTypeDataAccess dataAccess, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(dataAccess);

        int numBuckets = HashCodes.HashTableSize(dataAccess.Size(ordinal));

        for (int bucket = 0; bucket < numBuckets; bucket++)
        {
            long entry = dataAccess.RelativeBucket(ordinal, bucket);

            if (entry != -1L)
            {
                yield return Entry(entry, bucket);
            }
        }
    }

    /// <summary>
    /// The entries of a map record whose keys could match <paramref name="hashCode"/>.
    /// </summary>
    public static IEnumerable<HollowMapEntry> PotentialMatchMapEntries(
        IHollowMapTypeDataAccess dataAccess, int ordinal, int hashCode)
    {
        ArgumentNullException.ThrowIfNull(dataAccess);

        int numBuckets = HashCodes.HashTableSize(dataAccess.Size(ordinal));

        if (numBuckets == 0)
        {
            yield break;
        }

        int bucket = HashCodes.HashInt(hashCode) & (numBuckets - 1);

        // Bounded for the same reason as the set probe above.
        for (int probed = 0; probed < numBuckets; probed++)
        {
            long entry = dataAccess.RelativeBucket(ordinal, bucket);

            if (entry == -1L)
            {
                yield break;
            }

            yield return Entry(entry, bucket);

            bucket = (bucket + 1) & (numBuckets - 1);
        }

        throw new InvalidDataException(
            $"the map at ordinal {ordinal} has no empty bucket in its {numBuckets}, so it is not a hash "
            + "table this wrote");
    }

    /// <summary>
    /// Unpacks a bucket's <c>(key &lt;&lt; 32) | value</c> word.
    /// </summary>
    internal static HollowMapEntry Entry(long entry, int bucket) =>
        new((int)((ulong)entry >> 32), (int)entry, bucket);
}
