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
using Hollow.Core.Read.Iterator;
using Hollow.Core.Schema;

namespace Hollow.Core.Read.DataAccess;

/// <summary>
/// Read access to the records of a single type.
/// </summary>
/// <remarks>
/// <para>
/// Named <c>HollowTypeDataAccess</c> in Java; the <c>I</c> prefix follows the .NET interface naming
/// convention. The same applies to the derived interfaces in this file.
/// </para>
/// <para>
/// <strong>Port note.</strong> The Java interface also exposes the sampling hooks
/// (<c>setSamplingDirector</c>, <c>getSampler</c>, and friends) from <c>api/sampling</c>, which is
/// outside the core engine and is not ported — see <c>PORTING.md</c>.
/// </para>
/// </remarks>
public interface IHollowTypeDataAccess
{
    /// <summary>The dataset this type belongs to.</summary>
    IHollowDataAccess DataAccess { get; }

    /// <summary>The schema of this type.</summary>
    HollowSchema Schema { get; }

    /// <summary>The read state backing this data access.</summary>
    HollowTypeReadState TypeState { get; }
}

/// <summary>
/// Read access to a whole dataset.
/// </summary>
/// <remarks>Named <c>HollowDataAccess</c> in Java.</remarks>
public interface IHollowDataAccess : IHollowDataset
{
    /// <summary>
    /// Gets read access to the named type, or <see langword="null"/> when the dataset has no such type.
    /// </summary>
    IHollowTypeDataAccess? GetTypeDataAccess(string type);

    /// <summary>
    /// Gets read access to the named type, resolved for a specific record.
    /// </summary>
    IHollowTypeDataAccess? GetTypeDataAccess(string type, int ordinal);
}

/// <summary>
/// Read access to the fields of the records of an object type.
/// </summary>
public interface IHollowObjectTypeDataAccess : IHollowTypeDataAccess
{
    /// <summary>The schema of this type.</summary>
    new HollowObjectSchema Schema { get; }

    /// <summary>Whether the given field of the given record is null.</summary>
    bool IsNull(int ordinal, int fieldIndex);

    /// <summary>Reads a <see cref="FieldType.Reference"/> field as the referenced record's ordinal.</summary>
    int ReadOrdinal(int ordinal, int fieldIndex);

    /// <summary>Reads an <see cref="FieldType.Int"/> field.</summary>
    int ReadInt(int ordinal, int fieldIndex);

    /// <summary>Reads a <see cref="FieldType.Float"/> field.</summary>
    float ReadFloat(int ordinal, int fieldIndex);

    /// <summary>Reads a <see cref="FieldType.Double"/> field.</summary>
    double ReadDouble(int ordinal, int fieldIndex);

    /// <summary>Reads a <see cref="FieldType.Long"/> field.</summary>
    long ReadLong(int ordinal, int fieldIndex);

    /// <summary>Reads a <see cref="FieldType.Boolean"/> field, which may be null.</summary>
    bool? ReadBoolean(int ordinal, int fieldIndex);

    /// <summary>Reads a <see cref="FieldType.Bytes"/> field.</summary>
    byte[]? ReadBytes(int ordinal, int fieldIndex);

    /// <summary>
    /// Reads a <see cref="FieldType.Decimal"/> field, which may be null.
    /// </summary>
    /// <remarks>
    /// <strong>Format extension.</strong> <see cref="FieldType.Decimal"/> is not part of Netflix
    /// Hollow — see <c>PORTING.md</c>.
    /// </remarks>
    decimal? ReadDecimal(int ordinal, int fieldIndex);

    /// <summary>Reads a <see cref="FieldType.String"/> field.</summary>
    string? ReadString(int ordinal, int fieldIndex);

    /// <summary>
    /// Compares a <see cref="FieldType.String"/> field against <paramref name="testValue"/> without
    /// materialising the stored string.
    /// </summary>
    bool IsStringFieldEqual(int ordinal, int fieldIndex, string? testValue);

    /// <summary>
    /// Hashes the stored bytes of a variable-length field without materialising them.
    /// </summary>
    int FindVarLengthFieldHashCode(int ordinal, int fieldIndex);
}

/// <summary>
/// Read access to the elements of the records of a collection type.
/// </summary>
public interface IHollowCollectionTypeDataAccess : IHollowTypeDataAccess
{
    /// <summary>The schema of this type.</summary>
    new HollowCollectionSchema Schema { get; }

    /// <summary>The number of elements in the given record.</summary>
    int Size(int ordinal);

    /// <summary>Iterates the element ordinals of the given record.</summary>
    IHollowOrdinalIterator OrdinalIterator(int ordinal);
}

/// <summary>
/// Read access to the elements of the records of a list type.
/// </summary>
public interface IHollowListTypeDataAccess : IHollowCollectionTypeDataAccess
{
    /// <summary>The schema of this type.</summary>
    new HollowListSchema Schema { get; }

    /// <summary>The ordinal of the element at <paramref name="listIndex"/> of the given record.</summary>
    int GetElementOrdinal(int ordinal, int listIndex);
}

/// <summary>
/// Read access to the elements of the records of a set type.
/// </summary>
public interface IHollowSetTypeDataAccess : IHollowCollectionTypeDataAccess
{
    /// <summary>The schema of this type.</summary>
    new HollowSetSchema Schema { get; }

    /// <summary>Whether the given record contains the element with ordinal <paramref name="value"/>.</summary>
    bool Contains(int ordinal, int value);

    /// <summary>
    /// Whether the given record contains the element with ordinal <paramref name="value"/>, using a
    /// precomputed hash code.
    /// </summary>
    bool Contains(int ordinal, int value, int hashCode);

    /// <summary>The ordinal stored in <paramref name="bucketIndex"/> of the given record's hash table.</summary>
    int RelativeBucketValue(int ordinal, int bucketIndex);

    /// <summary>
    /// Iterates the elements of the given record whose hash code matches
    /// <paramref name="hashCode"/>.
    /// </summary>
    IHollowOrdinalIterator PotentialMatchOrdinalIterator(int ordinal, int hashCode);
}

/// <summary>
/// Read access to the entries of the records of a map type.
/// </summary>
public interface IHollowMapTypeDataAccess : IHollowTypeDataAccess
{
    /// <summary>The schema of this type.</summary>
    new HollowMapSchema Schema { get; }

    /// <summary>The number of entries in the given record.</summary>
    int Size(int ordinal);

    /// <summary>
    /// The value ordinal mapped to <paramref name="keyOrdinal"/>, or
    /// <see cref="HollowConstants.OrdinalNone"/>.
    /// </summary>
    int Get(int ordinal, int keyOrdinal);

    /// <summary>
    /// The value ordinal mapped to <paramref name="keyOrdinal"/> using a precomputed hash code, or
    /// <see cref="HollowConstants.OrdinalNone"/>.
    /// </summary>
    int Get(int ordinal, int keyOrdinal, int hashCode);

    /// <summary>
    /// The key and value ordinals stored in <paramref name="bucketIndex"/> of the given record's hash
    /// table, packed as <c>(key &lt;&lt; 32) | value</c>.
    /// </summary>
    long RelativeBucket(int ordinal, int bucketIndex);

    /// <summary>
    /// Iterates the entries of the given record whose key hash code matches
    /// <paramref name="hashCode"/>.
    /// </summary>
    IHollowMapEntryOrdinalIterator PotentialMatchOrdinalIterator(int ordinal, int hashCode);

    /// <summary>Iterates every entry of the given record.</summary>
    IHollowMapEntryOrdinalIterator OrdinalIterator(int ordinal);
}
