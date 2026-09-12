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
using Hollow.Core.Memory.Encoding;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Iterator;
using Hollow.Core.Read.Missing;
using Hollow.Core.Schema;

namespace Hollow.Core.Read.DataAccess;

/// <summary>
/// Marks a type data access that stands in for a type the dataset does not have.
/// </summary>
/// <remarks>
/// A generated API is built against one data model and may be pointed at a dataset written from
/// another, in which case a whole type can be absent. Rather than failing to construct, the API holds
/// one of these, and every read of the missing type goes to
/// <see cref="IHollowDataAccess.MissingDataHandler"/>.
/// <para>
/// Java identifies these by their concrete classes, which means each check names all four. A marker
/// interface says the same thing once.
/// </para>
/// </remarks>
public interface IHollowMissingTypeDataAccess : IHollowTypeDataAccess
{
    /// <summary>The name of the type the dataset does not have.</summary>
    string MissingTypeName { get; }
}

/// <summary>
/// Stands in for an object type the dataset does not have.
/// </summary>
/// <param name="dataAccess">The dataset that is missing the type.</param>
/// <param name="typeName">The name of the missing type.</param>
public sealed class HollowObjectMissingDataAccess(IHollowDataAccess dataAccess, string typeName)
    : IHollowObjectTypeDataAccess, IHollowMissingTypeDataAccess
{
    /// <inheritdoc />
    public IHollowDataAccess DataAccess { get; } = dataAccess;

    /// <inheritdoc />
    public string MissingTypeName { get; } = typeName;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// The missing-data handler supplies no schema for the type, which the default one does not.
    /// </exception>
    public HollowObjectSchema Schema =>
        DataAccess.MissingDataHandler.HandleSchema(MissingTypeName) as HollowObjectSchema
        ?? throw MissingDataAccess.NoSchema(MissingTypeName);

    /// <inheritdoc />
    HollowSchema IHollowTypeDataAccess.Schema => Schema;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">Always: there is no state to read.</exception>
    public HollowTypeReadState TypeState => throw MissingDataAccess.NoTypeState(MissingTypeName);

    /// <inheritdoc />
    public bool IsNull(int ordinal, int fieldIndex) =>
        Handler.HandleIsNull(MissingTypeName, ordinal, FieldName(fieldIndex));

    /// <inheritdoc />
    public int ReadOrdinal(int ordinal, int fieldIndex) =>
        Handler.HandleReferencedOrdinal(MissingTypeName, ordinal, FieldName(fieldIndex));

    /// <inheritdoc />
    public int ReadInt(int ordinal, int fieldIndex) =>
        Handler.HandleInt(MissingTypeName, ordinal, FieldName(fieldIndex));

    /// <inheritdoc />
    public float ReadFloat(int ordinal, int fieldIndex) =>
        Handler.HandleFloat(MissingTypeName, ordinal, FieldName(fieldIndex));

    /// <inheritdoc />
    public double ReadDouble(int ordinal, int fieldIndex) =>
        Handler.HandleDouble(MissingTypeName, ordinal, FieldName(fieldIndex));

    /// <inheritdoc />
    public long ReadLong(int ordinal, int fieldIndex) =>
        Handler.HandleLong(MissingTypeName, ordinal, FieldName(fieldIndex));

    /// <inheritdoc />
    public bool? ReadBoolean(int ordinal, int fieldIndex) =>
        Handler.HandleBoolean(MissingTypeName, ordinal, FieldName(fieldIndex));

    /// <inheritdoc />
    public byte[]? ReadBytes(int ordinal, int fieldIndex) =>
        Handler.HandleBytes(MissingTypeName, ordinal, FieldName(fieldIndex));

    /// <inheritdoc />
    public decimal? ReadDecimal(int ordinal, int fieldIndex) =>
        Handler.HandleDecimal(MissingTypeName, ordinal, FieldName(fieldIndex));

    /// <inheritdoc />
    public string? ReadString(int ordinal, int fieldIndex) =>
        Handler.HandleString(MissingTypeName, ordinal, FieldName(fieldIndex));

    /// <inheritdoc />
    public bool IsStringFieldEqual(int ordinal, int fieldIndex, string? testValue) =>
        Handler.HandleStringEquals(MissingTypeName, ordinal, FieldName(fieldIndex), testValue);

    /// <inheritdoc />
    public int FindVarLengthFieldHashCode(int ordinal, int fieldIndex) =>
        Schema.GetFieldType(fieldIndex) == FieldType.String
            ? HashCodes.Compute(ReadString(ordinal, fieldIndex))
            : HashCodes.Compute(ReadBytes(ordinal, fieldIndex));

    /// <inheritdoc />
    public int VarLengthFieldByteLength(int ordinal, int fieldIndex) => -1;

    /// <inheritdoc />
    public int ReadStringInto(int ordinal, int fieldIndex, Span<char> destination) =>
        MissingDataAccess.CopyInto(ReadString(ordinal, fieldIndex), destination);

    /// <inheritdoc />
    public int ReadBytesInto(int ordinal, int fieldIndex, Span<byte> destination) =>
        MissingDataAccess.CopyInto(ReadBytes(ordinal, fieldIndex), destination);

    /// <inheritdoc />
    public bool TryGetBytesSpan(int ordinal, int fieldIndex, out ReadOnlySpan<byte> value)
    {
        // There is no storage to point into, so there is never a view — the caller copies instead.
        value = default;

        return false;
    }

    /// <inheritdoc />
    public ReadOnlySequence<byte> GetVarLengthSequence(int ordinal, int fieldIndex) =>
        ReadBytes(ordinal, fieldIndex) is { Length: > 0 } bytes
            ? new ReadOnlySequence<byte>(bytes)
            : ReadOnlySequence<byte>.Empty;

    private IMissingDataHandler Handler => DataAccess.MissingDataHandler;

    private string FieldName(int fieldIndex) =>
        DataAccess.MissingDataHandler.HandleSchema(MissingTypeName) is HollowObjectSchema schema
            ? schema.GetFieldName(fieldIndex)
            : MissingDataAccess.UnknownField;
}

