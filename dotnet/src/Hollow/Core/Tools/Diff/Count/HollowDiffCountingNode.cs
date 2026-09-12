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
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Schema;
using Hollow.Core.Tools.Diff.Exact;
using Hollow.Core.Util;

namespace Hollow.Core.Tools.Diff.Count;

/// <summary>
/// One node of the tree that walks a pair of differing records and works out which fields differ.
/// </summary>
/// <remarks>
/// <para>
/// The tree mirrors the data model: one node per type reachable from the type being diffed, built once
/// and then driven over every record pair. Each node is handed the ordinals reached on each side, pairs
/// off whatever is exactly equal, and passes only the remainder down.
/// </para>
/// <para>
/// Nodes hold their scratch lists between records, so a diff over a million records is not a million
/// allocations per branch. That makes a node single-use at a time, which is why Java builds one tree
/// per thread.
/// </para>
/// </remarks>
public abstract class HollowDiffCountingNode
{
    /// <summary>The empty list handed to a side that has no such field at all.</summary>
    protected static readonly IntList EmptyOrdinalList = new(1);

    private readonly HollowDiff? _diff;
    private readonly HollowTypeDiff? _topLevelTypeDiff;

    /// <summary>Builds a node identified by <paramref name="nodeId"/>.</summary>
    protected HollowDiffCountingNode(
        HollowDiff? diff, HollowTypeDiff? topLevelTypeDiff, HollowDiffNodeIdentifier? nodeId)
    {
        _diff = diff;
        _topLevelTypeDiff = topLevelTypeDiff;
        EqualityMapping = diff?.EqualityMapping;
        NodeId = nodeId;
    }

    /// <summary>Which records are exactly equal across the two states.</summary>
    protected DiffEqualityMapping? EqualityMapping { get; }

    /// <summary>Where this node sits in the type's hierarchy.</summary>
    protected HollowDiffNodeIdentifier? NodeId { get; }

    /// <summary>Tells the node which top-level record pair the ordinals it is about to see belong to.</summary>
    public abstract void Prepare(int topLevelFromOrdinal, int topLevelToOrdinal);

    /// <summary>
    /// Accounts for the difference between the records reached on each side.
    /// </summary>
    /// <returns>How much they differ.</returns>
    public abstract int TraverseDiffs(IntList fromOrdinals, IntList toOrdinals);

    /// <summary>
    /// Accounts only for fields one of the two schemas does not have, over records already known to be
    /// equal on the fields they share.
    /// </summary>
    public abstract int TraverseMissingFields(IntList fromOrdinals, IntList toOrdinals);

    /// <summary>What this node and everything below it found.</summary>
    public abstract IReadOnlyList<HollowFieldDiff> GetFieldDiffs();

    /// <summary>
    /// The node for a type reached through <paramref name="viaFieldName"/>.
    /// </summary>
    protected HollowDiffCountingNode CreateChildNode(
        HollowTypeReadState? refFromState, HollowTypeReadState? refToState, string viaFieldName)
    {
        // Neither state has the type, so there is nothing below this to account for.
        if (refFromState is null && refToState is null)
        {
            return HollowDiffMissingCountingNode.Instance;
        }

        HollowSchema elementSchema = (refFromState ?? refToState)!.Schema;
        HollowDiffNodeIdentifier childNodeId = new(NodeId, viaFieldName, elementSchema.Name);

        // A type the caller asked to stop at is counted whole rather than walked into, which is the
        // trade the caller made: less detail, and a diff that finishes.
        if (_topLevelTypeDiff?.IsShortcutType(elementSchema.Name) == true)
        {
            return new HollowDiffShortcutTypeCountingNode(_diff, _topLevelTypeDiff, childNodeId);
        }

        return elementSchema switch
        {
            HollowObjectSchema => new HollowDiffObjectCountingNode(
                _diff,
                _topLevelTypeDiff,
                childNodeId,
                (HollowObjectTypeReadState?)refFromState,
                (HollowObjectTypeReadState?)refToState),

            HollowMapSchema => new HollowDiffMapCountingNode(
                _diff, _topLevelTypeDiff, childNodeId, refFromState, refToState),

            HollowCollectionSchema => new HollowDiffCollectionCountingNode(
                _diff, _topLevelTypeDiff, childNodeId, refFromState, refToState),

            _ => throw new ArgumentException(
                $"{elementSchema.Name} is a {elementSchema.GetType().Name}, which is not a kind of record "
                + "this knows how to walk",
                nameof(refFromState)),
        };
    }
}

/// <summary>
/// The node for a type neither state has, which accounts for nothing.
/// </summary>
public sealed class HollowDiffMissingCountingNode : HollowDiffCountingNode
{
    /// <summary>The shared instance, since it holds nothing.</summary>
    public static readonly HollowDiffMissingCountingNode Instance = new();

    private HollowDiffMissingCountingNode()
        : base(null, null, null)
    {
    }

    /// <inheritdoc />
    public override void Prepare(int topLevelFromOrdinal, int topLevelToOrdinal)
    {
    }

    /// <inheritdoc />
    public override int TraverseDiffs(IntList fromOrdinals, IntList toOrdinals) => 0;

    /// <inheritdoc />
    public override int TraverseMissingFields(IntList fromOrdinals, IntList toOrdinals) => 0;

    /// <inheritdoc />
    public override IReadOnlyList<HollowFieldDiff> GetFieldDiffs() => [];
}

/// <summary>
/// The node for a type the caller asked the diff to stop at.
/// </summary>
/// <remarks>
/// Records reached here are counted rather than compared, so the diff reports that something under this
/// branch moved without saying what. For a type whose records are large and whose detail is beside the
/// point, that is the difference between a diff that finishes and one that does not.
/// </remarks>
public sealed class HollowDiffShortcutTypeCountingNode(
    HollowDiff? diff, HollowTypeDiff? topLevelTypeDiff, HollowDiffNodeIdentifier? nodeId)
    : HollowDiffCountingNode(diff, topLevelTypeDiff, nodeId)
{
    private readonly HollowFieldDiff _fieldDiff = new(nodeId!);

    private int _currentTopLevelFromOrdinal;
    private int _currentTopLevelToOrdinal;

    /// <inheritdoc />
    public override void Prepare(int topLevelFromOrdinal, int topLevelToOrdinal)
    {
        _currentTopLevelFromOrdinal = topLevelFromOrdinal;
        _currentTopLevelToOrdinal = topLevelToOrdinal;
    }

    /// <inheritdoc />
    public override int TraverseDiffs(IntList fromOrdinals, IntList toOrdinals) =>
        AddResult(fromOrdinals, toOrdinals);

    /// <inheritdoc />
    public override int TraverseMissingFields(IntList fromOrdinals, IntList toOrdinals) =>
        AddResult(fromOrdinals, toOrdinals);

    /// <inheritdoc />
    public override IReadOnlyList<HollowFieldDiff> GetFieldDiffs() =>
        _fieldDiff.TotalDiffScore > 0 ? [_fieldDiff] : [];

    private int AddResult(IntList fromOrdinals, IntList toOrdinals)
    {
        ArgumentNullException.ThrowIfNull(fromOrdinals);
        ArgumentNullException.ThrowIfNull(toOrdinals);

        int score = fromOrdinals.Count + toOrdinals.Count;

        if (score != 0)
        {
            _fieldDiff.AddDiff(_currentTopLevelFromOrdinal, _currentTopLevelToOrdinal, score);
        }

        return score;
    }
}
