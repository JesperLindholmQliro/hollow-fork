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
using Hollow.Core.Read.Engine;
using Hollow.Core.Schema;
using Hollow.Core.Util;

namespace Hollow.Core.Write;

/// <summary>
/// The producer-side counterpart of <see cref="Read.Engine.HollowReadStateEngine"/>: accumulates the
/// records of every type over a cycle, then serialises them as a snapshot blob.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Port note.</strong> The parallel snapshot calculation is not ported — see <c>PORTING.md</c>.
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

    /// <summary>
    /// Whether this engine is in the "adding records" phase rather than the "writing" phase. A fresh
    /// engine starts in the adding phase.
    /// </summary>
    private bool _preparedForNextCycle = true;

    /// <summary>The producer header tags written into the blob.</summary>
    public Dictionary<string, string> HeaderTags { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The header tags as they stood at the end of the previous cycle, which a reverse delta carries
    /// because it describes a transition back to that state.
    /// </summary>
    public IReadOnlyDictionary<string, string> PreviousHeaderTags { get; private set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// The size a single type shard is allowed to reach before the type is split across more shards.
    /// </summary>
    public long TargetMaxTypeShardSize { get; set; } = DefaultTargetMaxTypeShardSize;

    /// <summary>
    /// Whether to concentrate reclaimed ordinal holes in as few shards as possible, which keeps deltas
    /// smaller at the cost of a less even distribution.
    /// </summary>
    public bool FocusHoleFillInFewestShards { get; set; }

    /// <summary>
    /// Whether a type may change its shard count from one cycle to the next as its data grows or
    /// shrinks past <see cref="TargetMaxTypeShardSize"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Off by default. With it off a type's shard count is fixed the first time it is written and every
    /// later delta keeps it, which is what a consumer built before resharding existed expects. With it
    /// on a delta may declare a different count, and a consumer rearranges its records to match before
    /// applying that delta — so only turn it on once every consumer of the chain can do that.
    /// </para>
    /// <para>
    /// A count changes by at most a factor of two per cycle, so a type that has badly outgrown its
    /// shards takes several cycles to get where it is going.
    /// </para>
    /// </remarks>
    public bool AllowTypeResharding { get; set; }

    /// <summary>
    /// The random tag identifying the state this engine produces.
    /// </summary>
    /// <remarks>
    /// A fresh tag is minted for each cycle, and a delta names the tag of the state it applies to, so
    /// that a consumer cannot apply it to the wrong state. Assign this to pin it — which is only
    /// useful for a test that compares blob bytes.
    /// </remarks>
    public long RandomizedTag { get; set; } = MintNewRandomizedTag(0);

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
    /// <remarks>
    /// A no-op when this engine is already accepting records, so that a caller need not track which
    /// phase of the cycle it is in. That matters after <see cref="RestoreFrom"/>, which leaves the
    /// engine ready to accept records: rolling forward again there would discard the restored ordinal
    /// assignment and the delta would then claim every record had changed.
    /// </remarks>
    public void PrepareForNextCycle()
    {
        if (_preparedForNextCycle)
        {
            return;
        }

        PreviousRandomizedTag = RandomizedTag;
        PreviousHeaderTags = new Dictionary<string, string>(HeaderTags, StringComparer.Ordinal);
        RandomizedTag = MintNewRandomizedTag(PreviousRandomizedTag);

        foreach (HollowTypeWriteState typeState in _orderedTypeStates)
        {
            typeState.PrepareForNextCycle();
        }

        _preparedForNextCycle = true;
        RestoredStates = null;
    }

    /// <summary>
    /// Discards everything added since the last <see cref="PrepareForNextCycle"/>, leaving this engine
    /// exactly as it was at the start of the cycle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A producer calls this when a cycle turns out to have nothing to publish, or when one fails part
    /// way through. Without it a failed cycle's records would linger in the ordinal map and the next
    /// cycle would produce a delta describing changes that were never published.
    /// </para>
    /// <para>
    /// A fresh <see cref="RandomizedTag"/> is minted, so that an abandoned version's blobs can never be
    /// mistaken for the ones the retried cycle produces.
    /// </para>
    /// </remarks>
    public void ResetToLastPrepareForNextCycle()
    {
        foreach (HollowTypeWriteState typeState in _orderedTypeStates)
        {
            typeState.ResetToLastPrepareForNextCycle();
        }

        RandomizedTag = MintNewRandomizedTag(PreviousRandomizedTag);
        _preparedForNextCycle = true;
    }

    /// <summary>
    /// Finalises ordinal assignment and the shard layout for every type, after which no more records
    /// may be added until the next cycle.
    /// </summary>
    /// <remarks>
    /// A no-op when this engine is already prepared, so that writing a snapshot and a delta for the
    /// same cycle costs the work only once.
    /// </remarks>
    /// <param name="canReshard">
    /// Whether this write may change a type's shard count, which it does only when
    /// <see cref="AllowTypeResharding"/> is also set. A caller preparing for something other than a
    /// blob — measuring the state, say — passes <see langword="false"/> so that the decision is left to
    /// the write that follows.
    /// </param>
    public void PrepareForWrite(bool canReshard = false)
    {
        if (!_preparedForNextCycle)
        {
            return;
        }

        foreach (HollowTypeWriteState typeState in _orderedTypeStates)
        {
            typeState.PrepareForWrite(canReshard);
        }

        _preparedForNextCycle = false;
    }

    /// <summary>
    /// Populates this engine from a published read state, so that it can continue that state's delta
    /// chain rather than starting a new one with a fresh snapshot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what a producer does on restart. The data model must already be registered — through
    /// <see cref="HollowWriteStateCreator"/>, the object mapper, or <see cref="AddTypeState"/> — and no
    /// records may have been added yet.
    /// </para>
    /// <para>
    /// After restoring, add this cycle's records as usual and write a delta: every record that has not
    /// changed keeps the ordinal the published state gave it, so the delta carries only real changes. A
    /// type present in the read state but not in the data model is ignored, and a type in the data
    /// model but not in the read state simply starts empty.
    /// </para>
    /// <para>
    /// <see cref="PreviousRandomizedTag"/> is taken from the read state and a fresh
    /// <see cref="RandomizedTag"/> is minted, which is what lets the resulting delta be recognised as
    /// applying to the published state. Assign <see cref="RandomizedTag"/> afterwards to pin it.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// A type's shard count differs between the read state and the registered write state, or a write
    /// state already holds records.
    /// </exception>
    public void RestoreFrom(HollowReadStateEngine readStateEngine)
    {
        ArgumentNullException.ThrowIfNull(readStateEngine);

        // Check every shard count before restoring anything: a mismatch leaves the engine untouched
        // rather than half-populated.
        foreach (HollowTypeReadState readState in readStateEngine.TypeStates.Values)
        {
            if (GetTypeState(readState.TypeName) is not { } writeState)
            {
                continue;
            }

            if (writeState.NumShards == -1)
            {
                writeState.SetNumShards(readState.NumShards);
            }
            else if (readState.NumShards != 0 && writeState.NumShards != readState.NumShards)
            {
                throw new InvalidOperationException(
                    $"Attempting to restore from a read state with {readState.NumShards.Invariant()} shards for "
                    + $"type {readState.TypeName} into a write state with {writeState.NumShards.Invariant()} shards.");
            }
        }

        List<string> restoredStates = [];

        foreach (HollowTypeReadState readState in readStateEngine.TypeStates.Values)
        {
            restoredStates.Add(readState.TypeName);
            GetTypeState(readState.TypeName)?.RestoreFrom(readState);
        }

        RestoredStates = restoredStates;

        PreviousRandomizedTag = readStateEngine.RandomizedTag;
        RandomizedTag = MintNewRandomizedTag(PreviousRandomizedTag);
        OverridePreviousHeaderTags(readStateEngine.HeaderTags);
    }

    /// <summary>
    /// The names of the types found in the read state the last <see cref="RestoreFrom"/> call read, or
    /// <see langword="null"/> when this engine was never restored.
    /// </summary>
    public IReadOnlyList<string>? RestoredStates { get; private set; }

    /// <summary>Whether this engine was restored and has not yet completed a cycle.</summary>
    public bool IsRestored => RestoredStates is not null;

    /// <summary>
    /// Verifies that every type the restored read state held and this engine also declares was actually
    /// restored.
    /// </summary>
    /// <remarks>
    /// A type registered <em>after</em> <see cref="RestoreFrom"/> ran carries none of the published
    /// state's ordinals, so a delta written from it would claim every one of its records is new and
    /// would not apply to the published state. Refusing here turns that into an error at write time
    /// rather than a corrupt delta chain.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Some declared type was not restored.</exception>
    internal void EnsureAllNecessaryStatesRestored()
    {
        if (RestoredStates is not { } restoredStates)
        {
            return;
        }

        List<string> unrestored = [.. _orderedTypeStates
            .Where(state => restoredStates.Contains(state.Schema.Name, StringComparer.Ordinal) && !state.IsRestored)
            .Select(state => state.Schema.Name)];

        if (unrestored.Count > 0)
        {
            throw new InvalidOperationException(
                $"The current state was restored but holds unrestored state for the types [{string.Join(", ", unrestored)}]. "
                + "Register every type before calling RestoreFrom.");
        }
    }

    /// <summary>
    /// Replaces the header tags a reverse delta will carry, without touching the tags this cycle writes.
    /// </summary>
    public void OverridePreviousHeaderTags(IReadOnlyDictionary<string, string> previousHeaderTags)
    {
        ArgumentNullException.ThrowIfNull(previousHeaderTags);

        PreviousHeaderTags = new Dictionary<string, string>(previousHeaderTags, StringComparer.Ordinal);
    }

    /// <summary>
    /// Mints a tag distinguishable from <paramref name="previousRandomizedTag"/> in its high 32 bits,
    /// which is the part the object mapper stamps into a cached ordinal to tell cycles apart.
    /// </summary>
    private static long MintNewRandomizedTag(long previousRandomizedTag)
    {
        const long cycleMask = unchecked((long)0xFFFFFFFF00000000L);

        long tag;
        do
        {
            tag = Random.Shared.NextInt64(long.MinValue, long.MaxValue);
        }
        while ((tag & cycleMask) == 0
            || (tag & cycleMask) == cycleMask
            || (tag & cycleMask) == (previousRandomizedTag & cycleMask));

        return tag;
    }

    /// <summary>
    /// Whether any type's populated ordinals differ from the previous cycle's.
    /// </summary>
    /// <remarks>
    /// A producer uses this to decide whether a cycle has anything to publish. When nothing changed,
    /// publishing a version would cost consumers a refresh that moves them nowhere.
    /// </remarks>
    public bool HasChangedSinceLastCycle() =>
        _orderedTypeStates.Any(typeState => typeState.HasChangedSinceLastCycle());

    /// <summary>
    /// Adds a header tag to be written into the blob.
    /// </summary>
    public void AddHeaderTag(string name, string value) => HeaderTags[name] = value;

    /// <summary>
    /// Gets a header tag, or <see langword="null"/> when it is not set.
    /// </summary>
    public string? GetHeaderTag(string name) => HeaderTags.GetValueOrDefault(name);
}
