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

using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.Iterator;
using Hollow.Core.Read.Missing;
using Hollow.Core.Schema;

namespace Hollow.Api.Custom;

/// <summary>
/// Reads one type's records by ordinal, without a wrapper object per record.
/// </summary>
/// <remarks>
/// <para>
/// The ordinal is the handle. That makes this the layer to use in a tight loop, where allocating a
/// wrapper per record would dominate; the record wrappers in <c>Hollow.Api.Objects</c> sit on top of
/// this for the cases where a handle reads better than an integer.
/// </para>
/// <para>
/// Named <c>HollowTypeAPI</c> in Java. .NET spells acronyms of three or more letters in PascalCase,
/// so <c>API</c> becomes <c>Api</c> throughout this layer.
/// </para>
/// </remarks>
public abstract class HollowTypeApi
{
    /// <summary>
    /// Initialises a type API over <paramref name="typeDataAccess"/>.
    /// </summary>
    protected HollowTypeApi(HollowApi api, IHollowTypeDataAccess typeDataAccess)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(typeDataAccess);

        Api = api;
        TypeDataAccess = typeDataAccess;
    }

    /// <summary>The API this type belongs to.</summary>
    public HollowApi Api { get; }

    /// <summary>Read access to this type's records.</summary>
    public IHollowTypeDataAccess TypeDataAccess { get; }

    /// <summary>Answers reads of fields this dataset does not have.</summary>
    protected IMissingDataHandler MissingDataHandler => Api.DataAccess.MissingDataHandler;
}

/// <summary>
/// Reads an object type's fields by ordinal and field index.
/// </summary>
/// <remarks>
/// A generated API subclasses this once per object type, resolving each field's position up front so
/// that a read is an array lookup rather than a name lookup. A field the loaded dataset does not have
/// resolves to <see cref="MissingField"/>, and reads of it fall through to the missing-data handler.
/// </remarks>
public abstract class HollowObjectTypeApi : HollowTypeApi
{
    /// <summary>The field index standing for a field the loaded dataset does not have.</summary>
    protected const int MissingField = -1;

    private readonly string[] _fieldNames;
    private readonly int[] _fieldIndexes;

    /// <summary>
    /// Initialises a type API over <paramref name="typeDataAccess"/>, resolving the position of each
    /// of <paramref name="fieldNames"/> in the loaded schema.
    /// </summary>
    protected HollowObjectTypeApi(
        HollowApi api, IHollowObjectTypeDataAccess typeDataAccess, string[] fieldNames)
        : base(api, typeDataAccess)
    {
        ArgumentNullException.ThrowIfNull(fieldNames);

        _fieldNames = fieldNames;
        _fieldIndexes = new int[fieldNames.Length];

        HollowObjectSchema schema = typeDataAccess.Schema;

        for (int i = 0; i < fieldNames.Length; i++)
        {
            _fieldIndexes[i] = schema.GetPosition(fieldNames[i]);
        }
    }

    /// <summary>Read access to this type's records.</summary>
    public new IHollowObjectTypeDataAccess TypeDataAccess => (IHollowObjectTypeDataAccess)base.TypeDataAccess;

    /// <summary>The schema of this type as the loaded dataset declares it.</summary>
    public HollowObjectSchema Schema => TypeDataAccess.Schema;

    /// <summary>The name of this type.</summary>
    public string TypeName => Schema.Name;

    /// <summary>
    /// The position of the field the generated API knows as <paramref name="fieldPosition"/>, or
    /// <see cref="MissingField"/> when the loaded dataset does not have it.
    /// </summary>
    protected int FieldIndex(int fieldPosition) => _fieldIndexes[fieldPosition];

    /// <summary>The name the generated API knows a field by.</summary>
    protected string FieldName(int fieldPosition) => _fieldNames[fieldPosition];

    /// <summary>Whether the field is null, or missing from the loaded dataset.</summary>
    protected bool IsNullField(int ordinal, int fieldPosition) =>
        _fieldIndexes[fieldPosition] == MissingField
            ? MissingDataHandler.HandleIsNull(TypeName, ordinal, _fieldNames[fieldPosition])
            : TypeDataAccess.IsNull(ordinal, _fieldIndexes[fieldPosition]);

    /// <summary>Reads a reference field as the referenced record's ordinal.</summary>
    protected int ReadOrdinalField(int ordinal, int fieldPosition) =>
        _fieldIndexes[fieldPosition] == MissingField
            ? MissingDataHandler.HandleReferencedOrdinal(TypeName, ordinal, _fieldNames[fieldPosition])
            : TypeDataAccess.ReadOrdinal(ordinal, _fieldIndexes[fieldPosition]);

