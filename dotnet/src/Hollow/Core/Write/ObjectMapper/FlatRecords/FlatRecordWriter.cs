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

using Hollow.Core.Index.Key;
using Hollow.Core.Memory;
using Hollow.Core.Memory.Encoding;
using Hollow.Core.Schema;
using Hollow.Core.Util;

namespace Hollow.Core.Write.ObjectMapper.FlatRecords;

/// <summary>
/// Builds a <see cref="FlatRecord"/>, one write record at a time.
/// </summary>
/// <remarks>
/// <para>
/// Write the records a record references before the record itself, and <see cref="Write"/> hands back
/// the index to put in the reference field. The last record written is the top one, which is what
/// <see cref="GenerateFlatRecord"/> takes the record's identity and primary key from.
/// </para>
/// <para>
/// Records deduplicate as they are written: two identical ones share an index, exactly as they would
/// share an ordinal in a dataset. A collection's hashes are left out of what is compared — a set's
/// bucket layout is a property of the dataset it came from, not of the record.
/// </para>
/// <para>
/// Not thread-safe, and not reusable across two records without <see cref="Reset"/>.
/// </para>
/// </remarks>
public sealed class FlatRecordWriter
{
    private readonly IHollowDataset _dataset;
    private readonly IHollowSchemaIdentifierMapper _schemaIdMapper;
    private readonly ByteDataArray _buffer = new();

    // Written records by their content hash, so an identical one is found without comparing against
    // everything written so far.
    private readonly Dictionary<int, List<RecordLocation>> _locationsByHashCode = [];
    private readonly IntList _locationsByOrdinal = new();

    /// <summary>
    /// Writes records of <paramref name="dataset"/>'s model, naming schemas through
    /// <paramref name="schemaIdMapper"/>.
    /// </summary>
    public FlatRecordWriter(IHollowDataset dataset, IHollowSchemaIdentifierMapper schemaIdMapper)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(schemaIdMapper);

