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

using Hollow.Core.Read;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.List;
using Hollow.Core.Read.Engine.Map;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Read.Engine.Set;
using Hollow.Core.Read.Iterator;
using Hollow.Core.Schema;
using Hollow.Core.Tools.Traverse;
using Hollow.Core.Util;
using Hollow.Core.Write;
using Hollow.Core.Write.Copy;

namespace Hollow.Core.Tools.Patch.Delta;

/// <summary>
/// A remapper over explicit tables, which passes through anything it was not told about.
/// </summary>
/// <remarks>
/// Named <c>PartialOrdinalRemapper</c> in Java. <see cref="OrdinalIsMapped"/> always says yes, which
/// is what makes "partial" work: a copier asks it before consulting the mapping, and an ordinal with
/// no entry is meant to come through unchanged rather than be copied again.
/// </remarks>
public sealed class PartialOrdinalRemapper : IOrdinalRemapper
{
    private readonly Dictionary<string, IntMap> _ordinalMappings = new(StringComparer.Ordinal);

    /// <summary>Says where <paramref name="typeName"/>'s remapped ordinals went.</summary>
    public void AddOrdinalRemapping(string typeName, IntMap mapping) => _ordinalMappings[typeName] = mapping;

    /// <summary>Where <paramref name="typeName"/>'s remapped ordinals went, if anywhere.</summary>
    public IntMap? GetOrdinalRemapping(string typeName) => _ordinalMappings.GetValueOrDefault(typeName);

    /// <inheritdoc />
    public int GetMappedOrdinal(string type, int originalOrdinal)
    {
        if (_ordinalMappings.GetValueOrDefault(type) is { } mapping)
        {
            int remapped = mapping.Get(originalOrdinal);

            if (remapped != HollowConstants.OrdinalNone)
            {
                return remapped;
            }
        }

        return originalOrdinal;
    }

    /// <inheritdoc />
    /// <remarks>Always true: an ordinal with no entry passes through rather than being copied.</remarks>
    public bool OrdinalIsMapped(string type, int originalOrdinal) => true;

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always; the tables are set up in advance.</exception>
    public void RemapOrdinal(string type, int originalOrdinal, int mappedOrdinal) =>
        throw new NotSupportedException("a partial remapper's tables are fixed when it is built");
}

/// <summary>
/// Builds a state that sits between two unrelated states, so a delta chain can be spliced across them.
/// </summary>
/// <remarks>
/// <para>
/// A delta can only be written between two states whose ordinals line up. Two states produced
/// independently do not: the same record may sit at different ordinals, and the same ordinal may hold
/// different records. This produces an intermediate state that lines up with both — delta-compatible
/// with the earlier state on one side and with the later state on the other — so a consumer can be
/// walked from one to the other in two transitions instead of a double snapshot.
/// </para>
/// <para>
/// The trick is the ordinals nothing can share. A record that differs between the two states at the
/// same ordinal is moved to an ordinal neither state uses, which leaves that ordinal free to take the
/// later state's record on the second transition.
/// </para>
/// <para>
/// Named <c>HollowStateDeltaPatcher</c> in Java, which finds the changed ordinals and copies the
/// unchanged data on a <c>SimultaneousExecutor</c>. This port does both on one thread.
/// </para>
/// </remarks>
public sealed class HollowStateDeltaPatcher
{
    private readonly HollowReadStateEngine _from;
    private readonly HollowReadStateEngine _to;
    private readonly IReadOnlyList<HollowSchema> _schemas;
    private readonly Dictionary<string, BitSet> _changedOrdinalsBetweenStates;

    /// <summary>
    /// Patches between <paramref name="from"/> and <paramref name="to"/>.
    /// </summary>
    /// <remarks>
    /// Only the types both states have are carried, and for an object type only the fields both
    /// declare — a field one side does not have cannot take part in a delta.
    /// </remarks>
    public HollowStateDeltaPatcher(HollowReadStateEngine from, HollowReadStateEngine to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        _from = from;
        _to = to;
        _schemas = HollowSchemaSorter.DependencyOrderedSchemaList(GetCommonSchemas(from, to));

        StateEngine = HollowWriteStateCreator.CreateWithSchemas(_schemas);

        _changedOrdinalsBetweenStates = DiscoverChangedOrdinalsBetweenStates();
    }

    /// <summary>
    /// The intermediate state, which the blob writer produces the two transitions from.
    /// </summary>
    public HollowWriteStateEngine StateEngine { get; }

