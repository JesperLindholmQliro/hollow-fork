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

using System.Collections;
using Hollow.Core.Schema;

namespace Hollow.Core.Write.ObjectMapper.FlatRecords.Traversal;

/// <summary>
/// One record inside a flat record, reachable from the top one.
/// </summary>
/// <remarks>
/// <para>
/// A flat record is a tree — a top record, whatever it references, whatever those reference — and this
/// is how to walk it without a model class. What the explorer's generic records are to a dataset,
/// these are to a flat record.
/// </para>
/// <para>
/// Named <c>FlatRecordTraversalNode</c> in Java; the <c>I</c> prefix follows the .NET interface naming
/// convention. Java puts the node factory on the interface as a default method, which only a subclass
/// can call; here it is <see cref="FlatRecordTraversal.Node"/>.
/// </para>
/// </remarks>
public interface IFlatRecordTraversalNode
{
    /// <summary>Which record of the flat record this is.</summary>
    int Ordinal { get; }

    /// <summary>The schema of this record.</summary>
    HollowSchema Schema { get; }
}

/// <summary>Where a walk of a flat record starts.</summary>
public static class FlatRecordTraversal
{
    /// <summary>The top record of <paramref name="record"/>, which is what it is a record of.</summary>
    public static FlatRecordTraversalObjectNode From(FlatRecord record) => new(record);

    /// <summary>
    /// The record at <paramref name="ordinal"/>, as whichever kind of node its schema calls for.
    /// </summary>
    public static IFlatRecordTraversalNode Node(FlatRecordOrdinalReader reader, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(reader);

        HollowSchema schema = reader.ReadSchema(ordinal);

        return schema switch
        {
            HollowObjectSchema objectSchema => new FlatRecordTraversalObjectNode(reader, objectSchema, ordinal),
            HollowListSchema listSchema => new FlatRecordTraversalListNode(reader, listSchema, ordinal),
            HollowSetSchema setSchema => new FlatRecordTraversalSetNode(reader, setSchema, ordinal),
            HollowMapSchema mapSchema => new FlatRecordTraversalMapNode(reader, mapSchema, ordinal),
            _ => throw new InvalidOperationException($"unknown schema type {schema.SchemaType}"),
        };
    }
}

/// <summary>
/// An object record inside a flat record.
/// </summary>
/// <remarks>
/// Java carries two getters per field type — one returning a primitive with a sentinel for null, one
/// returning the boxed type. A nullable value type is both, so there are half as many here:
/// <c>GetInt(field) ?? 0</c> is Java's primitive getter, and <c>GetInt(field)</c> is its boxed one.
/// </remarks>
public sealed class FlatRecordTraversalObjectNode : IFlatRecordTraversalNode
{
    private readonly FlatRecordOrdinalReader _reader;

    /// <summary>Walks <paramref name="record"/> from its top record.</summary>
    public FlatRecordTraversalObjectNode(FlatRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        _reader = new FlatRecordOrdinalReader(record);

        // The top record is the last one written, because a record can only reference one already
        // written before it.
        Ordinal = _reader.OrdinalCount - 1;
        Schema = (HollowObjectSchema)_reader.ReadSchema(Ordinal);
        FlatRecord = record;
    }

    internal FlatRecordTraversalObjectNode(
        FlatRecordOrdinalReader reader, HollowObjectSchema schema, int ordinal)
    {
        _reader = reader;
        Schema = schema;
        Ordinal = ordinal;
    }

    /// <inheritdoc />
    public int Ordinal { get; }

    /// <summary>The schema of this record.</summary>
    public HollowObjectSchema Schema { get; }

    /// <summary>The flat record being walked, where this node is the top one; null otherwise.</summary>
    public FlatRecord? FlatRecord { get; }

    /// <inheritdoc />
    HollowSchema IFlatRecordTraversalNode.Schema => Schema;

    /// <summary>Whether <paramref name="field"/> is null, which a field this record lacks counts as.</summary>
    public bool IsFieldNull(string field) => _reader.IsNull(Ordinal, field);

    /// <summary>
    /// What a reference field points at, or null where the field is null or this record has no such
    /// field.
    /// </summary>
    /// <exception cref="ArgumentException">The field is not a reference.</exception>
    public IFlatRecordTraversalNode? GetFieldNode(string field)
    {
        int position = Schema.GetPosition(field);

        if (position == -1)
        {
            return null;
        }

        if (Schema.GetFieldType(position) != FieldType.Reference)
        {
            throw new ArgumentException(
                $"{field} is a {Schema.GetFieldType(position)} field, so it points at nothing",
                nameof(field));
        }

        int referenced = _reader.ReadFieldReference(Ordinal, field);

        return referenced == -1 ? null : FlatRecordTraversal.Node(_reader, referenced);
    }

