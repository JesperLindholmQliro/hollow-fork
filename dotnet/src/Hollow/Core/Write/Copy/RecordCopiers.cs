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

using Hollow.Core.Read.Engine.List;
using Hollow.Core.Read.Engine.Map;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Read.Engine.Set;
using Hollow.Core.Read.Iterator;
using Hollow.Core.Schema;

namespace Hollow.Core.Write.Copy;

/// <summary>
/// Copies object records, selecting and reordering fields to match a destination schema.
/// </summary>
/// <remarks>
/// A field the destination declares but the source does not is left null, and a field the source has
/// but the destination does not is dropped. That is what makes a schema change survivable across a
/// restore.
/// </remarks>
public sealed class HollowObjectCopier : HollowRecordCopier
{
    /// <summary>
    /// For each field of the destination schema, the position of the same-named field in the source
    /// schema, or -1 when the source does not declare it.
    /// </summary>
    private readonly int[] _fieldIndexMapping;

    private readonly HollowObjectTypeReadState _readState;
    private readonly HollowObjectWriteRecord _record;

    /// <summary>
    /// Initialises a copier producing records of <paramref name="destinationSchema"/>.
    /// </summary>
    public HollowObjectCopier(
        HollowObjectTypeReadState readTypeState,
        HollowObjectSchema destinationSchema,
        IOrdinalRemapper ordinalRemapper)
        : base(
            readTypeState,
            new HollowObjectWriteRecord(destinationSchema),
            ordinalRemapper,
            preserveHashPositions: false)
    {
        ArgumentNullException.ThrowIfNull(destinationSchema);

        _readState = readTypeState;
        _record = (HollowObjectWriteRecord)WriteRecord;

        HollowObjectSchema sourceSchema = (HollowObjectSchema)readTypeState.Schema;

        _fieldIndexMapping = new int[destinationSchema.FieldCount];
        for (int i = 0; i < _fieldIndexMapping.Length; i++)
        {
            _fieldIndexMapping[i] = sourceSchema.GetPosition(destinationSchema.GetFieldName(i));
        }
    }

    /// <inheritdoc />
    public override IHollowWriteRecord Copy(int ordinal)
    {
        HollowObjectSchema schema = _record.Schema;
        HollowObjectSchema sourceSchema = (HollowObjectSchema)_readState.Schema;

        _record.Reset();

        for (int i = 0; i < schema.FieldCount; i++)
        {
            int readFieldIndex = _fieldIndexMapping[i];
            if (readFieldIndex == -1)
            {
                continue;
            }

            string fieldName = schema.GetFieldName(i);

            switch (schema.GetFieldType(i))
            {
                case FieldType.Boolean:
                    if (_readState.ReadBoolean(ordinal, readFieldIndex) is bool boolValue)
                    {
                        _record.SetBoolean(fieldName, boolValue);
                    }

                    break;

                case FieldType.Bytes:
                    if (_readState.ReadBytes(ordinal, readFieldIndex) is byte[] bytesValue)
                    {
                        _record.SetBytes(fieldName, bytesValue);
                    }

                    break;

                case FieldType.String:
                    if (_readState.ReadString(ordinal, readFieldIndex) is string stringValue)
                    {
                        _record.SetString(fieldName, stringValue);
                    }

                    break;

                case FieldType.Double:
                    double doubleValue = _readState.ReadDouble(ordinal, readFieldIndex);
                    if (!double.IsNaN(doubleValue))
                    {
                        _record.SetDouble(fieldName, doubleValue);
                    }

                    break;

                case FieldType.Float:
                    float floatValue = _readState.ReadFloat(ordinal, readFieldIndex);
                    if (!float.IsNaN(floatValue))
                    {
                        _record.SetFloat(fieldName, floatValue);
                    }

                    break;

                // A format extension; see PORTING.md. Unlike float and double it has a real null rather
                // than a NaN standing in for one, so there is nothing to lose here.
                case FieldType.Decimal:
                    if (_readState.ReadDecimal(ordinal, readFieldIndex) is decimal decimalValue)
                    {
                        _record.SetDecimal(fieldName, decimalValue);
                    }

                    break;

                case FieldType.Int:
                    int intValue = _readState.ReadInt(ordinal, readFieldIndex);
                    if (intValue != int.MinValue)
                    {
                        _record.SetInt(fieldName, intValue);
                    }

                    break;

                case FieldType.Long:
                    long longValue = _readState.ReadLong(ordinal, readFieldIndex);
                    if (longValue != long.MinValue)
                    {
                        _record.SetLong(fieldName, longValue);
                    }

                    break;

                case FieldType.Reference:
                    int referencedOrdinal = _readState.ReadOrdinal(ordinal, readFieldIndex);
                    if (referencedOrdinal >= 0)
                    {
                        _record.SetReference(
                            fieldName,
                            OrdinalRemapper.GetMappedOrdinal(
                                sourceSchema.GetReferencedType(readFieldIndex)!, referencedOrdinal));
                    }

                    break;

                default:
                    throw new NotSupportedException(
                        $"Cannot copy field {schema.Name}.{fieldName} of type {schema.GetFieldType(i)}.");
            }
        }

        return _record;
    }
}

