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
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Iterator;
using Hollow.Core.Schema;
using Hollow.Core.Tools.Diff.Exact;
using Hollow.Core.Util;

namespace Hollow.Core.Tools.Diff.Count;

/// <summary>
/// The node for a list or set type: every element of every record reached, flattened and paired off.
/// </summary>
/// <remarks>
/// Flattened on purpose. Which record an element came from does not matter to the score — what matters
/// is how many elements on one side have no partner on the other, which is what an element being added,
/// removed or changed all show up as.
/// </remarks>
public sealed class HollowDiffCollectionCountingNode : HollowDiffCountingNode
{
    private readonly IHollowCollectionTypeDataAccess? _fromState;
    private readonly IHollowCollectionTypeDataAccess? _toState;
    private readonly HollowDiffCountingNode _elementNode;
    private readonly DiffEqualOrdinalFilter _referenceFilter;
    private readonly bool _requiresTraversalForMissingFields;

    private readonly IntList _traversalFromOrdinals = new();
    private readonly IntList _traversalToOrdinals = new();

    /// <summary>Builds a node over a list or set type.</summary>
    public HollowDiffCollectionCountingNode(
        HollowDiff? diff,
        HollowTypeDiff? topLevelTypeDiff,
        HollowDiffNodeIdentifier nodeId,
        HollowTypeReadState? fromState,
        HollowTypeReadState? toState)
        : base(diff, topLevelTypeDiff, nodeId)
    {
        _fromState = (IHollowCollectionTypeDataAccess?)fromState;
        _toState = (IHollowCollectionTypeDataAccess?)toState;

        HollowCollectionSchema schema = (_fromState ?? _toState)!.Schema;
        string referencedType = schema.ElementType;

        _elementNode = CreateChildNode(
            _fromState?.Schema.ElementTypeState, _toState?.Schema.ElementTypeState, "element");

        _referenceFilter = new DiffEqualOrdinalFilter(
            EqualityMapping?.GetEqualOrdinalMap(referencedType) ?? DiffEqualOrdinalMap.Empty);

        _requiresTraversalForMissingFields =
            EqualityMapping?.RequiresMissingFieldTraversal(referencedType) == true;
    }

    /// <inheritdoc />
    public override void Prepare(int topLevelFromOrdinal, int topLevelToOrdinal) =>
        _elementNode.Prepare(topLevelFromOrdinal, topLevelToOrdinal);

    /// <inheritdoc />
    public override int TraverseDiffs(IntList fromOrdinals, IntList toOrdinals)
    {
        CollectElements(fromOrdinals, toOrdinals);
        _referenceFilter.Filter(_traversalFromOrdinals, _traversalToOrdinals);

        int score = 0;

        if (_referenceFilter.UnmatchedFromOrdinals.Count != 0
            || _referenceFilter.UnmatchedToOrdinals.Count != 0)
        {
            score += _elementNode.TraverseDiffs(
                _referenceFilter.UnmatchedFromOrdinals, _referenceFilter.UnmatchedToOrdinals);
        }

        if (_requiresTraversalForMissingFields
            && (_referenceFilter.MatchedFromOrdinals.Count != 0
                || _referenceFilter.MatchedToOrdinals.Count != 0))
        {
            score += _elementNode.TraverseMissingFields(
                _referenceFilter.MatchedFromOrdinals, _referenceFilter.MatchedToOrdinals);
        }

        return score;
    }

    /// <inheritdoc />
    public override int TraverseMissingFields(IntList fromOrdinals, IntList toOrdinals)
    {
        CollectElements(fromOrdinals, toOrdinals);

        return _elementNode.TraverseMissingFields(_traversalFromOrdinals, _traversalToOrdinals);
    }

    /// <inheritdoc />
    public override IReadOnlyList<HollowFieldDiff> GetFieldDiffs() => _elementNode.GetFieldDiffs();

    private void CollectElements(IntList fromOrdinals, IntList toOrdinals)
    {
        ArgumentNullException.ThrowIfNull(fromOrdinals);
        ArgumentNullException.ThrowIfNull(toOrdinals);

        _traversalFromOrdinals.Clear();
        _traversalToOrdinals.Clear();

        Collect(_fromState, fromOrdinals, _traversalFromOrdinals);
        Collect(_toState, toOrdinals, _traversalToOrdinals);
    }

    private static void Collect(
        IHollowCollectionTypeDataAccess? typeState, IntList ordinals, IntList fillList)
    {
        if (typeState is null)
        {
            return;
        }

        for (int i = 0; i < ordinals.Count; i++)
        {
            foreach (int elementOrdinal in typeState.ElementOrdinals(ordinals.Get(i)))
            {
                fillList.Add(elementOrdinal);
            }
        }
    }
}

/// <summary>
/// The node for a map type, which pairs keys off against keys and values against values.
/// </summary>
/// <remarks>
/// Separately, which means a key moving to a different value is counted as a change in the value rather
/// than in the pairing. That is the same simplification the equality mapping makes, and for the same
/// reason: what a reader wants from a diff is which field moved.
/// </remarks>
public sealed class HollowDiffMapCountingNode : HollowDiffCountingNode
{
    private readonly IHollowMapTypeDataAccess? _fromState;
    private readonly IHollowMapTypeDataAccess? _toState;
    private readonly HollowDiffCountingNode _keyNode;
    private readonly HollowDiffCountingNode _valueNode;
    private readonly DiffEqualOrdinalFilter _keyFilter;
    private readonly DiffEqualOrdinalFilter _valueFilter;
    private readonly bool _keyRequiresTraversalForMissingFields;
    private readonly bool _valueRequiresTraversalForMissingFields;

