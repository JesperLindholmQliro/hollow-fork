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
using System.Text;
using Hollow.Core.Memory;
using Hollow.Core.Schema;

namespace Hollow.Core.Write.ObjectMapper.FlatRecords;

/// <summary>
/// Puts a <see cref="FlatRecord"/> into a write state engine.
/// </summary>
/// <remarks>
/// <para>
/// The records arrive in the order they were written, which is the order they can be added in: a
/// reference always points at something already dumped, so the ordinal it maps to is already known.
/// </para>
/// <para>
/// The destination's model need not be the one the record was written against. A field the destination
/// does not declare is dropped, which is what lets a record written by a newer producer land in an
/// older consumer. A <em>reference</em> to a type the destination lacks is refused: there is nothing to
/// point at, and a silently null reference would be a worse answer than a failure.
/// </para>
/// </remarks>
public sealed class FlatRecordDumper
{
    /// <summary>
    /// How much of a record to show in a diagnostic dump before giving up on it being readable.
    /// </summary>
    private const int MaxDiagnosticHexBytes = 512;

    private readonly HollowWriteStateEngine _stateEngine;
    private readonly Dictionary<string, IHollowWriteRecord> _writeRecords = new(StringComparer.Ordinal);
    private readonly List<int> _ordinals = [];

    /// <summary>Dumps into <paramref name="stateEngine"/>.</summary>
    public FlatRecordDumper(HollowWriteStateEngine stateEngine)
    {
        ArgumentNullException.ThrowIfNull(stateEngine);

        _stateEngine = stateEngine;
    }

    /// <summary>
    /// Adds every record <paramref name="record"/> holds to the state engine.
    /// </summary>
    /// <returns>The ordinal the top record took.</returns>
    /// <exception cref="InvalidOperationException">
    /// The record cannot be read against the destination's model. The message carries a dump of the
    /// record as this end decoded it.
    /// </exception>
    public int Dump(FlatRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        _ordinals.Clear();

        FlatRecordReader reader = new(record);

        try
        {
            while (reader.HasMore)
            {
                HollowSchema schema = reader.ReadSchema();

                _ordinals.Add(schema switch
                {
                    HollowObjectSchema objectSchema => CopyObjectRecord(objectSchema, reader),
                    HollowListSchema listSchema => CopyListRecord(listSchema, reader),
                    HollowSetSchema setSchema => CopySetRecord(setSchema, reader),
                    HollowMapSchema mapSchema => CopyMapRecord(mapSchema, reader),
                    _ => throw new InvalidOperationException($"unknown schema {schema.Name}"),
                });
            }
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            throw new InvalidOperationException(Diagnose(record, failure), failure);
        }

        return _ordinals.Count == 0
            ? throw new InvalidOperationException("the flat record holds no records")
            : _ordinals[^1];
    }

    /// <summary>
    /// Describes a flat record as this end decodes it, which is the only way to find a disagreement
    /// about what a schema identifier means.
    /// </summary>
    /// <remarks>
    /// When the two ends number their schemas differently, every symptom is a lie: a length read as
    /// enormous, a field type that does not belong, a pointer walking off the end. Reading the record
    /// as far as it goes and saying where it stopped turns that into something answerable.
    /// </remarks>
    public string Diagnose(FlatRecord record, Exception? failure = null)
    {
        ArgumentNullException.ThrowIfNull(record);

        StringBuilder text = new();

        text.Append("the flat record could not be dumped into this state engine");

        if (failure is not null)
        {
            text.Append(": ").Append(failure.Message);
        }

        text.AppendLine()
            .Append(CultureInfo.InvariantCulture, $"{_ordinals.Count} record(s) were read before that.")
            .AppendLine()
            .AppendLine("As decoded at this end:");

        DescribeRecordLayout(record, text);
        HexDumpRecordRegion(record, text);

        return text.ToString();
    }

    private void DescribeRecordLayout(FlatRecord record, StringBuilder text)
    {
        FlatRecordReader reader = new(record);
        int index = 0;

        try
        {
            while (reader.HasMore)
            {
                int start = reader.Pointer;
                HollowSchema schema = reader.ReadSchema();

                text.Append(CultureInfo.InvariantCulture, $"  [{index}] @{start} {schema.Name} ")
                    .Append(schema.SchemaType.ToString().ToLowerInvariant());

                if (schema is HollowObjectSchema objectSchema)
                {
                    text.Append(CultureInfo.InvariantCulture, $" ({objectSchema.FieldCount} field(s))");
                }

                text.AppendLine();

                reader.SkipSchema(schema);
                index++;
            }
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            text.Append(CultureInfo.InvariantCulture,
                    $"  [{index}] @{reader.Pointer} unreadable: {failure.Message}")
                .AppendLine();
        }
    }

