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

namespace Hollow.Api.Consumer.Index;

/// <summary>
/// Binds a member of a key or query type to a field path in the dataset.
/// </summary>
/// <remarks>
/// <para>
/// A key type declares one member per field the index matches on:
/// </para>
/// <code>
/// private sealed class MovieKey
/// {
///     [FieldPath("id")]
///     public int Id { get; init; }
///
///     [FieldPath("country.code.value", Order = 1)]
///     public string Country { get; init; }
/// }
/// </code>
/// <para>
/// With no path given, the member's own name is the path — so a member called <c>Id</c> binds to the
/// field path <c>Id</c>, which is case-sensitive and has to match the schema.
/// </para>
/// <para>
/// Named <c>@FieldPath</c> in Java; .NET spells an attribute with the <c>Attribute</c> suffix, and
/// Java's <c>order()</c> element becomes the <see cref="Order"/> property, since C# reflection does
/// not define an order over a type's members.
/// </para>
/// </remarks>
/// <param name="path">
/// The field path, or nothing to use the member's name.
/// </param>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class FieldPathAttribute(string path = "") : Attribute
{
    /// <summary>The field path, or an empty string to use the member's name.</summary>
    public string Path { get; } = path;

    /// <summary>
    /// Where this member falls among the key's fields, lowest first.
    /// </summary>
    /// <remarks>
    /// Only matters when the index is bound to a declared primary key, where the key's fields have to
    /// be supplied in the order the key declares them. Members that share an order keep the order
    /// reflection reports them in.
    /// </remarks>
    public int Order { get; init; }
}
