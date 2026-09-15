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

using System.Numerics;
using Hollow.Core.Memory.Encoding;
using Hollow.Core.Memory.Pool;
using Hollow.Core.Read.Iterator;

namespace Hollow.Core.Index;

/// <summary>
/// A ternary search tree over strings, holding the ordinals of the records each string came from.
/// </summary>
/// <remarks>
/// <para>
/// Each node carries one UTF-16 code unit and three child pointers — less, equal, greater — so
/// following a key walks one node per comparison and steps down the middle child once per matched
/// character. The whole tree is one bit-packed array of nodes, sized up front from an estimate; there
/// is no growing it, and running out of nodes throws.
/// </para>
/// <para>
/// How balanced the tree is depends entirely on insertion order. Keys inserted in sorted order
/// degenerate to a linked list; random order gives something close to logarithmic lookups.
/// </para>
/// <para>
/// Named <c>TST</c> in Java.
/// </para>
/// </remarks>
internal sealed class TernarySearchTree
{
    /// <summary>The node index the tree is rooted at, and the value a missing child carries.</summary>
    private const long RootNode = 0;

    private readonly bool _caseSensitive;
    private readonly int _bitsPerKey;
    private readonly int _bitsForChildPointer;
    private readonly int _bitsPerNode;

    private readonly long _leftChildOffset;
    private readonly long _middleChildOffset;
    private readonly long _rightChildOffset;
    private readonly long _isEndFlagOffset;

    private readonly FixedLengthElementArray _nodes;
    private readonly FixedLengthMultipleOccurrenceElementArray _ordinalSet;

    private long _nextFreeNode;

    /// <summary>
    /// Initialises a tree able to hold <paramref name="estimatedMaxNodes"/> nodes.
    /// </summary>
    /// <param name="estimatedMaxNodes">
    /// The node capacity, which is allocated up front and is a hard limit.
    /// </param>
    /// <param name="estimatedMaxStringDuplicates">
    /// How many records are expected to share a key, which decides how much room each node reserves
    /// for ordinals before it has to grow.
    /// </param>
    /// <param name="maxOrdinalValue">The largest ordinal the tree will be asked to hold.</param>
    /// <param name="caseSensitive">Whether keys are matched case-sensitively.</param>
    /// <param name="memoryRecycler">The pool to draw the tree's storage from.</param>
    internal TernarySearchTree(
        long estimatedMaxNodes,
        int estimatedMaxStringDuplicates,
        int maxOrdinalValue,
        bool caseSensitive,
        IArraySegmentRecycler memoryRecycler)
    {
        MaxNodes = estimatedMaxNodes;
        _caseSensitive = caseSensitive;

        _bitsPerKey = 16;
        _bitsForChildPointer = 64 - BitOperations.LeadingZeroCount((ulong)estimatedMaxNodes);

        int bitsPerOrdinal = maxOrdinalValue <= 0
            ? 1
            : 32 - BitOperations.LeadingZeroCount((uint)maxOrdinalValue);

        // One node: the key, three child pointers, and a bit marking the end of an indexed key.
        _bitsPerNode = _bitsPerKey + (3 * _bitsForChildPointer) + 1;

        _leftChildOffset = _bitsPerKey;
        _middleChildOffset = _leftChildOffset + _bitsForChildPointer;
        _rightChildOffset = _middleChildOffset + _bitsForChildPointer;
        _isEndFlagOffset = _rightChildOffset + _bitsForChildPointer;

        _nodes = new FixedLengthElementArray(memoryRecycler, _bitsPerNode * estimatedMaxNodes);
        _ordinalSet = new FixedLengthMultipleOccurrenceElementArray(
            memoryRecycler, estimatedMaxNodes, bitsPerOrdinal, estimatedMaxStringDuplicates);
    }

    private enum ChildDirection
    {
        Left,
        Middle,
        Right,
    }

    /// <summary>The node capacity, fixed when the tree was built.</summary>
    internal long MaxNodes { get; }

    /// <summary>How many nodes are in use.</summary>
    internal long NodesUsed => _nextFreeNode;

    /// <summary>How much of the reserved capacity went unused.</summary>
    internal long EmptyNodes => MaxNodes - _nextFreeNode;

    /// <summary>
    /// The longest path from the root, which bounds the number of nodes a lookup visits.
    /// </summary>
    internal long MaxDepth { get; private set; }