    /// <summary>
    /// Prepares the first transition. Write a delta or reverse delta after this, between the earlier
    /// state and the intermediate one.
    /// </summary>
    public void PrepareInitialTransition()
    {
        StateEngine.OverridePreviousStateRandomizedTag(_from.RandomizedTag);
        StateEngine.OverridePreviousHeaderTags(_from.HeaderTags);

        CopyUnchangedDataToIntermediateState();
        RemapTheChangedDataToUnusedOrdinals();
    }

    /// <summary>
    /// Prepares the second transition. Write a delta or reverse delta after this, between the
    /// intermediate state and the later one.
    /// </summary>
    public void PrepareFinalTransition()
    {
        StateEngine.PrepareForNextCycle();

        // Java calls this overrideNextStateRandomizedTag; here the tag is simply the property.
        StateEngine.RandomizedTag = _to.RandomizedTag;
        StateEngine.AddHeaderTags(_to.HeaderTags);

        CopyUnchangedDataToDestinationState();
        RemapTheChangedDataToDestinationOrdinals();
    }

    /// <summary>
    /// Puts every record of the earlier state at the ordinal it already had.
    /// </summary>
    /// <remarks>
    /// The ones that differ between the states are written but not marked as populated this cycle,
    /// which is what lets the next step move them without the first transition seeing two copies.
    /// </remarks>
    private void CopyUnchangedDataToIntermediateState()
    {
        foreach (HollowSchema schema in _schemas)
        {
            HollowTypeReadState fromTypeState = _from.GetTypeState(schema.Name)!;
            HollowTypeWriteState writeTypeState = StateEngine.GetTypeState(schema.Name)!;
            BitSet changedOrdinals = _changedOrdinalsBetweenStates[schema.Name];

            HollowRecordCopier copier = HollowRecordCopier.Create(
                fromTypeState, schema, IdentityOrdinalRemapper.Instance, preserveHashPositions: true);

            foreach (int ordinal in fromTypeState.PopulatedOrdinals.EnumerateSetBits())
            {
                writeTypeState.MapOrdinal(
                    copier.Copy(ordinal),
                    ordinal,
                    markPreviousCycle: true,
                    markCurrentCycle: !changedOrdinals.Get(ordinal));
            }

            writeTypeState.RecalculateFreeOrdinals();
        }
    }

    /// <summary>
    /// Moves every changed record to an ordinal neither state uses.
    /// </summary>
    /// <remarks>
    /// This is the whole point of the intermediate state. The first transition therefore removes the
    /// record from its old ordinal and adds it at a new one — leaving the old ordinal free for the
    /// later state's record on the second transition.
    /// </remarks>
    private void RemapTheChangedDataToUnusedOrdinals()
    {
        PartialOrdinalRemapper remapper = new();

        foreach (HollowSchema schema in _schemas)
        {
            BitSet ordinalsToRemap = _changedOrdinalsBetweenStates[schema.Name];

            HollowTypeReadState fromTypeState = _from.GetTypeState(schema.Name)!;
            HollowTypeReadState toTypeState = _to.GetTypeState(schema.Name)!;
            HollowTypeWriteState typeWriteState = StateEngine.GetTypeState(schema.Name)!;

            IntMap ordinalRemapping = new(ordinalsToRemap.Cardinality());

            // Past the end of both states, so the ordinal cannot collide with anything either holds.
            int nextFreeOrdinal = Math.Max(fromTypeState.MaxOrdinal, toTypeState.MaxOrdinal) + 1;

            // The references inside a moved record have to move with it, which is what the remapper
            // being filled in as we go does — the schemas are in dependency order, so a type's
            // referents have already been remapped by the time it is reached.
            HollowRecordCopier copier = HollowRecordCopier.Create(
                fromTypeState, schema, remapper, ShouldPreserveHashPositions(schema));

            foreach (int ordinal in ordinalsToRemap.EnumerateSetBits())
            {
                if (ordinal > fromTypeState.MaxOrdinal)
                {
                    break;
                }

                typeWriteState.MapOrdinal(
                    copier.Copy(ordinal), nextFreeOrdinal, markPreviousCycle: false, markCurrentCycle: true);

                ordinalRemapping.Put(ordinal, nextFreeOrdinal++);
            }

            remapper.AddOrdinalRemapping(schema.Name, ordinalRemapping);
            typeWriteState.RecalculateFreeOrdinals();
        }
    }

