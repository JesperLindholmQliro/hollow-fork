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

namespace Hollow.Tests.Core.Memory.Encoding;

/// <summary>
/// The many-elements-per-node array the prefix index hangs its ordinals off.
/// </summary>
/// <remarks>
/// Ported from <c>FixedLengthMultipleOccurrenceElementArrayTest</c>. Element zero is the case worth
/// the trouble: the storage cannot tell a written zero from an empty slot, so a node that holds one
/// is recorded separately — and a bug there loses exactly the ordinal zero, which is the first record
/// of every dataset.
/// </remarks>
public sealed class FixedLengthMultipleOccurrenceElementArrayTests
{
    private const long NodeCount = 10_000;
    private const int BitsPerElement = 5;
    private const int ElementsPerNodeEstimate = 4;

    [Fact]
    public void ANodeGivesBackTheElementsItWasGivenInOrder()
    {
        FixedLengthMultipleOccurrenceElementArray array = Create();
        long[] elements = [1, 2, 3];

        foreach (long element in elements)
        {
            array.AddElement(0, element);
        }

        Assert.Equal(elements, array.GetElements(0));
    }

    [Fact]
    public void AnElementOfZeroIsAnElementRatherThanAnEmptySlot()
    {
        // The whole reason for the separate record of which nodes hold a zero: the fixed-length storage
        // reads an unwritten slot as zero too.
        FixedLengthMultipleOccurrenceElementArray array = Create();
        long[] elements = [0, 1, 2];

        foreach (long element in elements)
        {
            array.AddElement(0, element);
        }

        Assert.Equal(elements, array.GetElements(0));
    }

    [Fact]
    public void ANodeWithNothingInItHasNothingInIt()
    {
        FixedLengthMultipleOccurrenceElementArray array = Create();

        Assert.Empty(array.GetElements(0));
        Assert.Empty(array.GetElements(NodeCount - 1));
    }

    [Fact]
    public void ANodeThatOutgrowsTheEstimateIsResizedRatherThanTruncated()
    {
        FixedLengthMultipleOccurrenceElementArray array = Create();
        long[] elements = [.. Enumerable.Range(2, 13).Select(value => (long)value)];

        foreach (long element in elements)
        {
            array.AddElement(1, element);
        }

        Assert.True(
            array.MaxElementsPerNode >= elements.Length,
            $"{elements.Length} elements fitted in room for {array.MaxElementsPerNode}");

        Assert.Equal(elements, array.GetElements(1));
    }

    [Fact]
    public void ResizingOneNodeLeavesTheOthersAsTheyWere()
    {
        // The resize rewrites every node's storage, so this is the case that catches a stride computed
        // from the old width.
        FixedLengthMultipleOccurrenceElementArray array = Create();

        long[] first = [0, 1, 2, 3];
        long[] second = [.. Enumerable.Range(2, 13).Select(value => (long)value)];
        long[] third = [1];

        foreach (long element in first)
        {
            array.AddElement(0, element);
        }

        foreach (long element in second)
        {
            array.AddElement(1, element);
        }

        foreach (long element in third)
        {
            array.AddElement(2, element);
        }

        Assert.Equal(first, array.GetElements(0));
        Assert.Equal(second, array.GetElements(1));
        Assert.Equal(third, array.GetElements(2));
    }

    [Fact]
    public void EveryNodeKeepsItsOwnElements()
    {
        FixedLengthMultipleOccurrenceElementArray array = Create();
        long[] elements = [.. Enumerable.Range(0, 31).Select(value => (long)value)];

        for (long node = 0; node < NodeCount; node++)
        {
            foreach (long element in elements)
            {
                array.AddElement(node, element);
            }
        }

        for (long node = 0; node < NodeCount; node++)
        {
            Assert.Equal(elements, array.GetElements(node));
        }
    }

    [Fact]
    public void TheWidestElementTheDeclaredWidthAllowsStillFits()
    {
        // Five bits, so 31 is the largest element and the one that would be truncated by an
        // off-by-one in the mask.
        FixedLengthMultipleOccurrenceElementArray array = Create();

        array.AddElement(0, 31);

        Assert.Equal([31L], array.GetElements(0));
    }

    private static FixedLengthMultipleOccurrenceElementArray Create() =>
        new(WastefulRecycler.SmallArrayRecycler, NodeCount, BitsPerElement, ElementsPerNodeEstimate);
}
