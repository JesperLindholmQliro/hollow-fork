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
using System.Collections;
using Hollow.Api.Objects.Delegate;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.Iterator;
using Hollow.Core.Schema;

namespace Hollow.Api.Objects;

/// <summary>
/// A handle to one record in a Hollow dataset.
/// </summary>
/// <remarks>
/// A handle is an ordinal plus a delegate that knows how to read it; the record's data stays in the
/// blob. Two handles to the same record of the same type are equal, so a record can be used as a
/// dictionary key without materialising anything.
/// </remarks>
public interface IHollowRecord
{
    /// <summary>The ordinal of this record within its type.</summary>
    int Ordinal { get; }

    /// <summary>The schema of this record's type.</summary>
    HollowSchema Schema { get; }

    /// <summary>Read access to this record's type.</summary>
    IHollowTypeDataAccess TypeDataAccess { get; }

    /// <summary>Where this record reads its data from.</summary>
    IHollowRecordDelegate Delegate { get; }
}

/// <summary>
/// A handle to one object record, reading its fields by name.
/// </summary>
/// <remarks>
/// A generated API subclasses this once per object type and adds a property per field, so that
/// <c>movie.Title</c> stands in for <c>GetString("title")</c>. <see cref="Generic.GenericHollowObject"/>
/// is the subclass that does not: it stops here, and reads by name.
/// </remarks>
public abstract class HollowObject : IHollowRecord, IEquatable<HollowObject>
{
    /// <summary>
    /// Initialises a handle to <paramref name="ordinal"/>.
    /// </summary>
    protected HollowObject(IHollowObjectDelegate objectDelegate, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(objectDelegate);

        ObjectDelegate = objectDelegate;
        Ordinal = ordinal;
    }

    /// <inheritdoc />
    public int Ordinal { get; }

    /// <summary>The schema of this record's type.</summary>
    public HollowObjectSchema Schema => ObjectDelegate.Schema;

    /// <inheritdoc />
    HollowSchema IHollowRecord.Schema => Schema;

    /// <summary>Read access to this record's type.</summary>
    public IHollowObjectTypeDataAccess TypeDataAccess => ObjectDelegate.TypeDataAccess;

    /// <inheritdoc />
    IHollowTypeDataAccess IHollowRecord.TypeDataAccess => TypeDataAccess;

    /// <inheritdoc />
    public IHollowRecordDelegate Delegate => ObjectDelegate;

    /// <summary>Where this record reads its data from.</summary>
    protected IHollowObjectDelegate ObjectDelegate { get; }

    /// <summary>Whether the named field is null.</summary>
    public bool IsNull(string fieldName) => ObjectDelegate.IsNull(Ordinal, fieldName);

    /// <summary>Reads a boolean field, which reads false when null.</summary>
    public bool GetBoolean(string fieldName) => ObjectDelegate.GetBoolean(Ordinal, fieldName);

    /// <summary>Reads a reference field as the referenced record's ordinal.</summary>
    public int GetOrdinal(string fieldName) => ObjectDelegate.GetOrdinal(Ordinal, fieldName);

    /// <summary>Reads an int field.</summary>
    public int GetInt(string fieldName) => ObjectDelegate.GetInt(Ordinal, fieldName);

    /// <summary>Reads a long field.</summary>
    public long GetLong(string fieldName) => ObjectDelegate.GetLong(Ordinal, fieldName);

    /// <summary>Reads a float field.</summary>
    public float GetFloat(string fieldName) => ObjectDelegate.GetFloat(Ordinal, fieldName);

    /// <summary>Reads a double field.</summary>
    public double GetDouble(string fieldName) => ObjectDelegate.GetDouble(Ordinal, fieldName);

    /// <summary>Reads a decimal field, which may be null.</summary>
    /// <remarks><strong>Format extension</strong> — see <c>PORTING.md</c>.</remarks>
    public decimal? GetDecimal(string fieldName) => ObjectDelegate.GetDecimal(Ordinal, fieldName);

