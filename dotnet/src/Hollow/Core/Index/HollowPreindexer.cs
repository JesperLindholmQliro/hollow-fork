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

using Hollow.Core.Index.Traversal;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Schema;

namespace Hollow.Core.Index;

/// <summary>
/// Resolves a hash index's select and match field paths into the traverser that collects their values
/// and the per-field steps that read them back.
/// </summary>
internal sealed class HollowPreindexer(
    IHollowDataAccess dataAccess, string type, string selectField, string[] matchFields)
{
    /// <summary>The type the paths start from.</summary>
    internal IHollowTypeDataAccess TypeDataAccess { get; private set; } = null!;

    /// <summary>The resolved match fields.</summary>
    internal HollowHashIndexField[] MatchFieldSpecs { get; private set; } = [];

    /// <summary>
    /// How many of the traverser's fields belong to match paths; the select path's fields follow.
    /// </summary>
    internal int MatchTraverserFieldCount { get; private set; }

    /// <summary>The resolved select field.</summary>
    internal HollowHashIndexField SelectFieldSpec { get; private set; } = null!;

    /// <summary>The traverser over the distinct base paths the fields share.</summary>
    internal HollowIndexerValueTraverser Traverser { get; private set; } = null!;

    /// <summary>
    /// Resolves every path, sharing a traverser field between any that have the same base path.
    /// </summary>
    /// <exception cref="FieldPathException">One of the paths cannot be bound.</exception>
    internal void BuildFieldSpecifications()
    {
        Dictionary<string, int> baseFieldToIndexMap = new(StringComparer.Ordinal);

        TypeDataAccess = dataAccess.GetTypeDataAccess(type)
            ?? throw new ArgumentException($"type {type} is not present in this state", nameof(type));

        MatchFieldSpecs = new HollowHashIndexField[matchFields.Length];
        for (int i = 0; i < matchFields.Length; i++)
        {
            MatchFieldSpecs[i] = ResolveField(matchFields[i], baseFieldToIndexMap, truncateToBase: true);
        }

        // The select field's traverser fields come after every match field's, which is what lets the
        // stored match key be a prefix of the traverser's fields.
        MatchTraverserFieldCount = baseFieldToIndexMap.Count;
        SelectFieldSpec = ResolveField(selectField, baseFieldToIndexMap, truncateToBase: false);

        string[] baseFields = new string[baseFieldToIndexMap.Count];
        foreach ((string path, int index) in baseFieldToIndexMap)
        {
            baseFields[index] = path;
        }

        Traverser = new HollowIndexerValueTraverser(dataAccess, type, baseFields);
    }

    /// <summary>
    /// Splits one path at the last point the traverser has to resolve: the last collection element for
    /// a match path, or the whole path for the select path, whose result is a record rather than a
    /// value.
    /// </summary>
    private HollowHashIndexField ResolveField(
        string fieldPath, Dictionary<string, int> baseFieldToIndexMap, bool truncateToBase)
    {
        FieldPath<FieldSegment> path = FieldPaths.CreateFieldPathForHashIndex(dataAccess, type, fieldPath);

        IHollowTypeDataAccess baseTypeState = TypeDataAccess;
        int baseFieldPathIndex = 0;

        IReadOnlyList<FieldSegment> segments = path.Segments;
        HollowHashIndexField.FieldPathSegment[] fieldPathIndexes =
            new HollowHashIndexField.FieldPathSegment[segments.Count];

        FieldType fieldType = FieldType.Reference;

        for (int i = 0; i < segments.Count; i++)
        {
            FieldSegment segment = segments[i];

            switch (segment.EnclosingSchema)
            {
                case HollowObjectSchema when segment is ObjectFieldSegment objectSegment:
                {
                    fieldType = objectSegment.Type;

                    IHollowObjectTypeDataAccess enclosing = (IHollowObjectTypeDataAccess)RequireTypeDataAccess(
                        objectSegment.EnclosingSchema.Name);

                    fieldPathIndexes[i] =
                        new HollowHashIndexField.FieldPathSegment(objectSegment.Index, enclosing);

                    // An object step is only part of the base path for the select field, whose result
                    // is the record the whole path names.
                    if (!truncateToBase)
                    {
                        baseFieldPathIndex = i + 1;
                    }

                    break;
                }

                case HollowCollectionSchema collectionSchema:
                    fieldType = FieldType.Reference;
                    baseTypeState = RequireTypeDataAccess(collectionSchema.ElementType);
                    baseFieldPathIndex = i + 1;
                    break;

                case HollowMapSchema mapSchema:
                    fieldType = FieldType.Reference;
                    baseTypeState = RequireTypeDataAccess(
                        segment.Name == "key" ? mapSchema.KeyType : mapSchema.ValueType);
                    baseFieldPathIndex = i + 1;
                    break;

                default:
                    break;
            }
        }

        string basePath = string.Join('.', segments.Take(baseFieldPathIndex).Select(s => s.Name));

        if (!baseFieldToIndexMap.TryGetValue(basePath, out int basePathIndex))
        {
            basePathIndex = baseFieldToIndexMap.Count;
            baseFieldToIndexMap[basePath] = basePathIndex;
        }

        return new HollowHashIndexField(
            basePathIndex, fieldPathIndexes[baseFieldPathIndex..], baseTypeState, fieldType);
    }

    private IHollowTypeDataAccess RequireTypeDataAccess(string typeName) =>
        dataAccess.GetTypeDataAccess(typeName)
        ?? throw new ArgumentException($"type {typeName} is not present in this state", nameof(typeName));
}
