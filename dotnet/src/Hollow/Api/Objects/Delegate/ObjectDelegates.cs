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
using Hollow.Api.Custom;
using Hollow.Core;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.Missing;
using Hollow.Core.Schema;

namespace Hollow.Api.Objects.Delegate;

/// <summary>
/// Where a record wrapper gets its data from.
/// </summary>
/// <remarks>
/// <para>
/// There are two kinds. A <em>lookup</em> delegate reads straight out of the blob every time, which
/// costs a little per read and nothing per record. A <em>cached</em> delegate copies a record's fields
/// once and answers from the copy, which costs memory per record and makes reads as cheap as reading a
/// field of an ordinary object.
/// </para>
/// <para>
/// The lookup delegate is the right default; caching is worth it for a type with few records that is
/// read very often. Which one is in use is invisible to the wrapper on top of it, which is the point
/// of the indirection.
/// </para>
/// <para>
/// Named <c>HollowRecordDelegate</c> in Java; the <c>I</c> prefix follows the .NET convention.
/// </para>
/// </remarks>
public interface IHollowRecordDelegate
{
}

/// <summary>
/// A delegate holding a copy of its record, which has to be repointed when the dataset updates.
/// </summary>
public interface IHollowCachedDelegate : IHollowRecordDelegate
{
    /// <summary>
    /// Repoints this delegate at <paramref name="typeApi"/>, which a cache provider calls when the
    /// API is rebuilt over a newer state.
    /// </summary>
    void UpdateTypeApi(HollowTypeApi typeApi);
}

/// <summary>
/// Reads the fields of an object record by name.
/// </summary>
public interface IHollowObjectDelegate : IHollowRecordDelegate
{
    /// <summary>The schema of the type this delegate reads.</summary>
    HollowObjectSchema Schema { get; }

    /// <summary>Read access to the type's records.</summary>
    IHollowObjectTypeDataAccess TypeDataAccess { get; }

    /// <summary>
    /// The type API this delegate belongs to, or <see langword="null"/> when it was built straight
    /// from a data access rather than through an API.
    /// </summary>
    HollowObjectTypeApi? TypeApi { get; }

    /// <summary>Whether the named field is null.</summary>
    bool IsNull(int ordinal, string fieldName);

    /// <summary>Reads a boolean field, which reads false when null.</summary>
    bool GetBoolean(int ordinal, string fieldName);

    /// <summary>Reads a reference field as the referenced record's ordinal.</summary>
    int GetOrdinal(int ordinal, string fieldName);

    /// <summary>Reads an int field.</summary>
    int GetInt(int ordinal, string fieldName);

    /// <summary>Reads a long field.</summary>
    long GetLong(int ordinal, string fieldName);

    /// <summary>Reads a float field.</summary>
    float GetFloat(int ordinal, string fieldName);

    /// <summary>Reads a double field.</summary>
    double GetDouble(int ordinal, string fieldName);

    /// <summary>Reads a decimal field, which may be null.</summary>
    /// <remarks><strong>Format extension</strong> — see <c>PORTING.md</c>.</remarks>
    decimal? GetDecimal(int ordinal, string fieldName);

    /// <summary>Reads a string field, which may be null.</summary>
    string? GetString(int ordinal, string fieldName);

    /// <summary>Compares a string field without materialising the stored string.</summary>
    bool IsStringFieldEqual(int ordinal, string fieldName, string? testValue);

    /// <summary>Reads a bytes field, which may be null.</summary>
    byte[]? GetBytes(int ordinal, string fieldName);

    /// <summary>
    /// The number of bytes stored for a variable-length field, or -1 when it is null; how big a buffer
    /// the allocation-free reads below need.
    /// </summary>
    int GetVarLengthByteLength(int ordinal, string fieldName);

    /// <summary>
    /// Decodes a string field into <paramref name="destination"/>, returning the characters written or
    /// -1 when the field is null.
    /// </summary>
    int ReadStringInto(int ordinal, string fieldName, Span<char> destination);

    /// <summary>
    /// Copies a bytes field into <paramref name="destination"/>, returning the bytes written or -1 when
    /// the field is null.
    /// </summary>
    int ReadBytesInto(int ordinal, string fieldName, Span<byte> destination);

    /// <summary>
    /// Views a bytes field as a span over the blob itself, copying nothing, where the storage allows.
    /// </summary>
    bool TryGetBytesSpan(int ordinal, string fieldName, out ReadOnlySpan<byte> value);

    /// <summary>
    /// Views the stored bytes of a variable-length field as a sequence, copying nothing, whether or not
    /// they are contiguous.
    /// </summary>
    ReadOnlySequence<byte> GetVarLengthSequence(int ordinal, string fieldName);
}

