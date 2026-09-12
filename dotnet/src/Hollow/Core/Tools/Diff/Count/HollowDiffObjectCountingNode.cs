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

using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Schema;
using Hollow.Core.Tools.Diff.Exact;
using Hollow.Core.Util;

namespace Hollow.Core.Tools.Diff.Count;

/// <summary>
/// The node for an object type: one child per field of the two schemas' union.
/// </summary>
/// <remarks>
/// The <em>union</em> rather than the intersection, because a field only one side has is a difference
/// worth reporting rather than one to skip. Where a field is missing from a side, that side is handed
/// an empty list and everything on the other counts.
/// </remarks>
public sealed class HollowDiffObjectCountingNode : HollowDiffCountingNode
{
    private readonly HollowObjectTypeReadState? _fromState;
    private readonly HollowObjectTypeReadState? _toState;
    private readonly HollowObjectSchema _unionSchema;

    private readonly int[] _fromFieldMapping;
    private readonly int[] _toFieldMapping;
    private readonly HollowDiffCountingNode[] _fieldNodes;
    private readonly bool[] _fieldRequiresMissingFieldTraversal;
    private readonly DiffEqualOrdinalFilter?[] _fieldEqualOrdinalFilters;

    private readonly IntList _traversalFromOrdinals = new();
    private readonly IntList _traversalToOrdinals = new();

    /// <summary>Builds a node over an object type.</summary>
    /// <exception cref="ArgumentException">The two states are of differently named types.</exception>
    public HollowDiffObjectCountingNode(
        HollowDiff? diff,
        HollowTypeDiff? topLevelTypeDiff,
        HollowDiffNodeIdentifier nodeId,
        HollowObjectTypeReadState? fromState,
        HollowObjectTypeReadState? toState)
        : base(diff, topLevelTypeDiff, nodeId)
    {
        ArgumentNullException.ThrowIfNull(nodeId);

        _fromState = fromState;
        _toState = toState;

        // A type only one side has is treated as present but empty, so that every one of its fields
        // counts as missing rather than the whole type being skipped.
        HollowObjectSchema fromSchema = fromState?.Schema ?? EmptySchema(toState!.Schema);
        HollowObjectSchema toSchema = toState?.Schema ?? EmptySchema(fromState!.Schema);

        if (!string.Equals(fromSchema.Name, toSchema.Name, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"cannot diff {fromSchema.Name} against {toSchema.Name}, which are different types",
                nameof(fromState));
        }

        _unionSchema = fromSchema.FindUnionSchema(toSchema);
        _fieldNodes = new HollowDiffCountingNode[_unionSchema.FieldCount];
        _fromFieldMapping = CreateFieldMapping(_unionSchema, fromSchema);
        _toFieldMapping = CreateFieldMapping(_unionSchema, toSchema);
        _fieldRequiresMissingFieldTraversal = new bool[_unionSchema.FieldCount];
        _fieldEqualOrdinalFilters = new DiffEqualOrdinalFilter?[_unionSchema.FieldCount];

        for (int i = 0; i < _unionSchema.FieldCount; i++)
        {
            if (_unionSchema.GetFieldType(i) != FieldType.Reference)
            {
                HollowDiffNodeIdentifier childNodeId =
                    new(nodeId, _unionSchema.GetFieldName(i), _unionSchema.GetFieldType(i).ToString());

                _fieldNodes[i] = new HollowDiffFieldCountingNode(
                    diff, topLevelTypeDiff, childNodeId, fromState, toState, _unionSchema, i);

                continue;
            }

            HollowTypeReadState? refFromState = _fromFieldMapping[i] == -1
                ? null
                : fromSchema.GetReferencedTypeState(_fromFieldMapping[i]);

            HollowTypeReadState? refToState = _toFieldMapping[i] == -1
                ? null
                : toSchema.GetReferencedTypeState(_toFieldMapping[i]);

            string referencedType = _unionSchema.GetReferencedType(i)!;

            _fieldNodes[i] = CreateChildNode(refFromState, refToState, _unionSchema.GetFieldName(i));
            _fieldEqualOrdinalFilters[i] = new DiffEqualOrdinalFilter(
                EqualityMapping?.GetEqualOrdinalMap(referencedType) ?? DiffEqualOrdinalMap.Empty);

            _fieldRequiresMissingFieldTraversal[i] =
                refFromState is null
                || refToState is null
                || EqualityMapping?.RequiresMissingFieldTraversal(referencedType) == true;
        }
    }

    /// <inheritdoc />
    public override void Prepare(int topLevelFromOrdinal, int topLevelToOrdinal)
    {
        foreach (HollowDiffCountingNode fieldNode in _fieldNodes)
        {
            fieldNode.Prepare(topLevelFromOrdinal, topLevelToOrdinal);
        }
    }

