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

using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.Iterator;
using Hollow.Core.Schema;
using Hollow.Core.Util;

namespace Hollow.Core.Index.Traversal;

/// <summary>
/// One node of the tree a <see cref="HollowIndexerValueTraverser"/> walks: a type, or a value field of
/// a type, that one or more indexed field paths pass through.
/// </summary>
/// <remarks>
/// <para>
/// The tree exists because a field path may cross a collection, so one record of the root type can
/// produce many values for one path. Where two paths cross different collections, every combination of
/// their values is a separate match, which is what <see cref="DoMultiply"/> produces.
/// </para>
/// <para>
/// Not intended for use outside the index.
/// </para>
/// </remarks>
internal abstract class HollowIndexerTraversalNode
{
    private bool _shouldMultiplyBranchResults;
    private int[] _childrenRepeatCounts = [];
    private int[] _childrenMatchCounts = [];
    private int[] _fieldChildMap = [];
    private int[] _childFirstFieldMap = [];
    private int _currentMultiplyFieldMatchListPosition;

    protected HollowIndexerTraversalNode(IHollowTypeDataAccess dataAccess, IntList[] fieldMatches)
    {
        DataAccess = dataAccess;
        FieldMatches = fieldMatches;
    }

    /// <summary>The position of the indexed field this node terminates, or -1 when it terminates none.</summary>
    internal int IndexedFieldPosition { get; set; } = -1;

    /// <summary>The child nodes, keyed by the path segment that reaches them.</summary>
    internal Dictionary<string, HollowIndexerTraversalNode> Children { get; } = new(StringComparer.Ordinal);

    /// <summary>The data this node reads from.</summary>
    protected IHollowTypeDataAccess DataAccess { get; }

    /// <summary>The per-field lists the traversal appends its matched ordinals to.</summary>
    protected IntList[] FieldMatches { get; }

    /// <summary>
    /// Prepares the multiplication bookkeeping, returning every indexed field position in this node's
    /// subtree.
    /// </summary>
    internal IntList SetUpMultiplication()
    {
        _shouldMultiplyBranchResults = ShouldMultiplyBranchResults();

        _childrenRepeatCounts = new int[Children.Count];
        _childrenMatchCounts = new int[Children.Count];
        _fieldChildMap = new int[FieldMatches.Length];
        _childFirstFieldMap = new int[Children.Count];

        Array.Fill(_fieldChildMap, -1);

        IntList branchFieldPositions = new();

        if (IndexedFieldPosition != -1)
        {
            branchFieldPositions.Add(IndexedFieldPosition);
        }

        int childCounter = 0;

        foreach (HollowIndexerTraversalNode child in Children.Values)
        {
            IntList childBranchFieldPositions = child.SetUpMultiplication();

            _childFirstFieldMap[childCounter] = childBranchFieldPositions.Get(0);

            for (int i = 0; i < childBranchFieldPositions.Count; i++)
            {
                _fieldChildMap[childBranchFieldPositions.Get(i)] = childCounter;
                branchFieldPositions.Add(childBranchFieldPositions.Get(i));
            }

            childCounter++;
        }

        return branchFieldPositions;
    }

    /// <summary>
    /// Visits <paramref name="ordinal"/>, appending whatever it matches to the field lists.
    /// </summary>
    internal void Traverse(int ordinal)
    {
        if (_childFirstFieldMap.Length == 0)
        {
            DoTraversal(ordinal);

            if (IndexedFieldPosition != -1)
            {
                FieldMatches[IndexedFieldPosition].Add(ordinal);
            }
        }
        else
        {
            int childMatchSize = DoTraversal(ordinal);

            // This node's own value repeats once per match its children produced, so that every field
            // list ends up the same length.
            if (IndexedFieldPosition != -1)
            {
                for (int i = 0; i < childMatchSize; i++)
                {
                    FieldMatches[IndexedFieldPosition].Add(ordinal);
                }
            }
        }
    }

    /// <summary>Records where the field lists stood before this node's children ran.</summary>
    protected void PrepareMultiply()
    {
        if (_childFirstFieldMap.Length > 0)
        {
            _currentMultiplyFieldMatchListPosition = FieldMatches[_childFirstFieldMap[0]].Count;
        }
    }