/// <summary>
/// Resolves a field name against the schema and falls through to the missing-data handler when the
/// loaded dataset does not have it.
/// </summary>
/// <remarks>
/// This is where reading by name is turned into reading by position, which is why the by-position
/// <see cref="HollowObjectTypeApi"/> is the faster layer: a generated API resolves each position once
/// at construction, where this resolves one per read.
/// </remarks>
public abstract class HollowObjectAbstractDelegate : IHollowObjectDelegate
{
    /// <inheritdoc />
    public abstract HollowObjectSchema Schema { get; }

    /// <inheritdoc />
    public abstract IHollowObjectTypeDataAccess TypeDataAccess { get; }

    /// <inheritdoc />
    public abstract HollowObjectTypeApi? TypeApi { get; }

    /// <inheritdoc />
    public bool IsNull(int ordinal, string fieldName)
    {
        int fieldIndex = Schema.GetPosition(fieldName);

        return fieldIndex == HollowConstants.OrdinalNone
            ? MissingDataHandler.HandleIsNull(Schema.Name, ordinal, fieldName)
            : TypeDataAccess.IsNull(ordinal, fieldIndex);
    }

    /// <inheritdoc />
    public bool GetBoolean(int ordinal, string fieldName)
    {
        int fieldIndex = Schema.GetPosition(fieldName);

        bool? value = fieldIndex == HollowConstants.OrdinalNone
            ? MissingDataHandler.HandleBoolean(Schema.Name, ordinal, fieldName)
            : TypeDataAccess.ReadBoolean(ordinal, fieldIndex);

        return value ?? false;
    }

    /// <inheritdoc />
    public int GetOrdinal(int ordinal, string fieldName)
    {
        int fieldIndex = Schema.GetPosition(fieldName);

        return fieldIndex == HollowConstants.OrdinalNone
            ? MissingDataHandler.HandleReferencedOrdinal(Schema.Name, ordinal, fieldName)
            : TypeDataAccess.ReadOrdinal(ordinal, fieldIndex);
    }

    /// <inheritdoc />
    public int GetInt(int ordinal, string fieldName)
    {
        int fieldIndex = Schema.GetPosition(fieldName);

        return fieldIndex == HollowConstants.OrdinalNone
            ? MissingDataHandler.HandleInt(Schema.Name, ordinal, fieldName)
            : TypeDataAccess.ReadInt(ordinal, fieldIndex);
    }

    /// <inheritdoc />
    public long GetLong(int ordinal, string fieldName)
    {
        int fieldIndex = Schema.GetPosition(fieldName);

        return fieldIndex == HollowConstants.OrdinalNone
            ? MissingDataHandler.HandleLong(Schema.Name, ordinal, fieldName)
            : TypeDataAccess.ReadLong(ordinal, fieldIndex);
    }

    /// <inheritdoc />
    public float GetFloat(int ordinal, string fieldName)
    {
        int fieldIndex = Schema.GetPosition(fieldName);

        return fieldIndex == HollowConstants.OrdinalNone
            ? MissingDataHandler.HandleFloat(Schema.Name, ordinal, fieldName)
            : TypeDataAccess.ReadFloat(ordinal, fieldIndex);
    }

    /// <inheritdoc />
    public double GetDouble(int ordinal, string fieldName)
    {
        int fieldIndex = Schema.GetPosition(fieldName);

        return fieldIndex == HollowConstants.OrdinalNone
            ? MissingDataHandler.HandleDouble(Schema.Name, ordinal, fieldName)
            : TypeDataAccess.ReadDouble(ordinal, fieldIndex);
    }

    /// <inheritdoc />
    public decimal? GetDecimal(int ordinal, string fieldName)
    {
        int fieldIndex = Schema.GetPosition(fieldName);

        return fieldIndex == HollowConstants.OrdinalNone
            ? MissingDataHandler.HandleDecimal(Schema.Name, ordinal, fieldName)
            : TypeDataAccess.ReadDecimal(ordinal, fieldIndex);
    }

    /// <inheritdoc />
    public string? GetString(int ordinal, string fieldName)
    {
        int fieldIndex = Schema.GetPosition(fieldName);

        return fieldIndex == HollowConstants.OrdinalNone
            ? MissingDataHandler.HandleString(Schema.Name, ordinal, fieldName)
            : TypeDataAccess.ReadString(ordinal, fieldIndex);
    }

    /// <inheritdoc />
    public bool IsStringFieldEqual(int ordinal, string fieldName, string? testValue)
    {
        int fieldIndex = Schema.GetPosition(fieldName);

        return fieldIndex == HollowConstants.OrdinalNone
            ? MissingDataHandler.HandleStringEquals(Schema.Name, ordinal, fieldName, testValue)
            : TypeDataAccess.IsStringFieldEqual(ordinal, fieldIndex, testValue);
    }

