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

using System.Collections.Concurrent;
using Hollow.Core;
using Hollow.Core.Index;
using Hollow.Core.Index.Key;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Schema;
using Hollow.Core.Tools.Traverse;
using Hollow.Core.Util;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Api.Producer;

/// <summary>
/// The state an incremental cycle describes its changes against.
/// </summary>
/// <remarks>
/// <para>
/// Where an ordinary <see cref="Populator"/> describes the whole dataset, this describes only what
/// moved since the last version: the producer carries every other record across unchanged. Records are
/// named by primary key, so every type touched here needs one.
/// </para>
/// <para>
/// Valid only during the incremental populate stage, and safe to use from several threads within it.
/// Every member throws once the stage has finished.
/// </para>
/// </remarks>
public interface IIncrementalWriteState
{
    /// <summary>
    /// Adds <paramref name="value"/>, replacing whatever record currently holds its primary key.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="value"/>'s type has no primary key.</exception>
    void AddOrModify(object value);

    /// <summary>
    /// Adds <paramref name="value"/> only if no record currently holds its primary key, leaving an
    /// existing record untouched.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="value"/>'s type has no primary key.</exception>
    void AddIfAbsent(object value);

    /// <summary>
    /// Deletes the record holding <paramref name="value"/>'s primary key. Deleting a record that is not
    /// there does nothing.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="value"/>'s type has no primary key.</exception>
    void Delete(object value);

    /// <summary>
    /// Deletes the record <paramref name="key"/> names. Deleting a record that is not there does
    /// nothing.
    /// </summary>
    void Delete(RecordPrimaryKey key);
}

/// <summary>
/// Describes what changed since the last version, for a cycle that carries the rest across.
/// </summary>
/// <remarks>
/// Java declares this as the functional interface
/// <c>HollowProducer.Incremental.IncrementalPopulator</c>; a delegate is the C# equivalent.
/// </remarks>
public delegate void IncrementalPopulator(IIncrementalWriteState state);

/// <summary>
/// One change an incremental cycle was told about.
/// </summary>
/// <remarks>
/// Java distinguishes these with a sentinel object and a wrapper class, because the map's values are
/// plain <c>Object</c>s; naming the three cases is clearer and costs nothing.
/// </remarks>
internal sealed class MutationEvent
{
    private MutationEvent(object? value, MutationKind kind)
    {
        Value = value;
        Kind = kind;
    }

    private enum MutationKind
    {
        AddOrModify,
        AddIfAbsent,
        Delete,
    }

    /// <summary>The record to write, or <see langword="null"/> for a deletion.</summary>
    internal object? Value { get; }

    private MutationKind Kind { get; }

    /// <summary>Whether this change removes the record rather than writing one.</summary>
    internal bool IsDeletion => Kind == MutationKind.Delete;

    /// <summary>
    /// Whether the previous version turned out to hold this key already, which an
    /// <see cref="AddIfAbsent"/> takes as its cue to do nothing.
    /// </summary>
    internal bool WasFound { get; set; }

    /// <summary>Whether this change should do nothing now that the record is known to exist.</summary>
    internal bool SupersededByExistingRecord => Kind == MutationKind.AddIfAbsent && WasFound;

    internal static MutationEvent AddOrModify(object value) => new(value, MutationKind.AddOrModify);

    internal static MutationEvent AddIfAbsent(object value) => new(value, MutationKind.AddIfAbsent);

    internal static MutationEvent Delete() => new(null, MutationKind.Delete);

    /// <summary>
    /// Whether this change leaves the record out of the new version, which is what decides whether the
    /// previous version's copy has to be removed.
    /// </summary>
    /// <remarks>
    /// An add-if-absent counts here only until the previous version is consulted: if the record turns
    /// out to be there, the removal is cancelled and the existing record survives untouched.
    /// </remarks>
    internal bool RemovesThePreviousRecord => Kind != MutationKind.AddIfAbsent;
}

/// <summary>
/// Collects the changes an <see cref="IncrementalPopulator"/> reports.
/// </summary>
internal sealed class IncrementalWriteStateForCycle(HollowObjectMapper objectMapper) : IIncrementalWriteState
{
    private bool _closed;

    /// <summary>
    /// The changes reported so far, one per record. A later change to the same record replaces an
    /// earlier one, which is what makes reporting the same record twice harmless.
    /// </summary>
    internal ConcurrentDictionary<RecordPrimaryKey, MutationEvent> Events { get; } = new();

    public void AddOrModify(object value) => Record(value, MutationEvent.AddOrModify(value));

    public void AddIfAbsent(object value) => Record(value, MutationEvent.AddIfAbsent(value));

