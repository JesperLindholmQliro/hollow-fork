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
using Hollow.Core.Memory.Pool;
using Hollow.Core.Util;

namespace Hollow.Core.Memory;

/// <summary>
/// Assigns an ordinal to each distinct serialised record, deduplicating identical records.
/// </summary>
/// <remarks>
/// <para>
/// Records are appended to one growing byte buffer and indexed by an open-addressed hash table whose
/// entries pack an ordinal into the high 29 bits and a pointer into that buffer into the low 35 bits.
/// Collisions are resolved by linear probing; the 70% load factor guarantees at least one empty
/// bucket, so probing always terminates.
/// </para>
/// <para>
/// Writes are serialised by <see cref="AssignOrdinal"/> taking a lock. Reads are lock-free: a bucket
/// is published with a release store after its record bytes are written, so any thread that observes
/// the pointer also observes the bytes it points at.
/// </para>
/// <para>
/// Java uses <c>AtomicLongArray</c> for the bucket array; this port uses a <c>long[]</c> with
/// <see cref="Volatile"/> and <see cref="Interlocked"/> operations, which gives the same guarantees.
/// </para>
/// </remarks>
public sealed class ByteArrayOrdinalMap
{
    private const long EmptyBucketValue = -1L;

    private const int BitsPerOrdinal = 29;
    private const int BitsPerPointer = 64 - BitsPerOrdinal;
    private const long PointerMask = (1L << BitsPerPointer) - 1;
    private const long OrdinalMask = (1L << BitsPerOrdinal) - 1;
    private const long MaxByteDataLength = 1L << BitsPerPointer;

    /// <summary>
    /// A safe limit for the ordinal. Although the ordinal value range is <c>[0, (1 &lt;&lt; 29) - 1]</c>,
    /// a cycle should fail once the ordinal exceeds <c>(1 &lt;&lt; 28) - 1</c> unless
    /// <see cref="IgnoreSoftLimits"/> is set. The limit catches the case where every record changes in
    /// the next version, which would otherwise exhaust the ordinal space and break the delta chain.
    /// </summary>
    private const int SoftOrdinalLimit = 1 << (BitsPerOrdinal - 1);

    private readonly Lock _writeLock = new();
    private readonly ByteDataArray _byteData;
    private readonly FreeOrdinalTracker _freeOrdinalTracker;

    /// <summary>
    /// The bucket array. Volatile because <see cref="GrowKeyArray()"/> replaces it wholesale, and
    /// readers must see either the old array or the fully populated new one.
    /// </summary>
    private long[] _pointersAndOrdinals;

    private bool _logSoftLimitsBreach = true;
    private int _size;
    private int _sizeBeforeGrow;
    private BitSet? _unusedPreviousOrdinals;
    private long[]? _pointersByOrdinal;

    /// <summary>
    /// Initialises a map sized for <paramref name="size"/> records.
    /// </summary>
    /// <param name="size">A hint for the initial bucket count; rounded up to a power of two.</param>
    /// <param name="ignoreSoftLimits">
    /// Whether to log rather than throw when the soft ordinal limit is breached.
    /// </param>
    public ByteArrayOrdinalMap(int size = 256, bool ignoreSoftLimits = true)
    {
        size = BucketSize(size);

        _freeOrdinalTracker = new FreeOrdinalTracker();
        _byteData = new ByteDataArray(WastefulRecycler.DefaultInstance);
        _pointersAndOrdinals = EmptyKeyArray(size);
        _sizeBeforeGrow = (int)(size * 0.7f);
        _size = 0;
        IgnoreSoftLimits = ignoreSoftLimits;
    }

    /// <summary>
    /// Whether to log rather than throw when an assigned ordinal breaches the soft ordinal limit.
    /// </summary>
    public bool IgnoreSoftLimits { get; set; }

    /// <summary>The bytes of every record added so far.</summary>
    public ByteDataArray ByteData => _byteData;

