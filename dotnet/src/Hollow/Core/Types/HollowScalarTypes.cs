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

using Hollow.Api.Custom;
using Hollow.Api.Objects;
using Hollow.Api.Objects.Delegate;
using Hollow.Api.Objects.Provider;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Schema;

namespace Hollow.Core.Types;

/// <summary>
/// Reads the single <c>value</c> field of one of Hollow's built-in scalar wrapper types.
/// </summary>
/// <remarks>
/// <para>
/// A string field is stored as a reference to a shared <c>String</c> record rather than inline, so
/// that a title repeated across a thousand records is stored once. The same goes for a nullable
/// number. Those shared records need a typed API of their own, which is what this is.
/// </para>
/// <para>
/// <strong>Port note.</strong> Java writes this out longhand: six types, each with a type API, a
/// delegate interface, a lookup and a cached implementation, a factory and a data accessor — around
/// 2,000 lines that differ only in the field type. One generic class does the same job here, and
/// <see cref="HollowScalarTypes"/> names the concrete ones. The namespace is <c>Core.Types</c> rather
/// than Java's <c>core.type</c>, because a namespace named <c>Type</c> shadows <see cref="System.Type"/>
/// in every namespace beneath <c>Hollow.Core</c>.
/// </para>
/// </remarks>
/// <typeparam name="TValue">The CLR type the field reads as.</typeparam>
public abstract class HollowScalarTypeApi<TValue> : HollowObjectTypeApi
{
    /// <summary>The position of the single field, which is always the first.</summary>
    protected const int ValueFieldPosition = 0;

    /// <summary>
    /// Initialises a type API over <paramref name="typeDataAccess"/>.
    /// </summary>
    protected HollowScalarTypeApi(HollowApi api, IHollowObjectTypeDataAccess typeDataAccess)
        : base(api, typeDataAccess, ["value"])
    {
    }

    /// <summary>The value of the record at <paramref name="ordinal"/>.</summary>
    public abstract TValue GetValue(int ordinal);

    /// <summary>Whether the record at <paramref name="ordinal"/> holds no value.</summary>
    public bool IsNull(int ordinal) => IsNullField(ordinal, ValueFieldPosition);
}

/// <summary>
/// A handle to one record of a built-in scalar wrapper type.
/// </summary>
/// <typeparam name="TValue">The CLR type the field reads as.</typeparam>
public abstract class HollowScalar<TValue> : HollowObject
{
    /// <summary>
    /// Initialises a handle to <paramref name="ordinal"/>.
    /// </summary>
    protected HollowScalar(IHollowObjectDelegate objectDelegate, int ordinal)
        : base(objectDelegate, ordinal)
    {
    }

    /// <summary>The value this record wraps.</summary>
    public abstract TValue Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value?.ToString() ?? "null";
}

/// <summary>A shared <c>String</c> record.</summary>
public sealed class HString(IHollowObjectDelegate objectDelegate, int ordinal)
    : HollowScalar<string?>(objectDelegate, ordinal)
{
    /// <inheritdoc />
    public override string? Value => GetString("value");

    /// <summary>
    /// Whether this record's value equals <paramref name="testValue"/>, without materialising the
    /// stored string.
    /// </summary>
    public bool IsValueEqual(string? testValue) => IsStringFieldEqual("value", testValue);
}

/// <summary>A shared <c>Integer</c> record.</summary>
public sealed class HInteger(IHollowObjectDelegate objectDelegate, int ordinal)
    : HollowScalar<int?>(objectDelegate, ordinal)
{
    /// <inheritdoc />
    public override int? Value => IsNull("value") ? null : GetInt("value");
}

/// <summary>A shared <c>Long</c> record.</summary>
public sealed class HLong(IHollowObjectDelegate objectDelegate, int ordinal)
    : HollowScalar<long?>(objectDelegate, ordinal)
{
    /// <inheritdoc />
    public override long? Value => IsNull("value") ? null : GetLong("value");
}

/// <summary>A shared <c>Double</c> record.</summary>
public sealed class HDouble(IHollowObjectDelegate objectDelegate, int ordinal)
    : HollowScalar<double?>(objectDelegate, ordinal)
{
    /// <inheritdoc />
    public override double? Value => IsNull("value") ? null : GetDouble("value");
}

