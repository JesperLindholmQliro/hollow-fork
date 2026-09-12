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

using Hollow.Api.Objects.Delegate;
using Hollow.Core;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Schema;

namespace Hollow.Api.Objects.Generic;

/// <summary>
/// Traverses any Hollow dataset without generated code.
/// </summary>
/// <remarks>
/// <para>
/// Every reference is followed by name and comes back as another generic record, so a whole dataset
/// can be walked knowing only the field names — which is what an explorer, a diff tool or an ad-hoc
/// query needs, and what makes the runtime useful before a generator exists.
/// </para>
/// <para>
/// The cost is that nothing is checked until it runs: a misspelled field name reads as missing rather
/// than failing to compile. A generated API is the same thing with the names checked.
/// </para>
/// </remarks>
public sealed class GenericHollowObject : HollowObject
{
    /// <summary>
    /// Wraps the record at <paramref name="ordinal"/> of <paramref name="typeName"/>.
    /// </summary>
    public GenericHollowObject(IHollowDataAccess dataAccess, string typeName, int ordinal)
        : this(ResolveTypeDataAccess(dataAccess, typeName, ordinal), ordinal)
    {
    }

    /// <summary>
    /// Wraps the record at <paramref name="ordinal"/>.
    /// </summary>
    public GenericHollowObject(IHollowObjectTypeDataAccess typeDataAccess, int ordinal)
        : this(new HollowObjectGenericDelegate(typeDataAccess), ordinal)
    {
    }

    /// <summary>
    /// Wraps the record at <paramref name="ordinal"/>.
    /// </summary>
    public GenericHollowObject(IHollowObjectDelegate objectDelegate, int ordinal)
        : base(objectDelegate, ordinal)
    {
    }

    /// <summary>The object record the named reference field points at, or <see langword="null"/>.</summary>
    public GenericHollowObject? GetObject(string fieldName) =>
        GetReferencedRecord(fieldName) as GenericHollowObject;

    /// <summary>The list record the named reference field points at, or <see langword="null"/>.</summary>
    public GenericHollowList? GetList(string fieldName) =>
        GetReferencedRecord(fieldName) as GenericHollowList;

    /// <summary>The set record the named reference field points at, or <see langword="null"/>.</summary>
    public GenericHollowSet? GetSet(string fieldName) => GetReferencedRecord(fieldName) as GenericHollowSet;

    /// <summary>The map record the named reference field points at, or <see langword="null"/>.</summary>
    public GenericHollowMap? GetMap(string fieldName) => GetReferencedRecord(fieldName) as GenericHollowMap;

    /// <summary>
    /// The record the named reference field points at, whatever kind it is, or <see langword="null"/>
    /// when the field is not a reference, is null, or is missing from the loaded dataset.
    /// </summary>
    public IHollowRecord? GetReferencedRecord(string fieldName)
    {
        string? referencedType = Schema.GetReferencedType(fieldName);

        if (referencedType is null)
        {
            // The loaded dataset does not declare this field. The missing-data handler may still know
            // the model it belongs to, which is how a client reading an older dataset keeps working.
            referencedType =
                (TypeDataAccess.DataAccess.MissingDataHandler.HandleSchema(Schema.Name) as HollowObjectSchema)
                    ?.GetReferencedType(fieldName);

            if (referencedType is null)
            {
                return null;
            }
        }

        int referencedOrdinal = GetOrdinal(fieldName);

        return referencedOrdinal == HollowConstants.OrdinalNone
            ? null
            : GenericHollowRecord.Instantiate(TypeDataAccess.DataAccess, referencedType, referencedOrdinal);
    }

    private static IHollowObjectTypeDataAccess ResolveTypeDataAccess(
        IHollowDataAccess dataAccess, string typeName, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(dataAccess);

        return dataAccess.GetTypeDataAccess(typeName, ordinal) as IHollowObjectTypeDataAccess
            ?? throw new ArgumentException($"{typeName} is not an object type in this dataset", nameof(typeName));
    }
}

