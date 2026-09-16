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

using Hollow.Core;
using Hollow.Core.Memory.Encoding;
using Hollow.Core.Memory.Pool;
using Hollow.Core.Read;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.List;
using Hollow.Core.Read.Engine.Map;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Read.Engine.Set;
using Hollow.Core.Read.Filter;
using Hollow.Core.Schema;
using Hollow.Core.Util;
using Hollow.Core.Write;

namespace Hollow.Tools.Filter;

/// <summary>
/// Rewrites a blob with some of its types and fields left out, without building a state engine.
/// </summary>
/// <remarks>
/// <para>
/// A consumer can filter as it loads, and usually should — that is where the memory is saved. This is
/// for the other case: a producer that must not <em>send</em> some of the data, whatever the consumer
/// would have done with it. The filtering happens once at the producer rather than on every consumer,
/// and what is removed is gone from the bytes.
/// </para>
/// <para>
/// Several filters can be applied in one pass, each to its own output. That is the point of the
/// design: the blob is read once and the parts every output keeps are written to all of them
/// together, so filtering for ten audiences costs barely more than filtering for one.
/// </para>
/// <para>
/// Nothing here decodes a record. Type states are copied verbatim, and the only piece that is picked
/// apart is an object type's fixed-length field storage — because dropping a field means rewriting
/// the bit packing of every record of that type. Everything else is a length-prefixed run of bytes
/// moved from one stream to several.
/// </para>
/// </remarks>
public sealed class FilteredHollowBlobWriter
{
    private readonly ITypeFilter[] _filters;
    private readonly HollowBlobHeaderReader _headerReader = new();
    private readonly HollowBlobHeaderWriter _headerWriter = new();
    private readonly IArraySegmentRecycler _memoryRecycler = WastefulRecycler.DefaultInstance;

    /// <summary>Writes one filtered blob per filter given.</summary>
    /// <exception cref="ArgumentException">No filter was given.</exception>
    public FilteredHollowBlobWriter(params ITypeFilter[] filters)
    {
        ArgumentNullException.ThrowIfNull(filters);

        _filters = filters.Length > 0
            ? [.. filters]
            : throw new ArgumentException("at least one filter is needed", nameof(filters));
    }

    /// <summary>
    /// Filters a snapshot from <paramref name="input"/> into <paramref name="outputs"/>.
    /// </summary>
    /// <remarks>One output per filter, in the same order.</remarks>
    public void FilterSnapshot(Stream input, params Stream[] outputs) =>
        Filter(isDelta: false, input, outputs);

    /// <summary>Filters a delta, or a reverse delta, the same way.</summary>
    public void FilterDelta(Stream input, params Stream[] outputs) =>
        Filter(isDelta: true, input, outputs);

    /// <summary>
    /// Filters a blob from <paramref name="input"/> into <paramref name="outputs"/>.
    /// </summary>
    /// <param name="isDelta">
    /// Whether the blob is a delta. A delta carries its removed and added ordinals where a snapshot
    /// carries its populated ones, so the two are walked differently.
    /// </param>
    /// <param name="input">The blob to read.</param>
    /// <param name="outputs">One stream per filter, in the order the filters were given.</param>
    /// <exception cref="ArgumentException">
    /// The number of outputs does not match the number of filters.
    /// </exception>
    public void Filter(bool isDelta, Stream input, params Stream[] outputs)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(outputs);

        if (outputs.Length != _filters.Length)
        {
            throw new ArgumentException(
                $"{_filters.Length} filter(s) need {_filters.Length} output(s), not {outputs.Length}",
                nameof(outputs));
        }

        using HollowBlobInput blob = HollowBlobInput.Serial(input, leaveOpen: true);

        FilteredOutput[] all =
            [.. outputs.Select((stream, i) => new FilteredOutput(
                _filters[i], HollowBlobOutput.Serial(stream, leaveOpen: true)))];