    /// <summary>
    /// The same, where the caller knows which kind of record it points at.
    /// </summary>
    /// <remarks>
    /// Java writes four methods for this — <c>getObjectFieldNode</c> and its three siblings — each of
    /// which is a cast.
    /// </remarks>
    /// <exception cref="InvalidCastException">It points at a record of another kind.</exception>
    public TNode? GetFieldNode<TNode>(string field)
        where TNode : class, IFlatRecordTraversalNode =>
        (TNode?)GetFieldNode(field);

    /// <summary>
    /// The value of a non-reference field, boxed.
    /// </summary>
    /// <remarks>Prefer the typed readers; this is for code that does not know the field's type.</remarks>
    /// <exception cref="ArgumentException">The field is a reference.</exception>
    public object? GetFieldValue(string field)
    {
        int position = Schema.GetPosition(field);

        if (position == -1)
        {
            return null;
        }

        return Schema.GetFieldType(position) switch
        {
            FieldType.Boolean => GetBoolean(field),
            FieldType.Int => GetInt(field),
            FieldType.Long => GetLong(field),
            FieldType.Float => GetFloat(field),
            FieldType.Double => GetDouble(field),
            FieldType.Decimal => GetDecimal(field),
            FieldType.String => GetString(field),
            FieldType.Bytes => GetBytes(field),
            _ => throw new ArgumentException(
                $"{field} is a reference field; ask {nameof(GetFieldNode)} for what it points at",
                nameof(field)),
        };
    }

    /// <summary>A boolean field.</summary>
    public bool? GetBoolean(string field) => _reader.ReadFieldBoolean(Ordinal, field);

    /// <summary>An int field.</summary>
    public int? GetInt(string field) =>
        _reader.ReadFieldInt(Ordinal, field) is int value && value != int.MinValue ? value : null;

    /// <summary>A long field.</summary>
    public long? GetLong(string field) =>
        _reader.ReadFieldLong(Ordinal, field) is long value && value != long.MinValue ? value : null;

    /// <summary>A float field.</summary>
    public float? GetFloat(string field) =>
        _reader.ReadFieldFloat(Ordinal, field) is float value && !float.IsNaN(value) ? value : null;

    /// <summary>A double field.</summary>
    public double? GetDouble(string field) =>
        _reader.ReadFieldDouble(Ordinal, field) is double value && !double.IsNaN(value) ? value : null;

    /// <summary>A decimal field, which is this port's own field type.</summary>
    public decimal? GetDecimal(string field) => _reader.ReadFieldDecimal(Ordinal, field);

    /// <summary>A string field.</summary>
    public string? GetString(string field) => _reader.ReadFieldString(Ordinal, field);

    /// <summary>A bytes field.</summary>
    public byte[]? GetBytes(string field) => _reader.ReadFieldBytes(Ordinal, field);
}