    /// <summary>Reads an int field, or the null sentinel when it is missing.</summary>
    protected int ReadIntField(int ordinal, int fieldPosition) =>
        _fieldIndexes[fieldPosition] == MissingField
            ? MissingDataHandler.HandleInt(TypeName, ordinal, _fieldNames[fieldPosition])
            : TypeDataAccess.ReadInt(ordinal, _fieldIndexes[fieldPosition]);

    /// <summary>Reads a long field, or the null sentinel when it is missing.</summary>
    protected long ReadLongField(int ordinal, int fieldPosition) =>
        _fieldIndexes[fieldPosition] == MissingField
            ? MissingDataHandler.HandleLong(TypeName, ordinal, _fieldNames[fieldPosition])
            : TypeDataAccess.ReadLong(ordinal, _fieldIndexes[fieldPosition]);

    /// <summary>Reads a float field, or NaN when it is missing.</summary>
    protected float ReadFloatField(int ordinal, int fieldPosition) =>
        _fieldIndexes[fieldPosition] == MissingField
            ? MissingDataHandler.HandleFloat(TypeName, ordinal, _fieldNames[fieldPosition])
            : TypeDataAccess.ReadFloat(ordinal, _fieldIndexes[fieldPosition]);

    /// <summary>Reads a double field, or NaN when it is missing.</summary>
    protected double ReadDoubleField(int ordinal, int fieldPosition) =>
        _fieldIndexes[fieldPosition] == MissingField
            ? MissingDataHandler.HandleDouble(TypeName, ordinal, _fieldNames[fieldPosition])
            : TypeDataAccess.ReadDouble(ordinal, _fieldIndexes[fieldPosition]);

    /// <summary>Reads a decimal field, which may be null.</summary>
    /// <remarks><strong>Format extension</strong> — see <c>PORTING.md</c>.</remarks>
    protected decimal? ReadDecimalField(int ordinal, int fieldPosition) =>
        _fieldIndexes[fieldPosition] == MissingField
            ? MissingDataHandler.HandleDecimal(TypeName, ordinal, _fieldNames[fieldPosition])
            : TypeDataAccess.ReadDecimal(ordinal, _fieldIndexes[fieldPosition]);

    /// <summary>Reads a boolean field, which may be null.</summary>
    protected bool? ReadBooleanField(int ordinal, int fieldPosition) =>
        _fieldIndexes[fieldPosition] == MissingField
            ? MissingDataHandler.HandleBoolean(TypeName, ordinal, _fieldNames[fieldPosition])
            : TypeDataAccess.ReadBoolean(ordinal, _fieldIndexes[fieldPosition]);

    /// <summary>Reads a string field, which may be null.</summary>
    protected string? ReadStringField(int ordinal, int fieldPosition) =>
        _fieldIndexes[fieldPosition] == MissingField
            ? MissingDataHandler.HandleString(TypeName, ordinal, _fieldNames[fieldPosition])
            : TypeDataAccess.ReadString(ordinal, _fieldIndexes[fieldPosition]);

    /// <summary>Reads a bytes field, which may be null.</summary>
    protected byte[]? ReadBytesField(int ordinal, int fieldPosition) =>
        _fieldIndexes[fieldPosition] == MissingField
            ? MissingDataHandler.HandleBytes(TypeName, ordinal, _fieldNames[fieldPosition])
            : TypeDataAccess.ReadBytes(ordinal, _fieldIndexes[fieldPosition]);

    /// <summary>
    /// Compares a string field against <paramref name="testValue"/> without materialising the stored
    /// string.
    /// </summary>
    protected bool IsStringFieldEqual(int ordinal, int fieldPosition, string? testValue) =>
        _fieldIndexes[fieldPosition] == MissingField
            ? MissingDataHandler.HandleStringEquals(
                TypeName, ordinal, _fieldNames[fieldPosition], testValue)
            : TypeDataAccess.IsStringFieldEqual(ordinal, _fieldIndexes[fieldPosition], testValue);
}

