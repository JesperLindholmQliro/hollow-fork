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
using Hollow.Core.Read.DataAccess.Disabled;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Iterator;
using Hollow.Core.Schema;

using Hollow.Api.Sampling;

namespace Hollow.Core.Read.DataAccess.Proxy;

/// <summary>
/// One type of a <see cref="HollowProxyDataAccess"/>, forwarding every read to another type access.
/// </summary>
/// <remarks>
/// Named <c>HollowTypeProxyDataAccess</c> in Java; the <c>I</c>-less name is kept because this is a
/// base class rather than an interface.
/// </remarks>
public abstract class HollowTypeProxyDataAccess : IHollowTypeDataAccess
{
    private IHollowTypeDataAccess _currentDataAccess;

    /// <summary>Forwards this type's reads through <paramref name="dataAccess"/>.</summary>
    protected HollowTypeProxyDataAccess(HollowProxyDataAccess dataAccess, IHollowTypeDataAccess disabled)
    {
        ArgumentNullException.ThrowIfNull(dataAccess);
        ArgumentNullException.ThrowIfNull(disabled);

        Proxy = dataAccess;
        DisabledDataAccess = disabled;
        _currentDataAccess = disabled;
    }

    /// <inheritdoc />
    public IHollowDataAccess DataAccess => Proxy;

    /// <inheritdoc />
    public HollowSchema Schema => CurrentDataAccess.Schema;

    /// <inheritdoc />
    public HollowTypeReadState TypeState => CurrentDataAccess.TypeState;

    /// <summary>What this type is pointed at now.</summary>
    public IHollowTypeDataAccess CurrentDataAccess => _currentDataAccess;

    /// <inheritdoc />
    /// <remarks>
    /// The proxy has no counters of its own; reads through it land on whichever state it currently
    /// points at, so that is whose sampler answers.
    /// </remarks>
    public IHollowSampler Sampler => CurrentDataAccess.Sampler;

    /// <summary>What to point this type at when the data is dropped.</summary>
    internal IHollowTypeDataAccess DisabledDataAccess { get; }

    /// <summary>The whole-dataset proxy this type belongs to.</summary>
    private protected HollowProxyDataAccess Proxy { get; }

    /// <summary>Forwards this type's reads to <paramref name="typeDataAccess"/> from now on.</summary>
    public void SetCurrentDataAccess(IHollowTypeDataAccess typeDataAccess)
    {
        ArgumentNullException.ThrowIfNull(typeDataAccess);

        _currentDataAccess = typeDataAccess;
    }

    /// <summary>
    /// The current access, having noted that a record was read.
    /// </summary>
    /// <remarks>
    /// Every read goes through here rather than through <see cref="CurrentDataAccess"/>, so that the
    /// stale reference detector can tell a reference that is held from one that is used.
    /// </remarks>
    private protected IHollowTypeDataAccess Read()
    {
        Proxy.NoteRead();

        return _currentDataAccess;
    }
}

/// <summary>
/// An object type of a <see cref="HollowProxyDataAccess"/>.
/// </summary>
public sealed class HollowObjectProxyDataAccess(HollowProxyDataAccess dataAccess)
    : HollowTypeProxyDataAccess(dataAccess, HollowObjectDisabledDataAccess.Instance), IHollowObjectTypeDataAccess
{
    /// <inheritdoc />
    public new HollowObjectSchema Schema => Current.Schema;

    private IHollowObjectTypeDataAccess Current => (IHollowObjectTypeDataAccess)CurrentDataAccess;

    private IHollowObjectTypeDataAccess Reading => (IHollowObjectTypeDataAccess)Read();

    /// <inheritdoc />
    public bool IsNull(int ordinal, int fieldIndex) => Reading.IsNull(ordinal, fieldIndex);

    /// <inheritdoc />
    public int ReadOrdinal(int ordinal, int fieldIndex) => Reading.ReadOrdinal(ordinal, fieldIndex);

    /// <inheritdoc />
    public int ReadInt(int ordinal, int fieldIndex) => Reading.ReadInt(ordinal, fieldIndex);

    /// <inheritdoc />
    public float ReadFloat(int ordinal, int fieldIndex) => Reading.ReadFloat(ordinal, fieldIndex);

    /// <inheritdoc />
    public double ReadDouble(int ordinal, int fieldIndex) => Reading.ReadDouble(ordinal, fieldIndex);

    /// <inheritdoc />
    public long ReadLong(int ordinal, int fieldIndex) => Reading.ReadLong(ordinal, fieldIndex);

    /// <inheritdoc />
    public bool? ReadBoolean(int ordinal, int fieldIndex) => Reading.ReadBoolean(ordinal, fieldIndex);

    /// <inheritdoc />
    public byte[]? ReadBytes(int ordinal, int fieldIndex) => Reading.ReadBytes(ordinal, fieldIndex);

    /// <inheritdoc />
    public decimal? ReadDecimal(int ordinal, int fieldIndex) => Reading.ReadDecimal(ordinal, fieldIndex);

    /// <inheritdoc />
    public string? ReadString(int ordinal, int fieldIndex) => Reading.ReadString(ordinal, fieldIndex);

    /// <inheritdoc />
    public bool IsStringFieldEqual(int ordinal, int fieldIndex, string? testValue) =>
        Reading.IsStringFieldEqual(ordinal, fieldIndex, testValue);

    /// <inheritdoc />
    public int FindVarLengthFieldHashCode(int ordinal, int fieldIndex) =>
        Reading.FindVarLengthFieldHashCode(ordinal, fieldIndex);

    /// <inheritdoc />
    public int VarLengthFieldByteLength(int ordinal, int fieldIndex) =>
        Reading.VarLengthFieldByteLength(ordinal, fieldIndex);

    /// <inheritdoc />
    public int ReadStringInto(int ordinal, int fieldIndex, Span<char> destination) =>
        Reading.ReadStringInto(ordinal, fieldIndex, destination);

    /// <inheritdoc />
    public int ReadBytesInto(int ordinal, int fieldIndex, Span<byte> destination) =>
        Reading.ReadBytesInto(ordinal, fieldIndex, destination);

    /// <inheritdoc />
    public bool TryGetBytesSpan(int ordinal, int fieldIndex, out ReadOnlySpan<byte> value) =>
        Reading.TryGetBytesSpan(ordinal, fieldIndex, out value);

    /// <inheritdoc />
    public ReadOnlySequence<byte> GetVarLengthSequence(int ordinal, int fieldIndex) =>
        Reading.GetVarLengthSequence(ordinal, fieldIndex);
}

