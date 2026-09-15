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

using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.Iterator;
using Hollow.Core.Schema;

namespace Hollow.Core.Index;

/// <summary>
/// A dot-separated field path that reads the values at its end out of a record.
/// </summary>
/// <remarks>
/// <para>
/// Where <see cref="FieldPaths"/> binds a path onto the schemas, this walks a bound path through the
/// data. A path may cross collections, in which case one record has many values at the end of it —
/// every element of the list, every entry of the map — and <see cref="FindValues(int)"/> returns all
/// of them.
/// </para>
/// <para>
/// A path through a map names <c>key</c> or <c>value</c> to say which side to follow. A path that
/// stops at a reference is auto-expanded to the value behind it unless the caller says otherwise, so
/// <c>Movie.title</c> reaches the string rather than the <c>String</c> record's ordinal.
/// </para>
/// <para>
/// Named <c>FieldPath</c> in Java, where it is package-private and so can share a name with the
/// <c>FieldPaths.FieldPath</c> binding. This port renames it rather than nest it, since the two are
/// used side by side.
/// </para>
/// </remarks>
public sealed class ValueFieldPath
{
    private readonly IHollowDataAccess _dataAccess;
    private readonly string _type;
    private readonly string[] _fields;
    private readonly int[] _fieldPositions;
    private readonly FieldType[] _fieldTypes;

    /// <summary>
    /// Binds <paramref name="fieldPath"/> against <paramref name="dataAccess"/>, starting from
    /// <paramref name="type"/>.
    /// </summary>
    /// <param name="dataAccess">The data to read from.</param>
    /// <param name="type">The type the path starts from.</param>
    /// <param name="fieldPath">The dot-separated path.</param>
    /// <param name="autoExpand">
    /// Whether a path that stops at a reference field should be extended until it reaches a value.
    /// </param>
    /// <exception cref="FieldPathException">The path cannot be bound.</exception>
    public ValueFieldPath(IHollowDataAccess dataAccess, string type, string fieldPath, bool autoExpand = true)
    {
        ArgumentNullException.ThrowIfNull(dataAccess);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrEmpty(fieldPath);

        _dataAccess = dataAccess;
        _type = type;

        FieldPath<FieldSegment> path =
            FieldPaths.CreateFieldPathForPrefixIndex(dataAccess, type, fieldPath, autoExpand);

        _fields = new string[path.Segments.Count];
        _fieldPositions = new int[path.Segments.Count];
        _fieldTypes = new FieldType[path.Segments.Count];

        string lastReferencedType = type;

        for (int i = 0; i < path.Segments.Count; i++)
        {
            FieldSegment segment = path.Segments[i];
            _fields[i] = segment.Name;

            if (segment is ObjectFieldSegment objectSegment)
            {
                _fieldPositions[i] = objectSegment.Index;
                _fieldTypes[i] = objectSegment.Type;
            }
            else
            {
                // A collection segment has no field position of its own; stepping through it is a
                // reference traversal.
                _fieldPositions[i] = 0;
                _fieldTypes[i] = FieldType.Reference;
            }

            if (segment.TypeName is { } referencedType)
            {
                lastReferencedType = referencedType;
            }
        }

        LastReferencedType = lastReferencedType;
    }

    /// <summary>
    /// The last type the path steps into, which is the type holding the values at its end.
    /// </summary>
    public string LastReferencedType { get; }

    /// <summary>The type of the value at the end of the path.</summary>
    public FieldType LastFieldType => _fieldTypes[^1];

    /// <summary>
    /// Every value at the end of the path for <paramref name="ordinal"/>'s record.
    /// </summary>
    /// <remarks>
    /// More than one only where the path crosses a collection; a record whose path stops at a null
    /// reference contributes none.
    /// </remarks>
    public IReadOnlyList<object?> FindValues(int ordinal) => FindValues(ordinal, _type, 0);

    /// <summary>
    /// The first value at the end of the path for <paramref name="ordinal"/>'s record, or
    /// <see langword="null"/> when there is none.
    /// </summary>
    public object? FindValue(int ordinal) => FindValue(ordinal, _type, 0);

