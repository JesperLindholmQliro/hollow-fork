/*
 *  Copyright 2021 Netflix, Inc.
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

using System.Globalization;
using Hollow.Api.Custom;
using Hollow.Core;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Schema;

namespace Hollow.Api.PerfApi;

/// <summary>
/// A generated API that hands out references rather than objects.
/// </summary>
/// <remarks>
/// <para>
/// The ordinary generated API (see <c>The typed API layer</c> in <c>PORTING.md</c>) gives a caller a
/// wrapper object per record it touches. That is the right default, and for a consumer walking a few
/// thousand records the allocations are beneath notice. For one walking millions of them on every
/// transition they are not, and this is the answer: every read hands back a <see cref="HollowRef"/>,
/// which is 64 bits of nothing, and a wrapper is built only where a caller asks for one.
/// </para>
/// <para>
/// Netflix Hollow generates the per-type subclasses from a data model with
/// <c>api.codegen.perfapi</c>, which this port does not have — see the status section of
/// <c>PORTING.md</c>. What is here is the runtime those generated classes sit on, which is also
/// perfectly usable by hand: subclass <see cref="HollowObjectTypePerfApi"/> for each object type, and
/// use the collection type APIs as they are.
/// </para>
/// </remarks>
public class HollowPerformanceApi : HollowApi
{
    /// <summary>Initialises an API over <paramref name="dataAccess"/>.</summary>
    public HollowPerformanceApi(IHollowDataAccess dataAccess)
        : base(dataAccess) =>
        TypeIdentifiers = new PerfApiTypeIdentifiers(dataAccess);

    /// <summary>The numbering this API's references are in terms of.</summary>
    public PerfApiTypeIdentifiers TypeIdentifiers { get; }
}

/// <summary>
/// Numbers a dataset's types, so that a reference can carry which type it points into.
/// </summary>
/// <remarks>
/// The numbering is the dataset's schema order, which means it is meaningful only against the dataset
/// it was built from. That is fine for what it is for — the numbers live in references that never
/// outlive the API — and is why a reference must not be stored or sent anywhere.
/// </remarks>
public sealed class PerfApiTypeIdentifiers
{
    private readonly string[] _typeNames;
    private readonly Dictionary<string, int> _identifiersByName;

    /// <summary>Numbers the types of <paramref name="dataset"/>, in schema order.</summary>
    public PerfApiTypeIdentifiers(IHollowDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        IReadOnlyList<HollowSchema> schemas = dataset.Schemas;

        _typeNames = new string[schemas.Count];
        _identifiersByName = new Dictionary<string, int>(schemas.Count, StringComparer.Ordinal);

        for (int i = 0; i < schemas.Count; i++)
        {
            _typeNames[i] = schemas[i].Name;
            _identifiersByName[_typeNames[i]] = i;
        }
    }

    /// <summary>How many types are numbered.</summary>
    public int Count => _typeNames.Length;

    /// <summary>
    /// The identifier of <paramref name="typeName"/>, or <see cref="HollowRef.TypeAbsent"/> where the
    /// dataset has no such type.
    /// </summary>
    public int GetIdentifier(string typeName) =>
        _identifiersByName.GetValueOrDefault(typeName, HollowRef.TypeAbsent);

    /// <summary>
    /// The name of the type <paramref name="identifier"/> numbers.
    /// </summary>
    /// <remarks>
    /// Answers with a description rather than throwing for an identifier it does not know, because
    /// every caller is building a message about something that has already gone wrong.
    /// </remarks>
    public string GetTypeName(int identifier) =>
        identifier >= 0 && identifier < _typeNames.Length
            ? _typeNames[identifier]
            : string.Create(CultureInfo.InvariantCulture, $"INVALID ({identifier})");
}

/// <summary>One type's reads, through references.</summary>
public abstract class HollowTypePerfApi
{
    /// <summary>The type half of every reference this API produces.</summary>
    private protected readonly long MaskedTypeIdentifier;

    /// <summary>Initialises the API for <paramref name="typeName"/>.</summary>
    protected HollowTypePerfApi(string typeName, HollowPerformanceApi api)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        ArgumentNullException.ThrowIfNull(api);

        Api = api;
        MaskedTypeIdentifier = HollowRef.ToTypeMasked(api.TypeIdentifiers.GetIdentifier(typeName));
        TypeName = typeName;
    }

    /// <summary>The API this type belongs to.</summary>
    public HollowPerformanceApi Api { get; }

    /// <summary>The name of the type this reads.</summary>
    public string TypeName { get; }

    /// <summary>Read access to this type's records.</summary>
    public abstract IHollowTypeDataAccess TypeAccess { get; }

    /// <summary>Whether the dataset has no such type.</summary>
    public bool IsMissingType => MaskedTypeIdentifier == HollowRef.ToTypeMasked(HollowRef.TypeAbsent);

    /// <summary>A reference to <paramref name="ordinal"/> of this type.</summary>
    public HollowRef RefForOrdinal(int ordinal) => HollowRef.Create(MaskedTypeIdentifier, ordinal);

    /// <summary>
    /// The ordinal <paramref name="reference"/> points at, having checked that it points into this
    /// type.
    /// </summary>
    /// <remarks>The check is the whole reason a reference carries a type. Every read goes through it.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="reference"/> is null.</exception>
    /// <exception cref="ArgumentException">It points into another type.</exception>
    public int Ordinal(HollowRef reference)
    {
        if (reference.IsOfTypeMasked(MaskedTypeIdentifier))
        {
            return reference.Ordinal;
        }

        // Java throws NullPointerException for the null case, which claims the fault is in the callee.
        // A null reference is a bad argument like any other.
        throw reference.IsNull
            ? new ArgumentNullException(nameof(reference), $"the reference is null; expected {TypeName}")
            : new ArgumentException(
                $"the reference is of type {Api.TypeIdentifiers.GetTypeName(reference.Type)}, "
                + $"and this is {TypeName}",
                nameof(reference));
    }
}