/// <summary>
/// Traverses a list record without generated code; every element is another generic record.
/// </summary>
public sealed class GenericHollowList : HollowList<IHollowRecord>
{
    /// <summary>
    /// Wraps the record at <paramref name="ordinal"/> of <paramref name="typeName"/>.
    /// </summary>
    public GenericHollowList(IHollowDataAccess dataAccess, string typeName, int ordinal)
        : this(ResolveTypeDataAccess(dataAccess, typeName, ordinal), ordinal)
    {
    }

    /// <summary>
    /// Wraps the record at <paramref name="ordinal"/>.
    /// </summary>
    public GenericHollowList(IHollowListTypeDataAccess typeDataAccess, int ordinal)
        : this(new HollowListLookupDelegate<IHollowRecord>(typeDataAccess), ordinal)
    {
    }

    /// <summary>
    /// Wraps the record at <paramref name="ordinal"/>.
    /// </summary>
    public GenericHollowList(IHollowListDelegate<IHollowRecord> listDelegate, int ordinal)
        : base(listDelegate, ordinal)
    {
    }

    /// <summary>The element at <paramref name="index"/> as an object record.</summary>
    public GenericHollowObject? GetObject(int index) => this[index] as GenericHollowObject;

    /// <summary>The element at <paramref name="index"/> as a list record.</summary>
    public GenericHollowList? GetList(int index) => this[index] as GenericHollowList;

    /// <summary>The element at <paramref name="index"/> as a set record.</summary>
    public GenericHollowSet? GetSet(int index) => this[index] as GenericHollowSet;

    /// <summary>The element at <paramref name="index"/> as a map record.</summary>
    public GenericHollowMap? GetMap(int index) => this[index] as GenericHollowMap;

    /// <inheritdoc />
    public override IHollowRecord InstantiateElement(int elementOrdinal) =>
        GenericHollowRecord.Instantiate(TypeDataAccess.DataAccess, Schema.ElementType, elementOrdinal);

    /// <inheritdoc />
    public override bool EqualsElement(int elementOrdinal, object? other) =>
        GenericHollowRecord.RecordEquals(Schema.ElementType, elementOrdinal, other);

    private static IHollowListTypeDataAccess ResolveTypeDataAccess(
        IHollowDataAccess dataAccess, string typeName, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(dataAccess);

        return dataAccess.GetTypeDataAccess(typeName, ordinal) as IHollowListTypeDataAccess
            ?? throw new ArgumentException($"{typeName} is not a list type in this dataset", nameof(typeName));
    }
}

/// <summary>
/// Traverses a set record without generated code; every element is another generic record.
/// </summary>
public sealed class GenericHollowSet : HollowSet<IHollowRecord>
{
    /// <summary>
    /// Wraps the record at <paramref name="ordinal"/> of <paramref name="typeName"/>.
    /// </summary>
    public GenericHollowSet(IHollowDataAccess dataAccess, string typeName, int ordinal)
        : this(ResolveTypeDataAccess(dataAccess, typeName, ordinal), ordinal)
    {
    }

    /// <summary>
    /// Wraps the record at <paramref name="ordinal"/>.
    /// </summary>
    public GenericHollowSet(IHollowSetTypeDataAccess typeDataAccess, int ordinal)
        : this(new HollowSetLookupDelegate<IHollowRecord>(typeDataAccess), ordinal)
    {
    }

    /// <summary>
    /// Wraps the record at <paramref name="ordinal"/>.
    /// </summary>
    public GenericHollowSet(IHollowSetDelegate<IHollowRecord> setDelegate, int ordinal)
        : base(setDelegate, ordinal)
    {
    }

    /// <inheritdoc />
    public override IHollowRecord InstantiateElement(int elementOrdinal) =>
        GenericHollowRecord.Instantiate(TypeDataAccess.DataAccess, Schema.ElementType, elementOrdinal);

    /// <inheritdoc />
    public override bool EqualsElement(int elementOrdinal, object? other) =>
        GenericHollowRecord.RecordEquals(Schema.ElementType, elementOrdinal, other);

    private static IHollowSetTypeDataAccess ResolveTypeDataAccess(
        IHollowDataAccess dataAccess, string typeName, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(dataAccess);

        return dataAccess.GetTypeDataAccess(typeName, ordinal) as IHollowSetTypeDataAccess
            ?? throw new ArgumentException($"{typeName} is not a set type in this dataset", nameof(typeName));
    }
}

