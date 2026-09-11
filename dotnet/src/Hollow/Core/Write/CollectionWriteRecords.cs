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

using Hollow.Core.Memory;
using Hollow.Core.Memory.Encoding;

namespace Hollow.Core.Write;

/// <summary>
/// How a hashable record's hash codes are written into its serialised form.
/// </summary>
/// <remarks>Java nests this as <c>HollowHashableWriteRecord.HashBehavior</c>.</remarks>
public enum HashBehavior
{
    /// <summary>Hashes are omitted entirely, so two records differing only in hash compare equal.</summary>
    IgnoredHashes,

    /// <summary>Hashes are written as supplied.</summary>
    UnmixedHashes,

    /// <summary>Hashes are mixed through <see cref="HashCodes.HashInt"/> before bucketing.</summary>
    MixedHashes,
}

/// <summary>
/// A record whose serialised form carries hash codes, so that a reader can locate an element without
/// scanning.
/// </summary>
/// <remarks>
/// Named <c>HollowHashableWriteRecord</c> in Java; the <c>I</c> prefix follows the .NET interface
/// naming convention.
/// </remarks>
public interface IHollowHashableWriteRecord : IHollowWriteRecord
{
    /// <summary>
    /// Writes this record using the given hash behaviour.
    /// </summary>
    void WriteDataTo(ByteDataArray buffer, HashBehavior hashBehavior);
}

/// <summary>
/// A record of a list type: an ordered sequence of element ordinals.
/// </summary>
public sealed class HollowListWriteRecord : IHollowWriteRecord
{
    private readonly List<int> _elementOrdinals = [];

    /// <summary>Appends an element.</summary>
    public void AddElement(int ordinal) => _elementOrdinals.Add(ordinal);

    /// <summary>The number of elements added so far.</summary>
    public int Count => _elementOrdinals.Count;

    /// <inheritdoc />
    public void WriteDataTo(ByteDataArray buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        VarInt.WriteVInt(buffer, _elementOrdinals.Count);
        foreach (int ordinal in _elementOrdinals)
        {
            VarInt.WriteVInt(buffer, ordinal);
        }
    }

    /// <inheritdoc />
    public void Reset() => _elementOrdinals.Clear();
}

/// <summary>
/// A record of a set type: an unordered collection of element ordinals, each carrying the hash bucket
/// it should land in.
/// </summary>
public sealed class HollowSetWriteRecord : IHollowHashableWriteRecord
{
    /// <summary>
    /// Element ordinals packed with their hash codes as <c>(ordinal &lt;&lt; 32) | hash</c>, so that
    /// sorting the packed values orders the elements by ordinal.
    /// </summary>
    private readonly List<long> _elementsAndHashes = [];

    private readonly HashBehavior _defaultHashBehavior;

    /// <summary>
    /// Initialises an empty set record.
    /// </summary>
    public HollowSetWriteRecord(HashBehavior defaultHashBehavior = HashBehavior.MixedHashes) =>
        _defaultHashBehavior = defaultHashBehavior;

    /// <summary>The number of elements added so far.</summary>
    public int Count => _elementsAndHashes.Count;

    /// <summary>Adds an element, hashed by its own ordinal.</summary>
    public void AddElement(int ordinal) => AddElement(ordinal, ordinal);

    /// <summary>Adds an element with an explicit hash code.</summary>
    public void AddElement(int ordinal, int hashCode) =>
        _elementsAndHashes.Add(((long)ordinal << 32) | (uint)hashCode);

    /// <inheritdoc />
    public void WriteDataTo(ByteDataArray buffer) => WriteDataTo(buffer, _defaultHashBehavior);

    /// <inheritdoc />
    public void WriteDataTo(ByteDataArray buffer, HashBehavior hashBehavior)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        _elementsAndHashes.Sort();

        int bucketMask = HashCodes.HashTableSize(_elementsAndHashes.Count) - 1;

        VarInt.WriteVInt(buffer, _elementsAndHashes.Count);

        int previousOrdinal = 0;
        foreach (long elementAndHash in _elementsAndHashes)
        {
            int ordinal = (int)((ulong)elementAndHash >> 32);
            VarInt.WriteVInt(buffer, ordinal - previousOrdinal);

            if (hashBehavior != HashBehavior.IgnoredHashes)
            {
                int hashCode = (int)elementAndHash;
                if (hashBehavior == HashBehavior.MixedHashes)
                {
                    hashCode = HashCodes.HashInt(hashCode);
                }

                VarInt.WriteVInt(buffer, hashCode & bucketMask);
            }

            previousOrdinal = ordinal;
        }
    }

    /// <inheritdoc />
    public void Reset() => _elementsAndHashes.Clear();
}

/// <summary>
/// A record of a map type: key/value ordinal pairs, each carrying the hash bucket it should land in.
/// </summary>
public sealed class HollowMapWriteRecord : IHollowHashableWriteRecord
{
    private readonly List<(int KeyOrdinal, int ValueOrdinal, int HashCode)> _entries = [];
    private readonly HashBehavior _defaultHashBehavior;

    /// <summary>
    /// Initialises an empty map record.
    /// </summary>
    public HollowMapWriteRecord(HashBehavior defaultHashBehavior = HashBehavior.MixedHashes) =>
        _defaultHashBehavior = defaultHashBehavior;

    /// <summary>The number of entries added so far.</summary>
    public int Count => _entries.Count;

    /// <summary>Adds an entry, hashed by its key ordinal.</summary>
    public void AddEntry(int keyOrdinal, int valueOrdinal) => AddEntry(keyOrdinal, valueOrdinal, keyOrdinal);

    /// <summary>Adds an entry with an explicit hash code.</summary>
    public void AddEntry(int keyOrdinal, int valueOrdinal, int hashCode) =>
        _entries.Add((keyOrdinal, valueOrdinal, hashCode));

    /// <inheritdoc />
    public void WriteDataTo(ByteDataArray buffer) => WriteDataTo(buffer, _defaultHashBehavior);

    /// <inheritdoc />
    public void WriteDataTo(ByteDataArray buffer, HashBehavior hashBehavior)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        _entries.Sort(static (first, second) =>
            first.KeyOrdinal != second.KeyOrdinal
                ? first.KeyOrdinal - second.KeyOrdinal
                : first.ValueOrdinal - second.ValueOrdinal);

        VarInt.WriteVInt(buffer, _entries.Count);

        int bucketMask = HashCodes.HashTableSize(_entries.Count) - 1;

        int previousKeyOrdinal = 0;
        foreach ((int keyOrdinal, int valueOrdinal, int hashCode) in _entries)
        {
            VarInt.WriteVInt(buffer, keyOrdinal - previousKeyOrdinal);
            VarInt.WriteVInt(buffer, valueOrdinal);

            if (hashBehavior != HashBehavior.IgnoredHashes)
            {
                int bucketHash = hashBehavior == HashBehavior.MixedHashes ? HashCodes.HashInt(hashCode) : hashCode;
                VarInt.WriteVInt(buffer, bucketHash & bucketMask);
            }

            previousKeyOrdinal = keyOrdinal;
        }
    }

    /// <inheritdoc />
    public void Reset() => _entries.Clear();
}