    /// <summary>
    /// Carries over every record the two states agree on, which the second transition then leaves
    /// alone.
    /// </summary>
    private void CopyUnchangedDataToDestinationState()
    {
        foreach (HollowSchema schema in _schemas)
        {
            HollowTypeWriteState writeTypeState = StateEngine.GetTypeState(schema.Name)!;
            BitSet toOrdinals = _to.GetTypeState(schema.Name)!.PopulatedOrdinals;
            BitSet fromOrdinals = _from.GetTypeState(schema.Name)!.PopulatedOrdinals;
            BitSet changedOrdinals = _changedOrdinalsBetweenStates[schema.Name];

            foreach (int ordinal in toOrdinals.EnumerateSetBits())
            {
                if (!changedOrdinals.Get(ordinal) && fromOrdinals.Get(ordinal))
                {
                    writeTypeState.AddOrdinalFromPreviousCycle(ordinal);
                }
            }
        }
    }

    /// <summary>
    /// Puts the later state's records at the ordinals they have there.
    /// </summary>
    /// <remarks>
    /// Those ordinals are free, because anything that used to be at one of them was moved out of the
    /// way by the first transition.
    /// </remarks>
    private void RemapTheChangedDataToDestinationOrdinals()
    {
        foreach (HollowSchema schema in _schemas)
        {
            BitSet changedOrdinals = _changedOrdinalsBetweenStates[schema.Name];
            HollowTypeWriteState typeWriteState = StateEngine.GetTypeState(schema.Name)!;

            HollowTypeReadState toReadState = _to.GetTypeState(schema.Name)!;
            BitSet toOrdinals = toReadState.PopulatedOrdinals;
            BitSet fromOrdinals = _from.GetTypeState(schema.Name)!.PopulatedOrdinals;

            HollowRecordCopier copier = HollowRecordCopier.Create(
                toReadState, schema, IdentityOrdinalRemapper.Instance, preserveHashPositions: true);

            foreach (int ordinal in toOrdinals.EnumerateSetBits())
            {
                if (!fromOrdinals.Get(ordinal) || changedOrdinals.Get(ordinal))
                {
                    typeWriteState.MapOrdinal(
                        copier.Copy(ordinal), ordinal, markPreviousCycle: false, markCurrentCycle: true);
                }
            }

            typeWriteState.RecalculateFreeOrdinals();
        }
    }

    /// <summary>
    /// The ordinals holding different records in the two states, grown to everything that references
    /// one.
    /// </summary>
    /// <remarks>
    /// A record whose reference now points somewhere else is itself a different record, even if none
    /// of its own fields moved — which is what the closure adds.
    /// </remarks>
    private Dictionary<string, BitSet> DiscoverChangedOrdinalsBetweenStates()
    {
        Dictionary<string, BitSet> changed = new(StringComparer.Ordinal);

        foreach (HollowSchema schema in _schemas)
        {
            changed[schema.Name] = FindOrdinalsPopulatedWithDifferentRecords(schema.Name);
        }

        TransitiveSetTraverser.AddReferencingOutsideClosure(_from, changed);

        return changed;
    }

    private BitSet FindOrdinalsPopulatedWithDifferentRecords(string typeName)
    {
        HollowTypeReadState fromTypeState = _from.GetTypeState(typeName)!;
        HollowTypeReadState toTypeState = _to.GetTypeState(typeName)!;

        // An object type is compared over the fields both declare; a collection has no such notion of
        // a common shape, so its schema has to match outright.
        if (fromTypeState.Schema.SchemaType != SchemaType.Object
            && !fromTypeState.Schema.Equals(toTypeState.Schema))
        {
            throw new InvalidOperationException(
                $"the FROM and TO schemas for {typeName} differ, so no delta between them is possible");
        }

        BitSet fromOrdinals = fromTypeState.PopulatedOrdinals;
        BitSet toOrdinals = toTypeState.PopulatedOrdinals;

        int maxSharedOrdinal = Math.Min(fromTypeState.MaxOrdinal, toTypeState.MaxOrdinal);

        BitSet different = new(Math.Max(maxSharedOrdinal + 1, 0));

        Func<int, bool> recordsAreEqual = RecordEquality(fromTypeState, toTypeState);

        for (int ordinal = 0; ordinal <= maxSharedOrdinal; ordinal++)
        {
            if (fromOrdinals.Get(ordinal) && toOrdinals.Get(ordinal) && !recordsAreEqual(ordinal))
            {
                different.Set(ordinal);
            }
        }

        return different;
    }

    /// <summary>
    /// Whether the record at an ordinal is the same on both sides, by the rules of its record kind.
    /// </summary>
    private static Func<int, bool> RecordEquality(
        HollowTypeReadState fromState, HollowTypeReadState toState) =>
        fromState switch
        {
            HollowObjectTypeReadState from => ObjectRecordEquality(from, (HollowObjectTypeReadState)toState),
            HollowListTypeReadState from => ListRecordEquality(from, (HollowListTypeReadState)toState),
            HollowSetTypeReadState from => SetRecordEquality(from, (HollowSetTypeReadState)toState),
            HollowMapTypeReadState from => MapRecordEquality(from, (HollowMapTypeReadState)toState),
            _ => throw new InvalidOperationException($"cannot compare a {fromState.Schema.SchemaType} record"),
        };