/// <summary>
/// A list type of a <see cref="HollowProxyDataAccess"/>.
/// </summary>
public sealed class HollowListProxyDataAccess(HollowProxyDataAccess dataAccess)
    : HollowTypeProxyDataAccess(dataAccess, HollowListDisabledDataAccess.Instance), IHollowListTypeDataAccess
{
    /// <inheritdoc />
    public new HollowListSchema Schema => Current.Schema;

    /// <inheritdoc />
    HollowCollectionSchema IHollowCollectionTypeDataAccess.Schema => Current.Schema;

    private IHollowListTypeDataAccess Current => (IHollowListTypeDataAccess)CurrentDataAccess;

    private IHollowListTypeDataAccess Reading => (IHollowListTypeDataAccess)Read();

    /// <inheritdoc />
    public int Size(int ordinal) => Reading.Size(ordinal);

    /// <inheritdoc />
    public IEnumerable<int> ElementOrdinals(int ordinal) => Reading.ElementOrdinals(ordinal);

    /// <inheritdoc />
    public int GetElementOrdinal(int ordinal, int listIndex) => Reading.GetElementOrdinal(ordinal, listIndex);
}

/// <summary>
/// A set type of a <see cref="HollowProxyDataAccess"/>.
/// </summary>
public sealed class HollowSetProxyDataAccess(HollowProxyDataAccess dataAccess)
    : HollowTypeProxyDataAccess(dataAccess, HollowSetDisabledDataAccess.Instance), IHollowSetTypeDataAccess
{
    /// <inheritdoc />
    public new HollowSetSchema Schema => Current.Schema;

    /// <inheritdoc />
    HollowCollectionSchema IHollowCollectionTypeDataAccess.Schema => Current.Schema;

    private IHollowSetTypeDataAccess Current => (IHollowSetTypeDataAccess)CurrentDataAccess;

    private IHollowSetTypeDataAccess Reading => (IHollowSetTypeDataAccess)Read();

    /// <inheritdoc />
    public int Size(int ordinal) => Reading.Size(ordinal);

    /// <inheritdoc />
    public IEnumerable<int> ElementOrdinals(int ordinal) => Reading.ElementOrdinals(ordinal);

    /// <inheritdoc />
    public bool Contains(int ordinal, int value) => Reading.Contains(ordinal, value);

    /// <inheritdoc />
    public bool Contains(int ordinal, int value, int hashCode) => Reading.Contains(ordinal, value, hashCode);

    /// <inheritdoc />
    public int RelativeBucketValue(int ordinal, int bucketIndex) => Reading.RelativeBucketValue(ordinal, bucketIndex);

    /// <inheritdoc />
    public IEnumerable<int> PotentialMatchElementOrdinals(int ordinal, int hashCode) =>
        Reading.PotentialMatchElementOrdinals(ordinal, hashCode);

    /// <inheritdoc />
    public int FindElement(int ordinal, params object?[] hashKey) => Reading.FindElement(ordinal, hashKey);
}

/// <summary>
/// A map type of a <see cref="HollowProxyDataAccess"/>.
/// </summary>
public sealed class HollowMapProxyDataAccess(HollowProxyDataAccess dataAccess)
    : HollowTypeProxyDataAccess(dataAccess, HollowMapDisabledDataAccess.Instance), IHollowMapTypeDataAccess
{
    /// <inheritdoc />
    public new HollowMapSchema Schema => Current.Schema;

    private IHollowMapTypeDataAccess Current => (IHollowMapTypeDataAccess)CurrentDataAccess;

    private IHollowMapTypeDataAccess Reading => (IHollowMapTypeDataAccess)Read();

    /// <inheritdoc />
    public int Size(int ordinal) => Reading.Size(ordinal);

    /// <inheritdoc />
    public int Get(int ordinal, int keyOrdinal) => Reading.Get(ordinal, keyOrdinal);

    /// <inheritdoc />
    public int Get(int ordinal, int keyOrdinal, int hashCode) => Reading.Get(ordinal, keyOrdinal, hashCode);

    /// <inheritdoc />
    public long RelativeBucket(int ordinal, int bucketIndex) => Reading.RelativeBucket(ordinal, bucketIndex);

    /// <inheritdoc />
    public IEnumerable<HollowMapEntry> PotentialMatchEntries(int ordinal, int hashCode) =>
        Reading.PotentialMatchEntries(ordinal, hashCode);

    /// <inheritdoc />
    public IEnumerable<HollowMapEntry> Entries(int ordinal) => Reading.Entries(ordinal);

    /// <inheritdoc />
    public int FindKey(int ordinal, params object?[] hashKey) => Reading.FindKey(ordinal, hashKey);

    /// <inheritdoc />
    public int FindValue(int ordinal, params object?[] hashKey) => Reading.FindValue(ordinal, hashKey);

    /// <inheritdoc />
    public long FindEntry(int ordinal, params object?[] hashKey) => Reading.FindEntry(ordinal, hashKey);
}
