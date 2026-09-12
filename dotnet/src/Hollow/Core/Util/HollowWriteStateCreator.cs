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
using Hollow.Core.Schema;
using Hollow.Core.Write;
using Hollow.Core.Write.Copy;

namespace Hollow.Core.Util;

/// <summary>
/// Builds a <see cref="HollowWriteStateEngine"/> that is already carrying a data model, and optionally
/// already carrying the records of a published state.
/// </summary>
public static class HollowWriteStateCreator
{
    /// <summary>
    /// Creates a write state engine with a type state registered for each of
    /// <paramref name="schemas"/>.
    /// </summary>
    public static HollowWriteStateEngine CreateWithSchemas(IEnumerable<HollowSchema> schemas)
    {
        HollowWriteStateEngine stateEngine = new();
        PopulateStateEngineWithTypeWriteStates(stateEngine, schemas);

        return stateEngine;
    }

    /// <summary>
    /// Registers a type state on <paramref name="stateEngine"/> for each of <paramref name="schemas"/>
    /// that it does not already have, in dependency order.
    /// </summary>
    public static void PopulateStateEngineWithTypeWriteStates(
        HollowWriteStateEngine stateEngine, IEnumerable<HollowSchema> schemas)
    {
        ArgumentNullException.ThrowIfNull(stateEngine);
        ArgumentNullException.ThrowIfNull(schemas);

        foreach (HollowSchema schema in HollowSchemaSorter.DependencyOrderedSchemaList(schemas))
        {
            if (stateEngine.GetTypeState(schema.Name) is not null)
            {
                continue;
            }

            stateEngine.AddTypeState(
                CreateTypeWriteState(schema, stateEngine.PartitionedOrdinalMap));
        }
    }

    /// <summary>
    /// Creates a write state engine holding the same data model and the same records as
    /// <paramref name="readEngine"/>.
    /// </summary>
    /// <remarks>
    /// The result is ready to write a snapshot that reproduces <paramref name="readEngine"/> exactly.
    /// To continue <paramref name="readEngine"/>'s delta chain instead, use
    /// <see cref="HollowWriteStateEngine.RestoreFrom"/>.
    /// </remarks>
    public static HollowWriteStateEngine RecreateAndPopulateUsingReadEngine(HollowReadStateEngine readEngine)
    {
        ArgumentNullException.ThrowIfNull(readEngine);

        HollowWriteStateEngine writeEngine = new();

        PopulateStateEngineWithTypeWriteStates(writeEngine, readEngine.Schemas);
        PopulateUsingReadEngine(writeEngine, readEngine);

        return writeEngine;
    }

    /// <summary>
    /// Copies every record of <paramref name="readEngine"/> into <paramref name="writeEngine"/>, keeping
    /// each record's ordinal.
    /// </summary>
    /// <param name="writeEngine">
    /// A newly created engine that already holds a data model and no records.
    /// </param>
    /// <param name="readEngine">The state to copy records from.</param>
    /// <param name="preserveHashPositions">
    /// Whether a copied set or map element keeps the bucket it occupied in the source. Keep this set
    /// unless the destination deliberately rehashes, since the bucket layout is part of a record's
    /// serialised form.
    /// </param>
    /// <remarks>
    /// Where the two data models differ: a removed field or type is dropped, an added field is null in
    /// every copied record, and an added type starts empty.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="writeEngine"/> already holds records.
    /// </exception>
    public static void PopulateUsingReadEngine(
        HollowWriteStateEngine writeEngine, HollowReadStateEngine readEngine, bool preserveHashPositions = true)
    {
        ArgumentNullException.ThrowIfNull(writeEngine);
        ArgumentNullException.ThrowIfNull(readEngine);

        foreach (HollowTypeWriteState writeState in writeEngine.OrderedTypeStates)
        {
            if (writeState.CurrentCyclePopulated.Cardinality() != 0
                || writeState.PreviousCyclePopulated.Cardinality() != 0)
            {
                throw new InvalidOperationException("The supplied HollowWriteStateEngine is already populated!");
            }
        }

        foreach (HollowTypeReadState readState in readEngine.TypeStates.Values)
        {
            if (writeEngine.GetTypeState(readState.TypeName) is not { } writeState)
            {
                continue;
            }

            writeState.SetNumShards(readState.NumShards);

            HollowRecordCopier copier = HollowRecordCopier.Create(
                readState, writeState.Schema, IdentityOrdinalRemapper.Instance, preserveHashPositions);

            BitSet populatedOrdinals = readState.PopulatedOrdinals;
            writeState.ResizeOrdinalMap(populatedOrdinals.Cardinality());

            for (int ordinal = populatedOrdinals.NextSetBit(0);
                ordinal != -1;
                ordinal = populatedOrdinals.NextSetBit(ordinal + 1))
            {
                writeState.MapOrdinal(
                    copier.Copy(ordinal), ordinal, markPreviousCycle: false, markCurrentCycle: true);
            }

            // MapOrdinal deliberately leaves the free-ordinal pool alone, so it has to be rebuilt once
            // the whole type is in; otherwise the next cycle hands out ordinals that are already taken.
            writeState.RecalculateFreeOrdinals();
        }

        writeEngine.OverridePreviousHeaderTags(readEngine.HeaderTags);

        foreach ((string name, string value) in readEngine.HeaderTags)
        {
            writeEngine.AddHeaderTag(name, value);
        }

        writeEngine.RandomizedTag = readEngine.RandomizedTag;
        writeEngine.PrepareForWrite();
    }

    private static HollowTypeWriteState CreateTypeWriteState(HollowSchema schema, bool partitioned) =>
        schema switch
        {
            HollowObjectSchema objectSchema => new HollowObjectTypeWriteState(
                objectSchema, usePartitionedOrdinalMap: partitioned),
            HollowListSchema listSchema => new HollowListTypeWriteState(
                listSchema, usePartitionedOrdinalMap: partitioned),
            HollowSetSchema setSchema => new HollowSetTypeWriteState(
                setSchema, usePartitionedOrdinalMap: partitioned),
            HollowMapSchema mapSchema => new HollowMapTypeWriteState(
                mapSchema, usePartitionedOrdinalMap: partitioned),
            _ => throw new UnrecognizedSchemaTypeException(schema.Name, schema.SchemaType),
        };
}
