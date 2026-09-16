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

using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.Iterator;
using Hollow.Core.Schema;

namespace Hollow.Api.PerfApi;

/// <summary>
/// The base for an object type's reads.
/// </summary>
/// <remarks>
/// <para>
/// This resolves the field positions and the referenced types once, at construction, and leaves the
/// reads themselves to a subclass — which is what a generator emits: one method per field, each a
/// single call through <see cref="TypeAccess"/> with a constant index.
/// </para>
/// <para>
/// A type the dataset does not have is not an error here. The field positions are all -1 and reads go
/// to a missing-data access, so a consumer built against a newer model keeps working against an older
/// dataset rather than failing at construction.
/// </para>
/// </remarks>
public abstract class HollowObjectTypePerfApi : HollowTypePerfApi
{
    /// <summary>Where each declared field sits in the schema, or -1 where the schema lacks it.</summary>
    private protected readonly int[] FieldIndexes;

    /// <summary>
    /// The type half of the reference each reference field yields, indexed as
    /// <see cref="FieldIndexes"/> is.
    /// </summary>
    private protected readonly long[] ReferenceMaskedTypeIdentifiers;

    /// <summary>
    /// Initialises the API for <paramref name="typeName"/>, resolving <paramref name="fieldNames"/>.
    /// </summary>
    protected HollowObjectTypePerfApi(
        IHollowDataAccess dataAccess,
        string typeName,
        HollowPerformanceApi api,
        params string[] fieldNames)
        : base(typeName, api)
    {
        ArgumentNullException.ThrowIfNull(dataAccess);
        ArgumentNullException.ThrowIfNull(fieldNames);

        FieldIndexes = new int[fieldNames.Length];
        ReferenceMaskedTypeIdentifiers = new long[fieldNames.Length];

        if (dataAccess.GetTypeDataAccess(typeName) is IHollowObjectTypeDataAccess typeAccess)
        {
            HollowObjectSchema schema = typeAccess.Schema;

            for (int i = 0; i < fieldNames.Length; i++)
            {
                FieldIndexes[i] = schema.GetPosition(fieldNames[i]);

                if (FieldIndexes[i] != -1 && schema.GetFieldType(FieldIndexes[i]) == FieldType.Reference)
                {
                    ReferenceMaskedTypeIdentifiers[i] = HollowRef.ToTypeMasked(
                        api.TypeIdentifiers.GetIdentifier(schema.GetReferencedType(FieldIndexes[i])!));
                }
            }

            TypeAccess = typeAccess;
        }
        else
        {
            Array.Fill(FieldIndexes, -1);
            Array.Fill(ReferenceMaskedTypeIdentifiers, HollowRef.ToTypeMasked(HollowRef.TypeAbsent));

            TypeAccess = new HollowObjectMissingDataAccess(dataAccess, typeName);
        }
    }

    /// <summary>Read access to this type's records.</summary>
    public override IHollowObjectTypeDataAccess TypeAccess { get; }
}

/// <summary>A list type's reads, through references.</summary>
public class HollowListTypePerfApi : HollowTypePerfApi
{
    /// <summary>Initialises the API for <paramref name="typeName"/>.</summary>
    public HollowListTypePerfApi(IHollowDataAccess dataAccess, string typeName, HollowPerformanceApi api)
        : base(typeName, api)
    {
        ArgumentNullException.ThrowIfNull(dataAccess);

        if (dataAccess.GetTypeDataAccess(typeName) is IHollowListTypeDataAccess typeAccess)
        {
            ElementMaskedTypeIdentifier = HollowRef.ToTypeMasked(
                api.TypeIdentifiers.GetIdentifier(typeAccess.Schema.ElementType));

            TypeAccess = typeAccess;
        }
        else
        {
            ElementMaskedTypeIdentifier = HollowRef.ToTypeMasked(HollowRef.TypeAbsent);
            TypeAccess = new HollowListMissingDataAccess(dataAccess, typeName);
        }
    }

    /// <summary>Read access to this type's records.</summary>
    public override IHollowListTypeDataAccess TypeAccess { get; }

    /// <summary>The type half of a reference to one of this list's elements.</summary>
    internal long ElementMaskedTypeIdentifier { get; }

    /// <summary>How many elements the referenced list holds.</summary>
    public int Size(HollowRef reference) => TypeAccess.Size(Ordinal(reference));

