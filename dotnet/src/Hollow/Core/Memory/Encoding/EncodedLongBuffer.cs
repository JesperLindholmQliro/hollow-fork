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

using Hollow.Core.Read;

namespace Hollow.Core.Memory.Encoding;

/// <summary>
/// A bit string read straight out of a memory-mapped blob.
/// </summary>
/// <remarks>
/// <para>
/// The shared-memory counterpart of <see cref="FixedLengthElementArray"/>: the same bit numbering and
/// the same window arithmetic, over words read from the mapping rather than from a pooled array. It
/// holds an offset and a length, not any data — which is the point of the mode.
/// </para>
/// <para>
/// Read-only. The mutating half of <see cref="IFixedLengthData"/> exists for the write path and for
/// delta application, neither of which happens in shared-memory mode.
/// </para>
/// <para>
/// Named <c>EncodedLongBuffer</c> in Java.
/// </para>
/// </remarks>
public sealed class EncodedLongBuffer : IFixedLengthData
{
    private readonly MemoryMappedBlob _blob;
    private readonly long _byteOffset;

    private EncodedLongBuffer(MemoryMappedBlob blob, long byteOffset, long wordCount)
    {
        _blob = blob;
        _byteOffset = byteOffset;
        WordCount = wordCount;
    }

    /// <summary>How many 64-bit words this bit string spans.</summary>
    public long WordCount { get; }

    /// <summary>
    /// Takes the next bit string from <paramref name="input"/> without reading it.
    /// </summary>
    /// <remarks>
    /// The word count is read, then the words themselves are skipped over: the buffer records where
    /// they are and the operating system pages them in if and when a record is read.
    /// </remarks>
    public static EncodedLongBuffer NewFrom(HollowBlobInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        long wordCount = VarInt.ReadVLong(input);

        return NewFrom(input, wordCount);
    }

    /// <summary>
    /// Takes the next <paramref name="wordCount"/> words from <paramref name="input"/> without reading
    /// them.
    /// </summary>
    public static EncodedLongBuffer NewFrom(HollowBlobInput input, long wordCount)
    {
        ArgumentNullException.ThrowIfNull(input);

        MemoryMappedBlob blob = input.RequireMappedBlob();
        long byteOffset = input.Position;

        input.Skip(wordCount * sizeof(long));

        return new EncodedLongBuffer(blob, byteOffset, wordCount);
    }

    /// <inheritdoc />
    public long GetElementValue(long index, int bitsPerElement) =>
        GetLargeElementValue(index, bitsPerElement);

    /// <inheritdoc />
    public long GetElementValue(long index, int bitsPerElement, long mask) =>
        GetLargeElementValue(index, bitsPerElement, mask);

    /// <inheritdoc />
    public long GetLargeElementValue(long index, int bitsPerElement) =>
        GetLargeElementValue(index, bitsPerElement, bitsPerElement == 64 ? -1L : (1L << bitsPerElement) - 1);

    /// <inheritdoc />
    public long GetLargeElementValue(long index, int bitsPerElement, long mask)
    {
        long whichWord = (long)((ulong)index >> 6);
        int whichBit = (int)(index & 0x3F);

        long value = (long)((ulong)_blob.GetWord(_byteOffset, whichWord) >> whichBit);

        int bitsRemaining = 64 - whichBit;

        if (bitsRemaining < bitsPerElement)
        {
            value |= _blob.GetWord(_byteOffset, whichWord + 1) << bitsRemaining;
        }

        return value & mask;
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always; a mapped blob is read-only.</exception>
    public void SetElementValue(long index, int bitsPerElement, long value) => throw ReadOnly();

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always; a mapped blob is read-only.</exception>
    public void ClearElementValue(long index, int bitsPerElement) => throw ReadOnly();

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always; a mapped blob is read-only.</exception>
    public void CopyBits(IFixedLengthData copyFrom, long sourceStartBit, long destinationStartBit, long numBits) =>
        throw ReadOnly();

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always; a mapped blob is read-only.</exception>
    public void IncrementMany(long startBit, long increment, long bitsBetweenIncrements, int numIncrements) =>
        throw ReadOnly();

    private static NotSupportedException ReadOnly() =>
        new("a memory-mapped bit string is read-only; shared-memory mode cannot write or apply deltas");
}
