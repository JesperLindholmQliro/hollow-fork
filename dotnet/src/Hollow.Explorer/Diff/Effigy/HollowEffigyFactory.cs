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
using Hollow.Core;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.Iterator;
using Hollow.Core.Schema;

namespace Hollow.Explorer.Diff.Effigy;

/// <summary>
/// Turns records into effigies.
/// </summary>
/// <remarks>
/// One factory per record being shown, because it memoises the leaf fields it builds: a dataset's
/// records repeat the same small values endlessly, and a diff page holds two whole record trees at
/// once. Sharing one instance of each distinct leaf also makes the pairers' equality checks a
/// reference comparison most of the time.
/// </remarks>
public sealed class HollowEffigyFactory
{
    /// <summary>
    /// The format Java writes a <c>Date</c> record's long in.
    /// </summary>
    /// <remarks>
    /// Java uses <c>SimpleDateFormat</c> with the default locale and time zone. This names the
    /// invariant culture and UTC, so that the same dataset reads the same wherever it is looked at —
    /// which for comparing two states is the point.
    /// </remarks>
    private const string DateFormat = "ddd MMM dd HH:mm:ss.fff 'UTC' yyyy";

    private readonly Dictionary<HollowEffigyField, HollowEffigyField> _fieldMemoizer = [];

    /// <summary>
    /// The record at <paramref name="ordinal"/> of <paramref name="typeName"/>, or
    /// <see langword="null"/> where there is no such record.
    /// </summary>
    public HollowEffigy? Effigy(IHollowDataAccess dataAccess, string typeName, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(dataAccess);

        if (ordinal == HollowConstants.OrdinalNone)
        {
            return null;
        }

        IHollowTypeDataAccess? typeState = dataAccess.GetTypeDataAccess(typeName, ordinal);

        return typeState is null ? null : new HollowEffigy(this, typeState, ordinal);
    }

    internal List<HollowEffigyField> CreateFields(HollowEffigy effigy) =>
        effigy.DataAccess switch
        {
            IHollowObjectTypeDataAccess objectAccess => CreateObjectFields(effigy, objectAccess),
            IHollowMapTypeDataAccess mapAccess => CreateMapFields(effigy, mapAccess),
            IHollowCollectionTypeDataAccess collectionAccess =>
                CreateCollectionFields(effigy, collectionAccess),

            _ => throw new ArgumentException(
                $"{effigy.ObjectType} is not a kind of record this knows how to lay out",
                nameof(effigy)),
        };

    private List<HollowEffigyField> CreateObjectFields(
        HollowEffigy effigy, IHollowObjectTypeDataAccess typeDataAccess)
    {
        HollowObjectSchema schema = typeDataAccess.Schema;
        List<HollowEffigyField> fields = new(schema.FieldCount);

        for (int i = 0; i < schema.FieldCount; i++)
        {
            FieldType fieldType = schema.GetFieldType(i);

            string typeName = fieldType == FieldType.Reference
                ? schema.GetReferencedType(i)!
                : fieldType.ToString();

            object? value = ReadFieldValue(effigy, typeDataAccess, schema, i, fieldType);

            HollowEffigyField field = new(schema.GetFieldName(i), typeName, value);

            // Only leaves are worth sharing; a reference's value is a whole subtree and no two records
            // reach the same one by the same route often enough to pay for the lookup.
            fields.Add(fieldType == FieldType.Reference ? field : Memoize(field));
        }

        return fields;
    }

