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