/// <summary>
/// Reads a list type's elements by ordinal.
/// </summary>
public class HollowListTypeApi(HollowApi api, IHollowListTypeDataAccess typeDataAccess)
    : HollowTypeApi(api, typeDataAccess)
{
    /// <summary>Read access to this type's records.</summary>
    public new IHollowListTypeDataAccess TypeDataAccess =>
        (IHollowListTypeDataAccess)base.TypeDataAccess;

    /// <summary>The number of elements in the given record.</summary>
    public int Size(int ordinal) => TypeDataAccess.Size(ordinal);

    /// <summary>The ordinal of the element at <paramref name="listIndex"/>.</summary>
    public int GetElementOrdinal(int ordinal, int listIndex) =>
        TypeDataAccess.GetElementOrdinal(ordinal, listIndex);

    /// <summary>Iterates the element ordinals of the given record.</summary>
    public IHollowOrdinalIterator OrdinalIterator(int ordinal) => TypeDataAccess.OrdinalIterator(ordinal);
}

/// <summary>
/// Reads a set type's elements by ordinal.
/// </summary>
public class HollowSetTypeApi(HollowApi api, IHollowSetTypeDataAccess typeDataAccess)
    : HollowTypeApi(api, typeDataAccess)
{
    /// <summary>Read access to this type's records.</summary>
    public new IHollowSetTypeDataAccess TypeDataAccess => (IHollowSetTypeDataAccess)base.TypeDataAccess;

    /// <summary>The number of elements in the given record.</summary>
    public int Size(int ordinal) => TypeDataAccess.Size(ordinal);

    /// <summary>Whether the given record contains the element with that ordinal.</summary>
    public bool Contains(int ordinal, int value) => TypeDataAccess.Contains(ordinal, value);

    /// <summary>
    /// Whether the given record contains the element with that ordinal, using a precomputed hash code.
    /// </summary>
    public bool Contains(int ordinal, int value, int hashCode) =>
        TypeDataAccess.Contains(ordinal, value, hashCode);

    /// <summary>The element matching a declared hash key, or <c>HollowConstants.OrdinalNone</c>.</summary>
    public int FindElement(int ordinal, params object?[] hashKey) =>
        TypeDataAccess.FindElement(ordinal, hashKey);

    /// <summary>Iterates the elements whose hash code matches.</summary>
    public IHollowOrdinalIterator PotentialMatchOrdinalIterator(int ordinal, int hashCode) =>
        TypeDataAccess.PotentialMatchOrdinalIterator(ordinal, hashCode);

    /// <summary>Iterates the element ordinals of the given record.</summary>
    public IHollowOrdinalIterator OrdinalIterator(int ordinal) => TypeDataAccess.OrdinalIterator(ordinal);
}

/// <summary>
/// Reads a map type's entries by ordinal.
/// </summary>
public class HollowMapTypeApi(HollowApi api, IHollowMapTypeDataAccess typeDataAccess)
    : HollowTypeApi(api, typeDataAccess)
{
    /// <summary>Read access to this type's records.</summary>
    public new IHollowMapTypeDataAccess TypeDataAccess => (IHollowMapTypeDataAccess)base.TypeDataAccess;

    /// <summary>The number of entries in the given record.</summary>
    public int Size(int ordinal) => TypeDataAccess.Size(ordinal);

    /// <summary>The value ordinal mapped to a key ordinal.</summary>
    public int Get(int ordinal, int keyOrdinal) => TypeDataAccess.Get(ordinal, keyOrdinal);

    /// <summary>The value ordinal mapped to a key ordinal, using a precomputed hash code.</summary>
    public int Get(int ordinal, int keyOrdinal, int hashCode) =>
        TypeDataAccess.Get(ordinal, keyOrdinal, hashCode);

    /// <summary>The key matching a declared hash key, or <c>HollowConstants.OrdinalNone</c>.</summary>
    public int FindKey(int ordinal, params object?[] hashKey) => TypeDataAccess.FindKey(ordinal, hashKey);

    /// <summary>The value matching a declared hash key, or <c>HollowConstants.OrdinalNone</c>.</summary>
    public int FindValue(int ordinal, params object?[] hashKey) =>
        TypeDataAccess.FindValue(ordinal, hashKey);

    /// <summary>The entry matching a declared hash key, packed as <c>(key &lt;&lt; 32) | value</c>.</summary>
    public long FindEntry(int ordinal, params object?[] hashKey) =>
        TypeDataAccess.FindEntry(ordinal, hashKey);

    /// <summary>Iterates every entry of the given record.</summary>
    public IHollowMapEntryOrdinalIterator OrdinalIterator(int ordinal) =>
        TypeDataAccess.OrdinalIterator(ordinal);

    /// <summary>Iterates the entries whose key hash code matches.</summary>
    public IHollowMapEntryOrdinalIterator PotentialMatchOrdinalIterator(int ordinal, int hashCode) =>
        TypeDataAccess.PotentialMatchOrdinalIterator(ordinal, hashCode);
}
