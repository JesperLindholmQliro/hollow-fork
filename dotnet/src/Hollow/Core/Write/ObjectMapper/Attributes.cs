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

namespace Hollow.Core.Write.ObjectMapper;

/// <summary>
/// Overrides the Hollow type name derived from a CLR type or member name.
/// </summary>
/// <remarks>Java's <c>@HollowTypeName</c>.</remarks>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Property | AttributeTargets.Field)]
public sealed class HollowTypeNameAttribute(string name) : Attribute
{
    /// <summary>The Hollow type name to use.</summary>
    public string Name { get; } = name;
}

/// <summary>
/// Stores a member's value directly in the record rather than as a reference to its own type.
/// </summary>
/// <remarks>
/// Java's <c>@HollowInline</c>. Inlining avoids a level of indirection but loses the deduplication a
/// referenced type gives, so it suits values that are rarely repeated.
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class HollowInlineAttribute : Attribute;

/// <summary>
/// Excludes a member from the mapped schema.
/// </summary>
/// <remarks>Java's <c>@HollowTransient</c>.</remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class HollowTransientAttribute : Attribute;

/// <summary>
/// Declares the field paths that uniquely identify a record of the annotated type.
/// </summary>
/// <remarks>Java's <c>@HollowPrimaryKey</c>.</remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class HollowPrimaryKeyAttribute(params string[] fields) : Attribute
{
    /// <summary>The field paths making up the key.</summary>
    public string[] Fields { get; } = fields;
}

/// <summary>
/// Names the Hollow type the elements of a list or set member are stored as.
/// </summary>
/// <remarks>
/// <para>
/// Java's <c>@HollowCollectionTypeName</c>, whose single element is <c>elementTypeName</c>; here it is
/// the attribute's one constructor argument, since there is nothing else to say.
/// </para>
/// <para>
/// A <c>List&lt;int&gt;</c> stores its elements in the shared <c>Integer</c> type by default, alongside
/// every other loose integer in the dataset. Naming the element type gives it a type of its own, which
/// means a smaller ordinal pool and fewer bits per reference. Compose it with
/// <see cref="HollowTypeNameAttribute"/> to rename the collection type as well.
/// </para>
/// <para>
/// <strong>Adding this to a member changes the schema</strong>, so a producer and its consumers have
/// to move together.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class HollowCollectionTypeNameAttribute(string elementTypeName) : Attribute
{
    /// <summary>The Hollow type name to store the list's or set's elements as.</summary>
    public string ElementTypeName { get; } = elementTypeName;
}

/// <summary>
/// Names the Hollow types the keys and values of a map member are stored as.
/// </summary>
/// <remarks>
/// <para>
/// Java's <c>@HollowMapTypeName</c>. Its two elements are optional there and are init-only properties
/// here — <c>[HollowMapTypeName(KeyTypeName = "SubTypeKey")]</c> — so that either may be given alone.
/// </para>
/// <para>
/// The reasoning is <see cref="HollowCollectionTypeNameAttribute"/>'s, and so is the warning: adding
/// this to a member changes the schema.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class HollowMapTypeNameAttribute : Attribute
{
    /// <summary>The Hollow type name to store the map's keys as, or null to derive one.</summary>
    public string? KeyTypeName { get; init; }

    /// <summary>The Hollow type name to store the map's values as, or null to derive one.</summary>
    public string? ValueTypeName { get; init; }
}

/// <summary>
/// Declares how the elements of a set, or the keys of a map, are hashed within each record's hash
/// table, so a consumer can find one by key rather than by ordinal.
/// </summary>
/// <remarks>
/// <para>
/// Java's <c>@HollowHashKey</c>. Apply it to a member whose type is a set or a dictionary; the field
/// paths are resolved against the element type (for a set) or the key type (for a map).
/// </para>
/// <para>
/// When this attribute is absent, a hash key is derived from the element or key type: its
/// <see cref="HollowPrimaryKeyAttribute"/> if it has one, otherwise the name of its single field if it
/// maps to exactly one non-reference field, otherwise none. Declaring the attribute with no field
/// paths suppresses that derivation and hashes by ordinal instead. Setting
/// <see cref="HollowObjectMapper.UseDefaultHashKeys"/> to <see langword="false"/> suppresses it for
/// every type.
/// </para>
/// <para>
/// A declared hash key replaces ordinal-based lookup rather than adding to it — see <c>PORTING.md</c>.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class HollowHashKeyAttribute(params string[] fields) : Attribute
{
    /// <summary>
    /// The field paths making up the hash key. Empty means the element or key ordinal is hashed.
    /// </summary>
    public string[] Fields { get; } = fields;
}

/// <summary>
/// Fixes the number of shards a type's records are split across, rather than deriving it from the
/// data size.
/// </summary>
/// <remarks>Java's <c>@HollowShardLargeType</c>.</remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class HollowShardLargeTypeAttribute(int numShards) : Attribute
{
    /// <summary>The number of shards, which must be a power of two.</summary>
    public int NumShards { get; } = numShards;
}
