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

namespace Hollow.Core.Schema;

/// <summary>
/// Orders a set of schemas so that a type comes after everything it references.
/// </summary>
public static class HollowSchemaSorter
{
    /// <summary>
    /// Orders <paramref name="dataset"/>'s schemas so that referenced types come before the types that
    /// reference them.
    /// </summary>
    public static IReadOnlyList<HollowSchema> DependencyOrderedSchemaList(IHollowDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        return DependencyOrderedSchemaList(dataset.Schemas);
    }

    /// <summary>
    /// Orders <paramref name="schemas"/> so that referenced types come before the types that reference
    /// them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ties are broken by name, so the result is stable across runs for a given input.
    /// </para>
    /// <para>
    /// A reference to a type not in <paramref name="schemas"/> is ignored rather than treated as a
    /// missing dependency, which is what lets a partial data model be ordered. Types caught in a
    /// reference cycle cannot be ordered at all, so they are appended by name after the rest: callers
    /// that require a strict ordering have to reject cycles themselves.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Two different schemas share a name.
    /// </exception>
    public static IReadOnlyList<HollowSchema> DependencyOrderedSchemaList(IEnumerable<HollowSchema> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);

        Dictionary<string, HollowSchema> schemasByName = new(StringComparer.Ordinal);
        foreach (HollowSchema schema in schemas)
        {
            if (schemasByName.TryGetValue(schema.Name, out HollowSchema? existing) && !existing.Equals(schema))
            {
                throw new ArgumentException(
                    $"Two different schemas are named {schema.Name}.", nameof(schemas));
            }

            schemasByName[schema.Name] = schema;
        }

        Dictionary<string, HashSet<string>> dependencies = schemasByName.Keys.ToDictionary(
            name => name, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);

        foreach (HollowSchema schema in schemasByName.Values)
        {
            foreach (string referenced in ReferencedTypes(schema))
            {
                if (schemasByName.ContainsKey(referenced) && referenced != schema.Name)
                {
                    dependencies[schema.Name].Add(referenced);
                }
            }
        }

        List<HollowSchema> ordered = [];

        // Kahn's algorithm, taking the alphabetically first of the currently unblocked types each round
        // so that the output does not depend on dictionary iteration order.
        while (true)
        {
            string? next = dependencies
                .Where(entry => entry.Value.Count == 0)
                .Select(entry => entry.Key)
                .Order(StringComparer.Ordinal)
                .FirstOrDefault();

            if (next is null)
            {
                break;
            }

            ordered.Add(schemasByName[next]);
            dependencies.Remove(next);

            foreach (HashSet<string> remaining in dependencies.Values)
            {
                remaining.Remove(next);
            }
        }

        // Whatever is left is in a cycle. Emitting it keeps the caller's type list complete, which
        // matters more than the ordering guarantee it cannot have anyway.
        ordered.AddRange(dependencies.Keys.Order(StringComparer.Ordinal).Select(name => schemasByName[name]));

        return ordered;
    }

    /// <summary>
    /// Whether <paramref name="dependencyType"/> is <paramref name="dependentType"/> itself, or is
    /// reachable from it by following references.
    /// </summary>
    public static bool TypeIsTransitivelyDependent(
        IHollowDataset dataset, string dependentType, string dependencyType)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        HashSet<string> visited = new(StringComparer.Ordinal);
        Stack<string> pending = new();
        pending.Push(dependentType);

        while (pending.TryPop(out string? typeName))
        {
            if (string.Equals(typeName, dependencyType, StringComparison.Ordinal))
            {
                return true;
            }

            if (!visited.Add(typeName) || dataset.GetSchema(typeName) is not { } schema)
            {
                continue;
            }

            foreach (string referenced in ReferencedTypes(schema))
            {
                pending.Push(referenced);
            }
        }

        return false;
    }

    private static IEnumerable<string> ReferencedTypes(HollowSchema schema)
    {
        switch (schema)
        {
            case HollowCollectionSchema collectionSchema:
                yield return collectionSchema.ElementType;
                break;

            case HollowMapSchema mapSchema:
                yield return mapSchema.KeyType;
                yield return mapSchema.ValueType;
                break;

            case HollowObjectSchema objectSchema:
                for (int i = 0; i < objectSchema.FieldCount; i++)
                {
                    if (objectSchema.GetFieldType(i) == FieldType.Reference)
                    {
                        yield return objectSchema.GetReferencedType(i)!;
                    }
                }

                break;
        }
    }
}
