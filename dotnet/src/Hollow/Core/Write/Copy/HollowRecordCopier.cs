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
using Hollow.Core.Schema;

namespace Hollow.Core.Write.Copy;

/// <summary>
/// Reads a record back out of a <see cref="HollowTypeReadState"/> as a write record, so that a producer
/// can put it into a <see cref="HollowWriteStateEngine"/> again.
/// </summary>
/// <remarks>
/// <para>
/// A copier reuses one write record across calls, so <see cref="Copy"/> returns the same instance each
/// time. Callers must serialise the returned record before copying the next one.
/// </para>
/// <para>
/// This is what restore is built on: the read state is the only surviving copy of a published cycle, so
/// resuming a delta chain means reading every record back and re-adding it at the ordinal it already
/// holds.
/// </para>
/// </remarks>
public abstract class HollowRecordCopier
{
    /// <summary>
    /// Initialises a copier reading from <paramref name="readTypeState"/> into
    /// <paramref name="writeRecord"/>.
    /// </summary>
    /// <param name="readTypeState">The state to read records from.</param>
    /// <param name="writeRecord">The record instance reused across copies.</param>
    /// <param name="ordinalRemapper">Translates the ordinals a copied record references.</param>
    /// <param name="preserveHashPositions">
    /// Whether a copied set or map element keeps the bucket it occupied in the source, rather than being
    /// rehashed from its ordinal.
    /// </param>
    protected HollowRecordCopier(
        HollowTypeReadState readTypeState,
        IHollowWriteRecord writeRecord,
        IOrdinalRemapper ordinalRemapper,
        bool preserveHashPositions)
    {
        ArgumentNullException.ThrowIfNull(readTypeState);
        ArgumentNullException.ThrowIfNull(writeRecord);
        ArgumentNullException.ThrowIfNull(ordinalRemapper);

        ReadTypeState = readTypeState;
        WriteRecord = writeRecord;
        OrdinalRemapper = ordinalRemapper;
        PreserveHashPositions = preserveHashPositions;
    }

    /// <summary>The state records are read from.</summary>
    public HollowTypeReadState ReadTypeState { get; }

    /// <summary>The record instance reused across copies.</summary>
    protected IHollowWriteRecord WriteRecord { get; }

    /// <summary>Translates the ordinals a copied record references.</summary>
    protected IOrdinalRemapper OrdinalRemapper { get; }

    /// <summary>
    /// Whether a copied set or map element keeps the bucket it occupied in the source.
    /// </summary>
    protected bool PreserveHashPositions { get; }

    /// <summary>
    /// Reads the record at <paramref name="ordinal"/> into this copier's write record.
    /// </summary>
    /// <returns>
    /// The write record, which is the same instance on every call and is overwritten by the next one.
    /// </returns>
    public abstract IHollowWriteRecord Copy(int ordinal);

    /// <summary>
    /// Creates a copier that reproduces <paramref name="typeState"/>'s records exactly.
    /// </summary>
    public static HollowRecordCopier Create(HollowTypeReadState typeState)
    {
        ArgumentNullException.ThrowIfNull(typeState);

        return Create(typeState, typeState.Schema);
    }

    /// <summary>
    /// Creates a copier that reproduces <paramref name="typeState"/>'s records under
    /// <paramref name="destinationSchema"/>, which may declare fewer or more fields than the source.
    /// </summary>
    public static HollowRecordCopier Create(HollowTypeReadState typeState, HollowSchema destinationSchema) =>
        Create(typeState, destinationSchema, IdentityOrdinalRemapper.Instance, preserveHashPositions: true);

    /// <summary>
    /// Creates a copier for <paramref name="typeState"/>.
    /// </summary>
    /// <param name="typeState">The state to read records from.</param>
    /// <param name="destinationSchema">
    /// The schema the copied records take. For an object type it selects and orders the fields; for a
    /// collection type only its kind matters.
    /// </param>
    /// <param name="ordinalRemapper">Translates the ordinals a copied record references.</param>
    /// <param name="preserveHashPositions">
    /// Whether a copied set or map element keeps the bucket it occupied in the source.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="destinationSchema"/> is not of the same kind as the source type.
    /// </exception>
    public static HollowRecordCopier Create(
        HollowTypeReadState typeState,
        HollowSchema destinationSchema,
        IOrdinalRemapper ordinalRemapper,
        bool preserveHashPositions)
    {
        ArgumentNullException.ThrowIfNull(typeState);
        ArgumentNullException.ThrowIfNull(destinationSchema);

        return typeState switch
        {
            HollowObjectTypeReadState objectState when destinationSchema is HollowObjectSchema objectSchema =>
                new HollowObjectCopier(objectState, objectSchema, ordinalRemapper),

            HollowListTypeReadState listState when destinationSchema is HollowListSchema =>
                new HollowListCopier(listState, ordinalRemapper),

            HollowSetTypeReadState setState when destinationSchema is HollowSetSchema =>
                new HollowSetCopier(setState, ordinalRemapper, preserveHashPositions),

            HollowMapTypeReadState mapState when destinationSchema is HollowMapSchema =>
                new HollowMapCopier(mapState, ordinalRemapper, preserveHashPositions),

            _ => throw new ArgumentException(
                $"Cannot copy records of type {typeState.TypeName} from a {typeState.Schema.SchemaType} read "
                + $"state into a {destinationSchema.SchemaType} schema.",
                nameof(destinationSchema)),
        };
    }
}