/// <summary>
/// Stands in for a list type the dataset does not have.
/// </summary>
/// <param name="dataAccess">The dataset that is missing the type.</param>
/// <param name="typeName">The name of the missing type.</param>
public sealed class HollowListMissingDataAccess(IHollowDataAccess dataAccess, string typeName)
    : IHollowListTypeDataAccess, IHollowMissingTypeDataAccess
{
    /// <inheritdoc />
    public IHollowDataAccess DataAccess { get; } = dataAccess;

    /// <inheritdoc />
    public string MissingTypeName { get; } = typeName;

    /// <inheritdoc />
    public HollowListSchema Schema =>
        DataAccess.MissingDataHandler.HandleSchema(MissingTypeName) as HollowListSchema
        ?? throw MissingDataAccess.NoSchema(MissingTypeName);

    /// <inheritdoc />
    HollowCollectionSchema IHollowCollectionTypeDataAccess.Schema => Schema;

    /// <inheritdoc />
    HollowSchema IHollowTypeDataAccess.Schema => Schema;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">Always: there is no state to read.</exception>
    public HollowTypeReadState TypeState => throw MissingDataAccess.NoTypeState(MissingTypeName);

    /// <inheritdoc />
    public int Size(int ordinal) => DataAccess.MissingDataHandler.HandleListSize(MissingTypeName, ordinal);

    /// <inheritdoc />
    public int GetElementOrdinal(int ordinal, int listIndex) =>
        DataAccess.MissingDataHandler.HandleListElementOrdinal(MissingTypeName, ordinal, listIndex);

    /// <inheritdoc />
    public IHollowOrdinalIterator OrdinalIterator(int ordinal) =>
        DataAccess.MissingDataHandler.HandleListIterator(MissingTypeName, ordinal);
}

