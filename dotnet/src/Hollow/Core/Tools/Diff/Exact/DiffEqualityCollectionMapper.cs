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

using Hollow.Core.Memory.Encoding;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Iterator;
using Hollow.Core.Util;

namespace Hollow.Core.Tools.Diff.Exact;

/// <summary>
/// Matching collection records by the identities of their elements.
/// </summary>
/// <remarks>
/// A set has no order of its own, so both sides' element identities are sorted before being compared.
/// <see cref="DiffEqualityOrderedListMapper"/> is the same thing for a list, where they are not.
/// </remarks>
public class DiffEqualityCollectionMapper : DiffEqualityTypeMapper
{
    private readonly DiffEqualOrdinalMap _elementEqualOrdinalMap;
    private readonly bool _orderingIsImportant;

    private readonly IntList _fromElements = new();
    private readonly IntList _toElements = new();

    /// <summary>Builds a mapper over a list or set type.</summary>
    public DiffEqualityCollectionMapper(
        DiffEqualityMapping mapping,
        HollowTypeReadState fromState,
        HollowTypeReadState toState,
        bool oneToOne,
        bool orderingIsImportant = false)
        : base(fromState, toState, oneToOne)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        string elementType = ((IHollowCollectionTypeDataAccess)fromState).Schema.ElementType;

        _elementEqualOrdinalMap = mapping.GetEqualOrdinalMap(elementType);
        RequiresTraversalForMissingFields = mapping.RequiresMissingFieldTraversal(elementType);
        _orderingIsImportant = orderingIsImportant;
    }

    /// <inheritdoc />
    public override bool RequiresTraversalForMissingFields { get; }

    private IHollowCollectionTypeDataAccess From => (IHollowCollectionTypeDataAccess)FromState;

    private IHollowCollectionTypeDataAccess To => (IHollowCollectionTypeDataAccess)ToState;

    /// <inheritdoc />
    protected override int FromRecordHashCode(int ordinal) =>
        RecordHashCode(From, ordinal, _elementEqualOrdinalMap.FromOrdinalIdentityTranslator);

    /// <inheritdoc />
    protected override int ToRecordHashCode(int ordinal) =>
        RecordHashCode(To, ordinal, _elementEqualOrdinalMap.ToOrdinalIdentityTranslator);

    /// <inheritdoc />
    protected override bool RecordsAreEqual(int fromOrdinal, int toOrdinal)
    {
        if (!PopulateElements(
            _fromElements, From.OrdinalIterator(fromOrdinal), _elementEqualOrdinalMap.FromOrdinalIdentityTranslator))
        {
            return false;
        }

        if (!PopulateElements(
            _toElements, To.OrdinalIterator(toOrdinal), _elementEqualOrdinalMap.ToOrdinalIdentityTranslator))
        {
            return false;
        }

        return _fromElements.ValuesEqual(_toElements);
    }

    /// <summary>
    /// A hash of the record's elements, by their identities rather than their ordinals.
    /// </summary>
    /// <remarks>
    /// Combined with exclusive-or, so that the same elements in a different order hash the same — which
    /// is what a set requires. The zero check keeps a record whose elements happen to cancel out from
    /// hashing to the same bucket as an empty one.
    /// </remarks>
    protected virtual int RecordHashCode(
        IHollowCollectionTypeDataAccess typeState, int ordinal, Func<int, int> identityTranslator)
    {
        ArgumentNullException.ThrowIfNull(typeState);
        ArgumentNullException.ThrowIfNull(identityTranslator);

        IHollowOrdinalIterator iterator = typeState.OrdinalIterator(ordinal);
        int hashCode = 0;

        for (int elementOrdinal = iterator.Next();
            elementOrdinal != IHollowOrdinalIterator.NoMoreOrdinals;
            elementOrdinal = iterator.Next())
        {
            int identity = identityTranslator(elementOrdinal);

            if (identity == HollowConstants.OrdinalNone && elementOrdinal != HollowConstants.OrdinalNone)
            {
                return -1;
            }

            hashCode ^= HashCodes.HashInt(identity);

            if (hashCode == 0)
            {
                hashCode ^= HashCodes.HashInt(identity);
            }
        }

        return hashCode;
    }

    private bool PopulateElements(
        IntList elements, IHollowOrdinalIterator iterator, Func<int, int> identityTranslator)
    {
        elements.Clear();

        for (int elementOrdinal = iterator.Next();
            elementOrdinal != IHollowOrdinalIterator.NoMoreOrdinals;
            elementOrdinal = iterator.Next())
        {
            int identity = identityTranslator(elementOrdinal);

            if (identity == HollowConstants.OrdinalNone && elementOrdinal != HollowConstants.OrdinalNone)
            {
                return false;
            }

            elements.Add(identity);
        }

        if (!_orderingIsImportant)
        {
            elements.Sort();
        }

        return true;
    }
}

/// <summary>
/// Matching list records, where the order of the elements is part of the value.
/// </summary>
public sealed class DiffEqualityOrderedListMapper(
    DiffEqualityMapping mapping, HollowTypeReadState fromState, HollowTypeReadState toState, bool oneToOne)
    : DiffEqualityCollectionMapper(mapping, fromState, toState, oneToOne, orderingIsImportant: true)
{
    /// <summary>
    /// A hash of the record's elements that depends on their order, unlike the set's.
    /// </summary>
    protected override int RecordHashCode(
        IHollowCollectionTypeDataAccess typeState, int ordinal, Func<int, int> identityTranslator)
    {
        ArgumentNullException.ThrowIfNull(typeState);
        ArgumentNullException.ThrowIfNull(identityTranslator);

        IHollowOrdinalIterator iterator = typeState.OrdinalIterator(ordinal);
        int hashCode = 0;

        for (int elementOrdinal = iterator.Next();
            elementOrdinal != IHollowOrdinalIterator.NoMoreOrdinals;
            elementOrdinal = iterator.Next())
        {
            int identity = identityTranslator(elementOrdinal);

            if (identity == HollowConstants.OrdinalNone && elementOrdinal != HollowConstants.OrdinalNone)
            {
                return -1;
            }

            hashCode = (7919 * hashCode) + identity;
        }

        return HashCodes.HashInt(hashCode);
    }
}
