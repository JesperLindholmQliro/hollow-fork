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
using Hollow.Core.Read.Engine.Map;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Read.Iterator;
using Hollow.Core.Schema;
using Hollow.Core.Util;

namespace Hollow.Core.Tools.Traverse;

/// <summary>
/// Follows references between records, to work out what else a selection of records implies.
/// </summary>
/// <remarks>
/// <para>
/// A selection is a set of ordinals per type. Given one, this can add everything the selection points
/// at (<see cref="AddTransitiveMatches(HollowReadStateEngine, Dictionary{string, BitSet})"/>), add
/// everything that points at the selection
/// (<see cref="AddReferencingOutsideClosure"/>), or drop from it anything that something outside it
/// still points at (<see cref="RemoveReferencedOutsideClosure"/>).
/// </para>
/// <para>
/// Deleting a record is what these are for together: take the record, add what it references, then drop
/// whatever else still needs those references — what is left is exactly the records that become
/// unreachable when it goes.
/// </para>
/// </remarks>
public static class TransitiveSetTraverser
{
    /// <summary>
    /// Adds to <paramref name="matches"/> every record the selection references, and everything those
    /// reference in turn.
    /// </summary>
    public static void AddTransitiveMatches(
        HollowReadStateEngine stateEngine, Dictionary<string, BitSet> matches)
    {
        ArgumentNullException.ThrowIfNull(stateEngine);
        ArgumentNullException.ThrowIfNull(matches);

        // Referencing types first, so that a record added to the selection is itself traversed when its
        // own type comes round.
        foreach (HollowSchema schema in ReferencersFirst(stateEngine))
        {
            if (matches.ContainsKey(schema.Name))
            {
                AddTransitiveMatches(stateEngine, schema.Name, matches);
            }
        }
    }

    /// <summary>
    /// Removes from <paramref name="matches"/> every record that a record outside the selection still
    /// references.
    /// </summary>
    public static void RemoveReferencedOutsideClosure(
        HollowReadStateEngine stateEngine, Dictionary<string, BitSet> matches)
    {
        ArgumentNullException.ThrowIfNull(stateEngine);
        ArgumentNullException.ThrowIfNull(matches);

        IReadOnlyList<HollowSchema> orderedSchemas = ReferencersFirst(stateEngine);

        foreach (HollowSchema referencedSchema in orderedSchemas)
        {
            if (!matches.ContainsKey(referencedSchema.Name))
            {
                continue;
            }

            foreach (HollowSchema referencerSchema in orderedSchemas)
            {
                if (ReferenceEquals(referencerSchema, referencedSchema))
                {
                    break;
                }

                if (matches.TryGetValue(referencedSchema.Name, out BitSet? referenced)
                    && referenced.Cardinality() > 0)
                {
                    TraverseReferencesOutsideClosure(
                        stateEngine,
                        referencerSchema.Name,
                        referencedSchema.Name,
                        matches,
                        ReferenceAction.DropTheReferenced);
                }
            }
        }
    }

    /// <summary>
    /// Adds to <paramref name="matches"/> every record outside the selection that references something
    /// in it, and everything that references those in turn.
    /// </summary>
    public static void AddReferencingOutsideClosure(
        HollowReadStateEngine stateEngine, Dictionary<string, BitSet> matches)
    {
        ArgumentNullException.ThrowIfNull(stateEngine);
        ArgumentNullException.ThrowIfNull(matches);

        IReadOnlyList<HollowSchema> orderedSchemas = HollowSchemaSorter.DependencyOrderedSchemaList(stateEngine);

        foreach (HollowSchema referencerSchema in orderedSchemas)
        {
            foreach (HollowSchema referencedSchema in orderedSchemas)
            {
                if (ReferenceEquals(referencedSchema, referencerSchema))
                {
                    break;
                }

                if (matches.TryGetValue(referencedSchema.Name, out BitSet? referenced)
                    && referenced.Cardinality() > 0)
                {
                    TraverseReferencesOutsideClosure(
                        stateEngine,
                        referencerSchema.Name,
                        referencedSchema.Name,
                        matches,
                        ReferenceAction.KeepTheReferencer);
                }
            }
        }
    }

    /// <summary>
    /// The schemas with the types that reference others first, which is the order a downward traversal
    /// has to visit them in.
    /// </summary>
    private static IReadOnlyList<HollowSchema> ReferencersFirst(HollowReadStateEngine stateEngine) =>
        [.. HollowSchemaSorter.DependencyOrderedSchemaList(stateEngine).Reverse()];