/// <summary>
/// Traverses a map record without generated code; every key and value is another generic record.
/// </summary>
public sealed class GenericHollowMap : HollowMap<IHollowRecord, IHollowRecord>
{
    /// <summary>
    /// Wraps the record at <paramref name="ordinal"/> of <paramref name="typeName"/>.
    /// </summary>
    public GenericHollowMap(IHollowDataAccess dataAccess, string typeName, int ordinal)
        : this(ResolveTypeDataAccess(dataAccess, typeName, ordinal), ordinal)
    {
    }

    /// <summary>
    /// Wraps the record at <paramref name="ordinal"/>.
    /// </summary>
    public GenericHollowMap(IHollowMapTypeDataAccess typeDataAccess, int ordinal)
        : this(new HollowMapLookupDelegate<IHollowRecord, IHollowRecord>(typeDataAccess), ordinal)
    {
    }

    /// <summary>
    /// Wraps the record at <paramref name="ordinal"/>.
    /// </summary>
    public GenericHollowMap(IHollowMapDelegate<IHollowRecord, IHollowRecord> mapDelegate, int ordinal)
        : base(mapDelegate, ordinal)
    {
    }

    /// <inheritdoc />
    public override IHollowRecord InstantiateKey(int keyOrdinal) =>
        GenericHollowRecord.Instantiate(TypeDataAccess.DataAccess, Schema.KeyType, keyOrdinal);

    /// <inheritdoc />
    public override IHollowRecord InstantiateValue(int valueOrdinal) =>
        GenericHollowRecord.Instantiate(TypeDataAccess.DataAccess, Schema.ValueType, valueOrdinal);

    /// <inheritdoc />
    public override bool EqualsKey(int keyOrdinal, object? other) =>
        GenericHollowRecord.RecordEquals(Schema.KeyType, keyOrdinal, other);

    /// <inheritdoc />
    public override bool EqualsValue(int valueOrdinal, object? other) =>
        GenericHollowRecord.RecordEquals(Schema.ValueType, valueOrdinal, other);

    private static IHollowMapTypeDataAccess ResolveTypeDataAccess(
        IHollowDataAccess dataAccess, string typeName, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(dataAccess);

        return dataAccess.GetTypeDataAccess(typeName, ordinal) as IHollowMapTypeDataAccess
            ?? throw new ArgumentException($"{typeName} is not a map type in this dataset", nameof(typeName));
    }
}

/// <summary>
/// Builds the generic wrapper a record's kind calls for.
/// </summary>
/// <remarks>Named <c>GenericHollowRecordHelper</c> in Java.</remarks>
public static class GenericHollowRecord
{
    /// <summary>
    /// Wraps the record at <paramref name="ordinal"/> of <paramref name="typeName"/>, as whichever
    /// generic record kind that type is.
    /// </summary>
    /// <exception cref="ArgumentException">The dataset has no such type.</exception>
    public static IHollowRecord Instantiate(IHollowDataAccess dataAccess, string typeName, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(dataAccess);

        return dataAccess.GetTypeDataAccess(typeName, ordinal) switch
        {
            IHollowObjectTypeDataAccess objectAccess => new GenericHollowObject(objectAccess, ordinal),
            IHollowListTypeDataAccess listAccess => new GenericHollowList(listAccess, ordinal),
            IHollowSetTypeDataAccess setAccess => new GenericHollowSet(setAccess, ordinal),
            IHollowMapTypeDataAccess mapAccess => new GenericHollowMap(mapAccess, ordinal),
            _ => throw new ArgumentException(
                $"the dataset holds no type named {typeName}", nameof(typeName)),
        };
    }

    /// <summary>
    /// Whether <paramref name="other"/> is a handle to the record at <paramref name="ordinal"/> of
    /// <paramref name="typeName"/>.
    /// </summary>
    /// <remarks>
    /// Comparing by type and ordinal rather than by value is what keeps a membership test from reading
    /// every field of every candidate.
    /// </remarks>
    public static bool RecordEquals(string typeName, int ordinal, object? other) =>
        other is IHollowRecord record
        && record.Ordinal == ordinal
        && string.Equals(record.Schema.Name, typeName, StringComparison.Ordinal);
}
