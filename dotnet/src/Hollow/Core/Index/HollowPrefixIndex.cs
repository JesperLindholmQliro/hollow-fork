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
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Iterator;
using Hollow.Core.Schema;
using Hollow.Core.Util;

namespace Hollow.Core.Index;

/// <summary>
/// Finds the records whose indexed string starts with a given prefix — the index behind an
/// autocomplete box.
/// </summary>
/// <remarks>
/// <para>
/// Backed by a ternary search tree whose whole node capacity is allocated up front from an estimate of
/// how many nodes the keys will need. A node reserves room for one record ordinal by default and grows
/// as duplicate keys turn up, so a caller who knows roughly how many records share a key should say so
/// — the growth copies the whole ordinal store each time.
/// </para>
/// <para>
/// <strong>Netflix marks this deprecated</strong>, as experimental and discontinued over its memory
/// efficiency, suggesting repeated lookups into a <see cref="HollowUniqueKeyIndex"/> instead. It is
/// ported for completeness and carries the same caveat: the tree is large, and it is rebuilt from
/// scratch on every delta that touches the indexed type.
/// </para>
/// </remarks>
public sealed class HollowPrefixIndex : IHollowTypeStateListener, IDisposable
{
    private readonly HollowReadStateEngine _readStateEngine;
    private readonly string _type;
    private readonly ValueFieldPath _fieldPath;
    private readonly int _estimatedMaxStringDuplicates;
    private readonly Func<IEnumerable<string>, IEnumerable<string>> _tokenizer;
    private readonly IArraySegmentRecycler _memoryRecycler = WastefulRecycler.DefaultInstance;

    private volatile TernarySearchTree _prefixIndex;

    private bool _rebuildOnNextUpdate;
    private bool _disposed;