    /// <summary>Reads a string field, which may be null.</summary>
    public string? GetString(string fieldName) => ObjectDelegate.GetString(Ordinal, fieldName);

    /// <summary>Compares a string field without materialising the stored string.</summary>
    public bool IsStringFieldEqual(string fieldName, string? testValue) =>
        ObjectDelegate.IsStringFieldEqual(Ordinal, fieldName, testValue);

    /// <summary>Reads a bytes field, which may be null.</summary>
    public byte[]? GetBytes(string fieldName) => ObjectDelegate.GetBytes(Ordinal, fieldName);

    /// <summary>
    /// How many bytes a variable-length field is stored in, or -1 when it is null.
    /// </summary>
    /// <remarks>
    /// How big a buffer <see cref="GetString(string, Span{char})"/> and
    /// <see cref="GetBytes(string, Span{byte})"/> need. For a string it is an upper bound rather than
    /// the exact character count, since a character is stored as one or more bytes.
    /// </remarks>
    public int GetVarLengthByteLength(string fieldName) =>
        ObjectDelegate.GetVarLengthByteLength(Ordinal, fieldName);

    /// <summary>
    /// Reads a string field into <paramref name="destination"/> and returns a span over what was
    /// written, allocating nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The returned span points into <paramref name="destination"/>, so it is valid for as long as that
    /// buffer is. Size the buffer with <see cref="GetVarLengthByteLength"/>, or from what the field can
    /// hold:
    /// </para>
    /// <code>
    /// Span&lt;char&gt; buffer = stackalloc char[64];
    /// ReadOnlySpan&lt;char&gt; title = movie.GetString("title", buffer);
    /// </code>
    /// <para>
    /// A null field and an empty one both come back empty. Use <see cref="IsNull"/>, or
    /// <see cref="ReadStringInto"/>, where the difference matters.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short.</exception>
    public ReadOnlySpan<char> GetString(string fieldName, Span<char> destination)
    {
        int written = ObjectDelegate.ReadStringInto(Ordinal, fieldName, destination);

        return written <= 0 ? [] : destination[..written];
    }

    /// <summary>
    /// Reads a string field into <paramref name="destination"/>, returning the characters written or -1
    /// when the field is null.
    /// </summary>
    /// <remarks>
    /// The form of <see cref="GetString(string, Span{char})"/> that tells null from empty.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short.</exception>
    public int ReadStringInto(string fieldName, Span<char> destination) =>
        ObjectDelegate.ReadStringInto(Ordinal, fieldName, destination);

    /// <summary>
    /// Reads a bytes field into <paramref name="destination"/> and returns a span over what was
    /// written, allocating nothing.
    /// </summary>
    /// <remarks>
    /// Prefer <see cref="TryGetBytes"/>, which can often hand back a view over the blob itself and copy
    /// nothing at all. A null field and an empty one both come back empty here.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short.</exception>
    public ReadOnlySpan<byte> GetBytes(string fieldName, Span<byte> destination)
    {
        int written = ObjectDelegate.ReadBytesInto(Ordinal, fieldName, destination);

        return written <= 0 ? [] : destination[..written];
    }

    /// <summary>
    /// Reads a bytes field into <paramref name="destination"/>, returning the bytes written or -1 when
    /// the field is null.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short.</exception>
    public int ReadBytesInto(string fieldName, Span<byte> destination) =>
        ObjectDelegate.ReadBytesInto(Ordinal, fieldName, destination);

    /// <summary>
    /// Views a bytes field as a span over the blob itself, copying nothing.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> for a null field, and for a value that straddles a storage
    /// segment boundary and so has no contiguous view — fall back to
    /// <see cref="GetBytes(string, Span{byte})"/> there.
    /// </remarks>
    public bool TryGetBytes(string fieldName, out ReadOnlySpan<byte> value) =>
        ObjectDelegate.TryGetBytesSpan(Ordinal, fieldName, out value);

