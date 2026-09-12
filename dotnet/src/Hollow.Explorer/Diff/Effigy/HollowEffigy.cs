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
using Hollow.Core.Read.DataAccess;

namespace Hollow.Explorer.Diff.Effigy;

/// <summary>
/// A Hollow record as an ordinary object tree, for showing two of them side by side.
/// </summary>
/// <remarks>
/// <para>
/// The diff pages lay a record out as rows, pairing each row with its counterpart in the other state.
/// Doing that against the blob directly would mean reading every field several times over, so the
/// record is first turned into this: a name, a type, and a value per field, with a reference becoming
/// a nested effigy.
/// </para>
/// <para>
/// Fields are read the first time they are asked for, so effigising a record does not walk everything
/// it references until something looks. An effigy compares by its fields rather than by its ordinal,
/// which is what lets two records from different states be recognised as the same value.
/// </para>
/// </remarks>
public sealed class HollowEffigy : IEquatable<HollowEffigy>
{
    private readonly HollowEffigyFactory? _factory;
    private readonly string? _objectType;

    private List<HollowEffigyField>? _fields;

    /// <summary>
    /// A standalone effigy of <paramref name="objectType"/>, whose fields the caller adds.
    /// </summary>
    /// <remarks>
    /// For a node with no record behind it — a map entry, which is a pairing rather than something the
    /// blob holds.
    /// </remarks>
    public HollowEffigy(string objectType)
    {
        _objectType = objectType;
        _fields = [];
        Ordinal = HollowConstants.OrdinalNone;
    }

    internal HollowEffigy(HollowEffigyFactory factory, IHollowTypeDataAccess dataAccess, int ordinal)
    {
        _factory = factory;
        DataAccess = dataAccess;
        Ordinal = ordinal;
    }

    /// <summary>The type this effigy stands for.</summary>
    public string ObjectType => _objectType ?? DataAccess!.Schema.Name;

    /// <summary>Where the record is read from, or nothing for a node with no record behind it.</summary>
    public IHollowTypeDataAccess? DataAccess { get; }

    /// <summary>The ordinal the record lives at.</summary>
    public int Ordinal { get; }

    /// <summary>The fields, read the first time they are asked for.</summary>
    public IReadOnlyList<HollowEffigyField> Fields => _fields ??= _factory!.CreateFields(this);

    /// <summary>Appends a field to a standalone effigy.</summary>
    public void Add(HollowEffigyField field) => (_fields ??= []).Add(field);

    /// <inheritdoc />
    public bool Equals(HollowEffigy? other) =>
        ReferenceEquals(this, other) || (other is not null && Fields.SequenceEqual(other.Fields));

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as HollowEffigy);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = default;
        hash.Add(31);

        foreach (HollowEffigyField field in Fields)
        {
            hash.Add(field);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// One field of an effigy: its name, the type it holds, and its value.
/// </summary>
/// <remarks>
/// The value is either a scalar — already converted to something printable — or a nested
/// <see cref="HollowEffigy"/> where the field is a reference. Which of those it is decides whether a
/// diff row can be shown on one line or has to be opened.
/// </remarks>
public sealed class HollowEffigyField : IEquatable<HollowEffigyField>
{
    private readonly int _hashCode;

    /// <summary>A field holding a nested record.</summary>
    public HollowEffigyField(string fieldName, HollowEffigy value)
        : this(fieldName, value.ObjectType, value)
    {
    }

    /// <summary>A field holding <paramref name="value"/>, of type <paramref name="typeName"/>.</summary>
    public HollowEffigyField(string? fieldName, string typeName, object? value)
    {
        FieldName = fieldName;
        TypeName = typeName;
        Value = value;

        // Computed once: the pairers hash fields heavily, and a nested effigy's hash is the whole
        // subtree.
        _hashCode = HashCode.Combine(fieldName, typeName, value);
    }

    /// <summary>The field's name, or <c>element</c>, <c>key</c>, <c>value</c> or <c>entry</c>.</summary>
    public string? FieldName { get; }

    /// <summary>The type the field holds.</summary>
    public string TypeName { get; }

    /// <summary>The value, or a nested <see cref="HollowEffigy"/> where the field is a reference.</summary>
    public object? Value { get; }

    /// <summary>Whether the field holds a value rather than leading somewhere.</summary>
    public bool IsLeafNode => Value is not HollowEffigy;

    /// <inheritdoc />
    public bool Equals(HollowEffigyField? other) =>
        other is not null
        && string.Equals(FieldName, other.FieldName, StringComparison.Ordinal)
        && string.Equals(TypeName, other.TypeName, StringComparison.Ordinal)
        && Equals(Value, other.Value);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as HollowEffigyField);

    /// <inheritdoc />
    public override int GetHashCode() => _hashCode;
}

/// <summary>What kind of collection an effigy stands for, where it stands for one.</summary>
public enum EffigyCollectionType
{
    /// <summary>Not a collection.</summary>
    None,

    /// <summary>A map, whose fields are entries.</summary>
    Map,

    /// <summary>A list or a set, whose fields are elements.</summary>
    Collection,
}
