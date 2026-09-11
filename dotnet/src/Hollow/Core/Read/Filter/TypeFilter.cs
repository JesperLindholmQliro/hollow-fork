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

namespace Hollow.Core.Read.Filter;

/// <summary>
/// Factory for <see cref="ITypeFilter"/> instances.
/// </summary>
/// <remarks>
/// <strong>Port note.</strong> Java's <c>TypeFilter.Builder</c> offers a fluent rule DSL with
/// recursive include and exclude that is resolved against the dataset's schemas. Only the explicit
/// include and exclude sets are ported here, which is what the read path itself needs; the recursive
/// DSL is listed in <c>PORTING.md</c> as deferred.
/// </remarks>
public static class TypeFilter
{
    /// <summary>A filter that includes every type and every field.</summary>
    public static ITypeFilter IncludeAll { get; } = new IncludeAllFilter();

    /// <summary>
    /// Creates a filter that includes only the named types, and within them only the named fields.
    /// </summary>
    /// <param name="types">The types to include. A type absent from this set is excluded.</param>
    /// <param name="fields">
    /// The fields to include, keyed by type name. A type absent from this dictionary has all of its
    /// fields included.
    /// </param>
    public static ITypeFilter Include(
        IEnumerable<string> types,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? fields = null)
    {
        ArgumentNullException.ThrowIfNull(types);
        return new ExplicitFilter(types.ToHashSet(StringComparer.Ordinal), fields);
    }

    private sealed class IncludeAllFilter : ITypeFilter
    {
        public bool Includes(string type) => true;

        public bool Includes(string type, string field) => true;
    }

    private sealed class ExplicitFilter(
        HashSet<string> types,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? fields) : ITypeFilter
    {
        public bool Includes(string type) => types.Contains(type);

        public bool Includes(string type, string field) =>
            types.Contains(type)
            && (fields is null
                || !fields.TryGetValue(type, out IReadOnlySet<string>? included)
                || included.Contains(field));
    }
}