/// <summary>
/// Stands in for a set type the dataset does not have.
/// </summary>
/// <param name="dataAccess">The dataset that is missing the type.</param>
/// <param name="typeName">The name of the missing type.</param>
public sealed class HollowSetMissingDataAccess(IHollowDataAccess dataAccess, string typeName)
    : IHollowSetTypeDataAccess, IHollowMissingTypeDataAccess
{
    /// <inheritdoc />
    public IHollowDataAccess DataAccess { get; } = dataAccess;

    /// <inheritdoc />
    public string MissingTypeName { get; } = typeName;

    /// <inheritdoc />
    public HollowSetSchema Schema =>
        DataAccess.MissingDataHandler.HandleSchema(MissingTypeName) as HollowSetSchema
        ?? throw MissingDataAccess.NoSchema(MissingTypeName);

    /// <inheritdoc />
    HollowCollectionSchema IHollowCollectionTypeDataAccess.Schema => Schema;

    /// <inheritdoc />
    HollowSchema IHollowTypeDataAccess.Schema => Schema;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">Always: there is no state to read.</exception>
    public HollowTypeReadState TypeState => throw MissingDataAccess.NoTypeState(MissingTypeName);

    /// <inheritdoc />
    public int Size(int ordinal) => DataAccess.MissingDataHandler.HandleSetSize(MissingTypeName, ordinal);

    /// <inheritdoc />
    public bool Contains(int ordinal, int value) => Contains(ordinal, value, value);

    /// <inheritdoc />
    public bool Contains(int ordinal, int value, int hashCode) =>
        DataAccess.MissingDataHandler.HandleSetContainsElement(MissingTypeName, ordinal, value, hashCode);

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">Always: there are no buckets to read.</exception>
    public int RelativeBucketValue(int ordinal, int bucketIndex) =>
        throw MissingDataAccess.NoBuckets(MissingTypeName);

    /// <inheritdoc />
    public IHollowOrdinalIterator PotentialMatchOrdinalIterator(int ordinal, int hashCode) =>
        DataAccess.MissingDataHandler.HandleSetPotentialMatchIterator(MissingTypeName, ordinal, hashCode);

    /// <inheritdoc />
    public IHollowOrdinalIterator OrdinalIterator(int ordinal) =>
        DataAccess.MissingDataHandler.HandleSetIterator(MissingTypeName, ordinal);

    /// <inheritdoc />
    public int FindElement(int ordinal, params object?[] hashKey) =>
        DataAccess.MissingDataHandler.HandleSetFindElement(MissingTypeName, ordinal, hashKey);
}

