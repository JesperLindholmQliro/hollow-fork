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

namespace Hollow.Api.Codegen;

/// <summary>
/// Marks a type as the root of a data model, for the source generator to emit a client for.
/// </summary>
/// <remarks>
/// <para>
/// Put it on the type a producer adds; everything reachable from there is part of the model.
/// </para>
/// <code>
/// [HollowGeneratedApi]
/// [HollowPrimaryKey("Id")]
/// public sealed record Movie(int Id, string Title, List&lt;Actor&gt; Cast);
/// </code>
/// <para>
/// The client appears at compile time — there is nothing to check in and nothing to regenerate when
/// the model changes. <see cref="HollowCodeGenerator"/> does the same job as a text emitter, for when
/// the model is a dataset rather than declared types.
/// </para>
/// <para>
/// Several roots may carry the attribute; those naming the same namespace and API class are emitted as
/// one client covering all of them, so a model with several entry points does not produce two APIs
/// that each know half of it.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
public sealed class HollowGeneratedApiAttribute : Attribute
{
    /// <summary>
    /// The namespace the generated types are declared in, defaulting to the marked type's own with
    /// <c>.Generated</c> appended.
    /// </summary>
    /// <remarks>
    /// The default is not the model's own namespace because the wrapper generated for a type is named
    /// after that type, so the two would collide. Setting this to the model's namespace will.
    /// </remarks>
    public string? Namespace { get; init; }

    /// <summary>
    /// The name of the generated API class, defaulting to the last segment of the namespace the
    /// <em>model</em> lives in plus <c>Api</c> — so a model in <c>Acme.Catalogue</c> gets a
    /// <c>CatalogueApi</c>.
    /// </summary>
    public string? ApiClassName { get; init; }

    /// <summary>
    /// The Hollow type names the generated API caches when it is told to cache none explicitly.
    /// </summary>
    public string[]? CachedTypes { get; init; }

    /// <summary>
    /// Whether a field referencing a type with one value field reads as that value rather than as a
    /// wrapper. On by default.
    /// </summary>
    public bool UseErgonomicShortcuts { get; init; } = true;

    /// <summary>
    /// Whether to emit a unique-key index for each type that declares a primary key. On by default.
    /// </summary>
    public bool GenerateUniqueKeyIndexes { get; init; } = true;

    /// <summary>
    /// Whether to emit a data accessor for each type that declares a primary key — what the last
    /// transition added, removed and replaced, as records. On by default.
    /// </summary>
    public bool GenerateDataAccessors { get; init; } = true;

    /// <summary>
    /// Whether to emit a cached delegate per object type, which holds a record's field values. On by
    /// default.
    /// </summary>
    public bool GenerateCachedDelegates { get; init; } = true;

    /// <summary>
    /// Whether to emit the typed field paths, which let an index be given a route through the model
    /// rather than a string.
    /// </summary>
    public bool GenerateFieldPaths { get; init; } = true;
}
