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

using Hollow.Api.Error;
using Hollow.Core.Schema;

namespace Hollow.Core.Write;

/// <summary>
/// The producer-side counterpart of <see cref="Read.Engine.HollowReadStateEngine"/>: accumulates the
/// records of every type over a cycle, then serialises them as a snapshot blob.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Port note.</strong> Delta and reverse-delta production, restoring from a prior read state,
/// and the parallel snapshot calculation are not ported — see <c>PORTING.md</c>.
/// </para>
/// </remarks>
public sealed class HollowWriteStateEngine : IHollowDataset
{
    /// <summary>
    /// The default size a single type shard is allowed to reach before the type is split, in bytes.
    /// </summary>
    public const long DefaultTargetMaxTypeShardSize = 16L * 1024 * 1024;

    private readonly Dictionary<string, HollowTypeWriteState> _typeStates = new(StringComparer.Ordinal);
    private readonly List<HollowTypeWriteState> _orderedTypeStates = [];

    /// <summary>The producer header tags written into the blob.</summary>
    public Dictionary<string, string> HeaderTags { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The size a single type shard is allowed to reach before the type is split across more shards.
    /// </summary>
    public long TargetMaxTypeShardSize { get; set; } = DefaultTargetMaxTypeShardSize;

    /// <summary>
    /// Whether to concentrate reclaimed ordinal holes in as few shards as possible, which keeps deltas
    /// smaller at the cost of a less even distribution.
    /// </summary>
    public bool FocusHoleFillInFewestShards { get; set; }

    /// <summary>The random tag identifying the state this engine produces.</summary>
    public long RandomizedTag { get; set; }

    /// <summary>
    /// The random tag of the state the previous cycle produced, which a delta names as its origin so
    /// that it cannot be applied to the wrong state.
    /// </summary>
    public long PreviousRandomizedTag { get; private set; }

    /// <summary>The type states in the order they were added.</summary>
    public IReadOnlyList<HollowTypeWriteState> OrderedTypeStates => _orderedTypeStates;

    /// <inheritdoc />
    public IReadOnlyList<HollowSchema> Schemas => [.. _orderedTypeStates.Select(state => state.Schema)];

    /// <summary>
    /// Registers a type with this engine.
    /// </summary>
    /// <exception cref="ArgumentException">A type of the same name is already registered.</exception>
    public void AddTypeState(HollowTypeWriteState typeState)
    {
        ArgumentNullException.ThrowIfNull(typeState);

        if (!_typeStates.TryAdd(typeState.Schema.Name, typeState))
        {
            throw new ArgumentException(
                $"Type {typeState.Schema.Name} is already added to this write state engine", nameof(typeState));
        }

        typeState.StateEngine = this;
        _orderedTypeStates.Add(typeState);
    }

    /// <summary>
    /// Gets the write state of the named type, or <see langword="null"/> when it is not registered.
    /// </summary>
    public HollowTypeWriteState? GetTypeState(string typeName) => _typeStates.GetValueOrDefault(typeName);

    /// <inheritdoc />
    public HollowSchema? GetSchema(string typeName) => GetTypeState(typeName)?.Schema;

    /// <inheritdoc />
    public HollowSchema GetNonNullSchema(string typeName) =>
        GetSchema(typeName) ?? throw new SchemaNotFoundException(typeName, _typeStates.Keys);

    /// <summary>
    /// Adds a record to the named type, returning the ordinal it was assigned.
    /// </summary>
    /// <exception cref="SchemaNotFoundException">The type is not registered.</exception>
    public int Add(string typeName, IHollowWriteRecord record)
    {
        HollowTypeWriteState typeState = GetTypeState(typeName)
            ?? throw new SchemaNotFoundException(typeName, _typeStates.Keys);

        return typeState.Add(record);
    }

    /// <summary>
    /// Rolls every type forward to the next cycle, discarding records no longer referenced.
    /// </summary>
    public void PrepareForNextCycle()
    {
        PreviousRandomizedTag = RandomizedTag;

        foreach (HollowTypeWriteState typeState in _orderedTypeStates)
        {
            typeState.PrepareForNextCycle();
        }
    }

    /// <summary>
    /// Finalises ordinal assignment and the shard layout for every type.
    /// </summary>
    public void PrepareForWrite()
    {
        foreach (HollowTypeWriteState typeState in _orderedTypeStates)
        {
            typeState.PrepareForWrite();
        }
    }

    /// <summary>
    /// Adds a header tag to be written into the blob.
    /// </summary>
    public void AddHeaderTag(string name, string value) => HeaderTags[name] = value;

    /// <summary>
    /// Gets a header tag, or <see langword="null"/> when it is not set.
    /// </summary>
    public string? GetHeaderTag(string name) => HeaderTags.GetValueOrDefault(name);
}