    private static void HexDumpRecordRegion(FlatRecord record, StringBuilder text)
    {
        int length = record.DataEndByte - record.DataStartByte;
        int shown = Math.Min(length, MaxDiagnosticHexBytes);

        text.Append(CultureInfo.InvariantCulture,
                $"Bytes {record.DataStartByte}..{record.DataEndByte} ({length} total")
            .Append(shown < length ? ", first " : string.Empty)
            .Append(shown < length ? shown.ToString(CultureInfo.InvariantCulture) : string.Empty)
            .Append(shown < length ? " shown" : string.Empty)
            .AppendLine("):");

        for (int i = 0; i < shown; i++)
        {
            if (i % 32 == 0)
            {
                text.Append(CultureInfo.InvariantCulture, $"  {record.DataStartByte + i,6}: ");
            }

            text.Append(CultureInfo.InvariantCulture, $"{record.Data.Get(record.DataStartByte + i):x2}");
            text.Append(i % 32 == 31 ? Environment.NewLine : " ");
        }

        if (shown % 32 != 0)
        {
            text.AppendLine();
        }
    }

    private int CopyObjectRecord(HollowObjectSchema schemaInRecord, FlatRecordReader reader)
    {
        // The destination's own schema decides what is kept. Where the destination does not have the
        // type at all, the fields still have to be read past, so the record after this one is found.
        HollowObjectSchema? destination =
            (_stateEngine.GetTypeState(schemaInRecord.Name) as HollowObjectTypeWriteState)?.Schema;

        HollowObjectWriteRecord? record =
            destination is null ? null : (HollowObjectWriteRecord)WriteRecordFor(destination);

        for (int field = 0; field < schemaInRecord.FieldCount; field++)
        {
            string name = schemaInRecord.GetFieldName(field);
            FieldType type = schemaInRecord.GetFieldType(field);

            int destinationField = destination?.GetPosition(name) ?? -1;

            if (destinationField == -1 || destination!.GetFieldType(destinationField) != type)
            {
                // The destination does not declare this field, or declares it as something else. Read
                // past it: a record written against a newer model still has to land.
                reader.SkipField(type);

                continue;
            }

            CopyObjectField(name, type, reader, record);
        }

        return record is null
            ? -1
            : _stateEngine.Add(schemaInRecord.Name, record);
    }

    private void CopyObjectField(
        string field, FieldType type, FlatRecordReader reader, HollowObjectWriteRecord? record)
    {
        switch (type)
        {
            case FieldType.Reference:
            {
                int referenced = reader.ReadOrdinal();

                if (referenced != -1)
                {
                    // Written out rather than folded into the call, because `record?.SetReference(...)`
                    // would not evaluate ResolveOrdinal at all where the destination lacks the type —
                    // and the whole point is that a dangling reference is refused.
                    int ordinal = ResolveOrdinal(referenced, field);

                    record?.SetReference(field, ordinal);
                }

                break;
            }

            // Every one of these reads the field whether or not there is anywhere to put it, because
            // the reader is sequential: skipping the set is fine, skipping the read is not. Null is
            // simply not set — a write record starts out with every field null.
            case FieldType.Boolean:
            {
                bool? value = reader.ReadBoolean();

                if (value is { } present)
                {
                    record?.SetBoolean(field, present);
                }

                break;
            }

            case FieldType.Int:
            {
                int value = reader.ReadInt();

                if (value != int.MinValue)
                {
                    record?.SetInt(field, value);
                }

                break;
            }

            case FieldType.Long:
            {
                long value = reader.ReadLong();

                if (value != long.MinValue)
                {
                    record?.SetLong(field, value);
                }

                break;
            }

            case FieldType.Float:
            {
                float value = reader.ReadFloat();

                // The format spends its whole float range on values and signals null with a NaN, so
                // this is a null test and not the equality it looks like.
                if (!float.IsNaN(value))
                {
                    record?.SetFloat(field, value);
                }

                break;
            }

            case FieldType.Double:
            {
                double value = reader.ReadDouble();

                if (!double.IsNaN(value))
                {
                    record?.SetDouble(field, value);
                }

                break;
            }

            case FieldType.Decimal:
            {
                decimal? value = reader.ReadDecimal();

                if (value is { } present)
                {
                    record?.SetDecimal(field, present);
                }

                break;
            }

            case FieldType.String:
            {
                string? value = reader.ReadString();

                if (value is not null)
                {
                    record?.SetString(field, value);
                }

                break;
            }

            case FieldType.Bytes:
            {
                byte[]? value = reader.ReadBytes();

                if (value is not null)
                {
                    record?.SetBytes(field, value);
                }

                break;
            }

            default:
                throw new InvalidOperationException($"unknown field type {type} on field {field}");
        }
    }