/// <summary>
/// Stands in for a map type the dataset does not have.
/// </summary>
/// <param name="dataAccess">The dataset that is missing the type.</param>
/// <param name="typeName">The name of the missing type.</param>
public sealed class HollowMapMissingDataAccess(IHollowDataAccess dataAccess, string typeName)
    : IHollowMapTypeDataAccess, IHollowMissingTypeDataAccess
{
    /// <inheritdoc />
    public IHollowDataAccess DataAccess { get; } = dataAccess;

    /// <inheritdoc />
    public string MissingTypeName { get; } = typeName;

    /// <inheritdoc />
    public HollowMapSchema Schema =>
        DataAccess.MissingDataHandler.HandleSchema(MissingTypeName) as HollowMapSchema
        ?? throw MissingDataAccess.NoSchema(MissingTypeName);

    /// <inheritdoc />
    HollowSchema IHollowTypeDataAccess.Schema => Schema;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">Always: there is no state to read.</exception>
    public HollowTypeReadState TypeState => throw MissingDataAccess.NoTypeState(MissingTypeName);

    /// <inheritdoc />
    public int Size(int ordinal) => DataAccess.MissingDataHandler.HandleMapSize(MissingTypeName, ordinal);

    /// <inheritdoc />
    public int Get(int ordinal, int keyOrdinal) => Get(ordinal, keyOrdinal, keyOrdinal);

    /// <inheritdoc />
    public int Get(int ordinal, int keyOrdinal, int hashCode) =>
        DataAccess.MissingDataHandler.HandleMapGet(MissingTypeName, ordinal, keyOrdinal, hashCode);

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">Always: there are no buckets to read.</exception>
    public long RelativeBucket(int ordinal, int bucketIndex) =>
        throw MissingDataAccess.NoBuckets(MissingTypeName);

    /// <inheritdoc />
    public IHollowMapEntryOrdinalIterator PotentialMatchOrdinalIterator(int ordinal, int hashCode) =>
        DataAccess.MissingDataHandler.HandleMapPotentialMatchOrdinalIterator(
            MissingTypeName, ordinal, hashCode);

    /// <inheritdoc />
    public IHollowMapEntryOrdinalIterator OrdinalIterator(int ordinal) =>
        DataAccess.MissingDataHandler.HandleMapOrdinalIterator(MissingTypeName, ordinal);

    /// <inheritdoc />
    public int FindKey(int ordinal, params object?[] hashKey) =>
        DataAccess.MissingDataHandler.HandleMapFindKey(MissingTypeName, ordinal, hashKey);

    /// <inheritdoc />
    public int FindValue(int ordinal, params object?[] hashKey) =>
        DataAccess.MissingDataHandler.HandleMapFindValue(MissingTypeName, ordinal, hashKey);

    /// <inheritdoc />
    public long FindEntry(int ordinal, params object?[] hashKey) =>
        DataAccess.MissingDataHandler.HandleMapFindEntry(MissingTypeName, ordinal, hashKey);
}

/// <summary>
/// Builds the stand-in for a type the dataset does not have, of whichever kind the model expects.
/// </summary>
public static class MissingDataAccess
{
    internal const string UnknownField = "?";

    /// <summary>
    /// A stand-in for <paramref name="typeName"/> of the kind <paramref name="schemaType"/> names.
    /// </summary>
    public static IHollowTypeDataAccess For(
        IHollowDataAccess dataAccess, string typeName, SchemaType schemaType)
    {
        ArgumentNullException.ThrowIfNull(dataAccess);

        return schemaType switch
        {
            SchemaType.Object => new HollowObjectMissingDataAccess(dataAccess, typeName),
            SchemaType.List => new HollowListMissingDataAccess(dataAccess, typeName),
            SchemaType.Set => new HollowSetMissingDataAccess(dataAccess, typeName),
            SchemaType.Map => new HollowMapMissingDataAccess(dataAccess, typeName),
            _ => throw new ArgumentOutOfRangeException(nameof(schemaType), schemaType, "unknown record kind"),
        };
    }

    internal static InvalidOperationException NoSchema(string typeName) =>
        new($"the dataset has no type named {typeName}, and the missing-data handler supplies no schema "
            + "for it");

    internal static InvalidOperationException NoTypeState(string typeName) =>
        new($"the dataset has no type named {typeName}, so there is no read state for it");

    internal static InvalidOperationException NoBuckets(string typeName) =>
        new($"the dataset has no type named {typeName}, so its hash buckets cannot be read");

    internal static int CopyInto(string? value, Span<char> destination)
    {
        if (value is null)
        {
            return -1;
        }

        if (destination.Length < value.Length)
        {
            throw new ArgumentException(
                $"the destination holds {destination.Length} characters, and the value is {value.Length}",
                nameof(destination));
        }

        value.CopyTo(destination);

        return value.Length;
    }

    internal static int CopyInto(byte[]? value, Span<byte> destination)
    {
        if (value is null)
        {
            return -1;
        }

        if (destination.Length < value.Length)
        {
            throw new ArgumentException(
                $"the destination holds {destination.Length} bytes, and the value is {value.Length}",
                nameof(destination));
        }

        value.CopyTo(destination);

        return value.Length;
    }
}
