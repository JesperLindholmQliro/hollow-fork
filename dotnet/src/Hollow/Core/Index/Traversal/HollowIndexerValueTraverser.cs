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

using Hollow.Core.Memory.Encoding;
using Hollow.Core.Read;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Schema;
using Hollow.Core.Util;

namespace Hollow.Core.Index.Traversal;

/// <summary>
/// Enumerates, for one record, every combination of values its indexed field paths reach.
/// </summary>
/// <remarks>
/// A path that crosses a collection reaches several values from one record, so a record of the root
/// type generally yields more than one match. Where two paths cross different collections, every
/// pairing of their values is its own match.
/// </remarks>
internal sealed class HollowIndexerValueTraverser
{
    private readonly string[] _fieldPaths;
    private readonly HollowIndexerTraversalNode _rootNode;
    private readonly IntList[] _fieldMatchLists;
    private readonly IHollowTypeDataAccess[] _fieldTypeDataAccess;
    private readonly int[] _fieldSchemaPosition;

    /// <summary>
    /// Builds a traverser over <paramref name="fieldPaths"/>, rooted at <paramref name="type"/>.
    /// </summary>
    internal HollowIndexerValueTraverser(IHollowDataAccess dataAccess, string type, string[] fieldPaths)
    {
        _fieldPaths = fieldPaths;

        TraversalTreeBuilder builder = new(dataAccess, type, fieldPaths);

        _rootNode = builder.BuildTree();
        _fieldMatchLists = builder.FieldMatchLists;
        _fieldTypeDataAccess = builder.FieldTypeDataAccesses;
        _fieldSchemaPosition = builder.FieldSchemaPositions;
    }

    /// <summary>The number of field paths this traverser covers.</summary>
    internal int FieldPathCount => _fieldPaths.Length;

    /// <summary>The number of matches the last <see cref="Traverse"/> produced.</summary>
    internal int MatchCount => _fieldMatchLists[0].Count;

    /// <summary>Collects the matches of the record at <paramref name="ordinal"/>.</summary>
    internal void Traverse(int ordinal)
    {
        foreach (IntList matches in _fieldMatchLists)
        {
            matches.Clear();
        }

        _rootNode.Traverse(ordinal);
    }

    /// <summary>The ordinal one match reached along one field path.</summary>
    internal int GetMatchOrdinal(int matchIndex, int fieldIndex) =>
        _fieldMatchLists[fieldIndex].Get(matchIndex);

    /// <summary>The data access a field path's values are read from.</summary>
    internal IHollowTypeDataAccess GetFieldTypeDataAccess(int fieldIndex) => _fieldTypeDataAccess[fieldIndex];

    /// <summary>The value one match reached along one field path.</summary>
    internal object? GetMatchedValue(int matchIndex, int fieldIndex) =>
        HollowReadFieldUtils.FieldValueObject(
            (IHollowObjectTypeDataAccess)_fieldTypeDataAccess[fieldIndex],
            _fieldMatchLists[fieldIndex].Get(matchIndex),
            _fieldSchemaPosition[fieldIndex]);

    /// <summary>Whether one match's value along one field path equals <paramref name="value"/>.</summary>
    internal bool IsMatchedValueEqual(int matchIndex, int fieldIndex, object? value) =>
        HollowReadFieldUtils.FieldValueEquals(
            (IHollowObjectTypeDataAccess)_fieldTypeDataAccess[fieldIndex],
            _fieldMatchLists[fieldIndex].Get(matchIndex),
            _fieldSchemaPosition[fieldIndex],
            value);

    /// <summary>A hash of all of one match's values.</summary>
    internal int GetMatchHash(int matchIndex)
    {
        int hashCode = 0;

        for (int i = 0; i < FieldPathCount; i++)
        {
            hashCode ^= HashCodes.HashInt(HollowReadFieldUtils.FieldHashCode(
                (IHollowObjectTypeDataAccess)_fieldTypeDataAccess[i],
                _fieldMatchLists[i].Get(matchIndex),
                _fieldSchemaPosition[i]));

            hashCode ^= HashCodes.HashInt(hashCode);
        }

        return hashCode;
    }

    /// <summary>A hash of one match's values along only the given field paths.</summary>
    /// <remarks>
    /// Hashing a subset is what lets a caller pair matches on a key and then ask whether the pair
    /// agrees about everything else: the key paths decide which matches meet, the rest decide whether
    /// meeting them counts as unchanged.
    /// </remarks>
    internal int GetMatchHash(int matchIndex, BitSet fields)
    {
        int hashCode = 0;

        for (int i = fields.NextSetBit(0); i != -1; i = fields.NextSetBit(i + 1))
        {
            hashCode ^= HashCodes.HashInt(HollowReadFieldUtils.FieldHashCode(
                (IHollowObjectTypeDataAccess)_fieldTypeDataAccess[i],
                _fieldMatchLists[i].Get(matchIndex),
                _fieldSchemaPosition[i]));

            hashCode ^= HashCodes.HashInt(hashCode);
        }

        return hashCode;
    }

