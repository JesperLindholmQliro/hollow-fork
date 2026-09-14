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

using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.Engine;
using Hollow.Core.Schema;
using Hollow.Core.Util;

namespace Hollow.Core.Tools.History;

/// <summary>
/// One type, read as it stood in a state that has since gone.
/// </summary>
/// <remarks>
/// <para>
/// A historical state does not hold the whole dataset. It holds only the records that the next
/// transition removed, copied aside before they were dropped; everything else is still in some later
/// state, ultimately the live one. So every read here asks one question first — <em>is this ordinal
/// one of the ones I kept?</em> — and either reads from the copy or hands the question forward along
/// the chain of states.
/// </para>
/// <para>
/// That is what makes a history over hundreds of states affordable: a record is stored once, in the
/// state that last had it, and every earlier state that also had it just points forward.
/// </para>
/// <para>
/// Java's class carries a <c>HollowSampler</c> and a stack-trace recorder through every method. This
/// port has neither — <c>api.sampling</c> is deliberately not ported, see PORTING.md — so what is left
/// is the redirection itself.
/// </para>
/// </remarks>
public abstract class HollowHistoricalTypeDataAccess : IHollowTypeDataAccess
{
    private readonly IntMap? _ordinalRemap;

    /// <summary>
    /// Prepares access to <paramref name="removedRecords"/> within <paramref name="dataAccess"/>.
    /// </summary>
    protected HollowHistoricalTypeDataAccess(
        HollowHistoricalStateDataAccess dataAccess, HollowTypeReadState removedRecords)
    {
        ArgumentNullException.ThrowIfNull(dataAccess);
        ArgumentNullException.ThrowIfNull(removedRecords);

        HistoricalDataAccess = dataAccess;
        RemovedRecords = removedRecords;

        // Only an explicit table tells this state which ordinals it kept and where it put them. The
        // equality-driven remapper has no such table, and then every ordinal is taken as present.
        _ordinalRemap = dataAccess.OrdinalMapping is IntMapOrdinalRemapper remapper
            ? remapper.GetOrdinalRemapping(removedRecords.Schema.Name)
            : null;
    }

    /// <summary>The state this type belongs to.</summary>
    public HollowHistoricalStateDataAccess HistoricalDataAccess { get; }

    /// <summary>The records this state kept, as a read state of their own.</summary>
    public HollowTypeReadState RemovedRecords { get; }

    /// <inheritdoc />
    public IHollowDataAccess DataAccess => HistoricalDataAccess;

    /// <inheritdoc />
    public HollowSchema Schema => RemovedRecords.Schema;

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">
    /// Always: a historical state is assembled from copies, and has no read state of its own to
    /// hand out.
    /// </exception>
    public HollowTypeReadState TypeState =>
        throw new NotSupportedException("a historical state has no type read state of its own");

    /// <summary>Where this state's records were copied to, or <see langword="null"/> for identity.</summary>
    internal IntMap? OrdinalRemap => _ordinalRemap;

    /// <summary>Whether <paramref name="ordinal"/> names a record this state kept.</summary>
    protected bool OrdinalIsPresent(int ordinal) => _ordinalRemap is null || _ordinalRemap.Get(ordinal) != -1;

    /// <summary>Where <paramref name="ordinal"/> sits in the copy this state kept.</summary>
    protected int GetMappedOrdinal(int ordinal) => _ordinalRemap is null ? ordinal : _ordinalRemap.Get(ordinal);

    /// <summary>
    /// The type as some later state holds it, for an ordinal this state did not keep.
    /// </summary>
    /// <remarks>
    /// This is the forwarding step. It walks the chain until it reaches a state that kept the record,
    /// or the live one, which has everything still current.
    /// </remarks>
    protected T ForwardTo<T>(int ordinal)
        where T : class, IHollowTypeDataAccess =>
        HistoricalDataAccess.GetTypeDataAccess(Schema.Name, ordinal) as T
        ?? throw new InvalidOperationException(
            $"no later state holds ordinal {ordinal} of {Schema.Name}");
}