    private IReadOnlyList<object?> FindValues(int ordinal, string type, int fieldIndex)
    {
        switch (_dataAccess.GetTypeDataAccess(type))
        {
            case IHollowMapTypeDataAccess mapAccess:
            {
                List<object?> values = [];
                bool throughKeys = IsKeySegment(fieldIndex);
                string keyOrValueType = throughKeys ? mapAccess.Schema.KeyType : mapAccess.Schema.ValueType;

                foreach (HollowMapEntry entry in mapAccess.Entries(ordinal))
                {
                    values.AddRange(
                        FindValues(
                            throughKeys ? entry.KeyOrdinal : entry.ValueOrdinal,
                            keyOrValueType,
                            fieldIndex + 1));
                }

                return values;
            }

            case IHollowCollectionTypeDataAccess collectionAccess:
            {
                List<object?> values = [];
                string elementType = collectionAccess.Schema.ElementType;

                foreach (int elementOrdinal in collectionAccess.ElementOrdinals(ordinal))
                {
                    values.AddRange(FindValues(elementOrdinal, elementType, fieldIndex + 1));
                }

                return values;
            }

            case IHollowObjectTypeDataAccess objectAccess when _fieldTypes[fieldIndex] == FieldType.Reference:
            {
                int referencedOrdinal = objectAccess.ReadOrdinal(ordinal, _fieldPositions[fieldIndex]);

                return referencedOrdinal < 0
                    ? []
                    : FindValues(
                        referencedOrdinal,
                        objectAccess.Schema.GetReferencedType(_fieldPositions[fieldIndex])!,
                        fieldIndex + 1);
            }

            case IHollowObjectTypeDataAccess objectAccess:
                return [ReadValue(objectAccess, ordinal, fieldIndex)];

            default:
                throw new InvalidOperationException($"the dataset holds no type named {type}");
        }
    }

    private object? FindValue(int ordinal, string type, int fieldIndex)
    {
        switch (_dataAccess.GetTypeDataAccess(type))
        {
            case IHollowMapTypeDataAccess mapAccess:
            {
                bool throughKeys = IsKeySegment(fieldIndex);
                string keyOrValueType = throughKeys ? mapAccess.Schema.KeyType : mapAccess.Schema.ValueType;

                // The first entry alone, where Java advances the cursor once and reads it.
                foreach (HollowMapEntry entry in mapAccess.Entries(ordinal))
                {
                    return FindValue(
                        throughKeys ? entry.KeyOrdinal : entry.ValueOrdinal, keyOrValueType, fieldIndex + 1);
                }

                return null;
            }

            case IHollowCollectionTypeDataAccess collectionAccess:
            {
                foreach (int elementOrdinal in collectionAccess.ElementOrdinals(ordinal))
                {
                    return FindValue(elementOrdinal, collectionAccess.Schema.ElementType, fieldIndex + 1);
                }

                return null;
            }

            case IHollowObjectTypeDataAccess objectAccess when _fieldTypes[fieldIndex] == FieldType.Reference:
            {
                int referencedOrdinal = objectAccess.ReadOrdinal(ordinal, _fieldPositions[fieldIndex]);

                return referencedOrdinal < 0
                    ? null
                    : FindValue(
                        referencedOrdinal,
                        objectAccess.Schema.GetReferencedType(_fieldPositions[fieldIndex])!,
                        fieldIndex + 1);
            }

            case IHollowObjectTypeDataAccess objectAccess:
                return ReadValue(objectAccess, ordinal, fieldIndex);

            default:
                throw new InvalidOperationException($"the dataset holds no type named {type}");
        }
    }

    /// <summary>
    /// Whether the segment at <paramref name="fieldIndex"/> follows a map's keys rather than its
    /// values.
    /// </summary>
    private bool IsKeySegment(int fieldIndex) =>
        fieldIndex < _fields.Length && string.Equals(_fields[fieldIndex], "key", StringComparison.Ordinal);

    private object? ReadValue(IHollowObjectTypeDataAccess objectAccess, int ordinal, int fieldIndex)
    {
        int position = _fieldPositions[fieldIndex];

        return _fieldTypes[fieldIndex] switch
        {
            FieldType.Int => objectAccess.ReadInt(ordinal, position),
            FieldType.Long => objectAccess.ReadLong(ordinal, position),
            FieldType.Double => objectAccess.ReadDouble(ordinal, position),
            FieldType.Float => objectAccess.ReadFloat(ordinal, position),
            FieldType.Decimal => objectAccess.ReadDecimal(ordinal, position),
            FieldType.Boolean => objectAccess.ReadBoolean(ordinal, position),
            FieldType.String => objectAccess.ReadString(ordinal, position),
            FieldType.Bytes => objectAccess.ReadBytes(ordinal, position),
            _ => throw new InvalidOperationException(
                $"a {_fieldTypes[fieldIndex]} field cannot be read as a value"),
        };
    }
}