    /// <summary>
    /// Views a bytes field as a sequence over the blob itself, copying nothing, whether or not the
    /// value is contiguous.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="TryGetBytes"/> without the failure case: a value that straddles a storage segment
    /// boundary comes back as several pieces rather than not at all, so this always works and never
    /// copies. Walk it with a <see cref="System.Buffers.SequenceReader{T}"/>, or <c>foreach</c> over
    /// its spans.
    /// </para>
    /// <code>
    /// foreach (ReadOnlyMemory&lt;byte&gt; piece in record.GetBytesSequence("blob"))
    /// {
    ///     hash.Append(piece.Span);
    /// }
    /// </code>
    /// <para>A null field and an empty one both come back empty.</para>
    /// </remarks>
    public ReadOnlySequence<byte> GetBytesSequence(string fieldName) =>
        ObjectDelegate.GetVarLengthSequence(Ordinal, fieldName);

    /// <summary>
    /// Views the <em>encoded</em> bytes of a string field as a sequence, copying nothing.
    /// </summary>
    /// <remarks>
    /// A character is stored as a variable-length integer, so these bytes are not characters — they are
    /// useful for hashing or copying the stored form, not for reading text. There is no
    /// <c>ReadOnlySequence&lt;char&gt;</c> for the same reason: no characters are stored to point at.
    /// Use <see cref="GetString(string, Span{char})"/>, which decodes into a buffer you own.
    /// </remarks>
    public ReadOnlySequence<byte> GetStringBytesSequence(string fieldName) =>
        ObjectDelegate.GetVarLengthSequence(Ordinal, fieldName);

    /// <inheritdoc />
    public bool Equals(HollowObject? other) =>
        other is not null
        && Ordinal == other.Ordinal
        && string.Equals(Schema.Name, other.Schema.Name, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as HollowObject);

    /// <inheritdoc />
    public override int GetHashCode() => Ordinal;

    /// <inheritdoc />
    public override string ToString() => $"Hollow Object: {Schema.Name} ({Ordinal})";
}

/// <summary>
/// A handle to one list record, read as an ordinary read-only list.
/// </summary>
/// <remarks>
/// The elements stay in the blob: indexing this reads the element's ordinal and wraps it, so nothing
/// is materialised until it is asked for. Java's version extends <c>AbstractList</c>; this implements
/// <see cref="IReadOnlyList{T}"/>, so LINQ and <c>foreach</c> work without a shim.
/// </remarks>
/// <typeparam name="T">The wrapper type an element is read as.</typeparam>
public abstract class HollowList<T> : IHollowRecord, IReadOnlyList<T>
{
    /// <summary>
    /// Initialises a handle to <paramref name="ordinal"/>.
    /// </summary>
    protected HollowList(IHollowListDelegate<T> listDelegate, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(listDelegate);

        ListDelegate = listDelegate;
        Ordinal = ordinal;
    }

    /// <inheritdoc />
    public int Ordinal { get; }

    /// <summary>The schema of this record's type.</summary>
    public HollowListSchema Schema => ListDelegate.Schema;

    /// <inheritdoc />
    HollowSchema IHollowRecord.Schema => Schema;

    /// <summary>Read access to this record's type.</summary>
    public IHollowListTypeDataAccess TypeDataAccess => ListDelegate.TypeDataAccess;

    /// <inheritdoc />
    IHollowTypeDataAccess IHollowRecord.TypeDataAccess => TypeDataAccess;

    /// <inheritdoc />
    public IHollowRecordDelegate Delegate => ListDelegate;

    /// <inheritdoc />
    public int Count => ListDelegate.Size(Ordinal);

    /// <summary>Where this record reads its data from.</summary>
    protected IHollowListDelegate<T> ListDelegate { get; }

    /// <inheritdoc />
    public T this[int index] => ListDelegate.GetElement(this, Ordinal, index);

    /// <summary>Whether this list holds an element equal to <paramref name="item"/>.</summary>
    public bool Contains(object? item) => IndexOf(item) != -1;