    private readonly IntList _traversalFromKeyOrdinals = new();
    private readonly IntList _traversalToKeyOrdinals = new();
    private readonly IntList _traversalFromValueOrdinals = new();
    private readonly IntList _traversalToValueOrdinals = new();

    /// <summary>Builds a node over a map type.</summary>
    public HollowDiffMapCountingNode(
        HollowDiff? diff,
        HollowTypeDiff? topLevelTypeDiff,
        HollowDiffNodeIdentifier nodeId,
        HollowTypeReadState? fromState,
        HollowTypeReadState? toState)
        : base(diff, topLevelTypeDiff, nodeId)
    {
        _fromState = (IHollowMapTypeDataAccess?)fromState;
        _toState = (IHollowMapTypeDataAccess?)toState;

        HollowMapSchema schema = (_fromState ?? _toState)!.Schema;

        _keyNode = CreateChildNode(_fromState?.Schema.KeyTypeState, _toState?.Schema.KeyTypeState, "key");
        _valueNode = CreateChildNode(
            _fromState?.Schema.ValueTypeState, _toState?.Schema.ValueTypeState, "value");

        _keyFilter = new DiffEqualOrdinalFilter(
            EqualityMapping?.GetEqualOrdinalMap(schema.KeyType) ?? DiffEqualOrdinalMap.Empty);

        _valueFilter = new DiffEqualOrdinalFilter(
            EqualityMapping?.GetEqualOrdinalMap(schema.ValueType) ?? DiffEqualOrdinalMap.Empty);

        _keyRequiresTraversalForMissingFields =
            EqualityMapping?.RequiresMissingFieldTraversal(schema.KeyType) == true;

        _valueRequiresTraversalForMissingFields =
            EqualityMapping?.RequiresMissingFieldTraversal(schema.ValueType) == true;
    }

    /// <inheritdoc />
    public override void Prepare(int topLevelFromOrdinal, int topLevelToOrdinal)
    {
        _keyNode.Prepare(topLevelFromOrdinal, topLevelToOrdinal);
        _valueNode.Prepare(topLevelFromOrdinal, topLevelToOrdinal);
    }

    /// <inheritdoc />
    public override int TraverseDiffs(IntList fromOrdinals, IntList toOrdinals)
    {
        CollectEntries(fromOrdinals, toOrdinals);

        _keyFilter.Filter(_traversalFromKeyOrdinals, _traversalToKeyOrdinals);
        _valueFilter.Filter(_traversalFromValueOrdinals, _traversalToValueOrdinals);

        int score = 0;

        score += TraverseSide(_keyNode, _keyFilter, _keyRequiresTraversalForMissingFields);
        score += TraverseSide(_valueNode, _valueFilter, _valueRequiresTraversalForMissingFields);

        return score;
    }

    /// <inheritdoc />
    public override int TraverseMissingFields(IntList fromOrdinals, IntList toOrdinals)
    {
        CollectEntries(fromOrdinals, toOrdinals);

        return _keyNode.TraverseMissingFields(_traversalFromKeyOrdinals, _traversalToKeyOrdinals)
            + _valueNode.TraverseMissingFields(_traversalFromValueOrdinals, _traversalToValueOrdinals);
    }

    /// <inheritdoc />
    public override IReadOnlyList<HollowFieldDiff> GetFieldDiffs() =>
        [.. _keyNode.GetFieldDiffs(), .. _valueNode.GetFieldDiffs()];

    private static int TraverseSide(
        HollowDiffCountingNode node, DiffEqualOrdinalFilter filter, bool requiresMissingFieldTraversal)
    {
        int score = 0;

        if (filter.UnmatchedFromOrdinals.Count != 0 || filter.UnmatchedToOrdinals.Count != 0)
        {
            score += node.TraverseDiffs(filter.UnmatchedFromOrdinals, filter.UnmatchedToOrdinals);
        }

        if (requiresMissingFieldTraversal
            && (filter.MatchedFromOrdinals.Count != 0 || filter.MatchedToOrdinals.Count != 0))
        {
            score += node.TraverseMissingFields(filter.MatchedFromOrdinals, filter.MatchedToOrdinals);
        }

        return score;
    }

    private void CollectEntries(IntList fromOrdinals, IntList toOrdinals)
    {
        ArgumentNullException.ThrowIfNull(fromOrdinals);
        ArgumentNullException.ThrowIfNull(toOrdinals);

        _traversalFromKeyOrdinals.Clear();
        _traversalToKeyOrdinals.Clear();
        _traversalFromValueOrdinals.Clear();
        _traversalToValueOrdinals.Clear();

        Collect(_fromState, fromOrdinals, _traversalFromKeyOrdinals, _traversalFromValueOrdinals);
        Collect(_toState, toOrdinals, _traversalToKeyOrdinals, _traversalToValueOrdinals);
    }

    private static void Collect(
        IHollowMapTypeDataAccess? typeState, IntList ordinals, IntList fillKeys, IntList fillValues)
    {
        if (typeState is null)
        {
            return;
        }

        for (int i = 0; i < ordinals.Count; i++)
        {
            foreach (HollowMapEntry entry in typeState.Entries(ordinals.Get(i)))
            {
                fillKeys.Add(entry.KeyOrdinal);
                fillValues.Add(entry.ValueOrdinal);
            }
        }
    }
}