    /// <inheritdoc />
    public override int TraverseDiffs(IntList fromOrdinals, IntList toOrdinals)
    {
        ArgumentNullException.ThrowIfNull(fromOrdinals);
        ArgumentNullException.ThrowIfNull(toOrdinals);

        int score = 0;

        for (int i = 0; i < _fieldNodes.Length; i++)
        {
            if (_unionSchema.GetFieldType(i) != FieldType.Reference)
            {
                score += _fieldNodes[i].TraverseDiffs(
                    _fromFieldMapping[i] == -1 ? EmptyOrdinalList : fromOrdinals,
                    _toFieldMapping[i] == -1 ? EmptyOrdinalList : toOrdinals);

                continue;
            }

            CollectReferencedOrdinals(fromOrdinals, toOrdinals, i);

            if (_traversalFromOrdinals.Count == 0 && _traversalToOrdinals.Count == 0)
            {
                continue;
            }

            DiffEqualOrdinalFilter filter = _fieldEqualOrdinalFilters[i]!;
            filter.Filter(_traversalFromOrdinals, _traversalToOrdinals);

            // Whatever did not pair off is where the difference is.
            if (filter.UnmatchedFromOrdinals.Count != 0 || filter.UnmatchedToOrdinals.Count != 0)
            {
                score += _fieldNodes[i].TraverseDiffs(
                    filter.UnmatchedFromOrdinals, filter.UnmatchedToOrdinals);
            }

            // What did pair off is still worth walking where a field could be missing from one schema,
            // since the equality map only compared the fields they share.
            if (_fieldRequiresMissingFieldTraversal[i]
                && (filter.MatchedFromOrdinals.Count != 0 || filter.MatchedToOrdinals.Count != 0))
            {
                score += _fieldNodes[i].TraverseMissingFields(
                    filter.MatchedFromOrdinals, filter.MatchedToOrdinals);
            }
        }

        return score;
    }

    /// <inheritdoc />
    public override int TraverseMissingFields(IntList fromOrdinals, IntList toOrdinals)
    {
        ArgumentNullException.ThrowIfNull(fromOrdinals);
        ArgumentNullException.ThrowIfNull(toOrdinals);

        int score = 0;

        for (int i = 0; i < _fieldNodes.Length; i++)
        {
            if (_fieldRequiresMissingFieldTraversal[i])
            {
                CollectReferencedOrdinals(fromOrdinals, toOrdinals, i);
                score += _fieldNodes[i].TraverseMissingFields(_traversalFromOrdinals, _traversalToOrdinals);
            }
            else if (_fieldNodes[i] is HollowDiffFieldCountingNode)
            {
                // A value field's own presence is the thing being checked, so it is handed the records
                // rather than anything they reference.
                score += _fieldNodes[i].TraverseMissingFields(fromOrdinals, toOrdinals);
            }
        }

        return score;
    }

    /// <inheritdoc />
    public override IReadOnlyList<HollowFieldDiff> GetFieldDiffs() =>
        [.. _fieldNodes.SelectMany(node => node.GetFieldDiffs())];

    private static HollowObjectSchema EmptySchema(HollowObjectSchema other) => new(other.Name, 0);

    private static int[] CreateFieldMapping(HollowObjectSchema unionSchema, HollowObjectSchema schema)
    {
        int[] mapping = new int[unionSchema.FieldCount];

        for (int i = 0; i < mapping.Length; i++)
        {
            mapping[i] = schema.GetPosition(unionSchema.GetFieldName(i));
        }

        return mapping;
    }

    private void CollectReferencedOrdinals(IntList fromOrdinals, IntList toOrdinals, int fieldIndex)
    {
        _traversalFromOrdinals.Clear();
        _traversalToOrdinals.Clear();

        if (_fromFieldMapping[fieldIndex] != -1)
        {
            for (int i = 0; i < fromOrdinals.Count; i++)
            {
                int refOrdinal = _fromState!.ReadOrdinal(fromOrdinals.Get(i), _fromFieldMapping[fieldIndex]);

                if (refOrdinal != HollowConstants.OrdinalNone)
                {
                    _traversalFromOrdinals.Add(refOrdinal);
                }
            }
        }

        if (_toFieldMapping[fieldIndex] != -1)
        {
            for (int i = 0; i < toOrdinals.Count; i++)
            {
                int refOrdinal = _toState!.ReadOrdinal(toOrdinals.Get(i), _toFieldMapping[fieldIndex]);

                if (refOrdinal != HollowConstants.OrdinalNone)
                {
                    _traversalToOrdinals.Add(refOrdinal);
                }
            }
        }
    }
}