    /// <summary>How many ordinals a single node has room for.</summary>
    internal int MaxElementsPerNode => _ordinalSet.MaxElementsPerNode;

    /// <summary>An approximation of the memory this tree occupies, in bytes.</summary>
    internal long ApproxHeapFootprintInBytes =>
        _nodes.ApproxHeapFootprintInBytes + _ordinalSet.ApproxHeapFootprintInBytes;

    /// <summary>Returns this tree's storage to <paramref name="memoryRecycler"/>.</summary>
    internal void RecycleMemory(IArraySegmentRecycler memoryRecycler)
    {
        _nodes.Destroy(memoryRecycler);
        _ordinalSet.Destroy();
    }

    /// <summary>
    /// Indexes <paramref name="key"/> against <paramref name="ordinal"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">The tree ran out of nodes.</exception>
    internal void Insert(string key, int ordinal)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (!_caseSensitive)
        {
            key = key.ToLowerInvariant();
        }

        long currentNode = RootNode;
        int keyIndex = 0;
        int depth = 0;

        while (keyIndex < key.Length)
        {
            char character = key[keyIndex];

            if (GetKey(currentNode) == 0)
            {
                SetKey(currentNode, character);
                _nextFreeNode++;

                if (_nextFreeNode >= MaxNodes)
                {
                    throw new InvalidOperationException(
                        "The prefix index ran out of nodes. Build it with a larger estimate of the number "
                        + "of nodes it needs.");
                }
            }

            long keyAtCurrentNode = GetKey(currentNode);

            if (character < keyAtCurrentNode)
            {
                currentNode = StepDown(currentNode, ChildDirection.Left);
            }
            else if (character > keyAtCurrentNode)
            {
                currentNode = StepDown(currentNode, ChildDirection.Right);
            }
            else
            {
                keyIndex++;

                if (keyIndex < key.Length)
                {
                    currentNode = StepDown(currentNode, ChildDirection.Middle);
                }
            }

            depth++;
        }

        _ordinalSet.AddElement(currentNode, ordinal);
        SetEndNode(currentNode);