    /// <summary>The index of the first element equal to <paramref name="item"/>, or -1.</summary>
    public int IndexOf(object? item) => ListDelegate.IndexOf(this, Ordinal, item);

    /// <summary>The index of the last element equal to <paramref name="item"/>, or -1.</summary>
    public int LastIndexOf(object? item) => ListDelegate.LastIndexOf(this, Ordinal, item);

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator()
    {
        int count = Count;
        for (int i = 0; i < count; i++)
        {
            yield return this[i];
        }
    }

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Wraps the element at <paramref name="elementOrdinal"/>.</summary>
    public abstract T InstantiateElement(int elementOrdinal);

    /// <summary>
    /// Whether the element at <paramref name="elementOrdinal"/> equals <paramref name="other"/>.
    /// </summary>
    /// <remarks>
    /// Comparing by ordinal where possible is what keeps a membership test from materialising every
    /// element.
    /// </remarks>
    public abstract bool EqualsElement(int elementOrdinal, object? other);

    /// <inheritdoc />
    public override string ToString() => $"Hollow List: {Schema.Name} ({Ordinal})";
}

/// <summary>
/// A handle to one set record, read as an ordinary read-only set.
/// </summary>
/// <typeparam name="T">The wrapper type an element is read as.</typeparam>
public abstract class HollowSet<T> : IHollowRecord, IReadOnlyCollection<T>
{
    /// <summary>
    /// Initialises a handle to <paramref name="ordinal"/>.
    /// </summary>
    protected HollowSet(IHollowSetDelegate<T> setDelegate, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(setDelegate);

        SetDelegate = setDelegate;
        Ordinal = ordinal;
    }

    /// <inheritdoc />
    public int Ordinal { get; }

    /// <summary>The schema of this record's type.</summary>
    public HollowSetSchema Schema => SetDelegate.Schema;

    /// <inheritdoc />
    HollowSchema IHollowRecord.Schema => Schema;

    /// <summary>Read access to this record's type.</summary>
    public IHollowSetTypeDataAccess TypeDataAccess => SetDelegate.TypeDataAccess;

    /// <inheritdoc />
    IHollowTypeDataAccess IHollowRecord.TypeDataAccess => TypeDataAccess;

    /// <inheritdoc />
    public IHollowRecordDelegate Delegate => SetDelegate;

    /// <inheritdoc />
    public int Count => SetDelegate.Size(Ordinal);

    /// <summary>Where this record reads its data from.</summary>
    protected IHollowSetDelegate<T> SetDelegate { get; }

    /// <summary>Whether this set holds an element equal to <paramref name="item"/>.</summary>
    public bool Contains(object? item) => SetDelegate.Contains(this, Ordinal, item);

    /// <summary>
    /// The element matching the type's declared hash key, or <see langword="default"/> when there is
    /// none.
    /// </summary>
    /// <remarks>
    /// Only meaningful where the set type declares a hash key; without one there is nothing to match
    /// against and this always comes back empty.
    /// </remarks>
    public T? FindElement(params object?[] hashKey) => SetDelegate.FindElement(this, Ordinal, hashKey);

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator()
    {
        foreach (int elementOrdinal in SetDelegate.Iterator(Ordinal).AsEnumerable())
        {
            yield return InstantiateElement(elementOrdinal);
        }
    }

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Wraps the element at <paramref name="elementOrdinal"/>.</summary>
    public abstract T InstantiateElement(int elementOrdinal);

    /// <summary>
    /// Whether the element at <paramref name="elementOrdinal"/> equals <paramref name="other"/>.
    /// </summary>
    public abstract bool EqualsElement(int elementOrdinal, object? other);

    /// <inheritdoc />
    public override string ToString() => $"Hollow Set: {Schema.Name} ({Ordinal})";
}