    /// <summary>The total size of the serialised records, in bytes.</summary>
    public long ByteDataLength => _byteData.Length;

    /// <summary>The current occupancy of the bucket array.</summary>
    public float LoadFactor => (float)_size / _pointersAndOrdinals.Length;

    /// <summary>
    /// The previously populated ordinals not yet reclaimed by this cycle, or <see langword="null"/>
    /// when <see cref="ReservePreviouslyPopulatedOrdinals"/> has not been called.
    /// </summary>
    public BitSet? UnusedPreviousOrdinals => _unusedPreviousOrdinals;

    /// <summary>Whether <see cref="GetPointerForData"/> may be called.</summary>
    public bool IsReadyForWriting => _pointersByOrdinal is not null;

    /// <summary>Whether new records may still be added.</summary>
    public bool IsReadyForAddingObjects => _pointersByOrdinal is null;

    /// <summary>Whether a packed bucket entry is empty.</summary>
    public static bool IsPointerAndOrdinalEmpty(long pointerAndOrdinal) => pointerAndOrdinal == EmptyBucketValue;

    /// <summary>Extracts the byte-buffer pointer from a packed bucket entry.</summary>
    public static long GetPointer(long pointerAndOrdinal) => pointerAndOrdinal & PointerMask;

    /// <summary>Extracts the ordinal from a packed bucket entry.</summary>
    public static int GetOrdinal(long pointerAndOrdinal) => (int)((ulong)pointerAndOrdinal >> BitsPerPointer);

    /// <summary>
    /// Returns the ordinal already assigned to <paramref name="serializedRepresentation"/>, assigning
    /// a new one if the record has not been seen before.
    /// </summary>
    public int GetOrAssignOrdinal(ByteDataArray serializedRepresentation, int preferredOrdinal = -1)
    {
        ArgumentNullException.ThrowIfNull(serializedRepresentation);

        int hash = HashCodes.Compute(serializedRepresentation);
        return GetOrAssignOrdinal(serializedRepresentation, hash, preferredOrdinal);
    }

    /// <summary>
    /// Returns the ordinal already assigned to <paramref name="serializedRepresentation"/>, assigning
    /// a new one if the record has not been seen before, using a precomputed hash code.
    /// </summary>
    public int GetOrAssignOrdinal(ByteDataArray serializedRepresentation, int hash, int preferredOrdinal)
    {
        int ordinal = Get(serializedRepresentation, hash);
        return ordinal != -1 ? ordinal : AssignOrdinal(serializedRepresentation, hash, preferredOrdinal);
    }

    /// <summary>
    /// Assigns an ordinal to a record known not to be present.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="preferredOrdinal"/> is outside the closed interval <c>[-1, 2^29 - 1]</c>.
    /// </exception>
    public int AssignOrdinal(ByteDataArray serializedRepresentation, int hash, int preferredOrdinal)
    {
        ArgumentNullException.ThrowIfNull(serializedRepresentation);

        if (preferredOrdinal < -1 || preferredOrdinal > OrdinalMask)
        {
            throw new ArgumentOutOfRangeException(
                nameof(preferredOrdinal),
                preferredOrdinal,
                $"The given preferred ordinal is out of bounds and not within the closed interval [-1, {OrdinalMask.Invariant()}]");
        }

        lock (_writeLock)
        {
            if (_size > _sizeBeforeGrow)
            {
                GrowKeyArray();
            }

            // Re-check under the lock: another thread may have added the record between our read and
            // acquiring the lock.
            long[] buckets = Volatile.Read(ref _pointersAndOrdinals);

            int modBitmask = buckets.Length - 1;
            int bucket = hash & modBitmask;
            long key = Volatile.Read(ref buckets[bucket]);

            while (key != EmptyBucketValue)
            {
                if (Compare(serializedRepresentation, key))
                {
                    return GetOrdinal(key);
                }

                bucket = (bucket + 1) & modBitmask;
                key = Volatile.Read(ref buckets[bucket]);
            }

            int ordinal = FindFreeOrdinal(preferredOrdinal);
            if (ordinal > OrdinalMask)
            {
                throw new InvalidOperationException(
                    $"Ordinal cannot be assigned. The to be assigned ordinal, {ordinal.Invariant()}, is greater than "
                    + $"the maximum supported ordinal value of {OrdinalMask.Invariant()}");
            }

            CheckSoftOrdinalLimit(ordinal);

            long pointer = _byteData.Length;

            VarInt.WriteVInt(_byteData, (int)serializedRepresentation.Length);

            // Copying may resize the segmented array behind byteData. A reading thread can observe a
            // null segment while the new segment array is being built, which is why the bucket is only
            // published afterwards.
            serializedRepresentation.CopyTo(_byteData);

            if (_byteData.Length > MaxByteDataLength)
            {
                throw new InvalidOperationException(
                    $"The number of bytes for the serialized representations, {_byteData.Length.Invariant()}, is too "
                    + $"large and is greater than the maximum of {MaxByteDataLength.Invariant()} bytes");
            }

            key = ((long)ordinal << BitsPerPointer) | pointer;
            _size++;

            // Release store: any thread that reads this bucket also sees the record bytes written above.
            Volatile.Write(ref buckets[bucket], key);

            return ordinal;
        }
    }