/// <summary>A shared <c>Float</c> record.</summary>
public sealed class HFloat(IHollowObjectDelegate objectDelegate, int ordinal)
    : HollowScalar<float?>(objectDelegate, ordinal)
{
    /// <inheritdoc />
    public override float? Value => IsNull("value") ? null : GetFloat("value");
}

/// <summary>A shared <c>Boolean</c> record.</summary>
public sealed class HBoolean(IHollowObjectDelegate objectDelegate, int ordinal)
    : HollowScalar<bool?>(objectDelegate, ordinal)
{
    /// <inheritdoc />
    public override bool? Value => IsNull("value") ? null : GetBoolean("value");
}

/// <summary>
/// A shared <c>Decimal</c> record.
/// </summary>
/// <remarks>
/// <strong>Format extension.</strong> No Java Hollow type corresponds to this one — see
/// <c>PORTING.md</c>.
/// </remarks>
public sealed class HDecimal(IHollowObjectDelegate objectDelegate, int ordinal)
    : HollowScalar<decimal?>(objectDelegate, ordinal)
{
    /// <inheritdoc />
    public override decimal? Value => GetDecimal("value");
}

/// <summary>Reads the <c>String</c> type's single field.</summary>
public sealed class HStringTypeApi(HollowApi api, IHollowObjectTypeDataAccess typeDataAccess)
    : HollowScalarTypeApi<string?>(api, typeDataAccess)
{
    /// <inheritdoc />
    public override string? GetValue(int ordinal) => ReadStringField(ordinal, ValueFieldPosition);

    /// <summary>
    /// Whether the record at <paramref name="ordinal"/> holds <paramref name="testValue"/>, without
    /// materialising the stored string.
    /// </summary>
    public bool IsValueEqual(int ordinal, string? testValue) =>
        IsStringFieldEqual(ordinal, ValueFieldPosition, testValue);
}

/// <summary>Reads the <c>Integer</c> type's single field.</summary>
public sealed class HIntegerTypeApi(HollowApi api, IHollowObjectTypeDataAccess typeDataAccess)
    : HollowScalarTypeApi<int?>(api, typeDataAccess)
{
    /// <inheritdoc />
    public override int? GetValue(int ordinal) =>
        IsNull(ordinal) ? null : ReadIntField(ordinal, ValueFieldPosition);
}

/// <summary>Reads the <c>Long</c> type's single field.</summary>
public sealed class HLongTypeApi(HollowApi api, IHollowObjectTypeDataAccess typeDataAccess)
    : HollowScalarTypeApi<long?>(api, typeDataAccess)
{
    /// <inheritdoc />
    public override long? GetValue(int ordinal) =>
        IsNull(ordinal) ? null : ReadLongField(ordinal, ValueFieldPosition);
}

/// <summary>Reads the <c>Double</c> type's single field.</summary>
public sealed class HDoubleTypeApi(HollowApi api, IHollowObjectTypeDataAccess typeDataAccess)
    : HollowScalarTypeApi<double?>(api, typeDataAccess)
{
    /// <inheritdoc />
    public override double? GetValue(int ordinal) =>
        IsNull(ordinal) ? null : ReadDoubleField(ordinal, ValueFieldPosition);
}

/// <summary>Reads the <c>Float</c> type's single field.</summary>
public sealed class HFloatTypeApi(HollowApi api, IHollowObjectTypeDataAccess typeDataAccess)
    : HollowScalarTypeApi<float?>(api, typeDataAccess)
{
    /// <inheritdoc />
    public override float? GetValue(int ordinal) =>
        IsNull(ordinal) ? null : ReadFloatField(ordinal, ValueFieldPosition);
}

/// <summary>Reads the <c>Boolean</c> type's single field.</summary>
public sealed class HBooleanTypeApi(HollowApi api, IHollowObjectTypeDataAccess typeDataAccess)
    : HollowScalarTypeApi<bool?>(api, typeDataAccess)
{
    /// <inheritdoc />
    public override bool? GetValue(int ordinal) => ReadBooleanField(ordinal, ValueFieldPosition);
}

