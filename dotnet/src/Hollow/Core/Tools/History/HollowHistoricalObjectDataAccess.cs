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
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Schema;

namespace Hollow.Core.Tools.History;

/// <summary>
/// An object type, read as it stood in a state that has since gone.
/// </summary>
/// <remarks>
/// Every field read follows the same rule as the base class describes: read from the copy this state
/// kept, or hand the question forward to the state that still has the record.
/// </remarks>
public sealed class HollowHistoricalObjectDataAccess(
    HollowHistoricalStateDataAccess dataAccess, HollowTypeReadState removedRecords)
    : HollowHistoricalTypeDataAccess(dataAccess, removedRecords), IHollowObjectTypeDataAccess
{
    /// <inheritdoc />
    public new HollowObjectSchema Schema => (HollowObjectSchema)RemovedRecords.Schema;

    private HollowObjectTypeReadState Removed => (HollowObjectTypeReadState)RemovedRecords;

    /// <inheritdoc />
    public bool IsNull(int ordinal, int fieldIndex) =>
        OrdinalIsPresent(ordinal)
            ? Removed.IsNull(GetMappedOrdinal(ordinal), fieldIndex)
            : ForwardTo<IHollowObjectTypeDataAccess>(ordinal).IsNull(ordinal, fieldIndex);

    /// <inheritdoc />
    public int ReadOrdinal(int ordinal, int fieldIndex) =>
        OrdinalIsPresent(ordinal)
            ? Removed.ReadOrdinal(GetMappedOrdinal(ordinal), fieldIndex)
            : ForwardTo<IHollowObjectTypeDataAccess>(ordinal).ReadOrdinal(ordinal, fieldIndex);

    /// <inheritdoc />
    public int ReadInt(int ordinal, int fieldIndex) =>
        OrdinalIsPresent(ordinal)
            ? Removed.ReadInt(GetMappedOrdinal(ordinal), fieldIndex)
            : ForwardTo<IHollowObjectTypeDataAccess>(ordinal).ReadInt(ordinal, fieldIndex);

    /// <inheritdoc />
    public float ReadFloat(int ordinal, int fieldIndex) =>
        OrdinalIsPresent(ordinal)
            ? Removed.ReadFloat(GetMappedOrdinal(ordinal), fieldIndex)
            : ForwardTo<IHollowObjectTypeDataAccess>(ordinal).ReadFloat(ordinal, fieldIndex);

    /// <inheritdoc />
    public double ReadDouble(int ordinal, int fieldIndex) =>
        OrdinalIsPresent(ordinal)
            ? Removed.ReadDouble(GetMappedOrdinal(ordinal), fieldIndex)
            : ForwardTo<IHollowObjectTypeDataAccess>(ordinal).ReadDouble(ordinal, fieldIndex);

    /// <inheritdoc />
    public long ReadLong(int ordinal, int fieldIndex) =>
        OrdinalIsPresent(ordinal)
            ? Removed.ReadLong(GetMappedOrdinal(ordinal), fieldIndex)
            : ForwardTo<IHollowObjectTypeDataAccess>(ordinal).ReadLong(ordinal, fieldIndex);

    /// <inheritdoc />
    public bool? ReadBoolean(int ordinal, int fieldIndex) =>
        OrdinalIsPresent(ordinal)
            ? Removed.ReadBoolean(GetMappedOrdinal(ordinal), fieldIndex)
            : ForwardTo<IHollowObjectTypeDataAccess>(ordinal).ReadBoolean(ordinal, fieldIndex);

    /// <inheritdoc />
    public byte[]? ReadBytes(int ordinal, int fieldIndex) =>
        OrdinalIsPresent(ordinal)
            ? Removed.ReadBytes(GetMappedOrdinal(ordinal), fieldIndex)
            : ForwardTo<IHollowObjectTypeDataAccess>(ordinal).ReadBytes(ordinal, fieldIndex);

    /// <inheritdoc />
    /// <remarks>
    /// Java has no decimal field, so it has nothing here; this port does — see the format extension
    /// note in PORTING.md.
    /// </remarks>
    public decimal? ReadDecimal(int ordinal, int fieldIndex) =>
        OrdinalIsPresent(ordinal)
            ? Removed.ReadDecimal(GetMappedOrdinal(ordinal), fieldIndex)
            : ForwardTo<IHollowObjectTypeDataAccess>(ordinal).ReadDecimal(ordinal, fieldIndex);

    /// <inheritdoc />
    public string? ReadString(int ordinal, int fieldIndex) =>
        OrdinalIsPresent(ordinal)
            ? Removed.ReadString(GetMappedOrdinal(ordinal), fieldIndex)
            : ForwardTo<IHollowObjectTypeDataAccess>(ordinal).ReadString(ordinal, fieldIndex);

    /// <inheritdoc />
    public bool IsStringFieldEqual(int ordinal, int fieldIndex, string? testValue) =>
        OrdinalIsPresent(ordinal)
            ? Removed.IsStringFieldEqual(GetMappedOrdinal(ordinal), fieldIndex, testValue)
            : ForwardTo<IHollowObjectTypeDataAccess>(ordinal).IsStringFieldEqual(ordinal, fieldIndex, testValue);

    /// <inheritdoc />
    public int FindVarLengthFieldHashCode(int ordinal, int fieldIndex) =>
        OrdinalIsPresent(ordinal)
            ? Removed.FindVarLengthFieldHashCode(GetMappedOrdinal(ordinal), fieldIndex)
            : ForwardTo<IHollowObjectTypeDataAccess>(ordinal).FindVarLengthFieldHashCode(ordinal, fieldIndex);

    /// <inheritdoc />
    public int VarLengthFieldByteLength(int ordinal, int fieldIndex) =>
        OrdinalIsPresent(ordinal)
            ? Removed.VarLengthFieldByteLength(GetMappedOrdinal(ordinal), fieldIndex)
            : ForwardTo<IHollowObjectTypeDataAccess>(ordinal).VarLengthFieldByteLength(ordinal, fieldIndex);

    /// <inheritdoc />
    public int ReadStringInto(int ordinal, int fieldIndex, Span<char> destination) =>
        OrdinalIsPresent(ordinal)
            ? Removed.ReadStringInto(GetMappedOrdinal(ordinal), fieldIndex, destination)
            : ForwardTo<IHollowObjectTypeDataAccess>(ordinal).ReadStringInto(ordinal, fieldIndex, destination);

    /// <inheritdoc />
    public int ReadBytesInto(int ordinal, int fieldIndex, Span<byte> destination) =>
        OrdinalIsPresent(ordinal)
            ? Removed.ReadBytesInto(GetMappedOrdinal(ordinal), fieldIndex, destination)
            : ForwardTo<IHollowObjectTypeDataAccess>(ordinal).ReadBytesInto(ordinal, fieldIndex, destination);

    /// <inheritdoc />
    public bool TryGetBytesSpan(int ordinal, int fieldIndex, out ReadOnlySpan<byte> value) =>
        OrdinalIsPresent(ordinal)
            ? Removed.TryGetBytesSpan(GetMappedOrdinal(ordinal), fieldIndex, out value)
            : ForwardTo<IHollowObjectTypeDataAccess>(ordinal).TryGetBytesSpan(ordinal, fieldIndex, out value);

    /// <inheritdoc />
    public ReadOnlySequence<byte> GetVarLengthSequence(int ordinal, int fieldIndex) =>
        OrdinalIsPresent(ordinal)
            ? Removed.GetVarLengthSequence(GetMappedOrdinal(ordinal), fieldIndex)
            : ForwardTo<IHollowObjectTypeDataAccess>(ordinal).GetVarLengthSequence(ordinal, fieldIndex);
}
