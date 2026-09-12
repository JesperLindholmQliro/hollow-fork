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
using Hollow.Core.Read;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Schema;
using Hollow.Core.Util;

namespace Hollow.Core.Tools.Query;

/// <summary>
/// Scans a dataset for records holding a given value in a given field.
/// </summary>
/// <remarks>
/// <para>
/// A scan rather than an index: nothing is built up front, and nothing has to have been declared
/// searchable. That makes it the right tool for a person poking at data they do not know, and the
/// wrong one for a query on a hot path.
/// </para>
/// <para>
/// What comes back is the matched ordinals per type. Pass it through
/// <see cref="Traverse.TransitiveSetTraverser"/> to pick up the records that reference a match — which
/// is how searching for an actor's name also turns up the films they are in.
/// </para>
/// </remarks>
/// <param name="readEngine">The dataset to search.</param>
public sealed class HollowFieldMatchQuery(HollowReadStateEngine readEngine)
{
    private readonly HollowReadStateEngine _readEngine =
        readEngine ?? throw new ArgumentNullException(nameof(readEngine));

    /// <summary>
    /// Every record of any type whose <paramref name="fieldName"/> holds <paramref name="fieldValue"/>.
    /// </summary>
    /// <param name="fieldName">The field to look at.</param>
    /// <param name="fieldValue">The value as text, read as whatever type the field turns out to be.</param>
    public IReadOnlyDictionary<string, BitSet> FindMatchingRecords(string fieldName, string fieldValue)
    {
        Dictionary<string, BitSet> matches = new(StringComparer.Ordinal);

        foreach (HollowTypeReadState typeState in _readEngine.TypeStates.Values)
        {
            AugmentMatchingRecords(typeState, fieldName, fieldValue, matches);
        }

        return matches;
    }

    /// <summary>
    /// The same, narrowed to one type.
    /// </summary>
    public IReadOnlyDictionary<string, BitSet> FindMatchingRecords(
        string typeName, string fieldName, string fieldValue)
    {
        Dictionary<string, BitSet> matches = new(StringComparer.Ordinal);

        if (_readEngine.GetTypeState(typeName) is { } typeState)
        {
            AugmentMatchingRecords(typeState, fieldName, fieldValue, matches);
        }

        return matches;
    }

    private void AugmentMatchingRecords(
        HollowTypeReadState typeState,
        string fieldName,
        string fieldValue,
        Dictionary<string, BitSet> matches)
    {
        if (typeState.Schema is not HollowObjectSchema schema)
        {
            return;
        }

        for (int i = 0; i < schema.FieldCount; i++)
        {
            if (schema.GetFieldName(i) != fieldName)
            {
                continue;
            }

            HollowObjectTypeReadState objectState = (HollowObjectTypeReadState)typeState;

            BitSet? typeQueryMatches = schema.GetFieldType(i) == FieldType.Reference
                ? AttemptReferenceTraversalQuery(objectState, i, fieldValue)
                : CastQueryValue(fieldValue, schema.GetFieldType(i)) is { } queryValue
                    ? QueryBasedOnValueMatches(objectState, i, queryValue)
                    : null;

            if (typeQueryMatches is not null && typeQueryMatches.Cardinality() > 0)
            {
                matches[schema.Name] = typeQueryMatches;
            }
        }
    }

    /// <summary>
    /// Matches a reference field by looking through it at the one value the referenced type holds.
    /// </summary>
    /// <remarks>
    /// This is what lets a search for a title match a <c>Movie</c> whose <c>Title</c> is a reference to
    /// the shared <c>String</c> type — which is nearly every string in a Hollow dataset. It only works
    /// where the referenced type has exactly one field, since otherwise there is no single value the
    /// text could be describing.
    /// </remarks>
    private BitSet? AttemptReferenceTraversalQuery(
        HollowObjectTypeReadState typeState, int fieldIndex, string fieldValue)
    {
        if (typeState.Schema.GetReferencedTypeState(fieldIndex) is not HollowObjectTypeReadState referenced
            || referenced.Schema.FieldCount != 1)
        {
            return null;
        }

        BitSet? referenceMatches = referenced.Schema.GetFieldType(0) == FieldType.Reference
            ? AttemptReferenceTraversalQuery(referenced, 0, fieldValue)
            : CastQueryValue(fieldValue, referenced.Schema.GetFieldType(0)) is { } queryValue
                ? QueryBasedOnValueMatches(referenced, 0, queryValue)
                : null;

        return referenceMatches is { } found && found.Cardinality() > 0
            ? QueryBasedOnMatchedReferences(typeState, fieldIndex, found)
            : null;
    }

    private static BitSet QueryBasedOnMatchedReferences(
        HollowObjectTypeReadState typeState, int referenceFieldPosition, BitSet matchedReferences)
    {
        BitSet populatedOrdinals = typeState.PopulatedOrdinals;
        BitSet typeQueryMatches = new(populatedOrdinals.Length);

        for (int ordinal = populatedOrdinals.NextSetBit(0);
            ordinal != HollowConstants.OrdinalNone;
            ordinal = populatedOrdinals.NextSetBit(ordinal + 1))
        {
            int referencedOrdinal = typeState.ReadOrdinal(ordinal, referenceFieldPosition);

            if (referencedOrdinal != HollowConstants.OrdinalNone && matchedReferences.Get(referencedOrdinal))
            {
                typeQueryMatches.Set(ordinal);
            }
        }

        return typeQueryMatches;
    }

    private static BitSet QueryBasedOnValueMatches(
        HollowObjectTypeReadState typeState, int fieldPosition, object queryValue)
    {
        BitSet populatedOrdinals = typeState.PopulatedOrdinals;
        BitSet typeQueryMatches = new(populatedOrdinals.Length);

        for (int ordinal = populatedOrdinals.NextSetBit(0);
            ordinal != HollowConstants.OrdinalNone;
            ordinal = populatedOrdinals.NextSetBit(ordinal + 1))
        {
            if (HollowReadFieldUtils.FieldValueEquals(typeState, ordinal, fieldPosition, queryValue))
            {
                typeQueryMatches.Set(ordinal);
            }
        }

        return typeQueryMatches;
    }

    /// <summary>
    /// <paramref name="fieldValue"/> read as <paramref name="fieldType"/>, or <see langword="null"/>
    /// when it is not one.
    /// </summary>
    /// <remarks>
    /// Text that does not parse is not an error: the same string is tried against every field of every
    /// type, and most of them will not be the type it names.
    /// </remarks>
    private static object? CastQueryValue(string fieldValue, FieldType fieldType) =>
        fieldType switch
        {
            FieldType.Boolean => bool.TryParse(fieldValue, out bool parsed) ? parsed : null,
            FieldType.Double =>
                double.TryParse(fieldValue, CultureInfo.InvariantCulture, out double parsed) ? parsed : null,
            FieldType.Float =>
                float.TryParse(fieldValue, CultureInfo.InvariantCulture, out float parsed) ? parsed : null,
            FieldType.Int =>
                int.TryParse(fieldValue, CultureInfo.InvariantCulture, out int parsed) ? parsed : null,
            FieldType.Long =>
                long.TryParse(fieldValue, CultureInfo.InvariantCulture, out long parsed) ? parsed : null,
            FieldType.Decimal =>
                decimal.TryParse(fieldValue, CultureInfo.InvariantCulture, out decimal parsed) ? parsed : null,
            FieldType.String => fieldValue,
            _ => null,
        };
}