/// <summary>
/// A handle to one map record, read as an ordinary read-only dictionary.
/// </summary>
/// <typeparam name="TKey">The wrapper type a key is read as.</typeparam>
/// <typeparam name="TValue">The wrapper type a value is read as.</typeparam>
public abstract class HollowMap<TKey, TValue> : IHollowRecord, IReadOnlyCollection<KeyValuePair<TKey, TValue>>
{
    /// <summary>
    /// Initialises a handle to <paramref name="ordinal"/>.
    /// </summary>
    protected HollowMap(IHollowMapDelegate<TKey, TValue> mapDelegate, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(mapDelegate);

        MapDelegate = mapDelegate;
        Ordinal = ordinal;
    }

    /// <inheritdoc />
    public int Ordinal { get; }

    /// <summary>The schema of this record's type.</summary>
    public HollowMapSchema Schema => MapDelegate.Schema;

    /// <inheritdoc />
    HollowSchema IHollowRecord.Schema => Schema;

    /// <summary>Read access to this record's type.</summary>
    public IHollowMapTypeDataAccess TypeDataAccess => MapDelegate.TypeDataAccess;

    /// <inheritdoc />
    IHollowTypeDataAccess IHollowRecord.TypeDataAccess => TypeDataAccess;

    /// <inheritdoc />
    public IHollowRecordDelegate Delegate => MapDelegate;

    /// <inheritdoc />
    public int Count => MapDelegate.Size(Ordinal);

    /// <summary>The keys of this map, in the order the hash table holds them.</summary>
    public IEnumerable<TKey> Keys => this.Select(static entry => entry.Key);

    /// <summary>The values of this map, in the order the hash table holds them.</summary>
    public IEnumerable<TValue> Values => this.Select(static entry => entry.Value);

    /// <summary>Where this record reads its data from.</summary>
    protected IHollowMapDelegate<TKey, TValue> MapDelegate { get; }

    /// <summary>The value mapped to <paramref name="key"/>, or <see langword="default"/>.</summary>
    public TValue? this[object? key] => MapDelegate.Get(this, Ordinal, key);

    /// <summary>Whether this map holds an entry with that key.</summary>
    public bool ContainsKey(object? key) => MapDelegate.ContainsKey(this, Ordinal, key);

    /// <summary>Whether this map holds an entry with that value.</summary>
    public bool ContainsValue(object? value) => MapDelegate.ContainsValue(this, Ordinal, value);

    /// <summary>The key matching the type's declared hash key, or <see langword="default"/>.</summary>
    public TKey? FindKey(params object?[] hashKey) => MapDelegate.FindKey(this, Ordinal, hashKey);

    /// <summary>The value matching the type's declared hash key, or <see langword="default"/>.</summary>
    public TValue? FindValue(params object?[] hashKey) => MapDelegate.FindValue(this, Ordinal, hashKey);

    /// <summary>The entry matching the type's declared hash key, or <see langword="null"/>.</summary>
    public KeyValuePair<TKey, TValue>? FindEntry(params object?[] hashKey) =>
        MapDelegate.FindEntry(this, Ordinal, hashKey);

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
    {
        IHollowMapEntryOrdinalIterator iterator = MapDelegate.Iterator(Ordinal);

        while (iterator.Next())
        {
            yield return new KeyValuePair<TKey, TValue>(
                InstantiateKey(iterator.Key), InstantiateValue(iterator.Value));
        }
    }

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Wraps the key at <paramref name="keyOrdinal"/>.</summary>
    public abstract TKey InstantiateKey(int keyOrdinal);

    /// <summary>Wraps the value at <paramref name="valueOrdinal"/>.</summary>
    public abstract TValue InstantiateValue(int valueOrdinal);

    /// <summary>Whether the key at <paramref name="keyOrdinal"/> equals <paramref name="other"/>.</summary>
    public abstract bool EqualsKey(int keyOrdinal, object? other);

    /// <summary>
    /// Whether the value at <paramref name="valueOrdinal"/> equals <paramref name="other"/>.
    /// </summary>
    public abstract bool EqualsValue(int valueOrdinal, object? other);

    /// <inheritdoc />
    public override string ToString() => $"Hollow Map: {Schema.Name} ({Ordinal})";
}
