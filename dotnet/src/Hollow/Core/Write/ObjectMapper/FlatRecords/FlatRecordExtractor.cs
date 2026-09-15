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

using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.List;
using Hollow.Core.Read.Engine.Map;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Read.Engine.Set;
using Hollow.Core.Read.Iterator;
using Hollow.Core.Schema;
using Hollow.Core.Write.Copy;

namespace Hollow.Core.Write.ObjectMapper.FlatRecords;

/// <summary>
/// Lifts one record, and everything it reaches, out of a read state engine as a
/// <see cref="FlatRecord"/>.
/// </summary>
/// <remarks>
/// <para>
/// The record copiers do the copying; all this adds is an ordinal remapper that answers with the flat
/// record's own indexes instead of the destination dataset's ordinals, and the traversal that makes
/// sure a record is written before anything that references it.
/// </para>
/// <para>
/// Safe to call from several threads: one extraction happens at a time, because the writer it shares
/// is not reusable half-way through.
/// </para>
/// </remarks>
public sealed class FlatRecordExtractor
{
    private readonly HollowReadStateEngine _stateEngine;
    private readonly FlatRecordWriter _writer;
    private readonly ExtractorOrdinalRemapper _ordinalRemapper = new();
    private readonly Dictionary<string, HollowRecordCopier> _copiers = new(StringComparer.Ordinal);
    private readonly Lock _extraction = new();

    /// <summary>
    /// Extracts from <paramref name="stateEngine"/>, naming schemas through
    /// <paramref name="schemaIdMapper"/>.
    /// </summary>
    public FlatRecordExtractor(
        HollowReadStateEngine stateEngine, IHollowSchemaIdentifierMapper schemaIdMapper)
    {
        ArgumentNullException.ThrowIfNull(stateEngine);
        ArgumentNullException.ThrowIfNull(schemaIdMapper);

        _stateEngine = stateEngine;
        _writer = new FlatRecordWriter(stateEngine, schemaIdMapper);
    }

    /// <summary>
    /// Extracts the record at <paramref name="ordinal"/> of <paramref name="type"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The dataset has no such type.</exception>
    public FlatRecord Extract(string type, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(type);

        HollowTypeReadState typeState = _stateEngine.GetTypeState(type)
            ?? throw new ArgumentException($"the dataset has no type {type}", nameof(type));

        lock (_extraction)
        {
            _writer.Reset();
            _ordinalRemapper.Clear();

            ExtractRecord(typeState, ordinal);

            return _writer.GenerateFlatRecord();
        }
    }

    /// <summary>
    /// Writes <paramref name="ordinal"/> and everything below it, and returns its index in the flat
    /// record.
    /// </summary>
    private int ExtractRecord(HollowTypeReadState typeState, int ordinal)
    {
        string type = typeState.Schema.Name;

        if (_ordinalRemapper.OrdinalIsMapped(type, ordinal))
        {
            // Already written, either because two fields lead to it or because it is shared.
            return _ordinalRemapper.GetMappedOrdinal(type, ordinal);
        }

        // Everything this record references has to be in the flat record before the record itself:
        // a reference is an index backwards into what is already written.
        ExtractReferencedRecords(typeState, ordinal);

        IHollowWriteRecord copy = CopierFor(typeState).Copy(ordinal);
        int index = _writer.Write(typeState.Schema, copy);

        _ordinalRemapper.RemapOrdinal(type, ordinal, index);

        return index;
    }

    private void ExtractReferencedRecords(HollowTypeReadState typeState, int ordinal)
    {
        switch (typeState)
        {
            case HollowObjectTypeReadState objectState:
            {
                HollowObjectSchema schema = objectState.Schema;

                for (int field = 0; field < schema.FieldCount; field++)
                {
                    if (schema.GetFieldType(field) != FieldType.Reference)
                    {
                        continue;
                    }

                    int referenced = objectState.ReadOrdinal(ordinal, field);

                    if (referenced != -1)
                    {
                        ExtractRecord(ReferencedState(schema.GetReferencedType(field)!), referenced);
                    }
                }

                break;
            }

            case HollowListTypeReadState listState:
            {
                HollowTypeReadState elementState = ReferencedState(listState.Schema.ElementType);

                foreach (int element in listState.ElementOrdinals(ordinal))
                {
                    ExtractRecord(elementState, element);
                }

                break;
            }

            case HollowSetTypeReadState setState:
            {
                HollowTypeReadState elementState = ReferencedState(setState.Schema.ElementType);

                foreach (int element in setState.ElementOrdinals(ordinal))
                {
                    ExtractRecord(elementState, element);
                }

                break;
            }

            case HollowMapTypeReadState mapState:
            {
                HollowTypeReadState keyState = ReferencedState(mapState.Schema.KeyType);
                HollowTypeReadState valueState = ReferencedState(mapState.Schema.ValueType);

                foreach (HollowMapEntry entry in mapState.Entries(ordinal))
                {
                    ExtractRecord(keyState, entry.KeyOrdinal);
                    ExtractRecord(valueState, entry.ValueOrdinal);
                }

                break;
            }

            default:
                throw new InvalidOperationException(
                    $"unknown type state {typeState.GetType().Name} for {typeState.Schema.Name}");
        }
    }

    private HollowTypeReadState ReferencedState(string type) =>
        _stateEngine.GetTypeState(type)
        ?? throw new InvalidOperationException(
            $"the dataset references type {type}, which it does not contain");

    private HollowRecordCopier CopierFor(HollowTypeReadState typeState)
    {
        if (_copiers.TryGetValue(typeState.Schema.Name, out HollowRecordCopier? copier))
        {
            return copier;
        }

        // Hash positions are the source dataset's layout of its buckets, and a flat record carries no
        // hash table, so preserving them would only pad the record with holes.
        copier = HollowRecordCopier.Create(
            typeState, typeState.Schema, _ordinalRemapper, preserveHashPositions: false);

        _copiers[typeState.Schema.Name] = copier;

        return copier;
    }

    /// <summary>
    /// Maps a dataset ordinal to the index the record took in the flat record being written.
    /// </summary>
    private sealed class ExtractorOrdinalRemapper : IOrdinalRemapper
    {
        private readonly Dictionary<(string Type, int Ordinal), int> _mapped = [];

        public void Clear() => _mapped.Clear();

        public int GetMappedOrdinal(string type, int originalOrdinal) =>
            _mapped.TryGetValue((type, originalOrdinal), out int mapped)
                ? mapped
                : throw new InvalidOperationException(
                    $"{type} ordinal {originalOrdinal} has not been written to the flat record yet");

        public void RemapOrdinal(string type, int originalOrdinal, int mappedOrdinal) =>
            _mapped[(type, originalOrdinal)] = mappedOrdinal;

        public bool OrdinalIsMapped(string type, int originalOrdinal) =>
            _mapped.ContainsKey((type, originalOrdinal));
    }
}
