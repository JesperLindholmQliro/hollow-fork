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

using Hollow.Core.Read.Engine;
using Hollow.Core.Schema;

namespace Hollow.Explorer.Models;

/// <summary>
/// One field of a <see cref="SchemaDisplay"/>: its name, what it holds, and where it leads.
/// </summary>
public sealed class SchemaDisplayField
{
    /// <summary>A collection's element, which is the only field a list or a set has.</summary>
    internal SchemaDisplayField(string fieldPath, HollowCollectionSchema parentSchema)
    {
        FieldPath = fieldPath;
        FieldName = "element";
        FieldType = FieldType.Reference;
        IsSearchable = false;
        ReferencedType = parentSchema.ElementTypeState is { } elementState
            ? SchemaDisplay.Referenced(elementState.Schema, fieldPath)
            : null;
    }

    /// <summary>A map's key or value.</summary>
    internal SchemaDisplayField(string fieldPath, HollowMapSchema parentSchema, int fieldNumber)
    {
        FieldPath = fieldPath;
        FieldName = fieldNumber == 0 ? "key" : "value";
        FieldType = FieldType.Reference;
        IsSearchable = false;

        HollowTypeReadState? referenced =
            fieldNumber == 0 ? parentSchema.KeyTypeState : parentSchema.ValueTypeState;

        ReferencedType = referenced is not null
            ? SchemaDisplay.Referenced(referenced.Schema, fieldPath)
            : null;
    }

    /// <summary>A field of an object type.</summary>
    internal SchemaDisplayField(string fieldPath, HollowObjectSchema parentSchema, int fieldNumber)
    {
        FieldPath = fieldPath;
        FieldName = parentSchema.GetFieldName(fieldNumber);
        FieldType = parentSchema.GetFieldType(fieldNumber);
        IsSearchable = GetIsSearchable(parentSchema, fieldNumber);
        ReferencedType = FieldType == FieldType.Reference
            && parentSchema.GetReferencedTypeState(fieldNumber) is { } referenced
            ? SchemaDisplay.Referenced(referenced.Schema, fieldPath)
            : null;
    }

    /// <summary>The field's name, or <c>element</c>, <c>key</c> or <c>value</c>.</summary>
    public string FieldName { get; }

    /// <summary>How this field is reached from the root of its tree, as a dotted path.</summary>
    public string FieldPath { get; }

    /// <summary>What the field holds.</summary>
    public FieldType FieldType { get; }

    /// <summary>Whether the search page can look for a value in this field.</summary>
    public bool IsSearchable { get; }

    /// <summary>
    /// The type this field points at, or <see langword="null"/> when it holds a value rather than a
    /// reference — or points at a type the dataset was filtered down to exclude.
    /// </summary>
    public SchemaDisplay? ReferencedType { get; }

    /// <summary>
    /// Whether a search can match this field by the text of a value.
    /// </summary>
    /// <remarks>
    /// A value field always can. A reference can only when it points at an object type holding a single
    /// field that is itself searchable — because then the text describes that one value and nothing is
    /// ambiguous about which field it means. That is what makes a <c>String</c> wrapper searchable and
    /// a reference to a record with several fields not.
    /// </remarks>
    private static bool GetIsSearchable(HollowObjectSchema schema, int fieldNumber)
    {
        if (schema.GetFieldType(fieldNumber) != FieldType.Reference)
        {
            return true;
        }

        return schema.GetReferencedTypeState(fieldNumber)?.Schema is HollowObjectSchema referenced
            && referenced.FieldCount == 1
            && GetIsSearchable(referenced, 0);
    }
}
