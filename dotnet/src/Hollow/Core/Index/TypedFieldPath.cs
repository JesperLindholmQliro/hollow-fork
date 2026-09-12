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

namespace Hollow.Core.Index;

/// <summary>
/// A route from one record type into the data reachable from it, written as a value rather than as a
/// string.
/// </summary>
/// <remarks>
/// <para>
/// The indexes have always taken their paths as text — <c>"Studio.Name.value"</c> — which means a typo
/// is a runtime failure and the type a path arrives at is something the caller has to know. A path
/// generated from the data model is neither: the compiler writes it, and it carries what it leads to.
/// </para>
/// <para>
/// The generated form is <c>CataloguePaths.Movie.Studio.Name.Value</c>, which is a
/// <see cref="FieldPath{TRoot, TValue}"/> of <c>Movie</c> and <c>string</c>. Each step is itself a path,
/// so a route that stops early is as usable as one that runs to a value.
/// </para>
/// <para>
/// This is not <see cref="FieldPath{TSegment}"/>, which is the low-level result of resolving a path
/// against a dataset. They differ in arity, so both can be in scope at once; they differ in purpose
/// entirely.
/// </para>
/// </remarks>
public abstract class FieldPath
{
    /// <summary>Creates a path from <paramref name="rootTypeName"/> along <paramref name="path"/>.</summary>
    /// <param name="rootTypeName">The Hollow type the path starts at.</param>
    /// <param name="path">The dotted field path, empty for the root itself.</param>
    protected FieldPath(string rootTypeName, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootTypeName);
        ArgumentNullException.ThrowIfNull(path);

        RootTypeName = rootTypeName;
        Path = path;
    }

    /// <summary>The Hollow type this path starts at.</summary>
    public string RootTypeName { get; }

    /// <summary>
    /// The path in the dotted form the indexes take, which is empty for the root itself.
    /// </summary>
    public string Path { get; }

    /// <inheritdoc />
    public override string ToString() => Path.Length == 0 ? RootTypeName : $"{RootTypeName}.{Path}";

    /// <summary>
    /// Throws unless this path starts at <paramref name="expectedRootTypeName"/>.
    /// </summary>
    /// <remarks>
    /// The compiler catches a path used on the wrong record type, since the root is a type argument.
    /// It cannot catch an index built for a type name that the root type does not read, which is what
    /// this is for.
    /// </remarks>
    /// <exception cref="ArgumentException">The path starts somewhere else.</exception>
    public void RequireRoot(string expectedRootTypeName)
    {
        if (RootTypeName != expectedRootTypeName)
        {
            throw new ArgumentException(
                $"the path {this} starts at {RootTypeName}, but it is being used to index "
                + expectedRootTypeName,
                nameof(expectedRootTypeName));
        }
    }

    /// <summary>
    /// This path with <paramref name="segment"/> appended, as the text a longer path is built from.
    /// </summary>
    protected string Extend(string segment) => Path.Length == 0 ? segment : $"{Path}.{segment}";
}

/// <summary>
/// A <see cref="FieldPath"/> from <typeparamref name="TRoot"/> to <typeparamref name="TValue"/>.
/// </summary>
/// <remarks>
/// Both ends are type arguments, as in Swift's <c>KeyPath&lt;Root, Value&gt;</c>: the root so that a
/// path cannot be handed to an index over some other type, and the value so that an index can type its
/// own query and results from the path it was given rather than from a second argument that might
/// disagree with it.
/// </remarks>
/// <typeparam name="TRoot">The record type the path starts at.</typeparam>
/// <typeparam name="TValue">What the path arrives at — a value, or another record's wrapper.</typeparam>
public class FieldPath<TRoot, TValue> : FieldPath
{
    /// <inheritdoc cref="FieldPath(string, string)" />
    public FieldPath(string rootTypeName, string path)
        : base(rootTypeName, path)
    {
    }
}
