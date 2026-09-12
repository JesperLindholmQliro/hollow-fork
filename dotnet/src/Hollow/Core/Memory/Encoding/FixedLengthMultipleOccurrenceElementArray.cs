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

using System.Globalization;
using Hollow.Core.Memory.Pool;
using Hollow.Core.Util;

namespace Hollow.Core.Memory.Encoding;

/// <summary>
/// Holds several fixed-width elements at each of a fixed number of node indexes.
/// </summary>
/// <remarks>
/// <para>
/// The storage is one flat bit-packed array with a bucket of the same size at every node, so a node
/// holding eight elements forces every node to have room for eight. Running out of room at any one
/// node grows every bucket by half again and copies the lot across, which is expensive — a caller who
/// knows roughly how many elements a node will hold should say so up front.
/// </para>
/// <para>
/// Zero is the empty marker, so a node holding element zero records that separately in a one-bit-per-
/// node side array rather than in the bucket.
/// </para>
/// <para>
/// Not safe for concurrent writes, and a read must not run alongside a write. Concurrent reads are
/// fine.
/// </para>
/// </remarks>
public sealed class FixedLengthMultipleOccurrenceElementArray
{
    /// <summary>How much bigger each bucket gets when one of them fills up.</summary>
    private const double ResizeMultiple = 1.5;

    /// <summary>The value that marks a bucket slot as unused.</summary>
    private const long NoElement = 0L;

    private readonly IArraySegmentRecycler _memoryRecycler;
    private readonly FixedLengthElementArray _nodesWithElementZero;
    private readonly int _bitsPerElement;
    private readonly long _elementMask;
    private readonly long _numNodes;

    private FixedLengthElementArray _storage;

