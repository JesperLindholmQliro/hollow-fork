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

using Hollow.Core.Read.Iterator;
using Hollow.Core.Schema;

namespace Hollow.Core.Read.Missing;

/// <summary>
/// Answers a read of a field or type the loaded dataset does not have.
/// </summary>
/// <remarks>
/// <para>
/// A typed client is compiled against one version of the data model and may be pointed at a dataset
/// written against another — an older producer that has not added a field yet, or a consumer whose
/// filter excluded one. Rather than fail, every typed accessor falls through to this when the field it
/// wants is not there.
/// </para>
/// <para>
/// <see cref="DefaultMissingDataHandler"/> answers "absent" to everything, which is what makes a
/// missing field read as null. An implementation could instead consult another dataset, or throw to
/// turn a model mismatch into a loud failure.
/// </para>
/// <para>
/// Named <c>MissingDataHandler</c> in Java; the <c>I</c> prefix follows the .NET convention.
/// </para>
/// </remarks>
public interface IMissingDataHandler
{
    /// <summary>Whether a missing object field reads as null.</summary>
    bool HandleIsNull(string type, int ordinal, string field);

    /// <summary>The value a missing boolean field reads as.</summary>
    bool? HandleBoolean(string type, int ordinal, string field);

    /// <summary>The ordinal a missing reference field reads as.</summary>
    int HandleReferencedOrdinal(string type, int ordinal, string field);

    /// <summary>The value a missing int field reads as.</summary>
    int HandleInt(string type, int ordinal, string field);

    /// <summary>The value a missing long field reads as.</summary>
    long HandleLong(string type, int ordinal, string field);

    /// <summary>The value a missing float field reads as.</summary>
    float HandleFloat(string type, int ordinal, string field);

    /// <summary>The value a missing double field reads as.</summary>
    double HandleDouble(string type, int ordinal, string field);

    /// <summary>
    /// The value a missing decimal field reads as.
    /// </summary>
    /// <remarks>
    /// <strong>Format extension.</strong> <see cref="FieldType.Decimal"/> is not part of Netflix
    /// Hollow, so this method has no Java counterpart — see <c>PORTING.md</c>.
    /// </remarks>
    decimal? HandleDecimal(string type, int ordinal, string field);

    /// <summary>The value a missing string field reads as.</summary>
    string? HandleString(string type, int ordinal, string field);

    /// <summary>Whether a missing string field compares equal to <paramref name="testValue"/>.</summary>
    bool HandleStringEquals(string type, int ordinal, string field, string? testValue);

    /// <summary>The value a missing bytes field reads as.</summary>
    byte[]? HandleBytes(string type, int ordinal, string field);

    /// <summary>The size a missing list record reads as.</summary>
    int HandleListSize(string type, int ordinal);

    /// <summary>The element ordinal a missing list record reads as.</summary>
    int HandleListElementOrdinal(string type, int ordinal, int index);

    /// <summary>The elements a missing list record reads as.</summary>
    IEnumerable<int> HandleListElementOrdinals(string type, int ordinal);

    /// <summary>The size a missing set record reads as.</summary>
    int HandleSetSize(string type, int ordinal);

    /// <summary>The elements a missing set record reads as.</summary>
    IEnumerable<int> HandleSetElementOrdinals(string type, int ordinal);

    /// <summary>The potential matches a missing set record reads as.</summary>
    IEnumerable<int> HandleSetPotentialMatchElementOrdinals(string type, int ordinal, int hashCode);

    /// <summary>Whether a missing set record contains an element.</summary>
    bool HandleSetContainsElement(string type, int ordinal, int elementOrdinal, int elementOrdinalHashCode);

    /// <summary>The element a missing set record finds by hash key.</summary>
    int HandleSetFindElement(string type, int ordinal, params object?[] hashKey);

    /// <summary>The size a missing map record reads as.</summary>
    int HandleMapSize(string type, int ordinal);

    /// <summary>The entries a missing map record reads as.</summary>
    IEnumerable<HollowMapEntry> HandleMapEntries(string type, int ordinal);

    /// <summary>The potential matches a missing map record reads as.</summary>
    IEnumerable<HollowMapEntry> HandleMapPotentialMatchEntries(string type, int ordinal, int keyHashCode);

    /// <summary>The value a missing map record maps a key to.</summary>
    int HandleMapGet(string type, int ordinal, int keyOrdinal, int keyOrdinalHashCode);

    /// <summary>The key a missing map record finds by hash key.</summary>
    int HandleMapFindKey(string type, int ordinal, params object?[] hashKey);

