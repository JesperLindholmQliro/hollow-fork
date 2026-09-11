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

using System.Buffers;
using System.Numerics;

namespace Hollow.Core.Memory.Encoding;

/// <summary>
/// The hash functions Hollow uses to place records in its hash tables.
/// </summary>
/// <remarks>
/// The Java class names every one of these <c>hashCode</c>; they are named <c>Compute</c> here because
/// <c>HashCode</c> would read as the unrelated <see cref="System.HashCode"/> type and because a static
/// method named after <see cref="object.GetHashCode"/> invites confusion with it. The hash values
/// themselves are unchanged — they are part of the blob layout contract.
/// </remarks>
public static class HashCodes
{
    private const int MurmurHashSeed = unchecked((int)0xeab524b9);

    /// <summary>
    /// Hashes everything written to <paramref name="data"/>.
    /// </summary>
    public static int Compute(ByteDataArray data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Compute(data.UnderlyingArray, 0, (int)data.Length);
    }

    /// <summary>
    /// Hashes a string, or returns -1 for <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Each UTF-16 code unit is encoded as a variable-length integer first, so that the hash matches
    /// the bytes Hollow stores for a string field.
    /// </remarks>
    public static int Compute(string? data)
    {
        if (data is null)
        {
            return -1;
        }

        int byteCount = 0;
        foreach (char c in data)
        {
            byteCount += VarInt.SizeOfVInt(c);
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            int pos = 0;
            foreach (char c in data)
            {
                pos = VarInt.WriteVInt(rented, pos, c);
            }

            return Compute(rented.AsSpan(0, byteCount));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Hashes a contiguous range of bytes.
    /// </summary>
    public static int Compute(ReadOnlySpan<byte> data) =>
        Compute(new SpanAccessor(data), 0, data.Length);

    /// <summary>
    /// Hashes <paramref name="length"/> bytes of <paramref name="data"/> starting at
    /// <paramref name="offset"/>.
    /// </summary>
    public static int Compute(IByteData data, long offset, int length)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Compute(new ByteDataAccessor(data), offset, length);
    }

    /// <summary>
    /// Mixes a 64-bit key down to 32 bits.
    /// </summary>
    public static int HashLong(long key)
    {
        unchecked
        {
            key = ~key + (key << 18);
            key ^= (long)((ulong)key >> 31);
            key *= 21;
            key ^= (long)((ulong)key >> 11);
            key += key << 6;
            key ^= (long)((ulong)key >> 22);
            return (int)key;
        }
    }

    /// <summary>
    /// Mixes a 32-bit key.
    /// </summary>
    public static int HashInt(int key)
    {
        unchecked
        {
            key = ~key + (key << 15);
            key ^= (int)((uint)key >> 12);
            key += key << 2;
            key ^= (int)((uint)key >> 4);
            key *= 2057;
            key ^= (int)((uint)key >> 16);
            return key;
        }
    }

    /// <summary>
    /// Determines the size of a hash table capable of storing <paramref name="numElements"/> elements
    /// with a load factor applied. The result is always a power of two.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="numElements"/> is negative or exceeds
    /// <see cref="HollowConstants.HashTableMaxSize"/>.
    /// </exception>
    public static int HashTableSize(int numElements)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(numElements);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(numElements, HollowConstants.HashTableMaxSize);

        if (numElements == 0)
        {
            return 1;
        }

        if (numElements < 3)
        {
            return numElements * 2;
        }

        // Apply the load factor to the number of elements, then round up to a power of two.
        int sizeAfterLoadFactor = (int)((long)numElements * 10 / 7);
        int bits = 32 - BitOperations.LeadingZeroCount((uint)(sizeAfterLoadFactor - 1));
        return 1 << bits;
    }

    /// <summary>
    /// Determines the size of an in-memory index hash table capable of storing
    /// <paramref name="numElements"/> elements with a load factor applied. The result is always a
    /// power of two.
    /// </summary>
    /// <remarks>
    /// Allows up to 2^31 buckets, versus <see cref="HashTableSize"/>'s 2^30.
    /// <see cref="HashTableSize"/> is retained for backwards compatibility of the bucket layout for
    /// serialised SET and MAP records with existing consumers.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="numElements"/> is negative or exceeds
    /// <see cref="HollowConstants.IndexHashTableMaxSize"/>.
    /// </exception>
    public static long IndexHashTableSize(long numElements)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(numElements);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(numElements, HollowConstants.IndexHashTableMaxSize);

