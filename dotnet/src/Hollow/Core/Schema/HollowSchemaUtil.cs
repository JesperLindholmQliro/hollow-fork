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
/// Questions about a data model that are answered by looking at every schema at once.
/// </summary>
public static class HollowSchemaUtil
{
    /// <summary>
    /// The types nothing else references, which is where a reader starts.
    /// </summary>
    /// <remarks>
    /// Everything a dataset holds is reachable from these: a type that something points at is part of
    /// that thing's shape rather than an entry point of its own.
    /// </remarks>
    public static IReadOnlySet<string> GetTopLevelTypes(IEnumerable<HollowSchema> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);

        List<HollowSchema> all = [.. schemas];
        HashSet<string> topLevel = new(all.Select(schema => schema.Name), StringComparer.Ordinal);

        foreach (HollowSchema schema in all)
        {
            foreach (string referenced in GetReferencedTypeNames(schema))
            {
                topLevel.Remove(referenced);
            }
        }

        return topLevel;
    }

    /// <summary>
    /// The types <paramref name="schema"/> points at: its reference fields, a collection's elements,
    /// or a map's keys and values.
    /// </summary>
    public static IReadOnlyList<string> GetReferencedTypeNames(HollowSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        switch (schema)
        {
            case HollowObjectSchema objectSchema:
                List<string> references = [];

                for (int i = 0; i < objectSchema.FieldCount; i++)
                {
                    if (objectSchema.GetFieldType(i) == FieldType.Reference)
                    {
                        references.Add(objectSchema.GetReferencedType(i)!);
                    }
                }

                return references;

            case HollowMapSchema mapSchema:
                return [mapSchema.KeyType, mapSchema.ValueType];

            case HollowCollectionSchema collectionSchema:
                return [collectionSchema.ElementType];

            default:
                return [];
        }
    }
}