    /// <summary>
    /// Expands the children's matches into every combination of them, and returns how many there are.
    /// </summary>
    protected int DoMultiply()
    {
        if (!_shouldMultiplyBranchResults)
        {
            return _childFirstFieldMap.Length != 0
                ? FieldMatches[_childFirstFieldMap[0]].Count - _currentMultiplyFieldMatchListPosition
                : 1;
        }

        int totalCombinations = 1;
        for (int i = 0; i < _childrenMatchCounts.Length; i++)
        {
            _childrenMatchCounts[i] =
                FieldMatches[_childFirstFieldMap[i]].Count - _currentMultiplyFieldMatchListPosition;
            _childrenRepeatCounts[i] = totalCombinations;
            totalCombinations *= _childrenMatchCounts[i];
        }

        // One branch matched nothing, so there are no combinations at all; discard what the others
        // contributed.
        if (totalCombinations == 0)
        {
            for (int i = 0; i < _childrenMatchCounts.Length; i++)
            {
                FieldMatches[_childFirstFieldMap[i]].ExpandTo(_currentMultiplyFieldMatchListPosition);
            }

            return 0;
        }

        int newFieldMatchListPosition = _currentMultiplyFieldMatchListPosition + totalCombinations;

        for (int i = 0; i < FieldMatches.Length; i++)
        {
            if (_fieldChildMap[i] == -1)
            {
                continue;
            }

            FieldMatches[i].ExpandTo(newFieldMatchListPosition);

            // Each branch's values repeat with its own stride, so the combined lists enumerate the
            // cartesian product. Filled backwards because the source values are a prefix of the target.
            int currentCopyToIndex = newFieldMatchListPosition - 1;
            int startCopyFromIndex =
                _currentMultiplyFieldMatchListPosition + _childrenMatchCounts[_fieldChildMap[i]] - 1;
            int currentCopyFromIndex = startCopyFromIndex;

            while (currentCopyToIndex > _currentMultiplyFieldMatchListPosition)
            {
                for (int j = 0; j < _childrenRepeatCounts[_fieldChildMap[i]]; j++)
                {
                    FieldMatches[i].Set(currentCopyToIndex, FieldMatches[i].Get(currentCopyFromIndex));
                    currentCopyToIndex--;
                }

                currentCopyFromIndex--;
                if (currentCopyFromIndex < _currentMultiplyFieldMatchListPosition)
                {
                    currentCopyFromIndex = startCopyFromIndex;
                }
            }
        }

        return totalCombinations;
    }

    /// <summary>
    /// Visits this node's children for <paramref name="ordinal"/>, returning how many matches they
    /// produced.
    /// </summary>
    protected abstract int DoTraversal(int ordinal);

    /// <summary>
    /// Resolves the child lookups into the form traversal uses, once the whole tree exists.
    /// </summary>
    protected internal abstract void SetUpChildren();

    /// <summary>
    /// Whether descending into this node's children can yield more than one match per record, which is
    /// true of a collection and false of an object.
    /// </summary>
    protected abstract bool FollowingChildrenMultipliesTraversal();

    /// <summary>
    /// Multiplication is only needed where two or more sibling branches exist and at least one of them
    /// can produce several matches.
    /// </summary>
    private bool ShouldMultiplyBranchResults() =>
        Children.Count > 1 && Children.Values.Any(child => child.BranchMayProduceMoreThanOneMatch());

    private bool BranchMayProduceMoreThanOneMatch() =>
        (Children.Count != 0 && FollowingChildrenMultipliesTraversal())
        || Children.Values.Any(child => child.BranchMayProduceMoreThanOneMatch());
}

/// <summary>
/// A node for a record of an object type, which descends into its reference fields and value fields.
/// </summary>
internal class HollowIndexerObjectTraversalNode(
    IHollowObjectTypeDataAccess dataAccess, IntList[] fieldMatches)
    : HollowIndexerTraversalNode(dataAccess, fieldMatches)
{
    private HollowIndexerTraversalNode[] _children = [];
    private int[] _childOrdinalFieldPositions = [];

    /// <inheritdoc />
    protected internal override void SetUpChildren()
    {
        _children = new HollowIndexerTraversalNode[Children.Count];
        _childOrdinalFieldPositions = new int[_children.Length];

        int index = 0;
        foreach ((string name, HollowIndexerTraversalNode child) in Children)
        {
            _childOrdinalFieldPositions[index] = ObjectDataAccess.Schema.GetPosition(name);
            _children[index] = child;
            index++;
        }
    }

    /// <inheritdoc />
    protected override int DoTraversal(int ordinal)
    {
        PrepareMultiply();

        for (int i = 0; i < _children.Length; i++)
        {
            if (_children[i] is HollowIndexerObjectFieldTraversalNode)
            {
                // A value field has no record of its own; it is read from this one.
                _children[i].Traverse(ordinal);
            }
            else
            {
                int childOrdinal = ObjectDataAccess.ReadOrdinal(ordinal, _childOrdinalFieldPositions[i]);
                if (childOrdinal != HollowConstants.OrdinalNone)
                {
                    _children[i].Traverse(childOrdinal);
                }
            }
        }

        return DoMultiply();
    }

    /// <inheritdoc />
    protected override bool FollowingChildrenMultipliesTraversal() => false;

    private IHollowObjectTypeDataAccess ObjectDataAccess => (IHollowObjectTypeDataAccess)DataAccess;
}

