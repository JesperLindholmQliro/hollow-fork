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

using Hollow.Core.Schema;

namespace Hollow.Explorer.Models;

/// <summary>
/// A type's schema as a tree the reader opens one branch at a time.
/// </summary>
/// <remarks>
/// A data model is a graph, and writing all of it out at once is unreadable — so the page shows one
/// type's fields and lets the reader open the ones that lead somewhere. Which branches are open is
/// what this holds, and it is why it outlives a request: it is the reader's place in the model.
/// </remarks>
public sealed class SchemaDisplay
{
    /// <summary>Builds the tree rooted at <paramref name="schema"/>.</summary>
    public SchemaDisplay(HollowSchema schema)
        : this(schema, "")
    {
    }

    private SchemaDisplay(HollowSchema schema, string fieldPath)
    {
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        FieldPath = fieldPath;
        Fields = CreateFields(schema, fieldPath);
    }

    /// <summary>The schema this node shows.</summary>
    public HollowSchema Schema { get; }

    /// <summary>The type this node shows.</summary>
    public string TypeName => Schema.Name;

    /// <summary>How this node is reached from the root, as a dotted path.</summary>
    public string FieldPath { get; }

    /// <summary>The fields of the schema, in the order it declares them.</summary>
    public IReadOnlyList<SchemaDisplayField> Fields { get; }

    /// <summary>Whether this node's fields are shown.</summary>
    public bool IsExpanded { get; set; }

    /// <summary>
    /// Opens or closes the node <paramref name="fieldPath"/> names.
    /// </summary>
    /// <remarks>
    /// The path starts with a dot, since it was built by appending one per step from an empty root —
    /// so the first segment is empty and the walk starts at the second.
    /// </remarks>
    public void ExpandOrCollapse(string fieldPath, bool isExpand)
    {
        ArgumentNullException.ThrowIfNull(fieldPath);

        ExpandOrCollapse(this, fieldPath.Split('.'), 1, isExpand);
    }

    private static void ExpandOrCollapse(SchemaDisplay? display, string[] fieldPaths, int cursor, bool isExpand)
    {
        if (display is null)
        {
            return;
        }

        if (cursor >= fieldPaths.Length)
        {
            display.IsExpanded = isExpand;

            return;
        }

        foreach (SchemaDisplayField field in display.Fields)
        {
            if (field.FieldName == fieldPaths[cursor])
            {
                ExpandOrCollapse(field.ReferencedType, fieldPaths, cursor + 1, isExpand);
            }
        }
    }

    private static List<SchemaDisplayField> CreateFields(HollowSchema schema, string fieldPath)
    {
        switch (schema)
        {
            case HollowObjectSchema objectSchema:
                List<SchemaDisplayField> fields = new(objectSchema.FieldCount);

                for (int i = 0; i < objectSchema.FieldCount; i++)
                {
                    fields.Add(new SchemaDisplayField(
                        $"{fieldPath}.{objectSchema.GetFieldName(i)}", objectSchema, i));
                }

                return fields;

            // A map is checked before a collection because it is neither a list nor a set, and the
            // collection case below would otherwise have to exclude it.
            case HollowMapSchema mapSchema:
                return
                [
                    new SchemaDisplayField($"{fieldPath}.key", mapSchema, 0),
                    new SchemaDisplayField($"{fieldPath}.value", mapSchema, 1),
                ];

            case HollowCollectionSchema collectionSchema:
                return [new SchemaDisplayField($"{fieldPath}.element", collectionSchema)];

            default:
                throw new ArgumentException(
                    $"{schema.Name} is a {schema.GetType().Name}, which is not a kind of schema this "
                    + "knows how to show",
                    nameof(schema));
        }
    }

    /// <summary>
    /// Builds a node for a type reached from somewhere, so that its path carries how it was reached.
    /// </summary>
    internal static SchemaDisplay Referenced(HollowSchema schema, string fieldPath) => new(schema, fieldPath);
}
