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
/// What the generator emits, and where.
/// </summary>
/// <remarks>
/// Java spreads the equivalent across a dozen boolean setters on the generator itself. Defaults here
/// are what a new project wants; the only required value is the namespace.
/// </remarks>
public sealed class HollowCodeGeneratorOptions
{
    /// <summary>The namespace the generated types are declared in.</summary>
    public required string Namespace { get; init; }

    /// <summary>
    /// The name of the generated API class, which defaults to the last namespace segment plus
    /// <c>Api</c>.
    /// </summary>
    public string? ApiClassName { get; init; }

    /// <summary>
    /// The types to read through a cache rather than straight from the blob, by Hollow type name.
    /// </summary>
    /// <remarks>
    /// Only a hint in the generated code: the API takes the set to cache at construction, and this
    /// decides what it defaults to.
    /// </remarks>
    public IReadOnlyCollection<string> DefaultCachedTypes { get; init; } = [];

    /// <summary>
    /// Whether a field referencing a type with one value field reads as that value rather than as a
    /// wrapper.
    /// </summary>
    /// <remarks>
    /// On by default, as in Java: <c>movie.Title</c> is a <see langword="string"/> rather than an
    /// <c>HString</c>, and the wrapper is still reachable as <c>movie.TitleRecord</c>. It matters more
    /// in C# than in Java, where a wrapper type in a property signature reads as noise.
    /// </remarks>
    public bool UseErgonomicShortcuts { get; init; } = true;

    /// <summary>
    /// Whether to emit a unique-key index class for each type that declares a primary key.
    /// </summary>
    public bool GenerateUniqueKeyIndexes { get; init; } = true;

    /// <summary>
    /// Whether to emit a data accessor for each type that declares a primary key — what the last
    /// transition added, removed and replaced, as records. On by default.
    /// </summary>
    public bool GenerateDataAccessors { get; init; } = true;

    /// <summary>
    /// Whether to emit a cached delegate per object type, which holds a record's field values.
    /// </summary>
    /// <remarks>
    /// Without it, caching a type saves rebuilding the wrapper but still reads every field from the
    /// blob. The cost is a class per type in the output.
    /// </remarks>
    public bool GenerateCachedDelegates { get; init; } = true;

    /// <summary>The API class name to use, resolved from <see cref="ApiClassName"/>.</summary>
    internal string ResolvedApiClassName =>
        ApiClassName ?? CodeNames.Pascal(Namespace.Split('.')[^1]) + "Api";

    /// <summary>
    /// These options in the form the emitters take, which is shared with the source generator and so
    /// knows nothing about this class.
    /// </summary>
    internal EmitterOptions ToEmitterOptions() =>
        new()
        {
            Namespace = Namespace,
            ApiClassName = ResolvedApiClassName,
            DefaultCachedTypes = DefaultCachedTypes,
            UseErgonomicShortcuts = UseErgonomicShortcuts,
            GenerateUniqueKeyIndexes = GenerateUniqueKeyIndexes,
            GenerateDataAccessors = GenerateDataAccessors,
            GenerateCachedDelegates = GenerateCachedDelegates,
        };
}