/// <summary>
/// A node for a value field of an object type, which is where a path ends.
/// </summary>
internal sealed class HollowIndexerObjectFieldTraversalNode(
    IHollowTypeDataAccess dataAccess, IntList[] fieldMatches)
    : HollowIndexerTraversalNode(dataAccess, fieldMatches)
{
    /// <inheritdoc />
    protected internal override void SetUpChildren()
    {
        // A value field has no children.
    }

    /// <inheritdoc />
    protected override int DoTraversal(int ordinal) => 1;

    /// <inheritdoc />
    protected override bool FollowingChildrenMultipliesTraversal() => false;
}

/// <summary>
/// A node for a set, which visits each element in turn.
/// </summary>
internal class HollowIndexerCollectionTraversalNode(
    IHollowTypeDataAccess dataAccess, IntList[] fieldMatches)
    : HollowIndexerTraversalNode(dataAccess, fieldMatches)
{
    /// <summary>The node reached through the <c>element</c> segment, if the paths use one.</summary>
    protected HollowIndexerTraversalNode? Child { get; private set; }

    /// <inheritdoc />
    protected internal override void SetUpChildren() => Child = Children.GetValueOrDefault("element");

    /// <inheritdoc />
    protected override int DoTraversal(int ordinal)
    {
        if (Child is null)
        {
            return 1;
        }

        int numMatches = 0;

        foreach (int elementOrdinal in CollectionDataAccess.ElementOrdinals(ordinal))
        {
            PrepareMultiply();
            Child.Traverse(elementOrdinal);
            numMatches += DoMultiply();
        }

        return numMatches;
    }

    /// <inheritdoc />
    protected override bool FollowingChildrenMultipliesTraversal() => true;

    /// <summary>The collection this node reads from.</summary>
    protected IHollowCollectionTypeDataAccess CollectionDataAccess =>
        (IHollowCollectionTypeDataAccess)DataAccess;
}

/// <summary>
/// A node for a list, which visits its elements by index rather than through an iterator.
/// </summary>
internal sealed class HollowIndexerListTraversalNode(
    IHollowListTypeDataAccess dataAccess, IntList[] fieldMatches)
    : HollowIndexerCollectionTraversalNode(dataAccess, fieldMatches)
{
    /// <inheritdoc />
    protected override int DoTraversal(int ordinal)
    {
        if (Child is null)
        {
            return 1;
        }

        IHollowListTypeDataAccess listDataAccess = (IHollowListTypeDataAccess)CollectionDataAccess;
        int size = listDataAccess.Size(ordinal);
        int numMatches = 0;

        for (int i = 0; i < size; i++)
        {
            PrepareMultiply();
            Child.Traverse(listDataAccess.GetElementOrdinal(ordinal, i));
            numMatches += DoMultiply();
        }

        return numMatches;
    }
}

/// <summary>
/// A node for a map, which visits each entry's key and value together so that a path through the key
/// and a path through the value of the same entry form one match.
/// </summary>
internal sealed class HollowIndexerMapTraversalNode(
    IHollowMapTypeDataAccess dataAccess, IntList[] fieldMatches)
    : HollowIndexerTraversalNode(dataAccess, fieldMatches)
{
    private HollowIndexerTraversalNode? _keyNode;
    private HollowIndexerTraversalNode? _valueNode;

    /// <inheritdoc />
    protected internal override void SetUpChildren()
    {
        _keyNode = Children.GetValueOrDefault("key");
        _valueNode = Children.GetValueOrDefault("value");
    }

    /// <inheritdoc />
    protected override int DoTraversal(int ordinal)
    {
        int numMatches = 0;

        foreach (HollowMapEntry entry in MapDataAccess.Entries(ordinal))
        {
            PrepareMultiply();

            _keyNode?.Traverse(entry.KeyOrdinal);
            _valueNode?.Traverse(entry.ValueOrdinal);

            numMatches += DoMultiply();
        }

        return numMatches;
    }

    /// <inheritdoc />
    protected override bool FollowingChildrenMultipliesTraversal() => true;

    private IHollowMapTypeDataAccess MapDataAccess => (IHollowMapTypeDataAccess)DataAccess;
}