    private int CopyListRecord(HollowListSchema schema, FlatRecordReader reader)
    {
        HollowListWriteRecord? record =
            _stateEngine.GetTypeState(schema.Name) is null
                ? null
                : (HollowListWriteRecord)WriteRecordFor(schema);

        int size = reader.ReadCollectionSize();

        for (int i = 0; i < size; i++)
        {
            int element = ResolveOrdinal(reader.ReadOrdinal(), schema.Name);

            record?.AddElement(element);
        }

        return record is null ? -1 : _stateEngine.Add(schema.Name, record);
    }

    private int CopySetRecord(HollowSetSchema schema, FlatRecordReader reader)
    {
        HollowSetWriteRecord? record =
            _stateEngine.GetTypeState(schema.Name) is null
                ? null
                : (HollowSetWriteRecord)WriteRecordFor(schema);

        int size = reader.ReadCollectionSize();
        int element = 0;

        for (int i = 0; i < size; i++)
        {
            // Gap-encoded: what is stored is the step from the last element, not the element.
            element += reader.ReadOrdinal();

            record?.AddElement(ResolveOrdinal(element, schema.Name));
        }

        return record is null ? -1 : _stateEngine.Add(schema.Name, record);
    }

    private int CopyMapRecord(HollowMapSchema schema, FlatRecordReader reader)
    {
        HollowMapWriteRecord? record =
            _stateEngine.GetTypeState(schema.Name) is null
                ? null
                : (HollowMapWriteRecord)WriteRecordFor(schema);

        int size = reader.ReadCollectionSize();
        int key = 0;

        for (int i = 0; i < size; i++)
        {
            // Keys are gap-encoded as a set's elements are; values are not.
            key += reader.ReadOrdinal();
            int value = reader.ReadOrdinal();

            record?.AddEntry(ResolveOrdinal(key, schema.Name), ResolveOrdinal(value, schema.Name));
        }

        return record is null ? -1 : _stateEngine.Add(schema.Name, record);
    }

    /// <summary>
    /// Turns an index into the flat record into the ordinal the record took in the state engine.
    /// </summary>
    private int ResolveOrdinal(int index, string referencedBy)
    {
        if (index < 0 || index >= _ordinals.Count)
        {
            throw new InvalidOperationException(
                $"{referencedBy} references record {index} of the flat record, which is not one of the "
                + $"{_ordinals.Count} written before it");
        }

        return _ordinals[index] == -1
            ? throw new InvalidOperationException(
                $"{referencedBy} references a record of a type this state engine does not have, and a "
                + "reference has to point at something")
            : _ordinals[index];
    }

    /// <summary>
    /// The write record for a type, reset and ready. One per type: a record is added before the next
    /// of its type is read, so there is never a second one in flight.
    /// </summary>
    private IHollowWriteRecord WriteRecordFor(HollowSchema schema)
    {
        if (_writeRecords.TryGetValue(schema.Name, out IHollowWriteRecord? record))
        {
            record.Reset();

            return record;
        }

        record = schema switch
        {
            HollowObjectSchema objectSchema => new HollowObjectWriteRecord(objectSchema),
            HollowListSchema => new HollowListWriteRecord(),
            HollowSetSchema => new HollowSetWriteRecord(),
            HollowMapSchema => new HollowMapWriteRecord(),
            _ => throw new InvalidOperationException($"unknown schema {schema.Name}"),
        };

        _writeRecords[schema.Name] = record;

        return record;
    }
}