    /// <summary>The element at <paramref name="index"/> of the referenced list.</summary>
    public HollowRef Get(HollowRef reference, int index) =>
        HollowRef.Create(
            ElementMaskedTypeIdentifier, TypeAccess.GetElementOrdinal(Ordinal(reference), index));

    /// <summary>
    /// The referenced list's elements.
    /// </summary>
    /// <remarks>
    /// Java hands back a <c>HollowPerfReferenceIterator</c>, a cursor of its own. This port replaced
    /// the ordinal cursors with sequences throughout — see <c>Java's cursors are sequences here</c> in
    /// <c>PORTING.md</c> — so the cursor class falls away and this is a sequence like any other.
    /// </remarks>
    public IEnumerable<HollowRef> Elements(HollowRef reference)
    {
        long maskedType = ElementMaskedTypeIdentifier;

        return TypeAccess.ElementOrdinals(Ordinal(reference))
            .Select(ordinal => HollowRef.Create(maskedType, ordinal));
    }

    /// <summary>
    /// The referenced list as a list of <typeparamref name="T"/>, each element built as it is read.
    /// </summary>
    public IReadOnlyList<T> BackedList<T>(HollowRef reference, Func<HollowRef, T> instantiate) =>
        new PerfBackedList<T>(this, Ordinal(reference), instantiate);
}

/// <summary>A set type's reads, through references.</summary>
public class HollowSetTypePerfApi : HollowTypePerfApi
{
    /// <summary>Initialises the API for <paramref name="typeName"/>.</summary>
    public HollowSetTypePerfApi(IHollowDataAccess dataAccess, string typeName, HollowPerformanceApi api)
        : base(typeName, api)
    {
        ArgumentNullException.ThrowIfNull(dataAccess);

        if (dataAccess.GetTypeDataAccess(typeName) is IHollowSetTypeDataAccess typeAccess)
        {
            ElementMaskedTypeIdentifier = HollowRef.ToTypeMasked(
                api.TypeIdentifiers.GetIdentifier(typeAccess.Schema.ElementType));

            TypeAccess = typeAccess;
        }
        else
        {
            ElementMaskedTypeIdentifier = HollowRef.ToTypeMasked(HollowRef.TypeAbsent);
            TypeAccess = new HollowSetMissingDataAccess(dataAccess, typeName);
        }
    }

    /// <summary>Read access to this type's records.</summary>
    public override IHollowSetTypeDataAccess TypeAccess { get; }

    /// <summary>The type half of a reference to one of this set's elements.</summary>
    internal long ElementMaskedTypeIdentifier { get; }

    /// <summary>How many elements the referenced set holds.</summary>
    public int Size(HollowRef reference) => TypeAccess.Size(Ordinal(reference));

    /// <summary>The referenced set's elements.</summary>
    public IEnumerable<HollowRef> Elements(HollowRef reference)
    {
        long maskedType = ElementMaskedTypeIdentifier;

        return TypeAccess.ElementOrdinals(Ordinal(reference))
            .Select(ordinal => HollowRef.Create(maskedType, ordinal));
    }

    /// <summary>
    /// The element matching <paramref name="hashKey"/>, or <see cref="HollowRef.Null"/>.
    /// </summary>
    /// <remarks>Requires the type to declare a hash key.</remarks>
    public HollowRef FindElement(HollowRef reference, params object?[] hashKey) =>
        HollowRef.Create(ElementMaskedTypeIdentifier, TypeAccess.FindElement(Ordinal(reference), hashKey));

    /// <summary>The referenced set as a set of <typeparamref name="T"/>.</summary>
    /// <param name="reference">The set record.</param>
    /// <param name="instantiate">Builds a <typeparamref name="T"/> from an element reference.</param>
    /// <param name="extractHashKey">
    /// Pulls the hash key out of a <typeparamref name="T"/>, which is what makes
    /// <see cref="IReadOnlySet{T}.Contains"/> a hash lookup rather than a scan. Pass
    /// <see langword="null"/> where the type declares no hash key; <c>Contains</c> then scans.
    /// </param>
    public IReadOnlySet<T> BackedSet<T>(
        HollowRef reference, Func<HollowRef, T> instantiate, Func<T, object?>? extractHashKey = null) =>
        new PerfBackedSet<T>(this, Ordinal(reference), instantiate, extractHashKey);
}

