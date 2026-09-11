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
using Hollow.Core.Memory.Pool;

namespace Hollow.Tests.Core.Memory.Encoding;

/// <summary>
/// Ported from <c>FixedLengthElementArrayTest</c>.
/// </summary>
public class FixedLengthElementArrayTests
{
    [Fact]
    public void SetAndGet()
    {
        const int TestValue = 53215;
        const int NumBitsPerElement = 17;
        const long BitMask = (1L << NumBitsPerElement) - 1;

        FixedLengthElementArray array = new(WastefulRecycler.SmallArrayRecycler, 17_000_000);

        for (int i = 0; i < 1_000_000; i++)
        {
            array.SetElementValue((long)i * NumBitsPerElement, NumBitsPerElement, TestValue);
        }

        for (int i = 0; i < 1_000_000; i++)
        {
            Assert.Equal(TestValue, array.GetElementValue((long)i * NumBitsPerElement, NumBitsPerElement, BitMask));
        }
    }

    [Fact]
    public void GetEmpty()
    {
        FixedLengthElementArray array = new(WastefulRecycler.SmallArrayRecycler, 17_000);

        Assert.Equal(0, array.GetElementValue(0, 4));
    }

    [Fact]
    public void SetAndGetLargeValues()
    {
        const long TestValue = 1913684435138312210L;
        const int NumBitsPerElement = 61;

        FixedLengthElementArray array = new(WastefulRecycler.SmallArrayRecycler, 610_000);

        for (int i = 0; i < 10_000; i++)
        {
            array.SetElementValue((long)i * NumBitsPerElement, NumBitsPerElement, TestValue);
        }

        for (int i = 0; i < 10_000; i++)
        {
            Assert.Equal(TestValue, array.GetLargeElementValue((long)i * NumBitsPerElement, NumBitsPerElement));
        }
    }