    private static Func<int, bool> ObjectRecordEquality(
        HollowObjectTypeReadState fromState, HollowObjectTypeReadState toState)
    {
        HollowObjectSchema commonSchema = fromState.Schema.FindCommonSchema(toState.Schema);

        // The positions are worked out once rather than per record, because the loop below runs over
        // every shared ordinal of the type.
        (int FromPosition, int ToPosition, bool IsReference)[] fields =
        [
            .. Enumerable.Range(0, commonSchema.FieldCount)
                .Select(i => (
                    fromState.Schema.GetPosition(commonSchema.GetFieldName(i)),
                    toState.Schema.GetPosition(commonSchema.GetFieldName(i)),
                    commonSchema.GetFieldType(i) == FieldType.Reference)),
        ];

        return ordinal =>
        {
            foreach ((int fromPosition, int toPosition, bool isReference) in fields)
            {
                bool equal = isReference
                    ? fromState.ReadOrdinal(ordinal, fromPosition) == toState.ReadOrdinal(ordinal, toPosition)
                    : HollowReadFieldUtils.FieldsAreEqual(
                        fromState, ordinal, fromPosition, toState, ordinal, toPosition);

                if (!equal)
                {
                    return false;
                }
            }

            return true;
        };
    }

    private static Func<int, bool> ListRecordEquality(
        HollowListTypeReadState fromState, HollowListTypeReadState toState) =>
        ordinal =>
        {
            int size = fromState.Size(ordinal);

            if (toState.Size(ordinal) != size)
            {
                return false;
            }

            for (int i = 0; i < size; i++)
            {
                if (fromState.GetElementOrdinal(ordinal, i) != toState.GetElementOrdinal(ordinal, i))
                {
                    return false;
                }
            }

            return true;
        };

    /// <summary>
    /// Compares two sets by their elements, sorted — a set's bucket order is not part of it.
    /// </summary>
    private static Func<int, bool> SetRecordEquality(
        HollowSetTypeReadState fromState, HollowSetTypeReadState toState) =>
        ordinal =>
            fromState.Size(ordinal) == toState.Size(ordinal)
            && fromState.ElementOrdinals(ordinal).Order()
                .SequenceEqual(toState.ElementOrdinals(ordinal).Order());

    /// <summary>
    /// Compares two maps by their entries, sorted, each entry packed into one long so that a key and
    /// its value are compared together.
    /// </summary>
    private static Func<int, bool> MapRecordEquality(
        HollowMapTypeReadState fromState, HollowMapTypeReadState toState) =>
        ordinal =>
            fromState.Size(ordinal) == toState.Size(ordinal)
            && SortedEntries(fromState, ordinal).SequenceEqual(SortedEntries(toState, ordinal));

    /// <summary>
    /// A map record's entries, each packed into one long so that a key and its value sort and compare
    /// together, in order.
    /// </summary>
    private static IEnumerable<long> SortedEntries(HollowMapTypeReadState typeState, int ordinal) =>
        typeState.Entries(ordinal)
            .Select(entry => ((long)entry.KeyOrdinal << 32) | (uint)entry.ValueOrdinal)
            .Order();

    /// <summary>
    /// The schemas both states have, narrowed for an object type to the fields both declare.
    /// </summary>
    /// <remarks>
    /// A type only one state has is dropped. Java reads the later state's schema before checking
    /// whether it has the type at all, and fails with a null reference.
    /// </remarks>
    private static IEnumerable<HollowSchema> GetCommonSchemas(
        HollowReadStateEngine from, HollowReadStateEngine to)
    {
        List<HollowSchema> schemas = [];

        foreach (HollowSchema fromSchema in from.Schemas)
        {
            if (to.GetTypeState(fromSchema.Name)?.Schema is not { } toSchema)
            {
                continue;
            }

            schemas.Add(
                fromSchema is HollowObjectSchema fromObjectSchema
                    ? fromObjectSchema.FindCommonSchema((HollowObjectSchema)toSchema)
                    : toSchema);
        }

        return schemas;
    }

    /// <summary>
    /// Whether a collection's bucket layout has to survive the copy.
    /// </summary>
    /// <remarks>
    /// Only ever with <c>HollowObjectHashCodeFinder</c>, the deprecated custom-hash-code mechanism
    /// this port does not have — so this is always false, and is kept only to say where Java asks.
    /// </remarks>
    private static bool ShouldPreserveHashPositions(HollowSchema schema) => false;
}