    /// <summary>
    /// Indexes the strings <paramref name="fieldPath"/> reaches from <paramref name="type"/>.
    /// </summary>
    /// <param name="readStateEngine">The state to index.</param>
    /// <param name="type">The type whose ordinals a query returns.</param>
    /// <param name="fieldPath">
    /// The dot-separated path to a string field. It may cross a list, set or map, in which case one
    /// record is indexed under several keys.
    /// </param>
    /// <param name="estimatedMaxStringDuplicates">
    /// How many records are expected to share an exactly equal key. A higher value reserves more room
    /// per tree node up front, avoiding the copy that growing costs.
    /// </param>
    /// <param name="caseSensitive">
    /// Whether indexing and querying preserve case. When <see langword="false"/>, keys and queries are
    /// lowercased with the invariant culture.
    /// </param>
    /// <param name="tokenizer">
    /// Turns a record's strings into the keys it is indexed under, or <see langword="null"/> to index
    /// each string whole. Splitting on whitespace here is what makes a query match a word anywhere in
    /// a title rather than only at the start of it.
    /// </param>
    /// <exception cref="ArgumentException">The path does not lead to a string field.</exception>
    public HollowPrefixIndex(
        HollowReadStateEngine readStateEngine,
        string type,
        string fieldPath,
        int estimatedMaxStringDuplicates = 1,
        bool caseSensitive = false,
        Func<IEnumerable<string>, IEnumerable<string>>? tokenizer = null)
    {
        ArgumentNullException.ThrowIfNull(readStateEngine);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrEmpty(fieldPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(estimatedMaxStringDuplicates, 1);

        _readStateEngine = readStateEngine;
        _type = type;
        _estimatedMaxStringDuplicates = estimatedMaxStringDuplicates;
        _tokenizer = tokenizer ?? (static keys => keys);
        _fieldPath = new ValueFieldPath(readStateEngine, type, fieldPath);

        if (_fieldPath.LastFieldType != FieldType.String)
        {
            throw new ArgumentException(
                $"The field path has to lead to a string field, not a {_fieldPath.LastFieldType} one.",
                nameof(fieldPath));
        }

        CaseSensitive = caseSensitive;
        _prefixIndex = Build();
    }

    /// <summary>Whether indexing and querying preserve case.</summary>
    public bool CaseSensitive { get; }

    /// <summary>
    /// The ordinals of every record indexed under a key starting with <paramref name="prefix"/>.
    /// </summary>
    /// <remarks>
    /// An empty prefix returns every indexed ordinal. Shorter prefixes match more records, so a large
    /// dataset queried with one character returns a great many.
    /// </remarks>
    public IHollowOrdinalIterator FindKeysWithPrefix(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        TernarySearchTree current;
        IHollowOrdinalIterator iterator;

        // A delta rebuild replaces the whole tree, so a query that spans one re-runs against the new
        // tree rather than reading a half-recycled one.
        do
        {
            current = _prefixIndex;
            iterator = current.FindKeysWithPrefix(prefix);
        }
        while (!ReferenceEquals(current, _prefixIndex));

        return iterator;
    }

    /// <summary>
    /// The ordinals indexed under the longest indexed key that is a prefix of <paramref name="key"/>.
    /// </summary>
    /// <remarks>
    /// This matches whole indexed keys, not partial ones. With <c>abc</c> and <c>abcd</c> indexed,
    /// <c>abce</c> matches <c>abc</c> and <c>ab</c> matches nothing. More than one ordinal comes back
    /// only where several records share that key.
    /// </remarks>
    public IReadOnlyList<int> FindLongestMatch(string? key)
    {
        TernarySearchTree current;
        IReadOnlyList<int> ordinals;

        do
        {
            current = _prefixIndex;
            ordinals = current.GetOrdinals(current.FindLongestMatch(key));
        }
        while (!ReferenceEquals(current, _prefixIndex));

        return ordinals;
    }

    /// <summary>Whether <paramref name="key"/> was indexed in full.</summary>
    public bool Contains(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        TernarySearchTree current;
        bool result;

        do
        {
            current = _prefixIndex;
            result = current.Contains(key);
        }
        while (!ReferenceEquals(current, _prefixIndex));

        return result;
    }

    /// <summary>
    /// What the tree cost to build, as a guide to whether the estimates it was given were sensible.
    /// </summary>
    public PrefixIndexStats UsageStats()
    {
        TernarySearchTree current = _prefixIndex;

        return new PrefixIndexStats(
            current.MaxNodes,
            current.NodesUsed,
            current.EmptyNodes,
            current.MaxDepth,
            current.MaxElementsPerNode,
            current.ApproxHeapFootprintInBytes);
    }

    /// <summary>
    /// Keeps this index in step with deltas applied to the read state.
    /// </summary>
    /// <remarks>
    /// Each delta that touches the indexed type rebuilds the tree from scratch and swaps it in, which
    /// is expensive. The tree has no way to remove a key.
    /// </remarks>
    public void ListenForDeltaUpdates() => _readStateEngine.GetTypeState(_type)?.AddListener(this);

    /// <summary>Stops following delta updates.</summary>
    public void DetachFromDeltaUpdates() => _readStateEngine.GetTypeState(_type)?.RemoveListener(this);

    /// <inheritdoc />
    public void BeginUpdate()
    {
        // Nothing to do until the delta says whether anything in this type moved.
    }

    /// <inheritdoc />
    public void AddedOrdinal(int ordinal) => _rebuildOnNextUpdate = true;

    /// <inheritdoc />
    public void RemovedOrdinal(int ordinal) => _rebuildOnNextUpdate = true;

    /// <inheritdoc />
    public void EndUpdate()
    {
        if (!_rebuildOnNextUpdate)
        {
            return;
        }

        TernarySearchTree previous = _prefixIndex;

        _prefixIndex = Build();
        _rebuildOnNextUpdate = false;

        // Java releases the old tree's segments before building the new one, so that the new one can
        // reuse them; that leaves a concurrent query reading storage already handed back. This waits
        // until the new tree is published, at the cost of the reuse happening a cycle later.
        previous.RecycleMemory(_memoryRecycler);
        _memoryRecycler.Swap();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        DetachFromDeltaUpdates();
        _prefixIndex.RecycleMemory(_memoryRecycler);
        _disposed = true;
    }

    /// <summary>
    /// The keys <paramref name="ordinal"/>'s record is indexed under.
    /// </summary>
    /// <remarks>
    /// A record whose path reaches a null or empty string contributes no key and so is not indexed;
    /// Java throws on a null there instead. Java makes this a <c>protected</c> method to override for
    /// tokenizing; the port takes a delegate, so the class need not be subclassed.
    /// </remarks>
    private IEnumerable<string> GetKeys(int ordinal) =>
        _tokenizer(_fieldPath.FindValues(ordinal).OfType<string>())
            .Where(static key => !string.IsNullOrEmpty(key));

    private TernarySearchTree Build()
    {
        BitSet ordinals = _readStateEngine.GetTypeState(_type)?.PopulatedOrdinals ?? new BitSet();
        int maxOrdinal = _readStateEngine.GetTypeState(_type)?.MaxOrdinal ?? 0;

        // The node capacity is a hard limit, so it is sized for the worst case: a tree so unbalanced
        // that every character of every key gets its own node.
        long totalKeys = 0;
        long totalKeyLength = 0;

        foreach (int ordinal in ordinals.EnumerateSetBits())
        {
            foreach (string key in GetKeys(ordinal))
            {
                totalKeys++;
                totalKeyLength += key.Length;
            }
        }

        long averageKeyLength = totalKeys == 0 ? 0 : (long)Math.Ceiling((double)totalKeyLength / totalKeys);
        long estimatedMaxNodes = Math.Max(1, EstimateNumNodes(totalKeys, averageKeyLength));

        TernarySearchTree tree = new(
            estimatedMaxNodes,
            _estimatedMaxStringDuplicates,
            Math.Max(maxOrdinal, 0),
            CaseSensitive,
            _memoryRecycler);

        foreach (int ordinal in ordinals.EnumerateSetBits())
        {
            foreach (string key in GetKeys(ordinal))
            {
                tree.Insert(key, ordinal);
            }
        }

        return tree;
    }

    /// <summary>
    /// How many tree nodes to reserve, which is a hard limit the build cannot exceed.
    /// </summary>
    /// <remarks>
    /// The total length of every key, which is what a completely unbalanced tree would need. Java
    /// derives the average from the records of the type at the end of the path rather than from the
    /// keys themselves, which misses a key reached across a collection and reads the wrong field when
    /// the path ends at an inline string.
    /// </remarks>
    private static long EstimateNumNodes(long totalKeys, long averageKeyLength) =>
        totalKeys * averageKeyLength;
}

/// <summary>
/// What a <see cref="HollowPrefixIndex"/>'s tree cost to build.
/// </summary>
/// <param name="NodesCapacity">How many nodes were reserved up front.</param>
/// <param name="NodesUsed">How many of them the keys actually needed.</param>
/// <param name="NodesEmpty">How many went unused, which is capacity the estimate over-reserved.</param>
/// <param name="WorstCaseLookups">
/// The deepest path in the tree, which is how many nodes the slowest query visits.
/// </param>
/// <param name="MaxValuesPerNode">
/// How many record ordinals a single node has room for, which grew from the estimate given if
/// duplicate keys forced it to.
/// </param>
/// <param name="ApproxHeapFootprintInBytes">An approximation of the memory the tree occupies.</param>
public sealed record PrefixIndexStats(
    long NodesCapacity,
    long NodesUsed,
    long NodesEmpty,
    long WorstCaseLookups,
    int MaxValuesPerNode,
    long ApproxHeapFootprintInBytes)
{
    /// <inheritdoc />
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"nodesCapacity={NodesCapacity}, nodesUsed={NodesUsed}, nodesEmpty={NodesEmpty}, "
            + $"worstCaseLookups={WorstCaseLookups}, maxValuesPerNode={MaxValuesPerNode}, "
            + $"approxHeapFootprintInBytes={ApproxHeapFootprintInBytes}");
}
