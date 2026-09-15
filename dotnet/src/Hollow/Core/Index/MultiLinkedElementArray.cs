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

using Hollow.Core.Memory.Pool;
using Hollow.Core.Read.Iterator;

namespace Hollow.Core.Index;

/// <summary>
/// Many singly linked lists of ints sharing one pooled backing array.
/// </summary>
/// <remarks>
/// <para>
/// The hash index builds one list of matching ordinals per distinct match key, and there may be very
/// many of those with very few elements each. A list of one or two elements is therefore held entirely
/// in its own header word rather than costing a link; only the third element makes a list spill into
/// the linked array.
/// </para>
/// <para>
/// The header word is: sign bit set means "one or two elements inline", with the first in the high 32
/// bits and the second in the low 32; sign bit clear means the high 32 bits are the head link and the
/// low 31 the size. Elements are prepended, so a linked list iterates in reverse insertion order.
/// </para>
/// </remarks>
public sealed class MultiLinkedElementArray
{
    private const long InlineMarker = long.MinValue;

    private readonly GrowingSegmentedLongArray _listPointersAndSizes;
    private readonly GrowingSegmentedLongArray _linkedElements;

    private int _nextNewPointer;
    private long _nextLinkedElement;

    /// <summary>
    /// Initialises an empty set of lists drawing storage from <paramref name="memoryRecycler"/>.
    /// </summary>
    public MultiLinkedElementArray(IArraySegmentRecycler memoryRecycler)
    {
        ArgumentNullException.ThrowIfNull(memoryRecycler);

        _listPointersAndSizes = new GrowingSegmentedLongArray(memoryRecycler);
        _linkedElements = new GrowingSegmentedLongArray(memoryRecycler);
    }

    /// <summary>The number of lists created so far.</summary>
    public int ListCount => _nextNewPointer;

    /// <summary>Creates an empty list and returns its index.</summary>
    public int NewList() => _nextNewPointer++;

    /// <summary>Appends <paramref name="value"/> to the list at <paramref name="listIndex"/>.</summary>
    public void Add(int listIndex, int value)
    {
        long listPointer = _listPointersAndSizes.Get(listIndex);

        // Empty: keep the first element inline, in the high half.
        if (listPointer == 0)
        {
            _listPointersAndSizes.Set(listIndex, InlineMarker | ((long)value << 32));
            return;
        }

        // One element inline: keep the second inline too, in the low half.
        if ((listPointer & 0xFFFFFFFFL) == 0)
        {
            _listPointersAndSizes.Set(listIndex, listPointer | 0x80000000L | (uint)value);
            return;
        }

        if ((listPointer & InlineMarker) != 0)
        {
            // Two elements inline and a third arriving: spill all three into the linked array.
            _linkedElements.Set(_nextLinkedElement, listPointer);

            long newLink = ((long)value << 32) | (uint)_nextLinkedElement;
            _linkedElements.Set(++_nextLinkedElement, newLink);

            _listPointersAndSizes.Set(listIndex, ((long)_nextLinkedElement++ << 32) | 3);
        }
        else
        {
            long head = listPointer >> 32;
            long size = listPointer & int.MaxValue;

            long newLink = ((long)value << 32) | (uint)head;
            _linkedElements.Set(_nextLinkedElement, newLink);

            _listPointersAndSizes.Set(listIndex, ((long)_nextLinkedElement++ << 32) | (size + 1));
        }
    }

    /// <summary>The number of elements in the list at <paramref name="listIndex"/>.</summary>
    public int ListSize(int listIndex)
    {
        long listPointer = _listPointersAndSizes.Get(listIndex);

        if (listPointer == 0)
        {
            return 0;
        }

        return (listPointer & InlineMarker) != 0
            ? (listPointer & 0xFFFFFFFFL) == 0 ? 1 : 2
            : (int)(listPointer & int.MaxValue);
    }

    /// <summary>
    /// The elements of the list at <paramref name="listIndex"/>.
    /// </summary>
    /// <remarks>
    /// Named <c>iterator</c> in Java, where each of the two layouts below has its own
    /// <c>HollowOrdinalIterator</c> class.
    /// </remarks>
    public IEnumerable<int> Elements(int listIndex) =>
        (_listPointersAndSizes.Get(listIndex) & InlineMarker) != 0
            ? InlineElements(listIndex)
            : LinkedElements(listIndex);

    /// <summary>Returns the backing storage to the recycler.</summary>
    public void Destroy()
    {
        _listPointersAndSizes.Destroy();
        _linkedElements.Destroy();
    }

    /// <summary>
    /// Walks a list that spilled into the linked array, following the head link backwards.
    /// </summary>
    private IEnumerable<int> LinkedElements(int listIndex)
    {
        int currentElement = (int)(_listPointersAndSizes.Get(listIndex) >> 32);

        while (true)
        {
            long element = _linkedElements.Get(currentElement);

            if (element < 0)
            {
                // The spilled header word: its low half holds the second element and its high half the
                // first, so the walk ends with the two of them.
                yield return (int)element & int.MaxValue;
                yield return (int)((ulong)element >> 32) & int.MaxValue;

                yield break;
            }

            currentElement = (int)element;

            yield return (int)(element >> 32);
        }
    }

    /// <summary>
    /// Walks a list of one or two elements held in its own header word.
    /// </summary>
    private IEnumerable<int> InlineElements(int listIndex)
    {
        long element = _listPointersAndSizes.Get(listIndex);

        if ((element & 0xFFFFFFFFL) != 0)
        {
            yield return (int)element & int.MaxValue;
        }

        yield return (int)((ulong)element >> 32) & int.MaxValue;
    }
}