/// <summary>
/// Copies list records, preserving element order.
/// </summary>
public sealed class HollowListCopier : HollowRecordCopier
{
    private readonly HollowListTypeReadState _readState;
    private readonly HollowListWriteRecord _record;

    /// <summary>
    /// Initialises a copier over <paramref name="readTypeState"/>.
    /// </summary>
    public HollowListCopier(HollowListTypeReadState readTypeState, IOrdinalRemapper ordinalRemapper)
        : base(readTypeState, new HollowListWriteRecord(), ordinalRemapper, preserveHashPositions: false)
    {
        _readState = readTypeState;
        _record = (HollowListWriteRecord)WriteRecord;
    }

    /// <inheritdoc />
    public override IHollowWriteRecord Copy(int ordinal)
    {
        _record.Reset();

        string elementType = ((HollowListSchema)_readState.Schema).ElementType;
        int size = _readState.Size(ordinal);

        for (int i = 0; i < size; i++)
        {
            int elementOrdinal = _readState.GetElementOrdinal(ordinal, i);
            _record.AddElement(OrdinalRemapper.GetMappedOrdinal(elementType, elementOrdinal));
        }

        return _record;
    }
}

/// <summary>
/// Copies set records, optionally keeping each element in the bucket it already occupies.
/// </summary>
/// <remarks>
/// Keeping the buckets matters for restore: a set's hash layout is part of its serialised form, so a
/// record rehashed from its ordinals would serialise differently and be assigned a different ordinal
/// than the one the published state gave it.
/// </remarks>
public sealed class HollowSetCopier : HollowRecordCopier
{
    private readonly HollowSetTypeReadState _readState;
    private readonly HollowSetWriteRecord _record;

    /// <summary>
    /// Initialises a copier over <paramref name="typeState"/>.
    /// </summary>
    public HollowSetCopier(
        HollowSetTypeReadState typeState, IOrdinalRemapper ordinalRemapper, bool preserveHashPositions)
        : base(
            typeState,
            new HollowSetWriteRecord(preserveHashPositions ? HashBehavior.UnmixedHashes : HashBehavior.MixedHashes),
            ordinalRemapper,
            preserveHashPositions)
    {
        _readState = typeState;
        _record = (HollowSetWriteRecord)WriteRecord;
    }

    /// <inheritdoc />
    public override IHollowWriteRecord Copy(int ordinal)
    {
        _record.Reset();

        string elementType = ((HollowSetSchema)_readState.Schema).ElementType;
        HollowSetOrdinalIterator iterator = new(ordinal, _readState);

        int elementOrdinal = iterator.Next();
        while (elementOrdinal != IHollowOrdinalIterator.NoMoreOrdinals)
        {
            int remappedOrdinal = OrdinalRemapper.GetMappedOrdinal(elementType, elementOrdinal);
            _record.AddElement(
                remappedOrdinal, PreserveHashPositions ? iterator.CurrentBucket : remappedOrdinal);

            elementOrdinal = iterator.Next();
        }

        return _record;
    }
}

/// <summary>
/// Copies map records, optionally keeping each entry in the bucket it already occupies.
/// </summary>
/// <remarks>See <see cref="HollowSetCopier"/> for why the buckets matter.</remarks>
public sealed class HollowMapCopier : HollowRecordCopier
{
    private readonly HollowMapTypeReadState _readState;
    private readonly HollowMapWriteRecord _record;

    /// <summary>
    /// Initialises a copier over <paramref name="readTypeState"/>.
    /// </summary>
    public HollowMapCopier(
        HollowMapTypeReadState readTypeState, IOrdinalRemapper ordinalRemapper, bool preserveHashPositions)
        : base(
            readTypeState,
            new HollowMapWriteRecord(preserveHashPositions ? HashBehavior.UnmixedHashes : HashBehavior.MixedHashes),
            ordinalRemapper,
            preserveHashPositions)
    {
        _readState = readTypeState;
        _record = (HollowMapWriteRecord)WriteRecord;
    }

    /// <inheritdoc />
    public override IHollowWriteRecord Copy(int ordinal)
    {
        _record.Reset();

        HollowMapSchema schema = (HollowMapSchema)_readState.Schema;
        HollowMapEntryOrdinalIteratorImpl iterator = new(ordinal, _readState);

        while (iterator.Next())
        {
            int keyOrdinal = OrdinalRemapper.GetMappedOrdinal(schema.KeyType, iterator.Key);
            int valueOrdinal = OrdinalRemapper.GetMappedOrdinal(schema.ValueType, iterator.Value);

            _record.AddEntry(
                keyOrdinal, valueOrdinal, PreserveHashPositions ? iterator.CurrentBucket : keyOrdinal);
        }

        return _record;
    }
}