    /// <summary>
    /// Whether two matches hold the same values along only the given field paths.
    /// </summary>
    internal bool IsMatchEqual(
        int matchIndex, HollowIndexerValueTraverser other, int otherMatchIndex, BitSet fields)
    {
        for (int i = fields.NextSetBit(0); i != -1; i = fields.NextSetBit(i + 1))
        {
            if (!HollowReadFieldUtils.FieldsAreEqual(
                (IHollowObjectTypeDataAccess)_fieldTypeDataAccess[i],
                _fieldMatchLists[i].Get(matchIndex),
                _fieldSchemaPosition[i],
                (IHollowObjectTypeDataAccess)other._fieldTypeDataAccess[i],
                other._fieldMatchLists[i].Get(otherMatchIndex),
                other._fieldSchemaPosition[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether two matches, of this traverser and another over the same paths, hold the same values.
    /// </summary>
    internal bool IsMatchEqual(
        int matchIndex, HollowIndexerValueTraverser other, int otherMatchIndex)
    {
        for (int i = 0; i < FieldPathCount; i++)
        {
            if (!HollowReadFieldUtils.FieldsAreEqual(
                (IHollowObjectTypeDataAccess)_fieldTypeDataAccess[i],
                _fieldMatchLists[i].Get(matchIndex),
                _fieldSchemaPosition[i],
                (IHollowObjectTypeDataAccess)other._fieldTypeDataAccess[i],
                other._fieldMatchLists[i].Get(otherMatchIndex),
                other._fieldSchemaPosition[i]))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Builds the tree of traversal nodes a set of field paths describes.
/// </summary>
internal sealed class TraversalTreeBuilder
{
    private readonly IHollowDataAccess _dataAccess;
    private readonly string _type;
    private readonly string[] _fieldPaths;

    internal TraversalTreeBuilder(IHollowDataAccess dataAccess, string type, string[] fieldPaths)
    {
        ArgumentNullException.ThrowIfNull(dataAccess);
        ArgumentNullException.ThrowIfNull(fieldPaths);

        _dataAccess = dataAccess;
        _type = type;
        _fieldPaths = fieldPaths;

        FieldMatchLists = new IntList[fieldPaths.Length];
        for (int i = 0; i < fieldPaths.Length; i++)
        {
            FieldMatchLists[i] = new IntList();
        }

        FieldTypeDataAccesses = new IHollowTypeDataAccess[fieldPaths.Length];
        FieldSchemaPositions = new int[fieldPaths.Length];
    }

    /// <summary>The per-field lists the tree appends its matched ordinals to.</summary>
    internal IntList[] FieldMatchLists { get; }

    /// <summary>The data access each field path's values are read from.</summary>
    internal IHollowTypeDataAccess[] FieldTypeDataAccesses { get; }

    /// <summary>The schema position each field path's values are read at, or -1 for a reference.</summary>
    internal int[] FieldSchemaPositions { get; }

    /// <summary>
    /// Builds the tree, which shares a node between paths wherever their prefixes coincide.
    /// </summary>
    /// <exception cref="ArgumentException">A path names something that cannot be traversed.</exception>
    internal HollowIndexerTraversalNode BuildTree()
    {
        IHollowTypeDataAccess rootTypeDataAccess = _dataAccess.GetTypeDataAccess(_type)
            ?? throw new ArgumentException($"type {_type} is not present in this state", nameof(_type));

        HollowIndexerTraversalNode rootNode = CreateTypeNode(rootTypeDataAccess);
        List<HollowIndexerTraversalNode> allNodes = [rootNode];

        for (int i = 0; i < _fieldPaths.Length; i++)
        {
            string[] pathElements = _fieldPaths[i].Length == 0 ? [] : _fieldPaths[i].Split('.');

            if (pathElements.Length == 0)
            {
                // The empty path selects the root record itself.
                rootNode.IndexedFieldPosition = i;
                FieldTypeDataAccesses[i] = rootTypeDataAccess;
                continue;
            }

            IHollowTypeDataAccess typeDataAccess = rootTypeDataAccess;
            HollowIndexerTraversalNode currentNode = rootNode;

            for (int j = 0; j < pathElements.Length; j++)
            {
                string pathElement = pathElements[j];

                if (!currentNode.Children.TryGetValue(pathElement, out HollowIndexerTraversalNode? child))
                {
                    child = CreateChildNode(typeDataAccess, pathElement);
                    currentNode.Children[pathElement] = child;
                    allNodes.Add(child);
                }

                currentNode = child;

                if (j == pathElements.Length - 1)
                {
                    currentNode.IndexedFieldPosition = i;
                    BindLeaf(i, typeDataAccess, pathElement);
                }
                else
                {
                    typeDataAccess = GetChildDataAccess(typeDataAccess, pathElement);
                }
            }
        }

        foreach (HollowIndexerTraversalNode node in allNodes)
        {
            node.SetUpMultiplication();
            node.SetUpChildren();
        }

        return rootNode;
    }

    /// <summary>
    /// Records where a path's final value is read from: a value field is read off its own record, while
    /// a reference or a collection element is read as the ordinal of the record it names.
    /// </summary>
    private void BindLeaf(int fieldIndex, IHollowTypeDataAccess typeDataAccess, string pathElement)
    {
        if (typeDataAccess is IHollowObjectTypeDataAccess objectAccess)
        {
            HollowObjectSchema schema = objectAccess.Schema;
            int position = schema.GetPosition(pathElement);

            if (position != -1 && schema.GetFieldType(position) != FieldType.Reference)
            {
                FieldSchemaPositions[fieldIndex] = position;
                FieldTypeDataAccesses[fieldIndex] = typeDataAccess;
                return;
            }
        }

        FieldTypeDataAccesses[fieldIndex] = GetChildDataAccess(typeDataAccess, pathElement);
        FieldSchemaPositions[fieldIndex] = -1;
    }

    private HollowIndexerTraversalNode CreateChildNode(IHollowTypeDataAccess typeDataAccess, string childName)
    {
        switch (typeDataAccess)
        {
            case IHollowObjectTypeDataAccess objectAccess:
            {
                HollowObjectSchema schema = objectAccess.Schema;
                int fieldIndex = RequirePosition(schema, childName);

                // A value field becomes a leaf node; a reference descends into the referenced type.
                return schema.GetFieldType(fieldIndex) == FieldType.Reference
                    ? CreateTypeNode(RequireTypeDataAccess(schema.GetReferencedType(fieldIndex)!))
                    : new HollowIndexerObjectFieldTraversalNode(objectAccess, FieldMatchLists);
            }

            case IHollowCollectionTypeDataAccess collectionAccess:
                return CreateTypeNode(RequireTypeDataAccess(collectionAccess.Schema.ElementType));

            case IHollowMapTypeDataAccess mapAccess:
                return CreateTypeNode(RequireTypeDataAccess(MapChildType(mapAccess.Schema, childName)));

            default:
                throw new ArgumentException(
                    $"cannot traverse into {typeDataAccess.Schema.Name}", nameof(typeDataAccess));
        }
    }

    private IHollowTypeDataAccess GetChildDataAccess(IHollowTypeDataAccess typeDataAccess, string childName) =>
        typeDataAccess switch
        {
            IHollowObjectTypeDataAccess objectAccess => RequireTypeDataAccess(
                objectAccess.Schema.GetReferencedType(RequirePosition(objectAccess.Schema, childName))
                ?? throw new ArgumentException(
                    $"field {childName} of {objectAccess.Schema.Name} is a value field and cannot be "
                    + "traversed through",
                    nameof(childName))),

            IHollowCollectionTypeDataAccess collectionAccess =>
                RequireTypeDataAccess(collectionAccess.Schema.ElementType),

            IHollowMapTypeDataAccess mapAccess =>
                RequireTypeDataAccess(MapChildType(mapAccess.Schema, childName)),

            _ => throw new ArgumentException(
                $"cannot traverse into {typeDataAccess.Schema.Name}", nameof(typeDataAccess)),
        };

    private HollowIndexerTraversalNode CreateTypeNode(IHollowTypeDataAccess typeDataAccess) =>
        typeDataAccess switch
        {
            IHollowObjectTypeDataAccess objectAccess =>
                new HollowIndexerObjectTraversalNode(objectAccess, FieldMatchLists),
            IHollowListTypeDataAccess listAccess =>
                new HollowIndexerListTraversalNode(listAccess, FieldMatchLists),
            IHollowSetTypeDataAccess setAccess =>
                new HollowIndexerCollectionTraversalNode(setAccess, FieldMatchLists),
            IHollowMapTypeDataAccess mapAccess =>
                new HollowIndexerMapTraversalNode(mapAccess, FieldMatchLists),
            _ => throw new ArgumentException(
                $"cannot index {typeDataAccess.Schema.Name}", nameof(typeDataAccess)),
        };

    private static string MapChildType(HollowMapSchema schema, string childName) =>
        childName switch
        {
            "key" => schema.KeyType,
            "value" => schema.ValueType,
            _ => throw new ArgumentException(
                $"a map may only be traversed through \"key\" or \"value\", not \"{childName}\"",
                nameof(childName)),
        };

    private static int RequirePosition(HollowObjectSchema schema, string fieldName)
    {
        int position = schema.GetPosition(fieldName);

        return position != -1
            ? position
            : throw new ArgumentException(
                $"type {schema.Name} has no field named {fieldName}", nameof(fieldName));
    }

    private IHollowTypeDataAccess RequireTypeDataAccess(string typeName) =>
        _dataAccess.GetTypeDataAccess(typeName)
        ?? throw new ArgumentException($"type {typeName} is not present in this state", nameof(typeName));
}