        WriteHeaders(blob, all);

        int numStates = VarInt.ReadVInt(blob);

        for (int i = 0; i < numStates; i++)
        {
            HollowSchema schema = HollowSchema.ReadFrom(blob);
            int numShards = ReadNumShards(blob);

            FilteredOutput[] keeping = [.. all.Where(output => output.Filter.Includes(schema.Name))];

            CopyTypeState(isDelta, blob, schema, numShards, keeping);
        }
    }

    /// <summary>
    /// Writes each output's header, carrying only the schemas that output keeps.
    /// </summary>
    /// <remarks>
    /// The schema count follows the header as its own varint, and is what the type states below are
    /// counted against — so it has to be the filtered count, not the original.
    /// </remarks>
    private void WriteHeaders(HollowBlobInput blob, FilteredOutput[] all)
    {
        HollowBlobHeader header = _headerReader.ReadHeader(blob);
        IReadOnlyList<HollowSchema> schemas = header.Schemas;

        foreach (FilteredOutput output in all)
        {
            HollowSchema[] kept = [.. schemas
                .Where(schema => output.Filter.Includes(schema.Name))
                .Select(schema => Filtered(schema, output.Filter))];

            header.Schemas = kept;
            _headerWriter.WriteHeader(header, output.Output);

            VarInt.WriteVInt(output.Output, kept.Length);
        }

        // Left as the last output saw it otherwise, which would be a trap for anything reading the
        // header afterwards. Nothing does, but restoring it costs nothing and removes the question.
        header.Schemas = schemas;
    }

    private void CopyTypeState(
        bool isDelta,
        HollowBlobInput blob,
        HollowSchema schema,
        int numShards,
        FilteredOutput[] keeping)
    {
        if (schema is HollowObjectSchema objectSchema)
        {
            // The one type whose body is rewritten rather than copied, because a dropped field
            // changes the bit packing of every record.
            if (keeping.Length == 0)
            {
                HollowObjectTypeDataElements.DiscardFromInput(blob, objectSchema, numShards, isDelta);
            }
            else
            {
                CopyFilteredObjectState(isDelta, blob, objectSchema, numShards, keeping);
            }

            return;
        }

        // A collection type is kept whole or dropped whole: there are no fields to select.
        foreach (FilteredOutput output in keeping)
        {
            WriteSchemaAndShards(schema, numShards, output.Output);
        }

        HollowBlobOutput[] outputs = [.. keeping.Select(output => output.Output)];

        switch (schema)
        {
            case HollowListSchema when keeping.Length == 0:
                HollowListTypeDataElements.DiscardFromInput(blob, numShards, isDelta);
                break;

            case HollowListSchema:
                CopyCollectionState(isDelta, blob, numShards, outputs, varInts: 2, vLongs: 1);
                break;

            case HollowSetSchema when keeping.Length == 0:
                HollowSetTypeDataElements.DiscardFromInput(blob, numShards, isDelta);
                break;

            case HollowSetSchema:
                CopyCollectionState(isDelta, blob, numShards, outputs, varInts: 3, vLongs: 1);
                break;

            case HollowMapSchema when keeping.Length == 0:
                HollowMapTypeDataElements.DiscardFromInput(blob, numShards, isDelta);
                break;

            case HollowMapSchema:
                CopyCollectionState(isDelta, blob, numShards, outputs, varInts: 4, vLongs: 1);
                break;

            default:
                throw new InvalidOperationException($"unknown schema kind for {schema.Name}");
        }
    }

    /// <summary>
    /// Copies a list, set or map type state verbatim.
    /// </summary>
    /// <remarks>
    /// Java writes this out three times, once per collection kind. The three differ only in how many
    /// bit-width varints sit between the ordinals and the two long arrays — two for a list, three for
    /// a set, four for a map — so they are one method with a count. Getting that count wrong
    /// misaligns everything after it, which is why it is named at each call site.
    /// </remarks>
    private static void CopyCollectionState(
        bool isDelta,
        HollowBlobInput blob,
        int numShards,
        HollowBlobOutput[] outputs,
        int varInts,
        int vLongs)
    {
        if (numShards > 1)
        {
            BlobCopy.CopyVInt(blob, outputs);
        }

        for (int shard = 0; shard < numShards; shard++)
        {
            BlobCopy.CopyVInt(blob, outputs);

            if (isDelta)
            {
                BlobCopy.CopyEncodedDeltaOrdinals(blob, outputs);
                BlobCopy.CopyEncodedDeltaOrdinals(blob, outputs);
            }

            for (int i = 0; i < varInts; i++)
            {
                BlobCopy.CopyVInt(blob, outputs);
            }

            for (int i = 0; i < vLongs; i++)
            {
                BlobCopy.CopyVLong(blob, outputs);
            }

            BlobCopy.CopySegmentedLongArray(blob, outputs);
            BlobCopy.CopySegmentedLongArray(blob, outputs);
        }

        if (!isDelta)
        {
            BlobCopy.CopyPopulatedOrdinals(blob, outputs);
        }
    }

    /// <summary>
    /// Copies an object type state, dropping the fields each output does not keep.
    /// </summary>
    /// <remarks>
    /// Every record's fixed-length fields are packed end to end in one bit string, so dropping a
    /// field means reading that string and writing a narrower one. The variable-length data behind
    /// it is per-field and can be copied or skipped wholesale.
    /// </remarks>
    private void CopyFilteredObjectState(
        bool isDelta,
        HollowBlobInput blob,
        HollowObjectSchema schema,
        int numShards,
        FilteredOutput[] keeping)
    {
        HollowBlobOutput[] outputs = [.. keeping.Select(output => output.Output)];

        HollowObjectSchema[] filteredSchemas =
            [.. keeping.Select(output => (HollowObjectSchema)Filtered(schema, output.Filter))];

        for (int i = 0; i < keeping.Length; i++)
        {
            WriteSchemaAndShards(filteredSchemas[i], numShards, outputs[i]);
        }

        if (numShards > 1)
        {
            BlobCopy.CopyVInt(blob, outputs);
        }

        for (int shard = 0; shard < numShards; shard++)
        {
            CopyObjectShard(isDelta, blob, schema, filteredSchemas, outputs);
        }

        if (!isDelta)
        {
            BlobCopy.CopyPopulatedOrdinals(blob, outputs);
        }
    }

    private void CopyObjectShard(
        bool isDelta,
        HollowBlobInput blob,
        HollowObjectSchema schema,
        HollowObjectSchema[] filteredSchemas,
        HollowBlobOutput[] outputs)
    {
        int maxShardOrdinal = BlobCopy.CopyVInt(blob, outputs);
        int recordsToCopy = maxShardOrdinal + 1;

        if (isDelta)
        {
            BlobCopy.CopyEncodedDeltaOrdinals(blob, outputs);

            // The added ordinals have to be materialised rather than copied: how many records follow
            // is their count, because a delta's storage holds only the records it added.
            GapEncodedVariableLengthIntegerReader added =
                GapEncodedVariableLengthIntegerReader.ReadEncodedDeltaOrdinals(blob, _memoryRecycler);

            recordsToCopy = added.RemainingElements();

            foreach (HollowBlobOutput output in outputs)
            {
                added.WriteTo(output);
            }
        }

        int[] bitsPerField = new int[schema.FieldCount];

        for (int i = 0; i < bitsPerField.Length; i++)
        {
            bitsPerField[i] = VarInt.ReadVInt(blob);
        }

        // One writer per output, and a list per field of the writers that keep it, so that reading a
        // field once feeds every output that wants it.
        FixedLengthElementArray[] rewritten = new FixedLengthElementArray[outputs.Length];
        long[] bitsPerOutput = new long[outputs.Length];
        List<FixedLengthArrayWriter>[] writersPerField =
            [.. Enumerable.Range(0, schema.FieldCount).Select(_ => new List<FixedLengthArrayWriter>())];

        for (int i = 0; i < outputs.Length; i++)
        {
            long bitsPerRecord = WriteBitsPerField(schema, bitsPerField, filteredSchemas[i], outputs[i]);

            bitsPerOutput[i] = bitsPerRecord * recordsToCopy;
            rewritten[i] = new FixedLengthElementArray(_memoryRecycler, bitsPerOutput[i]);

            FixedLengthArrayWriter writer = new(rewritten[i]);

            for (int field = 0; field < schema.FieldCount; field++)
            {
                if (filteredSchemas[i].GetPosition(schema.GetFieldName(field)) != -1)
                {
                    writersPerField[field].Add(writer);
                }
            }
        }

        RewriteFixedLengthFields(blob, schema, bitsPerField, recordsToCopy, writersPerField);

        for (int i = 0; i < outputs.Length; i++)
        {
            long numLongs = bitsPerOutput[i] == 0 ? 0 : ((bitsPerOutput[i] - 1) / 64) + 1;

            rewritten[i].WriteTo(outputs[i], numLongs);
        }

        CopyVariableLengthData(blob, schema, field =>
            [.. outputs.Where((_, i) => filteredSchemas[i].GetPosition(schema.GetFieldName(field)) != -1)]);
    }

    /// <summary>
    /// Reads the packed fields of every record and writes each field to the outputs that keep it.
    /// </summary>
    private static void RewriteFixedLengthFields(
        HollowBlobInput blob,
        HollowObjectSchema schema,
        int[] bitsPerField,
        int recordsToCopy,
        List<FixedLengthArrayWriter>[] writersPerField)
    {
        FixedLengthElementArray unfiltered =
            FixedLengthElementArray.NewFrom(blob, WastefulRecycler.DefaultInstance);

        long bitsPerRecord = bitsPerField.Sum(bits => (long)bits);
        long stopBit = bitsPerRecord * recordsToCopy;
        long bitCursor = 0;
        int field = 0;

        while (bitCursor < stopBit)
        {
            if (writersPerField[field].Count > 0)
            {
                // Above 56 bits a value can straddle three words, which the fast read does not handle.
                long value = bitsPerField[field] > 56
                    ? unfiltered.GetLargeElementValue(bitCursor, bitsPerField[field])
                    : unfiltered.GetElementValue(bitCursor, bitsPerField[field]);

                foreach (FixedLengthArrayWriter writer in writersPerField[field])
                {
                    writer.WriteField(value, bitsPerField[field]);
                }
            }

            bitCursor += bitsPerField[field];

            if (++field == schema.FieldCount)
            {
                field = 0;
            }
        }
    }

    /// <summary>
    /// Copies the variable-length data behind each field to the outputs that keep it.
    /// </summary>
    /// <remarks>
    /// Every field has a run here, whether or not it is variable-length — a fixed-length field's run
    /// is empty — so all of them have to be walked even where none is kept, or the next type state
    /// starts at the wrong byte.
    /// </remarks>
    private static void CopyVariableLengthData(
        HollowBlobInput blob,
        HollowObjectSchema schema,
        Func<int, HollowBlobOutput[]> keepingPerField)
    {
        for (int field = 0; field < schema.FieldCount; field++)
        {
            HollowBlobOutput[] keeping = keepingPerField(field);

            long numBytes = BlobCopy.CopyVLong(blob, keeping);

            BlobCopy.CopyBytes(blob, keeping, numBytes);
        }
    }

    /// <summary>
    /// Writes the bit widths of the fields <paramref name="filtered"/> keeps, returning how many bits
    /// a record of it occupies.
    /// </summary>
    private static long WriteBitsPerField(
        HollowObjectSchema schema,
        int[] bitsPerField,
        HollowObjectSchema filtered,
        HollowBlobOutput output)
    {
        long bitsPerRecord = 0;

        for (int i = 0; i < schema.FieldCount; i++)
        {
            if (filtered.GetPosition(schema.GetFieldName(i)) != -1)
            {
                VarInt.WriteVInt(output, bitsPerField[i]);
                bitsPerRecord += bitsPerField[i];
            }
        }

        return bitsPerRecord;
    }

    /// <summary>Writes a type's schema and shard count, in the framing a type state header takes.</summary>
    private static void WriteSchemaAndShards(
        HollowSchema schema, int numShards, HollowBlobOutput output)
    {
        schema.WriteTo(output);

        VarInt.WriteVInt(output, 1 + VarInt.SizeOfVInt(numShards));
        VarInt.WriteVInt(output, 0); // Forwards compatibility: no extra bytes.
        VarInt.WriteVInt(output, numShards);
    }

    /// <summary>
    /// <paramref name="schema"/> with the fields <paramref name="filter"/> drops removed.
    /// </summary>
    /// <remarks>The schema itself where nothing is dropped, rather than an identical rebuild.</remarks>
    private static HollowSchema Filtered(HollowSchema schema, ITypeFilter filter)
    {
        if (schema is not HollowObjectSchema objectSchema)
        {
            return schema;
        }

        string[] kept = [.. Enumerable.Range(0, objectSchema.FieldCount)
            .Select(objectSchema.GetFieldName)
            .Where(field => filter.Includes(schema.Name, field))];

        if (kept.Length == objectSchema.FieldCount)
        {
            return schema;
        }

        HollowObjectSchema filtered = new(objectSchema.Name, kept.Length, objectSchema.PrimaryKey);

        for (int i = 0; i < objectSchema.FieldCount; i++)
        {
            if (filter.Includes(schema.Name, objectSchema.GetFieldName(i)))
            {
                filtered.AddField(
                    objectSchema.GetFieldName(i),
                    objectSchema.GetFieldType(i),
                    objectSchema.GetReferencedType(i));
            }
        }

        return filtered;
    }

    /// <summary>
    /// Reads a type state's shard count, tolerating the framing Hollow used before 2.1.0.
    /// </summary>
    private static int ReadNumShards(HollowBlobInput blob)
    {
        int backwardsCompatibilityBytes = VarInt.ReadVInt(blob);

        if (backwardsCompatibilityBytes == 0)
        {
            // Written before sharding existed, so there is one shard and no count to read.
            return 1;
        }

        long bytesToSkip = VarInt.ReadVInt(blob);

        while (bytesToSkip > 0)
        {
            long skipped = blob.SkipBytes(bytesToSkip);

            if (skipped <= 0)
            {
                throw new EndOfStreamException("the blob ended inside a type state's header");
            }

            bytesToSkip -= skipped;
        }

        return VarInt.ReadVInt(blob);
    }

    /// <summary>One output and the filter that decides what reaches it.</summary>
    private readonly record struct FilteredOutput(ITypeFilter Filter, HollowBlobOutput Output);
}

/// <summary>
/// Appends bit-packed fields to a <see cref="FixedLengthElementArray"/>, keeping its own cursor.
/// </summary>
/// <remarks>
/// The array indexes by bit position, and a rewrite has to know where the last field ended. Holding
/// the cursor here is what lets one pass over the source feed several differently-filtered outputs.
/// </remarks>
internal sealed class FixedLengthArrayWriter(FixedLengthElementArray array)
{
    private long _bitCursor;

    /// <summary>Appends <paramref name="value"/> as <paramref name="numBits"/> bits.</summary>
    internal void WriteField(long value, int numBits)
    {
        array.SetElementValue(_bitCursor, numBits, value);
        _bitCursor += numBits;
    }
}
