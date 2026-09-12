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
using Hollow.Core.Schema;
using Hollow.Core.Util;

namespace Hollow.Core.Tools.Diff.Exact;

/// <summary>
/// Matching map records by the identities of their entries.
/// </summary>
/// <remarks>
/// A map has no order either, so both sides' keys and values are sorted before being compared. They
/// are sorted <em>separately</em>, which is how Java does it — so two maps holding the same keys and
/// the same values, paired up differently, compare equal here. That is a real hole, and one the diff
/// then closes by walking the pair anyway when the hash collides but the contents do not agree.
/// </remarks>
public sealed class DiffEqualityMapMapper : DiffEqualityTypeMapper
{
    private readonly DiffEqualOrdinalMap _keyEqualOrdinalMap;
    private readonly DiffEqualOrdinalMap _valueEqualOrdinalMap;

    private readonly IntList _fromKeys = new();
    private readonly IntList _fromValues = new();
    private readonly IntList _toKeys = new();
    private readonly IntList _toValues = new();

    /// <summary>Builds a mapper over a map type.</summary>
    public DiffEqualityMapMapper(
        DiffEqualityMapping mapping, HollowTypeReadState fromState, HollowTypeReadState toState, bool oneToOne)
        : base(fromState, toState, oneToOne)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        HollowMapSchema schema = ((IHollowMapTypeDataAccess)fromState).Schema;

        _keyEqualOrdinalMap = mapping.GetEqualOrdinalMap(schema.KeyType);
        _valueEqualOrdinalMap = mapping.GetEqualOrdinalMap(schema.ValueType);

        RequiresTraversalForMissingFields =
            mapping.RequiresMissingFieldTraversal(schema.KeyType)
            || mapping.RequiresMissingFieldTraversal(schema.ValueType);
    }

    /// <inheritdoc />
    public override bool RequiresTraversalForMissingFields { get; }

    private IHollowMapTypeDataAccess From => (IHollowMapTypeDataAccess)FromState;

    private IHollowMapTypeDataAccess To => (IHollowMapTypeDataAccess)ToState;

    /// <inheritdoc />
    protected override int FromRecordHashCode(int ordinal) =>
        RecordHashCode(
            From.OrdinalIterator(ordinal),
            _keyEqualOrdinalMap.FromOrdinalIdentityTranslator,
            _valueEqualOrdinalMap.FromOrdinalIdentityTranslator);

    /// <inheritdoc />
    protected override int ToRecordHashCode(int ordinal) =>
        RecordHashCode(
            To.OrdinalIterator(ordinal),
            _keyEqualOrdinalMap.ToOrdinalIdentityTranslator,
            _valueEqualOrdinalMap.ToOrdinalIdentityTranslator);

    /// <inheritdoc />
    protected override bool RecordsAreEqual(int fromOrdinal, int toOrdinal)
    {
        if (!PopulateEntries(
            _fromKeys,
            _fromValues,
            From.OrdinalIterator(fromOrdinal),
            _keyEqualOrdinalMap.FromOrdinalIdentityTranslator,
            _valueEqualOrdinalMap.FromOrdinalIdentityTranslator))
        {
            return false;
        }

        if (!PopulateEntries(
            _toKeys,
            _toValues,
            To.OrdinalIterator(toOrdinal),
            _keyEqualOrdinalMap.ToOrdinalIdentityTranslator,
            _valueEqualOrdinalMap.ToOrdinalIdentityTranslator))
        {
            return false;
        }

        return _fromKeys.ValuesEqual(_toKeys) && _fromValues.ValuesEqual(_toValues);
    }

    private static int RecordHashCode(
        IHollowMapEntryOrdinalIterator iterator,
        Func<int, int> keyTranslator,
        Func<int, int> valueTranslator)
    {
        int hashCode = 0;

        while (iterator.Next())
        {
            int keyIdentity = keyTranslator(iterator.Key);
            int valueIdentity = valueTranslator(iterator.Value);

            if (keyIdentity == HollowConstants.OrdinalNone && iterator.Key != HollowConstants.OrdinalNone)
            {
                return -1;
            }

            if (valueIdentity == HollowConstants.OrdinalNone && iterator.Value != HollowConstants.OrdinalNone)
            {
                return -1;
            }

            hashCode ^= HashCodes.HashInt(keyIdentity + (31 * valueIdentity));
        }

        return hashCode;
    }

    private static bool PopulateEntries(
        IntList keys,
        IntList values,
        IHollowMapEntryOrdinalIterator iterator,
        Func<int, int> keyTranslator,
        Func<int, int> valueTranslator)
    {
        keys.Clear();
        values.Clear();

        while (iterator.Next())
        {
            int keyIdentity = keyTranslator(iterator.Key);
            int valueIdentity = valueTranslator(iterator.Value);

            if (keyIdentity == HollowConstants.OrdinalNone && iterator.Key != HollowConstants.OrdinalNone)
            {
                return false;
            }

            if (valueIdentity == HollowConstants.OrdinalNone && iterator.Value != HollowConstants.OrdinalNone)
            {
                return false;
            }

            keys.Add(keyIdentity);
            values.Add(valueIdentity);
        }

        keys.Sort();
        values.Sort();

        return true;
    }
}