        _dataset = dataset;
        _schemaIdMapper = schemaIdMapper;
    }

    /// <summary>Throws away what has been written, ready for the next record.</summary>
    public void Reset()
    {
        _buffer.Reset();
        _locationsByHashCode.Clear();
        _locationsByOrdinal.Clear();
    }

    /// <summary>
    /// Writes one record, and returns the index a reference to it takes.
    /// </summary>
    /// <remarks>
    /// The index is this flat record's own, not a dataset ordinal — which is the whole point: a flat
    /// record means nothing outside itself except through its schema identifiers.
    /// </remarks>
    public int Write(HollowSchema schema, IHollowWriteRecord record)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(record);

        int schemaId = _schemaIdMapper.GetSchemaId(schema);
        int nextOrdinal = _locationsByOrdinal.Count;

        int start = (int)_buffer.Length;
        VarInt.WriteVInt(_buffer, schemaId);

        if (record is IHollowHashableWriteRecord hashable)
        {
            // A set's or map's hash table is how one dataset laid it out, and says nothing about the
            // record. Two sets of the same elements have to flatten the same.
            hashable.WriteDataTo(_buffer, HashBehavior.IgnoredHashes);
        }
        else
        {
            record.WriteDataTo(_buffer);
        }

        int length = (int)(_buffer.Length - start);
        int hashCode = HashCodes.Compute(_buffer.UnderlyingArray, start, length);

        if (_locationsByHashCode.TryGetValue(hashCode, out List<RecordLocation>? existing))
        {
            foreach (RecordLocation candidate in existing)
            {
                if (candidate.Length == length
                    && _buffer.UnderlyingArray.RangeEquals(
                        start, _buffer.UnderlyingArray, candidate.Start, length))
                {
                    // Already written. Rewind over what was just appended and point at the first copy.
                    _buffer.Length = start;

                    return candidate.Ordinal;
                }
            }

            existing.Add(new RecordLocation(nextOrdinal, start, length));
        }
        else
        {
            _locationsByHashCode[hashCode] = [new RecordLocation(nextOrdinal, start, length)];
        }

        _locationsByOrdinal.Add(start);

        return nextOrdinal;
    }

    /// <summary>Finishes the record.</summary>
    /// <exception cref="InvalidOperationException">Nothing has been written.</exception>
    public FlatRecord GenerateFlatRecord()
    {
        using MemoryStream bytes = new();
        WriteTo(bytes);

        return new FlatRecord(new ArrayByteData(bytes.ToArray()), _schemaIdMapper);
    }

    /// <summary>Writes the record out, without reading it back.</summary>
    /// <exception cref="InvalidOperationException">Nothing has been written.</exception>
    public void WriteTo(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        if (_locationsByOrdinal.Count == 0)
        {
            throw new InvalidOperationException("nothing has been written, so there is no record to make");
        }

        int topRecordLocation = _locationsByOrdinal.Get(_locationsByOrdinal.Count - 1);
        int topRecordSchemaId = VarInt.ReadVInt(_buffer.UnderlyingArray, topRecordLocation);

        HollowSchema topRecordSchema = _schemaIdMapper.GetSchema(topRecordSchemaId)
            ?? throw new InvalidOperationException(
                $"the schema identifier mapper does not know schema {topRecordSchemaId}");

        WriteVInt(destination, topRecordLocation);

        int[] keyFieldLocations = LocatePrimaryKeyFields(topRecordSchema, topRecordLocation);

        WriteVInt(destination, (int)_buffer.Length - topRecordLocation);

        _buffer.UnderlyingArray.WriteTo(destination, 0, _buffer.Length);

        // The key's field locations go last, so a reader can key the record without walking it.
        foreach (int location in keyFieldLocations)
        {
            WriteVInt(destination, location);
        }
    }

    /// <summary>
    /// Writes one variable-length integer to a stream.
    /// </summary>
    /// <remarks>
    /// <see cref="VarInt"/> writes into a <see cref="ByteDataArray"/>, a <c>HollowBlobOutput</c> or a
    /// byte array, none of which a caller handing in a plain stream has. Encoding into the smallest
    /// array that fits and writing that is the whole of it.
    /// </remarks>
    private static void WriteVInt(Stream destination, int value)
    {
        byte[] encoded = new byte[VarInt.SizeOfVInt(value)];
        VarInt.WriteVInt(encoded, 0, value);

        destination.Write(encoded);
    }

    private int[] LocatePrimaryKeyFields(HollowSchema topRecordSchema, int topRecordLocation)
    {
        if (topRecordSchema is not HollowObjectSchema { PrimaryKey: { } primaryKey })
        {
            return [];
        }

        int[] locations = new int[primaryKey.FieldCount];

        for (int i = 0; i < primaryKey.FieldCount; i++)
        {
            locations[i] = LocatePrimaryKeyField(
                topRecordLocation, primaryKey.GetFieldPathIndex(_dataset, i), 0);

            if (locations[i] == -1)
            {
                throw new InvalidOperationException(
                    $"the primary key field {primaryKey.GetFieldPath(i)} of {topRecordSchema.Name} is "
                    + "null, and a flat record has to carry its key");
            }
        }

        return locations;
    }

    /// <summary>
    /// Walks a key's field path through the records written so far, to where the value sits.
    /// </summary>
    /// <remarks>
    /// A path may step through references, and a reference in a flat record is an index into it, so
    /// following one means going back to another record that has already been written.
    /// </remarks>
    private int LocatePrimaryKeyField(int recordLocation, int[] fieldPathIndex, int step)
    {
        int schemaId = VarInt.ReadVInt(_buffer.UnderlyingArray, recordLocation);
        HollowObjectSchema schema = (HollowObjectSchema)_schemaIdMapper.GetSchema(schemaId)!;
        recordLocation += VarInt.SizeOfVInt(schemaId);

        int fieldOffset = NavigateToField(schema, fieldPathIndex[step], recordLocation);

        if (VarInt.ReadVNull(_buffer.UnderlyingArray, fieldOffset))
        {
            return -1;
        }

        if (step == fieldPathIndex.Length - 1)
        {
            return fieldOffset;
        }

        int referenced = VarInt.ReadVInt(_buffer.UnderlyingArray, fieldOffset);

        return referenced == -1
            ? -1
            : LocatePrimaryKeyField(_locationsByOrdinal.Get(referenced), fieldPathIndex, step + 1);
    }

    private int NavigateToField(HollowObjectSchema schema, int fieldIndex, int offset)
    {
        for (int i = 0; i < fieldIndex; i++)
        {
            switch (schema.GetFieldType(i))
            {
                case FieldType.Int:
                case FieldType.Long:
                case FieldType.Reference:
                    offset += VarInt.NextVLongSize(_buffer.UnderlyingArray, offset);
                    break;

                case FieldType.Bytes:
                case FieldType.String:
                {
                    int length = VarInt.ReadVInt(_buffer.UnderlyingArray, offset);
                    offset += VarInt.SizeOfVInt(length) + length;
                    break;
                }

                case FieldType.Boolean:
                    offset++;
                    break;

                case FieldType.Double:
                    offset += sizeof(long);
                    break;

                case FieldType.Float:
                    offset += sizeof(int);
                    break;

                case FieldType.Decimal:
                    offset += sizeof(long) * 2;
                    break;

                default:
                    throw new InvalidOperationException($"unknown field type {schema.GetFieldType(i)}");
            }
        }

        return offset;
    }

    /// <summary>Where one already-written record sits, and how long it is.</summary>
    private readonly record struct RecordLocation(int Ordinal, long Start, int Length);
}