/// <summary>A map type's reads, through references.</summary>
public class HollowMapTypePerfApi : HollowTypePerfApi
{
    /// <summary>Initialises the API for <paramref name="typeName"/>.</summary>
    public HollowMapTypePerfApi(IHollowDataAccess dataAccess, string typeName, HollowPerformanceApi api)
        : base(typeName, api)
    {
        ArgumentNullException.ThrowIfNull(dataAccess);

        if (dataAccess.GetTypeDataAccess(typeName) is IHollowMapTypeDataAccess typeAccess)
        {
            KeyMaskedTypeIdentifier = HollowRef.ToTypeMasked(
                api.TypeIdentifiers.GetIdentifier(typeAccess.Schema.KeyType));
            ValueMaskedTypeIdentifier = HollowRef.ToTypeMasked(
                api.TypeIdentifiers.GetIdentifier(typeAccess.Schema.ValueType));

            TypeAccess = typeAccess;
        }
        else
        {
            KeyMaskedTypeIdentifier = HollowRef.ToTypeMasked(HollowRef.TypeAbsent);
            ValueMaskedTypeIdentifier = HollowRef.ToTypeMasked(HollowRef.TypeAbsent);

            TypeAccess = new HollowMapMissingDataAccess(dataAccess, typeName);
        }
    }

    /// <summary>Read access to this type's records.</summary>
    public override IHollowMapTypeDataAccess TypeAccess { get; }

    /// <summary>The type half of a reference to one of this map's keys.</summary>
    internal long KeyMaskedTypeIdentifier { get; }

    /// <summary>The type half of a reference to one of this map's values.</summary>
    internal long ValueMaskedTypeIdentifier { get; }

    /// <summary>How many entries the referenced map holds.</summary>
    public int Size(HollowRef reference) => TypeAccess.Size(Ordinal(reference));

    /// <summary>The referenced map's entries.</summary>
    public IEnumerable<HollowRefEntry> Entries(HollowRef reference) =>
        AsRefEntries(TypeAccess.Entries(Ordinal(reference)));

    /// <summary>
    /// The entries whose key hashes to <paramref name="hashCode"/>, for a caller that compares keys
    /// itself rather than paying for a full scan.
    /// </summary>
    public IEnumerable<HollowRefEntry> PotentialMatchEntries(HollowRef reference, int hashCode) =>
        AsRefEntries(TypeAccess.PotentialMatchEntries(Ordinal(reference), hashCode));

    /// <summary>The key matching <paramref name="hashKey"/>, or <see cref="HollowRef.Null"/>.</summary>
    public HollowRef FindKey(HollowRef reference, params object?[] hashKey) =>
        HollowRef.Create(KeyMaskedTypeIdentifier, TypeAccess.FindKey(Ordinal(reference), hashKey));

    /// <summary>The value matching <paramref name="hashKey"/>, or <see cref="HollowRef.Null"/>.</summary>
    public HollowRef FindValue(HollowRef reference, params object?[] hashKey) =>
        HollowRef.Create(ValueMaskedTypeIdentifier, TypeAccess.FindValue(Ordinal(reference), hashKey));

    /// <summary>The referenced map as a dictionary.</summary>
    /// <param name="reference">The map record.</param>
    /// <param name="instantiateKey">Builds a key from a key reference.</param>
    /// <param name="instantiateValue">Builds a value from a value reference.</param>
    /// <param name="extractHashKey">
    /// Pulls the hash key out of a <typeparamref name="TKey"/>, which is what makes a lookup a hash
    /// lookup rather than a scan. Pass <see langword="null"/> where the type declares no hash key.
    /// </param>
    public IReadOnlyDictionary<TKey, TValue> BackedMap<TKey, TValue>(
        HollowRef reference,
        Func<HollowRef, TKey> instantiateKey,
        Func<HollowRef, TValue> instantiateValue,
        Func<TKey, object?>? extractHashKey = null)
        where TKey : notnull =>
        new PerfBackedMap<TKey, TValue>(
            this, Ordinal(reference), instantiateKey, instantiateValue, extractHashKey);

    private IEnumerable<HollowRefEntry> AsRefEntries(IEnumerable<HollowMapEntry> entries)
    {
        long keyType = KeyMaskedTypeIdentifier;
        long valueType = ValueMaskedTypeIdentifier;

        return entries.Select(entry => new HollowRefEntry(
            HollowRef.Create(keyType, entry.KeyOrdinal),
            HollowRef.Create(valueType, entry.ValueOrdinal)));
    }
}