    private static void AddTransitiveMatches(
        HollowReadStateEngine stateEngine, string type, Dictionary<string, BitSet> matches)
    {
        switch (stateEngine.GetTypeState(type))
        {
            case HollowObjectTypeReadState objectState:
                AddTransitiveMatches(stateEngine, objectState, matches);
                break;

            case IHollowCollectionTypeDataAccess collectionState:
                AddTransitiveMatches(stateEngine, collectionState, matches);
                break;

            case HollowMapTypeReadState mapState:
                AddTransitiveMatches(stateEngine, mapState, matches);
                break;
        }
    }

    private static void AddTransitiveMatches(
        HollowReadStateEngine stateEngine,
        HollowObjectTypeReadState typeState,
        Dictionary<string, BitSet> matches)
    {
        HollowObjectSchema schema = typeState.Schema;
        BitSet matchingOrdinals = GetOrCreate(matches, schema.Name);

        BitSet?[] childOrdinals = new BitSet?[schema.FieldCount];

        for (int i = 0; i < schema.FieldCount; i++)
        {
            if (schema.GetFieldType(i) == FieldType.Reference
                && stateEngine.GetTypeState(schema.GetReferencedType(i)!) is { MaxOrdinal: >= 0 })
            {
                childOrdinals[i] = GetOrCreate(matches, schema.GetReferencedType(i)!);
            }
        }

        foreach (int ordinal in matchingOrdinals.EnumerateSetBits())
        {
            for (int i = 0; i < childOrdinals.Length; i++)
            {
                if (childOrdinals[i] is { } children)
                {
                    int childOrdinal = typeState.ReadOrdinal(ordinal, i);
                    if (childOrdinal != HollowConstants.OrdinalNone)
                    {
                        children.Set(childOrdinal);
                    }
                }
            }
        }
    }

    private static void AddTransitiveMatches(
        HollowReadStateEngine stateEngine,
        IHollowCollectionTypeDataAccess typeState,
        Dictionary<string, BitSet> matches)
    {
        HollowCollectionSchema schema = typeState.Schema;

        if (stateEngine.GetTypeState(schema.ElementType) is not { MaxOrdinal: >= 0 })
        {
            return;
        }

        BitSet matchingOrdinals = GetOrCreate(matches, schema.Name);
        BitSet childOrdinals = GetOrCreate(matches, schema.ElementType);

        foreach (int ordinal in matchingOrdinals.EnumerateSetBits())
        {
            foreach (int elementOrdinal in typeState.ElementOrdinals(ordinal))
            {
                childOrdinals.Set(elementOrdinal);
            }
        }
    }

    private static void AddTransitiveMatches(
        HollowReadStateEngine stateEngine,
        HollowMapTypeReadState typeState,
        Dictionary<string, BitSet> matches)
    {
        HollowMapSchema schema = typeState.Schema;
        BitSet matchingOrdinals = GetOrCreate(matches, schema.Name);

        BitSet? keyOrdinals = stateEngine.GetTypeState(schema.KeyType) is { MaxOrdinal: >= 0 }
            ? GetOrCreate(matches, schema.KeyType)
            : null;
        BitSet? valueOrdinals = stateEngine.GetTypeState(schema.ValueType) is { MaxOrdinal: >= 0 }
            ? GetOrCreate(matches, schema.ValueType)
            : null;

        foreach (int ordinal in matchingOrdinals.EnumerateSetBits())
        {
            foreach (HollowMapEntry entry in typeState.Entries(ordinal))
            {
                keyOrdinals?.Set(entry.KeyOrdinal);
                valueOrdinals?.Set(entry.ValueOrdinal);
            }
        }
    }

    private static void TraverseReferencesOutsideClosure(
        HollowReadStateEngine stateEngine,
        string referencerType,
        string referencedType,
        Dictionary<string, BitSet> matches,
        ReferenceAction action)
    {
        switch (stateEngine.GetTypeState(referencerType))
        {
            case HollowObjectTypeReadState objectState:
                TraverseReferencesOutsideClosure(objectState, referencedType, matches, action);
                break;

            case IHollowCollectionTypeDataAccess collectionState:
                TraverseReferencesOutsideClosure(collectionState, referencedType, matches, action);
                break;

            case HollowMapTypeReadState mapState:
                TraverseReferencesOutsideClosure(mapState, referencedType, matches, action);
                break;
        }
    }