/// <summary>A list record inside a flat record, as a read-only list of its elements.</summary>
public sealed class FlatRecordTraversalListNode
    : IFlatRecordTraversalNode, IReadOnlyList<IFlatRecordTraversalNode?>
{
    private readonly FlatRecordOrdinalReader _reader;
    private readonly int[] _elementOrdinals;

    internal FlatRecordTraversalListNode(
        FlatRecordOrdinalReader reader, HollowListSchema schema, int ordinal)
    {
        _reader = reader;
        Schema = schema;
        Ordinal = ordinal;

        _elementOrdinals = new int[reader.ReadSize(ordinal)];
        reader.ReadListElementsInto(ordinal, _elementOrdinals);
    }

    /// <inheritdoc />
    public int Ordinal { get; }

    /// <summary>The schema of this record.</summary>
    public HollowListSchema Schema { get; }

    /// <inheritdoc />
    HollowSchema IFlatRecordTraversalNode.Schema => Schema;

    /// <inheritdoc />
    public int Count => _elementOrdinals.Length;

    /// <inheritdoc />
    public IFlatRecordTraversalNode? this[int index] =>
        _elementOrdinals[index] == -1 ? null : FlatRecordTraversal.Node(_reader, _elementOrdinals[index]);

    /// <inheritdoc />
    public IEnumerator<IFlatRecordTraversalNode?> GetEnumerator()
    {
        for (int i = 0; i < _elementOrdinals.Length; i++)
        {
            yield return this[i];
        }
    }

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// A set record inside a flat record, as a read-only collection of its elements.
/// </summary>
/// <remarks>
/// A collection rather than a set: two nodes are equal when they are the same object, which is not
/// what a set of them would want, and the elements are already distinct — the record they came from
/// saw to that.
/// </remarks>
public sealed class FlatRecordTraversalSetNode
    : IFlatRecordTraversalNode, IReadOnlyCollection<IFlatRecordTraversalNode?>
{
    private readonly FlatRecordOrdinalReader _reader;
    private readonly int[] _elementOrdinals;

    internal FlatRecordTraversalSetNode(
        FlatRecordOrdinalReader reader, HollowSetSchema schema, int ordinal)
    {
        _reader = reader;
        Schema = schema;
        Ordinal = ordinal;

        _elementOrdinals = new int[reader.ReadSize(ordinal)];
        reader.ReadSetElementsInto(ordinal, _elementOrdinals);
    }

    /// <inheritdoc />
    public int Ordinal { get; }

    /// <summary>The schema of this record.</summary>
    public HollowSetSchema Schema { get; }

    /// <inheritdoc />
    HollowSchema IFlatRecordTraversalNode.Schema => Schema;

    /// <inheritdoc />
    public int Count => _elementOrdinals.Length;

    /// <inheritdoc />
    /// <remarks>
    /// Java offers <c>objects()</c>, <c>lists()</c>, <c>sets()</c> and <c>maps()</c> as well, each of
    /// which is this one with a cast on the way out. LINQ's <c>Cast</c> and <c>OfType</c> are those.
    /// </remarks>
    public IEnumerator<IFlatRecordTraversalNode?> GetEnumerator()
    {
        foreach (int elementOrdinal in _elementOrdinals)
        {
            yield return elementOrdinal == -1 ? null : FlatRecordTraversal.Node(_reader, elementOrdinal);
        }
    }

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>One entry of a map record inside a flat record.</summary>
/// <param name="Key">What the entry is keyed by, or null where the key is null.</param>
/// <param name="Value">What it holds, or null where the value is null.</param>
public readonly record struct FlatRecordMapEntry(
    IFlatRecordTraversalNode? Key, IFlatRecordTraversalNode? Value);

/// <summary>
/// A map record inside a flat record, as a read-only collection of its entries.
/// </summary>
/// <remarks>
/// Java extends <c>AbstractMap</c> and then can only really offer <c>entrySet()</c>, because a node
/// makes a poor key: it has no equality beyond identity, so nothing can be looked up in it. A sequence
/// of entries is what that amounts to, and is what this is.
/// </remarks>
public sealed class FlatRecordTraversalMapNode
    : IFlatRecordTraversalNode, IReadOnlyList<FlatRecordMapEntry>
{
    private readonly FlatRecordOrdinalReader _reader;
    private readonly int[] _keyOrdinals;
    private readonly int[] _valueOrdinals;

    internal FlatRecordTraversalMapNode(
        FlatRecordOrdinalReader reader, HollowMapSchema schema, int ordinal)
    {
        _reader = reader;
        Schema = schema;
        Ordinal = ordinal;

        int size = reader.ReadSize(ordinal);
        _keyOrdinals = new int[size];
        _valueOrdinals = new int[size];
        reader.ReadMapElementsInto(ordinal, _keyOrdinals, _valueOrdinals);
    }

    /// <inheritdoc />
    public int Ordinal { get; }

    /// <summary>The schema of this record.</summary>
    public HollowMapSchema Schema { get; }

    /// <inheritdoc />
    HollowSchema IFlatRecordTraversalNode.Schema => Schema;

    /// <inheritdoc />
    public int Count => _keyOrdinals.Length;

    /// <inheritdoc />
    public FlatRecordMapEntry this[int index] =>
        new(NodeOrNull(_keyOrdinals[index]), NodeOrNull(_valueOrdinals[index]));

    /// <inheritdoc />
    public IEnumerator<FlatRecordMapEntry> GetEnumerator()
    {
        for (int i = 0; i < _keyOrdinals.Length; i++)
        {
            yield return this[i];
        }
    }

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private IFlatRecordTraversalNode? NodeOrNull(int ordinal) =>
        ordinal == -1 ? null : FlatRecordTraversal.Node(_reader, ordinal);
}
