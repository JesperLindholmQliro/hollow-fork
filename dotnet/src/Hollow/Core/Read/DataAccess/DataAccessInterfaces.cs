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

using System.Buffers;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Iterator;
using Hollow.Core.Read.Missing;
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

    /// <summary>
    /// Answers reads of fields and types this dataset does not have.
    /// </summary>
    /// <remarks>
    /// A typed client compiled against one version of the data model may be pointed at a dataset
    /// written against another. This is what a typed accessor falls through to when the field it wants
    /// is not there.
    /// </remarks>
    IMissingDataHandler MissingDataHandler { get; }
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

    /// <summary>
    /// The number of bytes stored for a variable-length field, or -1 when the field is null.
    /// </summary>
    /// <remarks>
    /// This is how big a buffer has to be to read the field without allocating. For a string field it
    /// is an upper bound on the number of characters rather than the exact count, because a character
    /// is stored as one or more bytes — so a buffer of this size is always big enough, and the count
    /// that comes back from <see cref="ReadStringInto"/> is what was actually written.
    /// </remarks>
    int VarLengthFieldByteLength(int ordinal, int fieldIndex);

    /// <summary>
    /// Decodes a <see cref="FieldType.String"/> field into <paramref name="destination"/> without
    /// allocating.
    /// </summary>
    /// <returns>
    /// The number of characters written, or -1 when the field is null. An empty string writes nothing
    /// and returns 0, which is what distinguishes it from null.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="destination"/> is shorter than
    /// <see cref="VarLengthFieldByteLength"/> allows for.
    /// </exception>
    int ReadStringInto(int ordinal, int fieldIndex, Span<char> destination);

    /// <summary>
    /// Copies a <see cref="FieldType.Bytes"/> field into <paramref name="destination"/> without
    /// allocating.
    /// </summary>
    /// <returns>
    /// The number of bytes written, or -1 when the field is null. An empty value writes nothing and
    /// returns 0.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="destination"/> is shorter than the stored value.
    /// </exception>
    int ReadBytesInto(int ordinal, int fieldIndex, Span<byte> destination);

    /// <summary>
    /// Views a <see cref="FieldType.Bytes"/> field as a span over the blob itself, copying nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Succeeds only where the stored bytes sit inside one storage segment; a value that straddles a
    /// boundary has no contiguous view, and a caller that must not allocate falls back to
    /// <see cref="ReadBytesInto"/>. Where the range falls is an artefact of how the blob was loaded,
    /// so this is a fast path to try rather than a property of the data.
    /// </para>
    /// <para>
    /// A string field has no such fast path: a character is stored as a variable-length integer, so
    /// the stored bytes are not characters and always have to be decoded.
    /// </para>
    /// </remarks>
    /// <returns>Whether <paramref name="value"/> was set. A null field returns false.</returns>
    bool TryGetBytesSpan(int ordinal, int fieldIndex, out ReadOnlySpan<byte> value);

    /// <summary>
    /// Views the stored bytes of a variable-length field as a sequence, copying nothing, whether or not
    /// they sit inside one storage segment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is <see cref="TryGetBytesSpan"/> without the failure case: where a value straddles a
    /// segment boundary it comes back as one sequence segment per piece rather than not at all, so a
    /// caller can walk it with a <see cref="System.Buffers.SequenceReader{T}"/> and never copy. A value
    /// inside one segment comes back as a single-segment sequence and allocates nothing.
    /// </para>
    /// <para>
    /// For a <see cref="FieldType.Bytes"/> field these are the value's bytes. For a
    /// <see cref="FieldType.String"/> field they are the <em>encoded</em> bytes — a character is stored
    /// as a variable-length integer — so they are useful for hashing or copying the stored form, not
    /// for reading text. Use <see cref="ReadStringInto"/> for that; there is no
    /// <c>ReadOnlySequence&lt;char&gt;</c> because no characters are stored to point at.
    /// </para>
    /// <para>An empty sequence comes back for a null field as well as an empty one.</para>
    /// </remarks>
    ReadOnlySequence<byte> GetVarLengthSequence(int ordinal, int fieldIndex);
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

    /// <summary>
    /// The element ordinals of the given record.
    /// </summary>
    /// <remarks>Named <c>ordinalIterator</c> in Java, where it hands back a cursor.</remarks>
    IEnumerable<int> ElementOrdinals(int ordinal);
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
    /// The elements of the given record whose hash code matches <paramref name="hashCode"/>.
    /// </summary>
    /// <remarks>Named <c>potentialMatchOrdinalIterator</c> in Java.</remarks>
    IEnumerable<int> PotentialMatchElementOrdinals(int ordinal, int hashCode);

    /// <summary>
    /// The ordinal of the element matching <paramref name="hashKey"/>, or
    /// <see cref="HollowConstants.OrdinalNone"/>. Requires the type to declare a hash key.
    /// </summary>
    int FindElement(int ordinal, params object?[] hashKey);
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
    /// The entries of the given record whose key hash code matches <paramref name="hashCode"/>.
    /// </summary>
    /// <remarks>Named <c>potentialMatchOrdinalIterator</c> in Java.</remarks>
    IEnumerable<HollowMapEntry> PotentialMatchEntries(int ordinal, int hashCode);

    /// <summary>
    /// Every entry of the given record.
    /// </summary>
    /// <remarks>Named <c>ordinalIterator</c> in Java, where it hands back a cursor.</remarks>
    IEnumerable<HollowMapEntry> Entries(int ordinal);

    /// <summary>
    /// The key ordinal of the entry matching <paramref name="hashKey"/>, or
    /// <see cref="HollowConstants.OrdinalNone"/>. Requires the type to declare a hash key.
    /// </summary>
    int FindKey(int ordinal, params object?[] hashKey);

    /// <summary>
    /// The value ordinal of the entry matching <paramref name="hashKey"/>, or
    /// <see cref="HollowConstants.OrdinalNone"/>. Requires the type to declare a hash key.
    /// </summary>
    int FindValue(int ordinal, params object?[] hashKey);

    /// <summary>
    /// The entry matching <paramref name="hashKey"/> packed as <c>(key &lt;&lt; 32) | value</c>, or
    /// -1. Requires the type to declare a hash key.
    /// </summary>
    long FindEntry(int ordinal, params object?[] hashKey);
}