    private static void TraverseReferencesOutsideClosure(
        HollowObjectTypeReadState referencerState,
        string referencedType,
        Dictionary<string, BitSet> matches,
        ReferenceAction action)
    {
        HollowObjectSchema schema = referencerState.Schema;
        BitSet referencedMatches = GetOrCreate(matches, referencedType);
        BitSet referencerMatches = GetOrCreate(matches, schema.Name);

        for (int i = 0; i < schema.FieldCount; i++)
        {
            if (schema.GetFieldType(i) != FieldType.Reference
                || !string.Equals(referencedType, schema.GetReferencedType(i), StringComparison.Ordinal))
            {
                continue;
            }

            foreach (int ordinal in referencerState.PopulatedOrdinals.EnumerateSetBits())
            {
                if (referencerMatches.Get(ordinal))
                {
                    continue;
                }

                int referencedOrdinal = referencerState.ReadOrdinal(ordinal, i);
                if (referencedOrdinal != HollowConstants.OrdinalNone && referencedMatches.Get(referencedOrdinal))
                {
                    Apply(action, referencerMatches, ordinal, referencedMatches, referencedOrdinal);
                }
            }
        }
    }

    private static void TraverseReferencesOutsideClosure(
        IHollowCollectionTypeDataAccess referencerState,
        string referencedType,
        Dictionary<string, BitSet> matches,
        ReferenceAction action)
    {
        HollowCollectionSchema schema = referencerState.Schema;

        if (!string.Equals(referencedType, schema.ElementType, StringComparison.Ordinal))
        {
            return;
        }

        BitSet referencedMatches = GetOrCreate(matches, referencedType);
        BitSet referencerMatches = GetOrCreate(matches, schema.Name);

        foreach (int ordinal in referencerState.TypeState.PopulatedOrdinals.EnumerateSetBits())
        {
            if (referencerMatches.Get(ordinal))
            {
                continue;
            }

            foreach (int referencedOrdinal in referencerState.ElementOrdinals(ordinal))
            {
                if (referencedMatches.Get(referencedOrdinal))
                {
                    Apply(action, referencerMatches, ordinal, referencedMatches, referencedOrdinal);
                }
            }
        }
    }

    private static void TraverseReferencesOutsideClosure(
        HollowMapTypeReadState referencerState,
        string referencedType,
        Dictionary<string, BitSet> matches,
        ReferenceAction action)
    {
        HollowMapSchema schema = referencerState.Schema;

        bool keyTypeMatches = string.Equals(referencedType, schema.KeyType, StringComparison.Ordinal);
        bool valueTypeMatches = string.Equals(referencedType, schema.ValueType, StringComparison.Ordinal);

        if (!keyTypeMatches && !valueTypeMatches)
        {
            return;
        }

        BitSet referencedMatches = GetOrCreate(matches, referencedType);
        BitSet referencerMatches = GetOrCreate(matches, schema.Name);

        foreach (int ordinal in referencerState.PopulatedOrdinals.EnumerateSetBits())
        {
            if (referencerMatches.Get(ordinal))
            {
                continue;
            }

            foreach (HollowMapEntry entry in referencerState.Entries(ordinal))
            {
                if (keyTypeMatches && referencedMatches.Get(entry.KeyOrdinal))
                {
                    Apply(action, referencerMatches, ordinal, referencedMatches, entry.KeyOrdinal);
                }

                if (valueTypeMatches && referencedMatches.Get(entry.ValueOrdinal))
                {
                    Apply(action, referencerMatches, ordinal, referencedMatches, entry.ValueOrdinal);
                }
            }
        }
    }

    private static void Apply(
        ReferenceAction action,
        BitSet referencerMatches,
        int referencerOrdinal,
        BitSet referencedMatches,
        int referencedOrdinal)
    {
        switch (action)
        {
            case ReferenceAction.DropTheReferenced:
                referencedMatches.Clear(referencedOrdinal);
                break;

            case ReferenceAction.KeepTheReferencer:
                referencerMatches.Set(referencerOrdinal);
                break;
        }
    }

    private static BitSet GetOrCreate(Dictionary<string, BitSet> matches, string typeName)
    {
        if (!matches.TryGetValue(typeName, out BitSet? bitSet))
        {
            bitSet = new BitSet();
            matches[typeName] = bitSet;
        }

        return bitSet;
    }

    /// <summary>
    /// What to do on finding a reference from outside the selection into it.
    /// </summary>
    /// <remarks>
    /// Java models this as a two-instance functional interface; an enum says the same thing without the
    /// indirection.
    /// </remarks>
    private enum ReferenceAction
    {
        /// <summary>Drop the referenced record, because something outside still needs it.</summary>
        DropTheReferenced,

        /// <summary>Add the referencing record, because what it needs is going.</summary>
        KeepTheReferencer,
    }
}