    /// <summary>
    /// The Java implementation reads elements with an unaligned eight-byte load, which silently
    /// corrupts values wider than 58 bits at unlucky bit offsets; its own tests for that behaviour are
    /// commented out. This port composes values from the words that contain them, so the same reads are
    /// correct — this test pins that.
    /// </summary>
    [Fact]
    public void WideValuesReadCorrectlyAtByteUnalignedOffsets()
    {
        const long TestValue = 288_230_376_151_711_744L; // The smallest 59-bit number.
        const int NumBitsPerElement = 59;

        FixedLengthElementArray array = new(WastefulRecycler.SmallArrayRecycler, 64 * 16);

        for (int i = 0; i < 8; i++)
        {
            array.SetElementValue((long)i * NumBitsPerElement, NumBitsPerElement, TestValue);
        }

        // Element 2 starts six bits into a byte and element 5 starts seven bits in; both are offsets
        // the Java unaligned read cannot handle at this width.
        Assert.Equal(6, 2 * NumBitsPerElement % 8);
        Assert.Equal(7, 5 * NumBitsPerElement % 8);

        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(TestValue, array.GetElementValue((long)i * NumBitsPerElement, NumBitsPerElement));
        }
    }

    /// <summary>
    /// Reading two adjacent 29-bit elements as one 58-bit value also overflows the Java unaligned read
    /// at a seven-bit offset. Here it returns both elements intact.
    /// </summary>
    [Fact]
    public void AdjacentElementsCanBeReadAsOneWideValue()
    {
        const long TestValue = 268_435_456L; // The smallest 29-bit number.
        const int NumBitsPerElement = 29;

        FixedLengthElementArray array = new(WastefulRecycler.SmallArrayRecycler, 64 * 16);

        for (int i = 0; i < 6; i++)
        {
            array.SetElementValue((long)i * NumBitsPerElement, NumBitsPerElement, TestValue);
        }

        long bitOffset = 3 * NumBitsPerElement;
        Assert.Equal(7, bitOffset % 8);

        long multiValue = array.GetElementValue(bitOffset, NumBitsPerElement * 2);

        Assert.Equal(TestValue, multiValue & ((1L << NumBitsPerElement) - 1));
        Assert.Equal(TestValue, multiValue >> NumBitsPerElement);
        Assert.Equal(TestValue, array.GetElementValue(bitOffset, NumBitsPerElement));
        Assert.Equal(TestValue, array.GetElementValue(bitOffset + NumBitsPerElement, NumBitsPerElement));
    }

    [Fact]
    public void CopyBitRange()
    {
        Random random = new(20260911);

        for (int iteration = 0; iteration < 100; iteration++)
        {
            int totalBitsInArray = random.Next(1, 6_400_000);
            int totalBitsInCopyRange = random.Next(totalBitsInArray);
            int copyFromRangeStartBit = random.Next(totalBitsInArray - totalBitsInCopyRange);
            int copyToRangeStartBit = random.Next(100_000);

            FixedLengthElementArray source = new(WastefulRecycler.SmallArrayRecycler, totalBitsInArray + 64);
            FixedLengthElementArray destination =
                new(WastefulRecycler.DefaultInstance, totalBitsInArray + copyToRangeStartBit + 64);

            int numLongs = totalBitsInArray >>> 6;
            for (int i = 0; i <= numLongs; i++)
            {
                source.Set(i, random.NextInt64());
            }

            destination.CopyBits(source, copyFromRangeStartBit, copyToRangeStartBit, totalBitsInCopyRange);

            int compareBitStart = copyFromRangeStartBit;
            int copyToRangeOffset = copyToRangeStartBit - copyFromRangeStartBit;
            int numBitsLeftToCompare = totalBitsInCopyRange;

            while (numBitsLeftToCompare > 0)
            {
                int bitsToCompare = Math.Min(numBitsLeftToCompare, 56);

                Assert.Equal(
                    source.GetElementValue(compareBitStart, bitsToCompare),
                    destination.GetElementValue(compareBitStart + copyToRangeOffset, bitsToCompare));

                numBitsLeftToCompare -= bitsToCompare;
                compareBitStart += bitsToCompare;
            }
        }
    }

    [Fact]
    public void CopySmallBitRange()
    {
        FixedLengthElementArray source = new(WastefulRecycler.SmallArrayRecycler, 64);
        FixedLengthElementArray destination = new(WastefulRecycler.SmallArrayRecycler, 128);

        source.SetElementValue(0, 64, -1L);

        destination.CopyBits(source, 10, 10, 10);

        Assert.Equal(0, destination.GetElementValue(0, 10));
        Assert.Equal(1023, destination.GetElementValue(10, 10));
        Assert.Equal(0, destination.GetLargeElementValue(20, 10));
    }

    [Fact]
    public void CopyingZeroBitsFromTheEndIsANoOp()
    {
        FixedLengthElementArray array = new(new WastefulRecycler(2, 2), 256);

        array.CopyBits(array, 256, 10, 0);
    }

    [Fact]
    public void Increment()
    {
        FixedLengthElementArray array = new(WastefulRecycler.SmallArrayRecycler, 1_000_000);

        Random random = new(20260911);
        long startValue = random.Next(int.MaxValue);
        int elementCount = 0;

        for (int i = 0; i < 1_000_000; i += 65)
        {
            array.SetElementValue(i, 60, startValue + i);
            elementCount++;
        }

        array.IncrementMany(0, 1000, 65, elementCount);

        for (int i = 0; i < 1_000_000; i += 65)
        {
            Assert.Equal(startValue + i + 1000, array.GetElementValue(i, 60));
        }

        array.IncrementMany(0, -2000, 65, elementCount);

        for (int i = 0; i < 1_000_000; i += 65)
        {
            Assert.Equal(startValue + i - 1000, array.GetElementValue(i, 60));
        }
    }

    [Fact]
    public void ClearElementValueZeroesJustThatElement()
    {
        FixedLengthElementArray array = new(WastefulRecycler.SmallArrayRecycler, 1024);

        for (int i = 0; i < 10; i++)
        {
            array.SetElementValue(i * 17, 17, (1L << 17) - 1);
        }

        array.ClearElementValue(4 * 17, 17);

        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(i == 4 ? 0 : (1L << 17) - 1, array.GetElementValue(i * 17, 17));
        }
    }

    [Theory]
    [InlineData(0L, 1)]
    [InlineData(1L, 1)]
    [InlineData(2L, 2)]
    [InlineData(3L, 2)]
    [InlineData(4L, 3)]
    [InlineData(16L, 5)]
    [InlineData(31L, 5)]
    [InlineData(long.MaxValue, 63)]
    public void BitsRequiredToRepresentValue(long value, int expected) =>
        Assert.Equal(expected, IFixedLengthData.BitsRequiredToRepresentValue(value));
}
