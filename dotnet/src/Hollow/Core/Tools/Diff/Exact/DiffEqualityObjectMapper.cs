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

using Hollow.Core.Memory.Encoding;
using Hollow.Core.Read;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Schema;

namespace Hollow.Core.Tools.Diff.Exact;

/// <summary>
/// Matching object records by every field the two schemas have in common.
/// </summary>
/// <remarks>
/// Only the common fields are compared, because a field one side does not have is not a difference in
/// the record so much as a difference in the model. Whether that can hide a real difference is what
/// <see cref="RequiresTraversalForMissingFields"/> answers.
/// </remarks>
public sealed class DiffEqualityObjectMapper : DiffEqualityTypeMapper
{
    private readonly HollowObjectSchema _commonSchema;
    private readonly int[] _fromSchemaCommonFieldMapping;
    private readonly int[] _toSchemaCommonFieldMapping;
    private readonly DiffEqualOrdinalMap?[] _commonReferenceFieldEqualOrdinalMaps;

    /// <summary>Builds a mapper over the fields <paramref name="fromState"/> and <paramref name="toState"/> share.</summary>
    public DiffEqualityObjectMapper(
        DiffEqualityMapping mapping,
        HollowObjectTypeReadState fromState,
        HollowObjectTypeReadState toState,
        bool oneToOne)
        : base(fromState, toState, oneToOne)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        _commonSchema = fromState.Schema.FindCommonSchema(toState.Schema);
        _commonReferenceFieldEqualOrdinalMaps = new DiffEqualOrdinalMap?[_commonSchema.FieldCount];

        for (int i = 0; i < _commonSchema.FieldCount; i++)
        {
            if (_commonSchema.GetFieldType(i) == FieldType.Reference)
            {
                // Leaf-first: the referenced type's map is built before this one needs it.
                _commonReferenceFieldEqualOrdinalMaps[i] =
                    mapping.GetEqualOrdinalMap(_commonSchema.GetReferencedType(i)!);
            }
        }

        _fromSchemaCommonFieldMapping = BuildCommonSchemaFieldMapping(fromState.Schema);
        _toSchemaCommonFieldMapping = BuildCommonSchemaFieldMapping(toState.Schema);

        RequiresTraversalForMissingFields =
            fromState.Schema.FieldCount != _commonSchema.FieldCount
            || toState.Schema.FieldCount != _commonSchema.FieldCount
            || AnyReferencedTypeRequiresIt(mapping);
    }

    /// <inheritdoc />
    public override bool RequiresTraversalForMissingFields { get; }

    private HollowObjectTypeReadState From => (HollowObjectTypeReadState)FromState;

    private HollowObjectTypeReadState To => (HollowObjectTypeReadState)ToState;

    /// <inheritdoc />
    protected override int FromRecordHashCode(int ordinal) =>
        RecordHashCode(From, ordinal, _fromSchemaCommonFieldMapping, fromSide: true);

    /// <inheritdoc />
    protected override int ToRecordHashCode(int ordinal) =>
        RecordHashCode(To, ordinal, _toSchemaCommonFieldMapping, fromSide: false);

    /// <inheritdoc />
    protected override bool RecordsAreEqual(int fromOrdinal, int toOrdinal)
    {
        for (int i = 0; i < _commonSchema.FieldCount; i++)
        {
            if (_commonSchema.GetFieldType(i) != FieldType.Reference)
            {
                if (!HollowReadFieldUtils.FieldsAreEqual(
                    From, fromOrdinal, _fromSchemaCommonFieldMapping[i],
                    To, toOrdinal, _toSchemaCommonFieldMapping[i]))
                {
                    return false;
                }

                continue;
            }

            DiffEqualOrdinalMap referenceMap = _commonReferenceFieldEqualOrdinalMaps[i]!;

            int fromReferenceOrdinal = From.ReadOrdinal(fromOrdinal, _fromSchemaCommonFieldMapping[i]);
            int toReferenceOrdinal = To.ReadOrdinal(toOrdinal, _toSchemaCommonFieldMapping[i]);

            int fromIdentity = referenceMap.GetIdentityFromOrdinal(fromReferenceOrdinal);
            int toIdentity = referenceMap.GetIdentityToOrdinal(toReferenceOrdinal);

            // A reference that matched nothing is a difference, unless it points at nothing at all —
            // in which case both sides holding nothing is equality.
            if ((fromIdentity == HollowConstants.OrdinalNone && fromReferenceOrdinal != HollowConstants.OrdinalNone)
                || (toIdentity == HollowConstants.OrdinalNone && toReferenceOrdinal != HollowConstants.OrdinalNone)
                || fromIdentity != toIdentity)
            {
                return false;
            }
        }

        return true;
    }

    private bool AnyReferencedTypeRequiresIt(DiffEqualityMapping mapping)
    {
        for (int i = 0; i < _commonSchema.FieldCount; i++)
        {
            if (_commonSchema.GetFieldType(i) == FieldType.Reference
                && mapping.RequiresMissingFieldTraversal(_commonSchema.GetReferencedType(i)!))
            {
                return true;
            }
        }

        return false;
    }

    private int[] BuildCommonSchemaFieldMapping(HollowObjectSchema schema)
    {
        int[] mapping = new int[_commonSchema.FieldCount];

        for (int i = 0; i < mapping.Length; i++)
        {
            mapping[i] = schema.GetPosition(_commonSchema.GetFieldName(i));
        }

        return mapping;
    }

    private int RecordHashCode(
        HollowObjectTypeReadState typeState, int ordinal, int[] commonFieldMapping, bool fromSide)
    {
        int hashCode = 0;

        for (int i = 0; i < commonFieldMapping.Length; i++)
        {
            int fieldIndex = commonFieldMapping[i];

            if (_commonSchema.GetFieldType(i) != FieldType.Reference)
            {
                hashCode = (hashCode * 31)
                    ^ HashCodes.HashInt(HollowReadFieldUtils.FieldHashCode(typeState, ordinal, fieldIndex));

                continue;
            }

            DiffEqualOrdinalMap referenceMap = _commonReferenceFieldEqualOrdinalMaps[i]!;
            int referencedOrdinal = typeState.ReadOrdinal(ordinal, fieldIndex);

            int ordinalIdentity = fromSide
                ? referenceMap.GetIdentityFromOrdinal(referencedOrdinal)
                : referenceMap.GetIdentityToOrdinal(referencedOrdinal);

            // Referencing something that matched nothing means this record cannot match either, so it
            // is left out of the table rather than hashed into it.
            if (ordinalIdentity == HollowConstants.OrdinalNone
                && referencedOrdinal != HollowConstants.OrdinalNone)
            {
                return -1;
            }

            hashCode = (hashCode * 31) ^ HashCodes.HashInt(ordinalIdentity);
        }

        return hashCode;
    }
}