    private object? ReadFieldValue(
        HollowEffigy effigy,
        IHollowObjectTypeDataAccess typeDataAccess,
        HollowObjectSchema schema,
        int fieldIndex,
        FieldType fieldType)
    {
        switch (fieldType)
        {
            case FieldType.Boolean:
                return typeDataAccess.ReadBoolean(effigy.Ordinal, fieldIndex);

            case FieldType.Bytes:
                byte[]? bytes = typeDataAccess.ReadBytes(effigy.Ordinal, fieldIndex);

                // Shown as base64 rather than as a list of numbers, which for anything but a handful of
                // bytes is unreadable — but an empty array is left alone, since "" would look like a
                // value rather than like nothing.
                return bytes is null || bytes.Length == 0 ? bytes : Convert.ToBase64String(bytes);

            case FieldType.Double:
                return typeDataAccess.ReadDouble(effigy.Ordinal, fieldIndex);

            case FieldType.Float:
                return typeDataAccess.ReadFloat(effigy.Ordinal, fieldIndex);

            case FieldType.Int:
                return typeDataAccess.ReadInt(effigy.Ordinal, fieldIndex);

            case FieldType.Decimal:
                return typeDataAccess.ReadDecimal(effigy.Ordinal, fieldIndex);

            case FieldType.String:
                return typeDataAccess.ReadString(effigy.Ordinal, fieldIndex);

            case FieldType.Long:
                long longValue = typeDataAccess.ReadLong(effigy.Ordinal, fieldIndex);

                // A type actually called Date holding a long is read as one, which is a guess about the
                // model rather than something the schema says — but a timestamp shown as its epoch
                // millis tells a reader nothing.
                return longValue != long.MinValue
                    && string.Equals(schema.Name, "Date", StringComparison.Ordinal)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(longValue)
                        .UtcDateTime.ToString(DateFormat, CultureInfo.InvariantCulture)
                    : longValue;

            case FieldType.Reference:
                return Effigy(
                    typeDataAccess.DataAccess,
                    schema.GetReferencedType(fieldIndex)!,
                    typeDataAccess.ReadOrdinal(effigy.Ordinal, fieldIndex));

            default:
                return null;
        }
    }

    private List<HollowEffigyField> CreateCollectionFields(
        HollowEffigy effigy, IHollowCollectionTypeDataAccess typeDataAccess)
    {
        List<HollowEffigyField> fields = [];
        IHollowOrdinalIterator iterator = typeDataAccess.OrdinalIterator(effigy.Ordinal);

        for (int elementOrdinal = iterator.Next();
            elementOrdinal != IHollowOrdinalIterator.NoMoreOrdinals;
            elementOrdinal = iterator.Next())
        {
            HollowEffigy? element = Effigy(
                typeDataAccess.DataAccess, typeDataAccess.Schema.ElementType, elementOrdinal);

            fields.Add(new HollowEffigyField(
                "element", typeDataAccess.Schema.ElementType, element));
        }

        return fields;
    }

    private List<HollowEffigyField> CreateMapFields(
        HollowEffigy effigy, IHollowMapTypeDataAccess typeDataAccess)
    {
        HollowMapSchema schema = typeDataAccess.Schema;
        List<HollowEffigyField> fields = [];
        IHollowMapEntryOrdinalIterator iterator = typeDataAccess.OrdinalIterator(effigy.Ordinal);

        while (iterator.Next())
        {
            // An entry has no record of its own, so it becomes a node standing for the pairing.
            HollowEffigy entry = new("Map.Entry");

            entry.Add(new HollowEffigyField(
                "key", schema.KeyType, Effigy(typeDataAccess.DataAccess, schema.KeyType, iterator.Key)));

            entry.Add(new HollowEffigyField(
                "value",
                schema.ValueType,
                Effigy(typeDataAccess.DataAccess, schema.ValueType, iterator.Value)));

            fields.Add(new HollowEffigyField("entry", "Map.Entry", entry));
        }

        return fields;
    }

    private HollowEffigyField Memoize(HollowEffigyField field)
    {
        if (_fieldMemoizer.TryGetValue(field, out HollowEffigyField? canonical))
        {
            return canonical;
        }

        _fieldMemoizer[field] = field;

        return field;
    }
}

/// <summary>
/// A caller's own way of laying a type out, for a record whose shape means more than its fields do.
/// </summary>
/// <remarks>
/// Set both records, generate, then read both effigies — in that order, because a custom factory is
/// allowed to lay the two out against each other rather than independently.
/// </remarks>
public interface ICustomHollowEffigyFactory
{
    /// <summary>Sets the record from the earlier state.</summary>
    void SetFromHollowRecord(IHollowTypeDataAccess fromState, int ordinal);

    /// <summary>Sets the record from the later state.</summary>
    void SetToHollowRecord(IHollowTypeDataAccess toState, int ordinal);

    /// <summary>Builds both effigies.</summary>
    void GenerateEffigies();

    /// <summary>The effigy of the earlier record.</summary>
    HollowEffigy? FromEffigy { get; }

    /// <summary>The effigy of the later one.</summary>
    HollowEffigy? ToEffigy { get; }
}
