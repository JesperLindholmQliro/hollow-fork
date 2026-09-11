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
using Hollow.Core.Memory;
using Hollow.Core.Memory.Encoding;

namespace Hollow.Tests.Core.Memory.Encoding;

/// <summary>
/// Ported from <c>HashCodesTest</c>, plus assertions pinning the MurmurHash3 output so the .NET port
/// stays byte-compatible with blobs written by the Java implementation.
/// </summary>
public class HashCodesTests
{
    /// <summary>
    /// <see cref="HashCodes.IndexHashTableSize"/> is only safe to use in place of
    /// <see cref="HashCodes.HashTableSize"/> if the two agree everywhere <see cref="HashCodes.HashTableSize"/>
    /// is defined — an index built with one bucket count and read with the other is corrupt.
    /// </summary>
    [Fact]
    public void IndexHashTableSizeMatchesHashTableSizeWhereverBothApply()
    {
        foreach (int numElements in (int[])
            [0, 1, 2, 3, 4, 5, 7, 8, 9, 1023, 1024, 1 << 24, HollowConstants.HashTableMaxSize])
        {
            Assert.Equal(HashCodes.HashTableSize(numElements), HashCodes.IndexHashTableSize(numElements));
        }

        Random random = new(20260902);
        for (int i = 0; i < 100_000; i++)
        {
            int numElements = random.Next(HollowConstants.HashTableMaxSize);
            Assert.Equal(HashCodes.HashTableSize(numElements), HashCodes.IndexHashTableSize(numElements));
        }
    }

    /// <summary>
    /// The load factor is approximate: rounding in "next power of two at or above numElements * 10 / 7"
    /// leaves a few inputs (3 among them) slightly above 70%. That is inherited from
    /// <see cref="HashCodes.HashTableSize"/> and is harmless — what open addressing actually requires is
    /// a power-of-two table strictly larger than the element count.
    /// </summary>
    [Fact]
    public void IndexHashTableSizeReturnsAMinimalPowerOfTwoLargerThanTheElementCount()
    {
        foreach (long numElements in (long[])
        [
            3,
            100,
            1 << 24,
            HollowConstants.HashTableMaxSize,
            HollowConstants.HashTableMaxSize + 1L,
            HollowConstants.IndexHashTableMaxSize,
        ])
        {
            long size = HashCodes.IndexHashTableSize(numElements);

            Assert.Equal(0, size & (size - 1));
            Assert.True(size > numElements, $"numElements={numElements} does not fit in size={size}");
            Assert.True((size / 2) * 7 / 10 < numElements, $"numElements={numElements} leaves size={size} oversized");
        }
    }

    /// <summary>
    /// 2^31 buckets is the deliberate stopping point: the mask stays within 31 bits, so sign-extending
    /// an int hash code into a long mask is harmless.
    /// </summary>
    [Fact]
    public void IndexHashTableSizeStopsAtTwoToThe31Buckets()
    {
        Assert.Equal(1L << 30, HashCodes.IndexHashTableSize(HollowConstants.HashTableMaxSize));
        Assert.Equal(1L << 31, HashCodes.IndexHashTableSize(HollowConstants.IndexHashTableMaxSize));
        Assert.Equal(HollowConstants.IndexHashTableMaxSize, (1L << 31) * 7 / 10);
    }

    [Fact]
    public void IndexHashTableSizeRejectsMoreElementsThanItCanHold() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HashCodes.IndexHashTableSize(HollowConstants.IndexHashTableMaxSize + 1));

    [Fact]
    public void IndexHashTableSizeRejectsNegativeSizes() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => HashCodes.IndexHashTableSize(-1));

    [Fact]
    public void HashTableSizeRejectsMoreElementsThanItCanHold() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HashCodes.HashTableSize(HollowConstants.HashTableMaxSize + 1));

    /// <summary>
    /// These values come from the Java implementation. They are part of the blob layout contract for
    /// SET and MAP records, so a divergence here means blobs written by one implementation cannot be
    /// read by the other.
    /// </summary>
    [Theory]
    [InlineData("", 130510007)]
    [InlineData("a", -1734206478)]
    [InlineData("hello", 704330765)]
    [InlineData("Hollow", -2080287460)]
    [InlineData("the quick brown fox jumps over the lazy dog", 2017539017)]
    [InlineData("åäö", -925138216)]
    public void StringHashCodesMatchTheJavaImplementation(string value, int expected) =>
        Assert.Equal(expected, HashCodes.Compute(value));

    /// <summary>
    /// Pins the raw MurmurHash3 output, including the four-byte block loop and each tail length.
    /// </summary>
    [Theory]
    [InlineData(4, -1422474110)]
    [InlineData(5, -410510807)]
    [InlineData(6, -1455469502)]
    [InlineData(7, 1445790138)]
    [InlineData(8, 24275788)]
    public void ByteHashCodesMatchTheJavaImplementation(int length, int expected)
    {
        byte[] bytes = [.. Enumerable.Range(0, length).Select(i => (byte)i)];
        Assert.Equal(expected, HashCodes.Compute(bytes.AsSpan()));
    }

    [Fact]
    public void NullStringHashesToMinusOne() => Assert.Equal(-1, HashCodes.Compute((string?)null));

    [Fact]
    public void SpanAndByteDataAgree()
    {
        byte[] bytes = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

        Assert.Equal(-427289680, HashCodes.Compute(bytes.AsSpan()));
        Assert.Equal(
            HashCodes.Compute(bytes.AsSpan()),
            HashCodes.Compute(new ArrayByteData(bytes), 0, bytes.Length));
    }

    /// <summary>
    /// A byte with the high bit set is sign-extended before being shifted into the top of a block, so
    /// hashing must not treat the input as unsigned.
    /// </summary>
    [Fact]
    public void HighBitBytesAreSignExtendedInTheBlockLoop()
    {
        Assert.NotEqual(
            HashCodes.Compute(new byte[] { 0, 0, 0, 0x80 }.AsSpan()),
            HashCodes.Compute(new byte[] { 0, 0, 0, 0x00 }.AsSpan()));
    }
}