    /// <summary>The value a missing map record finds by hash key.</summary>
    int HandleMapFindValue(string type, int ordinal, params object?[] hashKey);

    /// <summary>The entry a missing map record finds by hash key.</summary>
    long HandleMapFindEntry(string type, int ordinal, params object?[] hashKey);

    /// <summary>The schema a missing type reads as.</summary>
    HollowSchema? HandleSchema(string type);
}

/// <summary>
/// Answers every read of missing data as though the record were there and empty.
/// </summary>
/// <remarks>
/// A missing field reads null — which for a primitive means the null sentinel its type uses, so a
/// missing int reads <see cref="int.MinValue"/> and a missing double reads
/// <see cref="double.NaN"/>. A missing collection is empty. That is what lets a client compiled
/// against a newer model read an older dataset without failing.
/// </remarks>
public class DefaultMissingDataHandler : IMissingDataHandler
{
    /// <summary>The shared instance, which holds no state.</summary>
    public static readonly DefaultMissingDataHandler Instance = new();

    /// <inheritdoc />
    public virtual bool HandleIsNull(string type, int ordinal, string field) => true;

    /// <inheritdoc />
    public virtual bool? HandleBoolean(string type, int ordinal, string field) => null;

    /// <inheritdoc />
    public virtual int HandleReferencedOrdinal(string type, int ordinal, string field) =>
        HollowConstants.OrdinalNone;

    /// <inheritdoc />
    public virtual int HandleInt(string type, int ordinal, string field) => int.MinValue;

    /// <inheritdoc />
    public virtual long HandleLong(string type, int ordinal, string field) => long.MinValue;

    /// <inheritdoc />
    public virtual float HandleFloat(string type, int ordinal, string field) => float.NaN;

    /// <inheritdoc />
    public virtual double HandleDouble(string type, int ordinal, string field) => double.NaN;

    /// <inheritdoc />
    public virtual decimal? HandleDecimal(string type, int ordinal, string field) => null;

    /// <inheritdoc />
    public virtual string? HandleString(string type, int ordinal, string field) => null;

    /// <inheritdoc />
    public virtual bool HandleStringEquals(string type, int ordinal, string field, string? testValue) =>
        testValue is null;

    /// <inheritdoc />
    public virtual byte[]? HandleBytes(string type, int ordinal, string field) => null;

    /// <inheritdoc />
    public virtual int HandleListSize(string type, int ordinal) => 0;

    /// <inheritdoc />
    public virtual int HandleListElementOrdinal(string type, int ordinal, int index) =>
        HollowConstants.OrdinalNone;

    /// <inheritdoc />
    public virtual IEnumerable<int> HandleListElementOrdinals(string type, int ordinal) => [];

    /// <inheritdoc />
    public virtual int HandleSetSize(string type, int ordinal) => 0;

    /// <inheritdoc />
    public virtual IEnumerable<int> HandleSetElementOrdinals(string type, int ordinal) => [];

    /// <inheritdoc />
    public virtual IEnumerable<int> HandleSetPotentialMatchElementOrdinals(
        string type, int ordinal, int hashCode) => [];

    /// <inheritdoc />
    public virtual bool HandleSetContainsElement(
        string type, int ordinal, int elementOrdinal, int elementOrdinalHashCode) => false;

    /// <inheritdoc />
    public virtual int HandleSetFindElement(string type, int ordinal, params object?[] hashKey) =>
        HollowConstants.OrdinalNone;

    /// <inheritdoc />
    public virtual int HandleMapSize(string type, int ordinal) => 0;

    /// <inheritdoc />
    public virtual IEnumerable<HollowMapEntry> HandleMapEntries(string type, int ordinal) => [];

    /// <inheritdoc />
    public virtual IEnumerable<HollowMapEntry> HandleMapPotentialMatchEntries(
        string type, int ordinal, int keyHashCode) => [];

    /// <inheritdoc />
    public virtual int HandleMapGet(string type, int ordinal, int keyOrdinal, int keyOrdinalHashCode) =>
        HollowConstants.OrdinalNone;

    /// <inheritdoc />
    public virtual int HandleMapFindKey(string type, int ordinal, params object?[] hashKey) =>
        HollowConstants.OrdinalNone;

    /// <inheritdoc />
    public virtual int HandleMapFindValue(string type, int ordinal, params object?[] hashKey) =>
        HollowConstants.OrdinalNone;

    /// <inheritdoc />
    public virtual long HandleMapFindEntry(string type, int ordinal, params object?[] hashKey) => -1L;

    /// <inheritdoc />
    public virtual HollowSchema? HandleSchema(string type) => null;
}
