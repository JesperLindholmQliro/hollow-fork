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

namespace Hollow.Core.Read.DataAccess.Disabled;

/// <summary>
/// The exception every disabled data access throws.
/// </summary>
/// <remarks>
/// <para>
/// A reference that reads through one of these was held past the point where the consumer stopped
/// keeping its data alive. That is a bug in the holder, not in the consumer: the message names the
/// feature so that the stack trace is enough to find it.
/// </para>
/// <para>
/// Java throws a bare <c>IllegalStateException</c> with the text "Data Access is Disabled".
/// </para>
/// </remarks>
public sealed class HollowDataAccessDisabledException()
    : InvalidOperationException(
        "This data access has been disabled. A Hollow record was read after the consumer dropped the "
        + "state it belonged to, which object longevity does once a reference has gone unused for "
        + "longer than the configured grace and usage detection periods.");

/// <summary>
/// A whole dataset that has been dropped.
/// </summary>
/// <remarks>
/// Swapped into a <see cref="Proxy.HollowProxyDataAccess"/> once the stale reference detector decides
/// that a reference is being held but not used, so that the data behind it can be released.
/// </remarks>
public sealed class HollowDisabledDataAccess : IHollowDataAccess
{
    private HollowDisabledDataAccess()
    {
    }

    /// <summary>The single instance; the type carries no state.</summary>
    public static HollowDisabledDataAccess Instance { get; } = new();

    /// <inheritdoc />
    public IReadOnlyList<HollowSchema> Schemas => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public IMissingDataHandler MissingDataHandler => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public IHollowTypeDataAccess? GetTypeDataAccess(string type) => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public IHollowTypeDataAccess? GetTypeDataAccess(string type, int ordinal) =>
        throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public HollowSchema? GetSchema(string typeName) => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public HollowSchema GetNonNullSchema(string typeName) => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public bool HasType(string typeName) => throw new HollowDataAccessDisabledException();
}

/// <summary>
/// An object type whose data has been dropped.
/// </summary>
public sealed class HollowObjectDisabledDataAccess : IHollowObjectTypeDataAccess
{
    private HollowObjectDisabledDataAccess()
    {
    }

    /// <summary>The single instance; the type carries no state.</summary>
    public static HollowObjectDisabledDataAccess Instance { get; } = new();

    /// <inheritdoc />
    public IHollowDataAccess DataAccess => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public HollowObjectSchema Schema => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    HollowSchema IHollowTypeDataAccess.Schema => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public HollowTypeReadState TypeState => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public bool IsNull(int ordinal, int fieldIndex) => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public int ReadOrdinal(int ordinal, int fieldIndex) => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public int ReadInt(int ordinal, int fieldIndex) => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public float ReadFloat(int ordinal, int fieldIndex) => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public double ReadDouble(int ordinal, int fieldIndex) => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public long ReadLong(int ordinal, int fieldIndex) => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public bool? ReadBoolean(int ordinal, int fieldIndex) => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public byte[]? ReadBytes(int ordinal, int fieldIndex) => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public decimal? ReadDecimal(int ordinal, int fieldIndex) => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public string? ReadString(int ordinal, int fieldIndex) => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public bool IsStringFieldEqual(int ordinal, int fieldIndex, string? testValue) =>
        throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public int FindVarLengthFieldHashCode(int ordinal, int fieldIndex) =>
        throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public int VarLengthFieldByteLength(int ordinal, int fieldIndex) =>
        throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public int ReadStringInto(int ordinal, int fieldIndex, Span<char> destination) =>
        throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public int ReadBytesInto(int ordinal, int fieldIndex, Span<byte> destination) =>
        throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public bool TryGetBytesSpan(int ordinal, int fieldIndex, out ReadOnlySpan<byte> value) =>
        throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public ReadOnlySequence<byte> GetVarLengthSequence(int ordinal, int fieldIndex) =>
        throw new HollowDataAccessDisabledException();
}

/// <summary>
/// A list type whose data has been dropped.
/// </summary>
public sealed class HollowListDisabledDataAccess : IHollowListTypeDataAccess
{
    private HollowListDisabledDataAccess()
    {
    }

    /// <summary>The single instance; the type carries no state.</summary>
    public static HollowListDisabledDataAccess Instance { get; } = new();

    /// <inheritdoc />
    public IHollowDataAccess DataAccess => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public HollowListSchema Schema => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    HollowCollectionSchema IHollowCollectionTypeDataAccess.Schema => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    HollowSchema IHollowTypeDataAccess.Schema => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public HollowTypeReadState TypeState => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public int Size(int ordinal) => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public IEnumerable<int> ElementOrdinals(int ordinal) => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public int GetElementOrdinal(int ordinal, int listIndex) => throw new HollowDataAccessDisabledException();
}

/// <summary>
/// A set type whose data has been dropped.
/// </summary>
public sealed class HollowSetDisabledDataAccess : IHollowSetTypeDataAccess
{
    private HollowSetDisabledDataAccess()
    {
    }

    /// <summary>The single instance; the type carries no state.</summary>
    public static HollowSetDisabledDataAccess Instance { get; } = new();

    /// <inheritdoc />
    public IHollowDataAccess DataAccess => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public HollowSetSchema Schema => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    HollowCollectionSchema IHollowCollectionTypeDataAccess.Schema => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    HollowSchema IHollowTypeDataAccess.Schema => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public HollowTypeReadState TypeState => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public int Size(int ordinal) => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public IEnumerable<int> ElementOrdinals(int ordinal) => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public bool Contains(int ordinal, int value) => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public bool Contains(int ordinal, int value, int hashCode) => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public int RelativeBucketValue(int ordinal, int bucketIndex) => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public IEnumerable<int> PotentialMatchElementOrdinals(int ordinal, int hashCode) =>
        throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public int FindElement(int ordinal, params object?[] hashKey) => throw new HollowDataAccessDisabledException();
}

/// <summary>
/// A map type whose data has been dropped.
/// </summary>
public sealed class HollowMapDisabledDataAccess : IHollowMapTypeDataAccess
{
    private HollowMapDisabledDataAccess()
    {
    }

    /// <summary>The single instance; the type carries no state.</summary>
    public static HollowMapDisabledDataAccess Instance { get; } = new();

    /// <inheritdoc />
    public IHollowDataAccess DataAccess => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public HollowMapSchema Schema => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    HollowSchema IHollowTypeDataAccess.Schema => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public HollowTypeReadState TypeState => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public int Size(int ordinal) => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public int Get(int ordinal, int keyOrdinal) => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public int Get(int ordinal, int keyOrdinal, int hashCode) => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public long RelativeBucket(int ordinal, int bucketIndex) => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public IEnumerable<HollowMapEntry> PotentialMatchEntries(int ordinal, int hashCode) =>
        throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public IEnumerable<HollowMapEntry> Entries(int ordinal) =>
        throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public int FindKey(int ordinal, params object?[] hashKey) => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public int FindValue(int ordinal, params object?[] hashKey) => throw new HollowDataAccessDisabledException();

    /// <inheritdoc />
    public long FindEntry(int ordinal, params object?[] hashKey) => throw new HollowDataAccessDisabledException();
}
