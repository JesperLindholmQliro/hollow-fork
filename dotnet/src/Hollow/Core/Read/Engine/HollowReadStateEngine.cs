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

using Hollow.Api.Error;
using Hollow.Core.Memory;
using Hollow.Core.Memory.Pool;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Schema;

namespace Hollow.Core.Read.Engine;

/// <summary>
/// A complete, readable copy of a dataset: the consumer-side counterpart of
/// <c>HollowWriteStateEngine</c>.
/// </summary>
/// <remarks>
/// <para>
/// A read state engine is populated by <c>HollowBlobReader</c> from a snapshot blob, and holds
/// one <see cref="HollowTypeReadState"/> per type in the dataset.
/// </para>
/// <para>
/// <strong>Port note.</strong> Delta and reverse-delta transitions, header tags, and the
/// <c>api/sampling</c> hooks present on the Java class are not ported — see <c>PORTING.md</c>.
/// </para>
/// </remarks>
public sealed class HollowReadStateEngine : IHollowDataAccess
{
    private readonly Dictionary<string, HollowTypeReadState> _typeStates = new(StringComparer.Ordinal);

    /// <summary>
    /// Initialises an empty read state engine.
    /// </summary>
    public HollowReadStateEngine(MemoryMode memoryMode = MemoryMode.OnHeap, IArraySegmentRecycler? memoryRecycler = null)
    {
        if (memoryMode != MemoryMode.OnHeap)
        {
            throw new NotSupportedException(
                $"Memory mode {memoryMode} is not supported by the .NET port; see PORTING.md");
        }

        MemoryMode = memoryMode;
        MemoryRecycler = memoryRecycler ?? new RecyclingRecycler();
    }

    /// <summary>The memory mode this engine holds its data in.</summary>
    public MemoryMode MemoryMode { get; }

    /// <summary>The pool this engine draws record storage from.</summary>
    public IArraySegmentRecycler MemoryRecycler { get; }

    /// <summary>
    /// The randomized tag of the state currently held, which a delta must name as its origin.
    /// </summary>
    public long RandomizedTag { get; internal set; }

    /// <summary>The header tags carried by the blob this state was read from.</summary>
    public IReadOnlyDictionary<string, string> HeaderTags { get; internal set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The read states of every type in this dataset, keyed by type name.</summary>
    public IReadOnlyDictionary<string, HollowTypeReadState> TypeStates => _typeStates;

    /// <inheritdoc />
    public IReadOnlyList<HollowSchema> Schemas => [.. _typeStates.Values.Select(state => state.Schema)];

    /// <summary>
    /// Gets the read state of the named type, or <see langword="null"/> when this dataset has no such
    /// type.
    /// </summary>
    public HollowTypeReadState? GetTypeState(string typeName) => _typeStates.GetValueOrDefault(typeName);

    /// <inheritdoc />
    public HollowSchema? GetSchema(string typeName) => GetTypeState(typeName)?.Schema;

    /// <inheritdoc />
    public HollowSchema GetNonNullSchema(string typeName) =>
        GetSchema(typeName) ?? throw new SchemaNotFoundException(typeName, _typeStates.Keys);

    /// <inheritdoc />
    public IHollowTypeDataAccess? GetTypeDataAccess(string type) => GetTypeState(type);

    /// <inheritdoc />
    public IHollowTypeDataAccess? GetTypeDataAccess(string type, int ordinal) => GetTypeState(type);

    /// <summary>
    /// Registers <paramref name="typeState"/>, attaching a <see cref="PopulatedOrdinalListener"/> to
    /// it.
    /// </summary>
    internal void AddTypeState(HollowTypeReadState typeState)
    {
        typeState.AddListener(new PopulatedOrdinalListener());
        _typeStates[typeState.TypeName] = typeState;
    }

    /// <summary>
    /// Tells every type that a delta transition is starting, so listeners can snapshot their state.
    /// </summary>
    internal void NotifyBeginUpdate()
    {
        foreach (HollowTypeReadState typeState in _typeStates.Values)
        {
            typeState.BeginUpdate();
        }
    }

    /// <summary>
    /// Tells every type that a delta transition has finished.
    /// </summary>
    internal void NotifyEndUpdate()
    {
        foreach (HollowTypeReadState typeState in _typeStates.Values)
        {
            typeState.EndUpdate();
        }
    }

    /// <summary>
    /// Resolves the cross-type references each schema holds, now that every type state exists.
    /// </summary>
    internal void WireSchemaReferences()
    {
        foreach (HollowTypeReadState typeState in _typeStates.Values)
        {
            switch (typeState.Schema)
            {
                case HollowObjectSchema objectSchema:
                    for (int i = 0; i < objectSchema.FieldCount; i++)
                    {
                        if (objectSchema.GetFieldType(i) == FieldType.Reference)
                        {
                            objectSchema.SetReferencedTypeState(i, GetTypeState(objectSchema.GetReferencedType(i)!));
                        }
                    }

                    break;

                case HollowCollectionSchema collectionSchema:
                    collectionSchema.ElementTypeState = GetTypeState(collectionSchema.ElementType);
                    break;

                case HollowMapSchema mapSchema:
                    mapSchema.KeyTypeState = GetTypeState(mapSchema.KeyType);
                    mapSchema.ValueTypeState = GetTypeState(mapSchema.ValueType);
                    break;

                default:
                    throw new UnrecognizedSchemaTypeException(typeState.TypeName, typeState.Schema.SchemaType);
            }
        }
    }
}