        if (numElements == 0)
        {
            return 1;
        }

        if (numElements < 3)
        {
            return numElements * 2;
        }

        long sizeAfterLoadFactor = numElements * 10 / 7;
        int bits = 64 - BitOperations.LeadingZeroCount((ulong)(sizeAfterLoadFactor - 1));
        return 1L << bits;
    }

    /// <summary>
    /// MurmurHash3, 32-bit x86 variant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Adapted from https://github.com/yonik/java_util/blob/master/src/util/hash/MurmurHash3.java.
    /// On 2013-11-19 the licence for that file read:
    /// </para>
    /// <para>
    /// The MurmurHash3 algorithm was created by Austin Appleby. This java port was authored by Yonik
    /// Seeley and is placed into the public domain. The author hereby disclaims copyright to this
    /// source code.
    /// </para>
    /// <para>
    /// This produces exactly the same hash values as the final C++ version of MurmurHash3 and is thus
    /// suitable for producing the same hash values across platforms. Note that the x86 and x64
    /// variants do <em>not</em> produce the same results, as each is optimised for its platform.
    /// </para>
    /// </remarks>
    private static int Compute<TAccessor>(TAccessor data, long offset, int length)
        where TAccessor : IByteAccessor, allows ref struct
    {
        unchecked
        {
            const int C1 = unchecked((int)0xcc9e2d51);
            const int C2 = 0x1b873593;

            int h1 = MurmurHashSeed;

            // Round the length down to a whole number of 4-byte blocks.
            long roundedEnd = offset + (length & ~0x03);

            for (long i = offset; i < roundedEnd; i += 4)
            {
                // Little-endian load order.
                int k1 = (data.Get(i) & 0xFF)
                    | ((data.Get(i + 1) & 0xFF) << 8)
                    | ((data.Get(i + 2) & 0xFF) << 16)
                    | ((sbyte)data.Get(i + 3) << 24);
                k1 *= C1;
                k1 = (int)BitOperations.RotateLeft((uint)k1, 15);
                k1 *= C2;

                h1 ^= k1;
                h1 = (int)BitOperations.RotateLeft((uint)h1, 13);
                h1 = (h1 * 5) + unchecked((int)0xe6546b64);
            }

            // Tail.
            int tail = 0;
            switch (length & 0x03)
            {
                case 3:
                    tail = (data.Get(roundedEnd + 2) & 0xFF) << 16;
                    goto case 2;
                case 2:
                    tail |= (data.Get(roundedEnd + 1) & 0xFF) << 8;
                    goto case 1;
                case 1:
                    tail |= data.Get(roundedEnd) & 0xFF;
                    tail *= C1;
                    tail = (int)BitOperations.RotateLeft((uint)tail, 15);
                    tail *= C2;
                    h1 ^= tail;
                    break;
                default:
                    break;
            }

            // Finalisation.
            h1 ^= length;

            // fmix(h1)
            h1 ^= (int)((uint)h1 >> 16);
            h1 *= unchecked((int)0x85ebca6b);
            h1 ^= (int)((uint)h1 >> 13);
            h1 *= unchecked((int)0xc2b2ae35);
            h1 ^= (int)((uint)h1 >> 16);

            return h1;
        }
    }

    /// <summary>
    /// Reads single bytes at 64-bit offsets.
    /// </summary>
    /// <remarks>
    /// Lets the hash loop above run over either an <see cref="IByteData"/> or a
    /// <see cref="ReadOnlySpan{T}"/> without duplicating it or boxing: the generic is constrained with
    /// <c>allows ref struct</c>, so the JIT specialises the loop for each accessor.
    /// </remarks>
    private interface IByteAccessor
    {
        byte Get(long index);
    }

    private readonly struct ByteDataAccessor(IByteData data) : IByteAccessor
    {
        public byte Get(long index) => data.Get(index);
    }

    private readonly ref struct SpanAccessor(ReadOnlySpan<byte> span) : IByteAccessor
    {
        private readonly ReadOnlySpan<byte> _span = span;

        public byte Get(long index) => _span[(int)index];
    }
}