/// <summary>Reads the <c>Decimal</c> type's single field.</summary>
public sealed class HDecimalTypeApi(HollowApi api, IHollowObjectTypeDataAccess typeDataAccess)
    : HollowScalarTypeApi<decimal?>(api, typeDataAccess)
{
    /// <inheritdoc />
    public override decimal? GetValue(int ordinal) => ReadDecimalField(ordinal, ValueFieldPosition);
}

/// <summary>
/// Builds handles to the built-in scalar wrapper types.
/// </summary>
/// <remarks>
/// Java gives each of these its own factory class; a lookup by schema field type does the same job
/// without seven of them.
/// </remarks>
public static class HollowScalarTypes
{
    /// <summary>
    /// Wraps the record at <paramref name="ordinal"/> as whichever scalar its type holds.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The type does not have exactly one field, or that field is not a scalar.
    /// </exception>
    public static HollowObject Instantiate(IHollowObjectTypeDataAccess typeDataAccess, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(typeDataAccess);

        HollowObjectSchema schema = typeDataAccess.Schema;

        if (schema.FieldCount != 1)
        {
            throw new ArgumentException(
                $"{schema.Name} has {schema.FieldCount} fields, so it is not a scalar wrapper type.",
                nameof(typeDataAccess));
        }

        HollowObjectGenericDelegate objectDelegate = new(typeDataAccess);

        return schema.GetFieldType(0) switch
        {
            FieldType.String => new HString(objectDelegate, ordinal),
            FieldType.Int => new HInteger(objectDelegate, ordinal),
            FieldType.Long => new HLong(objectDelegate, ordinal),
            FieldType.Double => new HDouble(objectDelegate, ordinal),
            FieldType.Float => new HFloat(objectDelegate, ordinal),
            FieldType.Boolean => new HBoolean(objectDelegate, ordinal),
            FieldType.Decimal => new HDecimal(objectDelegate, ordinal),
            _ => throw new ArgumentException(
                $"{schema.Name}'s only field is a {schema.GetFieldType(0)}, which is not a scalar.",
                nameof(typeDataAccess)),
        };
    }

    /// <summary>
    /// The type API for a built-in scalar wrapper type, or <see langword="null"/> if
    /// <paramref name="typeDataAccess"/> does not read one.
    /// </summary>
    /// <remarks>
    /// A generated API calls this rather than naming the concrete type APIs, so that the mapping from
    /// field type to wrapper lives in one place.
    /// </remarks>
    public static HollowObjectTypeApi? TypeApiFor(
        HollowApi api, IHollowObjectTypeDataAccess typeDataAccess)
    {
        ArgumentNullException.ThrowIfNull(typeDataAccess);

        HollowObjectSchema schema = typeDataAccess.Schema;

        if (schema.FieldCount != 1)
        {
            return null;
        }

        return schema.GetFieldType(0) switch
        {
            FieldType.String => new HStringTypeApi(api, typeDataAccess),
            FieldType.Int => new HIntegerTypeApi(api, typeDataAccess),
            FieldType.Long => new HLongTypeApi(api, typeDataAccess),
            FieldType.Double => new HDoubleTypeApi(api, typeDataAccess),
            FieldType.Float => new HFloatTypeApi(api, typeDataAccess),
            FieldType.Boolean => new HBooleanTypeApi(api, typeDataAccess),
            FieldType.Decimal => new HDecimalTypeApi(api, typeDataAccess),
            _ => null,
        };
    }

    /// <summary>
    /// A factory building handles of <typeparamref name="TScalar"/>, for a generated API to hold.
    /// </summary>
    /// <typeparam name="TScalar">The scalar wrapper class to build.</typeparam>
    public static HollowFactory<TScalar> Factory<TScalar>()
        where TScalar : HollowObject => new ScalarFactory<TScalar>();

    private sealed class ScalarFactory<TScalar> : HollowFactory<TScalar>
        where TScalar : HollowObject
    {
        public override TScalar NewHollowObject(
            IHollowTypeDataAccess typeDataAccess, HollowTypeApi? typeApi, int ordinal) =>
            (TScalar)Instantiate((IHollowObjectTypeDataAccess)typeDataAccess, ordinal);
    }
}