    public void Delete(object value) => Record(value, MutationEvent.Delete());

    public void Delete(RecordPrimaryKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ThrowIfClosed();

        Events[key] = MutationEvent.Delete();
    }

    internal void Close() => _closed = true;

    private void Record(object value, MutationEvent mutation)
    {
        ArgumentNullException.ThrowIfNull(value);
        ThrowIfClosed();

        Events[objectMapper.ExtractPrimaryKey(value)] = mutation;
    }

    private void ThrowIfClosed()
    {
        if (_closed)
        {
            throw new InvalidOperationException(
                "The incremental populate stage has finished; this write state can no longer be used. A "
                + "populator must not keep a reference to it, or hand one to work that outlives the call.");
        }
    }
}

/// <summary>
/// Turns a set of reported changes into an ordinary cycle population.
/// </summary>
/// <remarks>
/// <para>
/// The previous version is adopted wholesale, the records the changes supersede are removed, and the
/// new records are added. What comes out is an ordinary write state, so the rest of the cycle —
/// publishing, the integrity check, validation — is unchanged.
/// </para>
/// <para>
/// Removing a record also removes what it referenced, unless something else still references it. That
/// is what keeps the shared sub-records of a deleted record from accumulating in the state forever.
/// </para>
/// </remarks>
internal sealed class IncrementalCyclePopulator(
    IReadOnlyDictionary<RecordPrimaryKey, MutationEvent> mutations)
{
    internal void Populate(IWriteState writeState)
    {
        writeState.StateEngine.AddAllObjectsFromPreviousCycle();

        RemoveSupersededRecords(writeState);
        AddChangedRecords(writeState);
    }

    private void RemoveSupersededRecords(IWriteState writeState)
    {
        if (writeState.PriorState is not { } priorState)
        {
            // Nothing to supersede: this is the first version of a delta chain, so an add-if-absent
            // always adds and a deletion has nothing to delete.
            return;
        }

        Dictionary<string, BitSet> recordsToRemove = MarkRecordsToRemove(priorState.StateEngine);

        TransitiveSetTraverser.AddTransitiveMatches(priorState.StateEngine, recordsToRemove);
        TransitiveSetTraverser.RemoveReferencedOutsideClosure(priorState.StateEngine, recordsToRemove);

        foreach ((string typeName, BitSet ordinals) in recordsToRemove)
        {
            if (writeState.StateEngine.GetTypeState(typeName) is not { } typeState)
            {
                continue;
            }

            foreach (int ordinal in ordinals.EnumerateSetBits())
            {
                typeState.RemoveOrdinalFromThisCycle(ordinal);
            }
        }
    }

    /// <summary>
    /// Finds, per type, the previous version's records that the reported changes supersede.
    /// </summary>
    private Dictionary<string, BitSet> MarkRecordsToRemove(HollowReadStateEngine priorStateEngine)
    {
        Dictionary<string, BitSet> recordsToRemove = new(StringComparer.Ordinal);

        foreach (IGrouping<string, KeyValuePair<RecordPrimaryKey, MutationEvent>> byType in
            mutations.GroupBy(mutation => mutation.Key.Type, StringComparer.Ordinal))
        {
            if (priorStateEngine.GetTypeState(byType.Key) is not HollowObjectTypeReadState typeState)
            {
                // A type the previous version does not hold — newly introduced, say. Everything
                // reported for it is an addition.
                continue;
            }

            PrimaryKey primaryKey = typeState.Schema.PrimaryKey
                ?? throw new ArgumentException(
                    $"Type {byType.Key} does not have a primary key defined, so it cannot take part in an "
                    + "incremental cycle.");

            BitSet ordinals = new();
            using HollowPrimaryKeyIndex index = new(priorStateEngine, primaryKey);

            foreach ((RecordPrimaryKey key, MutationEvent mutation) in byType)
            {
                int priorOrdinal = index.GetMatchingOrdinal(key.ToKeyArray());

                if (priorOrdinal == HollowConstants.OrdinalNone)
                {
                    continue;
                }

                // An add-if-absent whose record is already there does nothing at all: neither the
                // removal below nor the addition afterwards.
                mutation.WasFound = true;

                if (mutation.RemovesThePreviousRecord)
                {
                    ordinals.Set(priorOrdinal);
                }
            }

            recordsToRemove[byType.Key] = ordinals;
        }

        return recordsToRemove;
    }

    private void AddChangedRecords(IWriteState writeState)
    {
        foreach (MutationEvent mutation in mutations.Values)
        {
            if (mutation.IsDeletion || mutation.SupersededByExistingRecord)
            {
                continue;
            }

            writeState.Add(mutation.Value!);
        }
    }
}