    /// <inheritdoc />
    public byte[]? GetBytes(int ordinal, string fieldName)
    {
        int fieldIndex = Schema.GetPosition(fieldName);

        return fieldIndex == HollowConstants.OrdinalNone
            ? MissingDataHandler.HandleBytes(Schema.Name, ordinal, fieldName)
            : TypeDataAccess.ReadBytes(ordinal, fieldIndex);
    }

    /// <inheritdoc />
    public int GetVarLengthByteLength(int ordinal, string fieldName)
    {
        int fieldIndex = Schema.GetPosition(fieldName);

        return fieldIndex == HollowConstants.OrdinalNone
            ? -1
            : TypeDataAccess.VarLengthFieldByteLength(ordinal, fieldIndex);
    }

    /// <inheritdoc />
    public int ReadStringInto(int ordinal, string fieldName, Span<char> destination)
    {
        int fieldIndex = Schema.GetPosition(fieldName);

        if (fieldIndex != HollowConstants.OrdinalNone)
        {
            return TypeDataAccess.ReadStringInto(ordinal, fieldIndex, destination);
        }

        // A field the loaded dataset does not have has no stored bytes to decode, so the fallback's
        // answer is copied in rather than read.
        string? missing = MissingDataHandler.HandleString(Schema.Name, ordinal, fieldName);

        if (missing is null)
        {
            return -1;
        }

        missing.AsSpan().CopyTo(destination);

        return missing.Length;
    }

    /// <inheritdoc />
    public int ReadBytesInto(int ordinal, string fieldName, Span<byte> destination)
    {
        int fieldIndex = Schema.GetPosition(fieldName);

        if (fieldIndex != HollowConstants.OrdinalNone)
        {
            return TypeDataAccess.ReadBytesInto(ordinal, fieldIndex, destination);
        }

        byte[]? missing = MissingDataHandler.HandleBytes(Schema.Name, ordinal, fieldName);

        if (missing is null)
        {
            return -1;
        }

        missing.CopyTo(destination);

        return missing.Length;
    }

    /// <inheritdoc />
    public bool TryGetBytesSpan(int ordinal, string fieldName, out ReadOnlySpan<byte> value)
    {
        int fieldIndex = Schema.GetPosition(fieldName);

        if (fieldIndex != HollowConstants.OrdinalNone)
        {
            return TypeDataAccess.TryGetBytesSpan(ordinal, fieldIndex, out value);
        }

        value = MissingDataHandler.HandleBytes(Schema.Name, ordinal, fieldName);

        return !value.IsEmpty;
    }

    /// <inheritdoc />
    public ReadOnlySequence<byte> GetVarLengthSequence(int ordinal, string fieldName)
    {
        int fieldIndex = Schema.GetPosition(fieldName);

        if (fieldIndex != HollowConstants.OrdinalNone)
        {
            return TypeDataAccess.GetVarLengthSequence(ordinal, fieldIndex);
        }

        byte[]? missing = MissingDataHandler.HandleBytes(Schema.Name, ordinal, fieldName);

        return missing is null ? ReadOnlySequence<byte>.Empty : new ReadOnlySequence<byte>(missing);
    }

    private IMissingDataHandler MissingDataHandler => TypeDataAccess.DataAccess.MissingDataHandler;
}

/// <summary>
/// Reads an object record straight out of the blob, by field name.
/// </summary>
/// <remarks>
/// This is what backs <see cref="Generic.GenericHollowObject"/>, and so is what makes traversing a
/// dataset possible without any generated code at all.
/// </remarks>
public sealed class HollowObjectGenericDelegate : HollowObjectAbstractDelegate
{
    /// <summary>
    /// Reads the records of <paramref name="typeDataAccess"/>.
    /// </summary>
    public HollowObjectGenericDelegate(IHollowObjectTypeDataAccess typeDataAccess)
    {
        ArgumentNullException.ThrowIfNull(typeDataAccess);

        TypeDataAccess = typeDataAccess;
    }

    /// <summary>
    /// Reads the records <paramref name="typeApi"/> covers.
    /// </summary>
    public HollowObjectGenericDelegate(HollowObjectTypeApi typeApi)
    {
        ArgumentNullException.ThrowIfNull(typeApi);

        TypeDataAccess = typeApi.TypeDataAccess;
        TypeApi = typeApi;
    }

    /// <inheritdoc />
    public override HollowObjectSchema Schema => TypeDataAccess.Schema;

    /// <inheritdoc />
    public override IHollowObjectTypeDataAccess TypeDataAccess { get; }

    /// <inheritdoc />
    public override HollowObjectTypeApi? TypeApi { get; }
}