    /// <summary>
    /// Adds a record at a caller-chosen ordinal, without checking whether it is already present.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="ordinal"/> is outside the closed interval <c>[0, 2^29 - 1]</c>.
    /// </exception>
    public void Put(ByteDataArray serializedRepresentation, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(serializedRepresentation);

        if (ordinal < 0 || ordinal > OrdinalMask)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ordinal),
                ordinal,
                $"The given ordinal is out of bounds and not within the closed interval [0, {OrdinalMask.Invariant()}]");
        }

        if (_size > _sizeBeforeGrow)
        {
            GrowKeyArray();
        }

        int hash = HashCodes.Compute(serializedRepresentation);

        long[] buckets = Volatile.Read(ref _pointersAndOrdinals);

        int modBitmask = buckets.Length - 1;
        int bucket = hash & modBitmask;

        while (Volatile.Read(ref buckets[bucket]) != EmptyBucketValue)
        {
            bucket = (bucket + 1) & modBitmask;
        }

        long pointer = _byteData.Length;

        VarInt.WriteVInt(_byteData, (int)serializedRepresentation.Length);
        serializedRepresentation.CopyTo(_byteData);

        if (_byteData.Length > MaxByteDataLength)
        {
            throw new InvalidOperationException(
                $"The number of bytes for the serialized representations, {_byteData.Length.Invariant()}, is too large "
                + $"and is greater than the maximum of {MaxByteDataLength.Invariant()} bytes");
        }

        _size++;
        Volatile.Write(ref buckets[bucket], ((long)ordinal << BitsPerPointer) | pointer);
    }

    /// <summary>
    /// Returns the ordinal assigned to <paramref name="serializedRepresentation"/>, or -1 when the
    /// record is not present.
    /// </summary>
    public int Get(ByteDataArray serializedRepresentation) =>
        Get(serializedRepresentation, HashCodes.Compute(serializedRepresentation));

    /// <summary>
    /// Returns the ordinal assigned to <paramref name="serializedRepresentation"/> using a precomputed
    /// hash code, or -1 when the record is not present.
    /// </summary>
    public int Get(ByteDataArray serializedRepresentation, int hash)
    {
        ArgumentNullException.ThrowIfNull(serializedRepresentation);

        // Read the bucket array into a local: a concurrent resize must not be able to change the array
        // mid-probe, or the "at least one empty bucket" invariant that terminates the loop is lost.
        long[] buckets = Volatile.Read(ref _pointersAndOrdinals);

        int modBitmask = buckets.Length - 1;
        int bucket = hash & modBitmask;
        long key = Volatile.Read(ref buckets[bucket]);

        while (key != EmptyBucketValue)
        {
            if (Compare(serializedRepresentation, key))
            {
                return GetOrdinal(key);
            }

            bucket = (bucket + 1) & modBitmask;
            key = Volatile.Read(ref buckets[bucket]);
        }

        return -1;
    }

    /// <summary>
    /// Builds the ordinal-to-pointer index used while writing a blob, returning the maximum populated
    /// ordinal, or -1 when the map is empty.
    /// </summary>
    public int PrepareForWrite()
    {
        long[] buckets = Volatile.Read(ref _pointersAndOrdinals);

        int maxOrdinal = -1;
        foreach (long key in buckets)
        {
            if (key != EmptyBucketValue)
            {
                maxOrdinal = Math.Max(maxOrdinal, GetOrdinal(key));
            }
        }

        long[] pointersByOrdinal = new long[maxOrdinal == -1 ? 1 : maxOrdinal + 1];
        Array.Fill(pointersByOrdinal, -1L);

        foreach (long key in buckets)
        {
            if (key != EmptyBucketValue)
            {
                pointersByOrdinal[GetOrdinal(key)] = key & PointerMask;
            }
        }

        _pointersByOrdinal = pointersByOrdinal;
        return maxOrdinal;
    }

    /// <summary>
    /// The position of <paramref name="ordinal"/>'s record bytes, past the length prefix.
    /// </summary>
    public long GetPointerForData(int ordinal)
    {
        long[] pointersByOrdinal = _pointersByOrdinal
            ?? throw new InvalidOperationException($"{nameof(PrepareForWrite)} has not been called");

        long pointer = pointersByOrdinal[ordinal] & PointerMask;
        return pointer + VarInt.NextVLongSize(_byteData.UnderlyingArray, pointer);
    }

    /// <summary>
    /// Recomputes the free ordinal pool from the ordinals currently in the map.
    /// </summary>
    public void RecalculateFreeOrdinals()
    {
        BitSet populatedOrdinals = new();

        foreach (long key in Volatile.Read(ref _pointersAndOrdinals))
        {
            if (key != EmptyBucketValue)
            {
                populatedOrdinals.Set(GetOrdinal(key));
            }
        }

        RecalculateFreeOrdinals(populatedOrdinals);
    }

    /// <summary>
    /// Records which ordinals the previous cycle populated, so that records unchanged since then keep
    /// their ordinals.
    /// </summary>
    public void ReservePreviouslyPopulatedOrdinals(BitSet populatedOrdinals)
    {
        ArgumentNullException.ThrowIfNull(populatedOrdinals);

        _unusedPreviousOrdinals = populatedOrdinals.Clone();
        RecalculateFreeOrdinals(populatedOrdinals);
    }

    /// <summary>
    /// Drops the records whose global ordinals are not in <paramref name="usedGlobalOrdinals"/>,
    /// compacting the byte buffer and returning their ordinals to the free pool.
    /// </summary>
    /// <param name="usedGlobalOrdinals">The global ordinals still referenced by the dataset.</param>
    /// <param name="numShards">The number of shards this type's records are split across.</param>
    /// <param name="focusHoleFillInFewestShards">
    /// Whether to concentrate the resulting ordinal holes in as few shards as possible.
    /// </param>
    /// <param name="mapIndex">The index of this map among the shards' maps.</param>
    /// <param name="mapIndexBits">The number of bits the map index occupies in a global ordinal.</param>
    public void Compact(
        ThreadSafeBitSet usedGlobalOrdinals,
        int numShards,
        bool focusHoleFillInFewestShards,
        int mapIndex,
        int mapIndexBits)
    {
        ArgumentNullException.ThrowIfNull(usedGlobalOrdinals);

        long[] buckets = Volatile.Read(ref _pointersAndOrdinals);

        // Pack each entry as (pointer, ordinal) rather than (ordinal, pointer) so that sorting orders
        // the records by their position in the byte buffer, which lets them be compacted in one pass.
        long[] populatedReverseKeys = new long[_size];
        int counter = 0;
        foreach (long key in buckets)
        {
            if (key != EmptyBucketValue)
            {
                populatedReverseKeys[counter++] = (key << BitsPerOrdinal) | (long)((ulong)key >> BitsPerPointer);
            }
        }

        Array.Sort(populatedReverseKeys);

        SegmentedByteArray data = _byteData.UnderlyingArray;
        long currentCopyPointer = 0;
        int usedOrdinalCount = 0;

        for (int i = 0; i < populatedReverseKeys.Length; i++)
        {
            int ordinal = (int)(populatedReverseKeys[i] & OrdinalMask);
            int globalOrdinal = (ordinal << mapIndexBits) | mapIndex;

            if (usedGlobalOrdinals.Get(globalOrdinal))
            {
                long pointer = (long)((ulong)populatedReverseKeys[i] >> BitsPerOrdinal);
                int length = VarInt.ReadVInt(data, pointer);
                length += VarInt.SizeOfVInt(length);

                if (currentCopyPointer != pointer)
                {
                    data.Copy(data, pointer, currentCopyPointer, length);
                }

                populatedReverseKeys[i] = (populatedReverseKeys[i] << BitsPerPointer) | currentCopyPointer;

                currentCopyPointer += length;
                usedOrdinalCount++;
            }
            else
            {
                _freeOrdinalTracker.ReturnOrdinalToPool(ordinal);
                populatedReverseKeys[i] = EmptyBucketValue;
            }
        }

        _byteData.Length = currentCopyPointer;

        if (focusHoleFillInFewestShards && numShards > 1)
        {
            _freeOrdinalTracker.Sort(numShards, mapIndexBits, mapIndex);
        }
        else
        {
            _freeOrdinalTracker.Sort();
        }

        Array.Fill(buckets, EmptyBucketValue);
        PopulateNewHashArray(buckets, populatedReverseKeys, populatedReverseKeys.Length);
        _size = usedOrdinalCount;

        _pointersByOrdinal = null;
        _unusedPreviousOrdinals = null;
    }

    /// <summary>
    /// Grows the bucket array to hold <paramref name="size"/> records, if it is not already big enough.
    /// </summary>
    public void Resize(int size)
    {
        size = BucketSize(size);

        if (_pointersAndOrdinals.Length < size)
        {
            GrowKeyArray(size);
        }
    }

    /// <summary>
    /// Rounds <paramref name="x"/> up to a power of two, clamped to the range <c>[256, 2^30]</c>.
    /// </summary>
    private static int BucketSize(int x)
    {
        // See Hacker's Delight, figure 3-3.
        x--;
        x |= x >> 1;
        x |= x >> 2;
        x |= x >> 4;
        x |= x >> 8;
        x |= x >> 16;
        return x < 256 ? 256 : x >= 1 << 30 ? 1 << 30 : x + 1;
    }

    private static long[] EmptyKeyArray(int size)
    {
        long[] array = new long[size];
        Array.Fill(array, EmptyBucketValue);
        return array;
    }

    private void CheckSoftOrdinalLimit(int ordinal)
    {
        if (ordinal < SoftOrdinalLimit)
        {
            return;
        }

        string message =
            $"Ordinal {ordinal.Invariant()} exceeds the soft ordinal limit of {SoftOrdinalLimit.Invariant()}.";

        if (!IgnoreSoftLimits)
        {
            throw new InvalidOperationException(message);
        }

        if (_logSoftLimitsBreach)
        {
            _logSoftLimitsBreach = false;
            SoftLimitBreached?.Invoke(this, message);
        }
    }

    /// <summary>
    /// Raised the first time an assigned ordinal breaches the soft ordinal limit while
    /// <see cref="IgnoreSoftLimits"/> is set.
    /// </summary>
    /// <remarks>
    /// Java logs to <c>java.util.logging</c> here. The .NET port has no logging dependency, so it
    /// surfaces the breach as an event the host can route to whatever logger it uses.
    /// </remarks>
    public event EventHandler<string>? SoftLimitBreached;

    /// <summary>
    /// Allows a subsequent breach of the soft ordinal limit to be reported again.
    /// </summary>
    public void ResetLogSoftLimitsBreach() => _logSoftLimitsBreach = true;

    private int FindFreeOrdinal(int preferredOrdinal)
    {
        if (preferredOrdinal != -1 && _unusedPreviousOrdinals?.Get(preferredOrdinal) == true)
        {
            _unusedPreviousOrdinals.Clear(preferredOrdinal);
            return preferredOrdinal;
        }

        return _freeOrdinalTracker.GetFreeOrdinal();
    }

    private void RecalculateFreeOrdinals(BitSet populatedOrdinals)
    {
        _freeOrdinalTracker.Reset();

        int length = populatedOrdinals.Length;

        for (int ordinal = populatedOrdinals.NextClearBit(0); ordinal < length;
            ordinal = populatedOrdinals.NextClearBit(ordinal + 1))
        {
            _freeOrdinalTracker.ReturnOrdinalToPool(ordinal);
        }

        _freeOrdinalTracker.SetNextEmptyOrdinal(length);
    }

    private bool Compare(ByteDataArray serializedRepresentation, long key)
    {
        long position = key & PointerMask;

        int sizeOfData = VarInt.ReadVInt(_byteData.UnderlyingArray, position);
        if (sizeOfData != serializedRepresentation.Length)
        {
            return false;
        }

        position += VarInt.SizeOfVInt(sizeOfData);

        for (int i = 0; i < sizeOfData; i++)
        {
            if (serializedRepresentation.Get(i) != _byteData.Get(position++))
            {
                return false;
            }
        }

        return true;
    }

    private void GrowKeyArray()
    {
        int newSize = _pointersAndOrdinals.Length << 1;
        if (newSize < 0)
        {
            throw new InvalidOperationException(
                "New size computed to grow the underlying array for the map is negative. This is most "
                + "likely because the total number of keys added to the map has exceeded the maximum "
                + $"capacity. Current array size: {_pointersAndOrdinals.Length.Invariant()}, size to grow: {newSize.Invariant()}");
        }

        GrowKeyArray(newSize);
    }

    private void GrowKeyArray(int newSize)
    {
        long[] buckets = Volatile.Read(ref _pointersAndOrdinals);
        long[] newKeys = EmptyKeyArray(newSize);

        long[] valuesToAdd = new long[_size];
        int counter = 0;

        foreach (long key in buckets)
        {
            if (key != EmptyBucketValue)
            {
                valuesToAdd[counter++] = key;
            }
        }

        // Reinsert in sorted order rather than in bucket order: bucket order would reproduce the
        // existing collision clusters, which linear probing then makes worse.
        Array.Sort(valuesToAdd);

        PopulateNewHashArray(newKeys, valuesToAdd, counter);

        _sizeBeforeGrow = (int)(newSize * 0.7f);
        Volatile.Write(ref _pointersAndOrdinals, newKeys);
    }

    private void PopulateNewHashArray(long[] newKeys, long[] valuesToAdd, int length)
    {
        int modBitmask = newKeys.Length - 1;

        for (int i = 0; i < length; i++)
        {
            long value = valuesToAdd[i];
            if (value == EmptyBucketValue)
            {
                continue;
            }

            int bucket = RehashPreviouslyAddedData(value) & modBitmask;
            while (newKeys[bucket] != EmptyBucketValue)
            {
                bucket = (bucket + 1) & modBitmask;
            }

            newKeys[bucket] = value;
        }
    }

    private int RehashPreviouslyAddedData(long key)
    {
        long position = key & PointerMask;

        int sizeOfData = VarInt.ReadVInt(_byteData.UnderlyingArray, position);
        position += VarInt.SizeOfVInt(sizeOfData);

        return HashCodes.Compute(_byteData.UnderlyingArray, position, sizeOfData);
    }
}
