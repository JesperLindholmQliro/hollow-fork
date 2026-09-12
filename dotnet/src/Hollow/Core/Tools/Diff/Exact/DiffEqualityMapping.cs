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
using Hollow.Core.Read.Engine.List;
using Hollow.Core.Read.Engine.Object;

namespace Hollow.Core.Tools.Diff.Exact;

/// <summary>
/// Which records are exactly equal between two states, per type.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes a diff over a large dataset finish. Most of a dataset does not change between
/// two states, and a pair of records that are identical can be skipped along with everything below
/// them — so the expensive walk is only done where something actually differs.
/// </para>
/// <para>
/// Maps are built lazily and leaf-first: asking for a type builds the types it references first,
/// because whether two records are equal is decided by the identities of what they point at rather
/// than by the ordinals, which mean nothing across two states.
/// </para>
/// </remarks>
/// <param name="fromState">The earlier state.</param>
/// <param name="toState">The later one.</param>
/// <param name="oneToOne">
/// Whether a record may be matched only once. The diff wants every match, so that a record equal to
/// three others is known to be equal to all of them; a caller pairing records up for display wants
/// each used once.
/// </param>
/// <param name="listOrderingIsImportant">
/// Whether two lists holding the same elements in a different order are the same list.
/// </param>
public sealed class DiffEqualityMapping(
    HollowReadStateEngine fromState,
    HollowReadStateEngine toState,
    bool oneToOne = false,
    bool listOrderingIsImportant = true)
{
    private readonly Dictionary<string, DiffEqualOrdinalMap> _maps = new(StringComparer.Ordinal);

    private readonly HashSet<string> _typesWhichRequireMissingFieldTraversal = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether a record of <paramref name="type"/> can differ by a field one of the schemas does not
    /// have, so that the equality map alone cannot settle it.
    /// </summary>
    public bool RequiresMissingFieldTraversal(string type) =>
        _typesWhichRequireMissingFieldTraversal.Contains(type);

    /// <summary>
    /// The equality map for <paramref name="type"/>, building it — and the types it references —
    /// if it has not been built.
    /// </summary>
    /// <remarks>
    /// Once <see cref="MarkPrepared"/> has been called a type that was never asked for returns the
    /// empty map rather than being built, so that a traversal that wanders outside the types being
    /// diffed does not start a long calculation on the spot.
    /// </remarks>
    public DiffEqualOrdinalMap GetEqualOrdinalMap(string type)
    {
        if (_maps.TryGetValue(type, out DiffEqualOrdinalMap? existing))
        {
            return existing;
        }

        return IsPrepared ? DiffEqualOrdinalMap.Empty : BuildMap(type);
    }

    /// <summary>
    /// Says that every type the diff cares about has had its map built.
    /// </summary>
    public void MarkPrepared() => IsPrepared = true;

    /// <summary>Whether <see cref="MarkPrepared"/> has been called.</summary>
    public bool IsPrepared { get; private set; }

    private DiffEqualOrdinalMap BuildMap(string type)
    {
        HollowTypeReadState? fromTypeState = fromState.GetTypeState(type);
        HollowTypeReadState? toTypeState = toState.GetTypeState(type);

        // A type only one state has can have no equal pairs, and nothing below it is worth building.
        if (fromTypeState is null || toTypeState is null)
        {
            return DiffEqualOrdinalMap.Empty;
        }

        // Recorded before the map is built, so that a type referencing itself asks for the empty map
        // rather than recursing until the stack runs out.
        _maps[type] = DiffEqualOrdinalMap.Empty;

        DiffEqualityTypeMapper mapper = CreateTypeMapper(fromTypeState, toTypeState);
        DiffEqualOrdinalMap equalOrdinalMap = mapper.MapEqualObjects();

        if (mapper.RequiresTraversalForMissingFields)
        {
            _typesWhichRequireMissingFieldTraversal.Add(type);
        }

        equalOrdinalMap.BuildToOrdinalIdentityMapping();

        _maps[type] = equalOrdinalMap;

        return equalOrdinalMap;
    }

    private DiffEqualityTypeMapper CreateTypeMapper(
        HollowTypeReadState fromTypeState, HollowTypeReadState toTypeState) =>
        fromTypeState switch
        {
            HollowObjectTypeReadState =>
                new DiffEqualityObjectMapper(
                    this,
                    (HollowObjectTypeReadState)fromTypeState,
                    (HollowObjectTypeReadState)toTypeState,
                    oneToOne),

            HollowListTypeReadState when listOrderingIsImportant =>
                new DiffEqualityOrderedListMapper(this, fromTypeState, toTypeState, oneToOne),

            IHollowCollectionTypeDataAccess =>
                new DiffEqualityCollectionMapper(this, fromTypeState, toTypeState, oneToOne),

            IHollowMapTypeDataAccess =>
                new DiffEqualityMapMapper(this, fromTypeState, toTypeState, oneToOne),

            _ => throw new ArgumentException(
                $"{fromTypeState.TypeName} is a {fromTypeState.GetType().Name}, which is not a kind of "
                + "record this knows how to match",
                nameof(fromTypeState)),
        };
}