    /// <summary>
    /// Initialises storage for <paramref name="numNodes"/> nodes of
    /// <paramref name="bitsPerElement"/>-bit elements.
    /// </summary>
    /// <param name="memoryRecycler">The pool to draw the storage from.</param>
    /// <param name="numNodes">How many node indexes the array addresses.</param>
    /// <param name="bitsPerElement">The width of one element, which must be under 61 bits.</param>
    /// <param name="maxElementsPerNodeEstimate">
    /// How many elements a node is expected to hold. Too low costs a resize; too high wastes the
    /// difference at every node.
    /// </param>
    public FixedLengthMultipleOccurrenceElementArray(
        IArraySegmentRecycler memoryRecycler, long numNodes, int bitsPerElement, int maxElementsPerNodeEstimate)
    {
        ArgumentNullException.ThrowIfNull(memoryRecycler);
        ArgumentOutOfRangeException.ThrowIfNegative(numNodes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bitsPerElement);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bitsPerElement, 60);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxElementsPerNodeEstimate);

        _memoryRecycler = memoryRecycler;
        _nodesWithElementZero = new FixedLengthElementArray(memoryRecycler, numNodes);
        _storage = new FixedLengthElementArray(
            memoryRecycler, numNodes * bitsPerElement * maxElementsPerNodeEstimate);
        _bitsPerElement = bitsPerElement;
        _elementMask = (1L << bitsPerElement) - 1;
        _numNodes = numNodes;

        MaxElementsPerNode = maxElementsPerNodeEstimate;
    }

    /// <summary>
    /// How many elements each node currently has room for, which grows as nodes fill up.
    /// </summary>
    public int MaxElementsPerNode { get; private set; }

    /// <summary>An approximation of the memory this array occupies, in bytes.</summary>
    public long ApproxHeapFootprintInBytes =>
        _storage.ApproxHeapFootprintInBytes + _nodesWithElementZero.ApproxHeapFootprintInBytes;

    /// <summary>
    /// Adds <paramref name="element"/> at <paramref name="nodeIndex"/>, growing every node's bucket if
    /// this one is full.
    /// </summary>
    /// <remarks>
    /// Duplicates are not detected: adding the same element twice stores it twice. The element goes in
    /// the first free slot from the start of the bucket rather than at the end, which keeps
    /// <see cref="GetElements"/> able to stop at the first empty slot.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="element"/> does not fit in this array's element width, or
    /// <paramref name="nodeIndex"/> is past the last node.
    /// </exception>
    public void AddElement(long nodeIndex, long element)
    {
        if (element < 0 || element > _elementMask)
        {
            throw new ArgumentOutOfRangeException(
                nameof(element),
                element,
                $"An element has to fit in {_bitsPerElement.Invariant()} bits.");
        }

        if (nodeIndex < 0 || nodeIndex >= _numNodes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nodeIndex),
                nodeIndex,
                $"This array addresses {_numNodes.Invariant()} nodes.");
        }

        if (element == NoElement)
        {
            // Zero marks a slot as unused, so a node holding element zero has to record it elsewhere.
            _nodesWithElementZero.SetElementValue(nodeIndex, 1, 1);
            return;
        }

        long bucketStart = nodeIndex * MaxElementsPerNode * _bitsPerElement;
        long currentIndex;
        int offset = 0;

        do
        {
            currentIndex = bucketStart + ((long)offset * _bitsPerElement);
            offset++;
        }
        while (_storage.GetElementValue(currentIndex, _bitsPerElement, _elementMask) != NoElement
            && offset < MaxElementsPerNode);

        if (_storage.GetElementValue(currentIndex, _bitsPerElement, _elementMask) != NoElement)
        {
            // The bucket is full. After the resize, offset is the first free slot in the wider bucket.
            ResizeElementsPerNode();
            currentIndex = (nodeIndex * MaxElementsPerNode * _bitsPerElement) + ((long)offset * _bitsPerElement);
        }

        _storage.SetElementValue(currentIndex, _bitsPerElement, element);
    }

    /// <summary>
    /// The elements at <paramref name="nodeIndex"/>, which may contain duplicates, or an empty list
    /// when the index is negative.
    /// </summary>
    public IReadOnlyList<long> GetElements(long nodeIndex)
    {
        if (nodeIndex < 0)
        {
            return [];
        }

        List<long> elements = [];

        if (_nodesWithElementZero.GetElementValue(nodeIndex, 1, 1) != NoElement)
        {
            elements.Add(NoElement);
        }

        long bucketStart = nodeIndex * MaxElementsPerNode * _bitsPerElement;

        for (int offset = 0; offset < MaxElementsPerNode; offset++)
        {
            long element = _storage.GetElementValue(
                bucketStart + ((long)offset * _bitsPerElement), _bitsPerElement, _elementMask);

            if (element == NoElement)
            {
                // Elements are packed from the start of the bucket, so the first gap is the end.
                break;
            }

            elements.Add(element);
        }

        return elements;
    }

    /// <summary>Returns this array's storage to the recycler.</summary>
    /// <remarks>
    /// Java's <c>destroy</c> releases only the element storage and leaves the element-zero side array
    /// behind; this releases both.
    /// </remarks>
    public void Destroy()
    {
        _storage.Destroy(_memoryRecycler);
        _nodesWithElementZero.Destroy(_memoryRecycler);
    }

    /// <summary>
    /// Gives every node half again as much room and copies the elements across.
    /// </summary>
    private void ResizeElementsPerNode()
    {
        int currentElementsPerNode = MaxElementsPerNode;
        int newElementsPerNode = (int)Math.Ceiling(currentElementsPerNode * ResizeMultiple);

        if (newElementsPerNode <= currentElementsPerNode)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Cannot grow the element array from {currentElementsPerNode} to {newElementsPerNode} "
                    + $"elements per node."));
        }

        FixedLengthElementArray newStorage = new(
            _memoryRecycler, _numNodes * _bitsPerElement * newElementsPerNode);

        for (long nodeIndex = 0; nodeIndex < _numNodes; nodeIndex++)
        {
            long currentBucketStart = nodeIndex * currentElementsPerNode * _bitsPerElement;
            long newBucketStart = nodeIndex * newElementsPerNode * _bitsPerElement;

            for (int offset = 0; offset < currentElementsPerNode; offset++)
            {
                long element = _storage.GetElementValue(
                    currentBucketStart + ((long)offset * _bitsPerElement), _bitsPerElement, _elementMask);

                if (element == NoElement)
                {
                    break;
                }

                newStorage.SetElementValue(
                    newBucketStart + ((long)offset * _bitsPerElement), _bitsPerElement, element);
            }
        }

        _storage.Destroy(_memoryRecycler);
        _storage = newStorage;
        MaxElementsPerNode = newElementsPerNode;
    }
}
