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
using Hollow.Core;

namespace Hollow.Api.PerfApi;

/// <summary>
/// A pointer to one record: a type identifier and an ordinal, packed into 64 bits.
/// </summary>
/// <remarks>
/// <para>
/// The type identifier is what makes the performance API safe to hand raw ordinals around in. An
/// ordinal on its own says nothing about which type it indexes, so passing one to the wrong type's
/// reader reads a different record and returns nonsense; carrying the type alongside turns that into
/// an exception.
/// </para>
/// <para>
/// Java calls this <c>Ref</c> and makes it a static class of helpers over a bare <c>long</c>, because
/// on the JVM a wrapper would be an allocation and this exists to avoid allocations. .NET has no such
/// trade to make: a <see langword="readonly"/> <see langword="struct"/> holding one <see cref="long"/>
/// is passed in a register exactly as the <c>long</c> would be, so the type safety is free. Every
/// signature that takes a <c>long ref</c> in Java takes a <see cref="HollowRef"/> here.
/// </para>
/// <para>
/// The type identifier is an index into the dataset's schema list, assigned by
/// <see cref="HollowPerformanceApi.TypeIdentifiers"/> — so a reference means something only against
/// the API that produced it, and must not be stored or sent anywhere.
/// </para>
/// </remarks>
public readonly struct HollowRef : IEquatable<HollowRef>
{
    /// <summary>The type identifier of a type the dataset does not have.</summary>
    public const int TypeAbsent = -1;

    private const long TypeMask = 0x0000FFFF_00000000L;

    private readonly long _value;

    private HollowRef(long value) => _value = value;

    /// <summary>A reference to nothing.</summary>
    /// <remarks>
    /// All bits set, so that its ordinal is <see cref="HollowConstants.OrdinalNone"/> and its type is
    /// no type. One value stands for both, as it does in Java.
    /// </remarks>
    public static HollowRef Null => new(-1);

    /// <summary>The raw 64 bits, for a caller that has to store one.</summary>
    public long Value => _value;

    /// <summary>Whether this points at a record.</summary>
    public bool IsNull => _value == -1;

    /// <summary>The ordinal of the record this points at.</summary>
    public int Ordinal => (int)_value;

    /// <summary>The identifier of the type this points into.</summary>
    public int Type => (int)((ulong)_value >> 32);

    /// <summary>
    /// The type half of this reference, with the ordinal cleared.
    /// </summary>
    /// <remarks>
    /// Kept as its own value so that a type API can compare a reference against its own type with one
    /// mask and one comparison, rather than shifting on every call.
    /// </remarks>
    public long TypeMasked => _value & TypeMask;

    /// <summary>Builds a reference to <paramref name="ordinal"/> of <paramref name="type"/>.</summary>
    public static HollowRef Create(int type, int ordinal) => Create(ToTypeMasked(type), ordinal);

    /// <summary>
    /// Builds a reference to <paramref name="ordinal"/> of an already-masked type.
    /// </summary>
    /// <remarks>
    /// An ordinal of <see cref="HollowConstants.OrdinalNone"/> gives <see cref="Null"/> rather than a
    /// reference to a record that does not exist. Java's <c>toRefWithTypeMasked</c> ORs the two
    /// together and leaves a caller who passes -1 holding a reference whose type bits have been
    /// overwritten — its own comment says "this erases the type" — so every caller has to check first.
    /// Here the one case that needs saying is said once, in the one place that can say it.
    /// </remarks>
    public static HollowRef Create(long typeMasked, int ordinal) =>
        ordinal == HollowConstants.OrdinalNone ? Null : new(typeMasked | (uint)ordinal);

    /// <summary>The type half of <paramref name="type"/>, with no ordinal.</summary>
    public static long ToTypeMasked(int type) => ((long)type << 32) & TypeMask;

    /// <summary>Whether this references a record of <paramref name="type"/>.</summary>
    public bool IsOfType(int type) => IsOfTypeMasked(ToTypeMasked(type));

    /// <summary>The same, against an already-masked type.</summary>
    public bool IsOfTypeMasked(long typeMasked) => TypeMasked == typeMasked;

    /// <inheritdoc />
    public bool Equals(HollowRef other) => _value == other._value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is HollowRef other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _value.GetHashCode();

    /// <inheritdoc />
    public override string ToString() =>
        IsNull ? "null" : string.Create(CultureInfo.InvariantCulture, $"type {Type}, ordinal {Ordinal}");

    /// <summary>Whether two references point at the same record of the same type.</summary>
    public static bool operator ==(HollowRef left, HollowRef right) => left.Equals(right);

    /// <summary>Whether two references point at different records.</summary>
    public static bool operator !=(HollowRef left, HollowRef right) => !left.Equals(right);
}

/// <summary>
/// A base for the generated wrappers a performance API hands out, carrying the reference the wrapper
/// reads through.
/// </summary>
/// <remarks>
/// Java calls this <c>HollowRef</c>, which this port cannot: the name is taken by the reference value
/// itself, which is the thing callers actually handle. <c>Object</c> says what it is — a wrapper
/// object standing for a reference, rather than the reference.
/// </remarks>
/// <param name="reference">The record this wrapper stands for.</param>
public abstract class HollowRefObject(HollowRef reference) : IEquatable<HollowRefObject>
{
    /// <summary>The record this wrapper stands for.</summary>
    public HollowRef Reference { get; } = reference;

    /// <inheritdoc />
    /// <remarks>
    /// Two wrappers are equal when they point at the same record, whatever their runtime types — as in
    /// Java, whose <c>equals</c> tests <c>instanceof HollowRef</c> rather than the exact class. Two
    /// wrappers of different generated types cannot share a reference anyway, because the type
    /// identifier is part of it.
    /// </remarks>
    public bool Equals(HollowRefObject? other) => other is not null && Reference == other.Reference;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as HollowRefObject);

    /// <inheritdoc />
    public override int GetHashCode() => Reference.GetHashCode();
}

/// <summary>One entry of a map record, as a pair of references.</summary>
public readonly record struct HollowRefEntry(HollowRef Key, HollowRef Value);