        MaxDepth = Math.Max(MaxDepth, depth);
    }

    /// <summary>
    /// The ordinals recorded at <paramref name="nodeIndex"/>, or none when the index is negative.
    /// </summary>
    internal IReadOnlyList<int> GetOrdinals(long nodeIndex) =>
        nodeIndex < 0 ? [] : [.. _ordinalSet.GetElements(nodeIndex).Select(static ordinal => (int)ordinal)];

    /// <summary>
    /// The node at the end of the longest indexed key that is a prefix of <paramref name="prefix"/>,
    /// or -1 when no indexed key is.
    /// </summary>
    /// <remarks>
    /// This matches whole indexed keys, not partial ones: with <c>abc</c> and <c>abcd</c> indexed,
    /// <c>abce</c> matches <c>abc</c> and <c>ab</c> matches nothing.
    /// </remarks>
    internal long FindLongestMatch(string? prefix)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            return -1;
        }

        if (!_caseSensitive)
        {
            prefix = prefix.ToLowerInvariant();
        }

        long match = -1;
        long currentNode = RootNode;
        int keyIndex = 0;
        bool atRoot = true;

        while (true)
        {
            if (currentNode == RootNode && !atRoot)
            {
                break;
            }

            long keyAtCurrentNode = GetKey(currentNode);
            char character = prefix[keyIndex];

            if (character < keyAtCurrentNode)
            {
                currentNode = GetChild(currentNode, ChildDirection.Left);
            }
            else if (character > keyAtCurrentNode)
            {
                currentNode = GetChild(currentNode, ChildDirection.Right);
            }
            else
            {
                if (IsEndNode(currentNode))
                {
                    match = currentNode;
                }

                if (keyIndex == prefix.Length - 1)
                {
                    break;
                }

                currentNode = GetChild(currentNode, ChildDirection.Middle);
                keyIndex++;
            }

            atRoot = false;
        }

        return match;
    }

    /// <summary>
    /// The node holding the last character of <paramref name="key"/>, or -1 when the tree has no such
    /// path. The node is not necessarily the end of an indexed key.
    /// </summary>
    internal long FindNodeWithKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return -1;
        }

        if (!_caseSensitive)
        {
            key = key.ToLowerInvariant();
        }

        long currentNode = RootNode;
        int keyIndex = 0;
        bool atRoot = true;

        while (true)
        {
            if (currentNode == RootNode && !atRoot)
            {
                return -1;
            }

            long keyAtCurrentNode = GetKey(currentNode);
            char character = key[keyIndex];

            if (character < keyAtCurrentNode)
            {
                currentNode = GetChild(currentNode, ChildDirection.Left);
            }
            else if (character > keyAtCurrentNode)
            {
                currentNode = GetChild(currentNode, ChildDirection.Right);
            }
            else
            {
                if (keyIndex == key.Length - 1)
                {
                    return currentNode;
                }

                currentNode = GetChild(currentNode, ChildDirection.Middle);
                keyIndex++;
            }

            atRoot = false;
        }
    }

    /// <summary>Whether <paramref name="key"/> was indexed in full.</summary>
    internal bool Contains(string? key)
    {
        long nodeIndex = FindNodeWithKey(key);

        return nodeIndex >= 0 && IsEndNode(nodeIndex);
    }

    /// <summary>
    /// Every ordinal indexed under a key starting with <paramref name="prefix"/>. An empty prefix
    /// reaches every ordinal in the tree.
    /// </summary>
    internal IEnumerable<int> FindKeysWithPrefix(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        if (!_caseSensitive)
        {
            prefix = prefix.ToLowerInvariant();
        }

        HashSet<int> ordinals = [];

        long currentNode = prefix.Length == 0 ? RootNode : FindNodeWithKey(prefix);

        if (currentNode >= 0)
        {
            if (IsEndNode(currentNode))
            {
                ordinals.UnionWith(GetOrdinals(currentNode));
            }

            // Everything under the middle child extends the prefix; everything to the left or right of
            // it diverges at this character, so the walk starts one step down.
            Queue<long> pending = new();

            if (prefix.Length == 0)
            {
                pending.Enqueue(RootNode);
            }
            else if (GetChild(currentNode, ChildDirection.Middle) is var subtree and not RootNode)
            {
                pending.Enqueue(subtree);
            }

            while (pending.TryDequeue(out long nodeIndex))
            {
                if (IsEndNode(nodeIndex))
                {
                    ordinals.UnionWith(GetOrdinals(nodeIndex));
                }

                foreach (ChildDirection direction in
                    (ReadOnlySpan<ChildDirection>)[ChildDirection.Left, ChildDirection.Middle, ChildDirection.Right])
                {
                    long child = GetChild(nodeIndex, direction);
                    if (child != RootNode)
                    {
                        pending.Enqueue(child);
                    }
                }
            }
        }

        return ordinals;
    }

    /// <summary>
    /// Follows a child, allocating it from the free pool when it does not exist yet.
    /// </summary>
    private long StepDown(long currentNode, ChildDirection direction)
    {
        long child = GetChild(currentNode, direction);

        if (child == RootNode)
        {
            // The root is never anyone's child, so zero doubles as "no child" and the next free node
            // takes its place.
            child = _nextFreeNode;
            SetChild(currentNode, direction, child);
        }

        return child;
    }

    private long ChildOffset(ChildDirection direction) => direction switch
    {
        ChildDirection.Left => _leftChildOffset,
        ChildDirection.Middle => _middleChildOffset,
        _ => _rightChildOffset,
    };

    private long GetChild(long nodeIndex, ChildDirection direction) =>
        _nodes.GetElementValue((nodeIndex * _bitsPerNode) + ChildOffset(direction), _bitsForChildPointer);

    private void SetChild(long nodeIndex, ChildDirection direction, long child) =>
        _nodes.SetElementValue(
            (nodeIndex * _bitsPerNode) + ChildOffset(direction), _bitsForChildPointer, child);

    private long GetKey(long nodeIndex) => _nodes.GetElementValue(nodeIndex * _bitsPerNode, _bitsPerKey);

    private void SetKey(long nodeIndex, char character) =>
        _nodes.SetElementValue(nodeIndex * _bitsPerNode, _bitsPerKey, character);

    private bool IsEndNode(long nodeIndex) =>
        _nodes.GetElementValue((nodeIndex * _bitsPerNode) + _isEndFlagOffset, 1) == 1;

    private void SetEndNode(long nodeIndex) =>
        _nodes.SetElementValue((nodeIndex * _bitsPerNode) + _isEndFlagOffset, 1, 1);

}
