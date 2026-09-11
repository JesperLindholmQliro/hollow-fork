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

using Hollow.Core.Index.Key;
using Hollow.Core.Schema;

namespace Hollow.Core.Index;

/// <summary>
/// Binds symbolic field paths such as <c>movie.country.id</c> onto the schemas of a dataset.
/// </summary>
public static class FieldPaths
{
    /// <summary>
    /// Binds <paramref name="path"/> as a primary key path, which auto-expands unless it ends in
    /// <c>!</c> and may not traverse collections.
    /// </summary>
    /// <exception cref="FieldPathException">The path cannot be bound.</exception>
    public static FieldPath<ObjectFieldSegment> CreateFieldPathForPrimaryKey(
        IHollowDataset dataset, string type, string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        bool autoExpand = !path.EndsWith('!');
        if (!autoExpand)
        {
            path = path[..^1];
        }

        FieldPath<FieldSegment> fieldPath = CreateFieldPath(dataset, type, path, autoExpand, false, false);

        // Not traversing collections means every segment is an object field segment. Java casts through
        // raw types to avoid the copy; C# generics are not variant here, so the list is rebuilt.
        return new FieldPath<ObjectFieldSegment>(
            fieldPath.RootType,
            [.. fieldPath.Segments.Cast<ObjectFieldSegment>()],
            fieldPath.NoAutoExpand);
    }

    /// <summary>
    /// Binds <paramref name="path"/> as a hash index path, which never auto-expands and may traverse
    /// collections.
    /// </summary>
    /// <exception cref="FieldPathException">The path cannot be bound.</exception>
    public static FieldPath<FieldSegment> CreateFieldPathForHashIndex(
        IHollowDataset dataset, string type, string path) =>
        CreateFieldPath(dataset, type, path, false, false, true);

    /// <summary>
    /// Binds <paramref name="path"/> as a prefix index path, which may traverse collections and, when
    /// <paramref name="autoExpand"/> is <see langword="false"/>, must already be a full path.
    /// </summary>
    /// <exception cref="FieldPathException">The path cannot be bound.</exception>
    public static FieldPath<FieldSegment> CreateFieldPathForPrefixIndex(
        IHollowDataset dataset, string type, string path, bool autoExpand) =>
        CreateFieldPath(dataset, type, path, autoExpand, requireFullPath: !autoExpand, traverseSequences: true);

    /// <summary>
    /// Binds <paramref name="path"/> against <paramref name="dataset"/>, starting from
    /// <paramref name="type"/>.
    /// </summary>
    /// <param name="dataset">The dataset whose schemas the path is bound against.</param>
    /// <param name="type">The type the path starts from.</param>
    /// <param name="path">The symbolic, dot-separated path.</param>
    /// <param name="autoExpand">
    /// Whether a path that stops at a reference field should be extended until it reaches a value field.
    /// </param>
    /// <param name="requireFullPath">
    /// Whether a path that stops at a reference field is an error. Ignored when
    /// <paramref name="autoExpand"/> is set.
    /// </param>
    /// <param name="traverseSequences">Whether lists, sets and maps may be traversed.</param>
    /// <exception cref="FieldPathException">The path cannot be bound.</exception>
    internal static FieldPath<FieldSegment> CreateFieldPath(
        IHollowDataset dataset,
        string type,
        string path,
        bool autoExpand,
        bool requireFullPath,
        bool traverseSequences)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(path);

        string[] segments = path.Length == 0 ? [] : path.Split('.');
        List<FieldSegment> fieldSegments = [];

        string? segmentType = type;

        for (int i = 0; i < segments.Length; i++)
        {
            HollowSchema? schema = segmentType is null ? null : dataset.GetSchema(segmentType);
            if (schema is null)
            {
                throw new FieldPathException(
                    FieldPathError.NotBindable, dataset, type, segments, fieldSegments, null, i);
            }

            string segment = segments[i];

            switch (schema)
            {
                case HollowObjectSchema objectSchema:
                {
                    int index = objectSchema.GetPosition(segment);
                    if (index == -1)
                    {
                        throw new FieldPathException(
                            FieldPathError.NotFound, dataset, type, segments, fieldSegments, schema, i);
                    }

                    segmentType = objectSchema.GetReferencedType(index);
                    fieldSegments.Add(new ObjectFieldSegment(objectSchema, segment, segmentType, index));
                    break;
                }

                case HollowCollectionSchema collectionSchema when traverseSequences:
                {
                    if (segment != "element")
                    {
                        throw new FieldPathException(
                            FieldPathError.NotFound, dataset, type, segments, fieldSegments, schema, i);
                    }

                    segmentType = collectionSchema.ElementType;
                    fieldSegments.Add(new FieldSegment(collectionSchema, segment, segmentType));
                    break;
                }

                case HollowMapSchema mapSchema when traverseSequences:
                {
                    segmentType = segment switch
                    {
                        "key" => mapSchema.KeyType,
                        "value" => mapSchema.ValueType,
                        _ => throw new FieldPathException(
                            FieldPathError.NotFound, dataset, type, segments, fieldSegments, schema, i),
                    };

                    fieldSegments.Add(new FieldSegment(mapSchema, segment, segmentType));
                    break;
                }

                default:
                    throw new FieldPathException(
                        FieldPathError.NotTraversable, dataset, type, segments, fieldSegments, schema, i);
            }

            if (i < segments.Length - 1 && segmentType is null)
            {
                throw new FieldPathException(
                    FieldPathError.NotTraversable, dataset, type, segments, fieldSegments, schema, i);
            }
        }

        if (autoExpand)
        {
            ExpandToValueField(dataset, type, segments, fieldSegments, ref segmentType);
        }
        else if (requireFullPath && segmentType is not null)
        {
            throw new FieldPathException(FieldPathError.NotFull, dataset, type, segments, fieldSegments);
        }

        return new FieldPath<FieldSegment>(type, fieldSegments, !autoExpand);
    }

    /// <summary>
    /// Extends a path that stopped at a reference field, following single-field records and
    /// single-field primary keys until it reaches a value field.
    /// </summary>
    private static void ExpandToValueField(
        IHollowDataset dataset,
        string type,
        string[] segments,
        List<FieldSegment> fieldSegments,
        ref string? segmentType)
    {
        while (segmentType is not null)
        {
            HollowSchema schema = dataset.GetNonNullSchema(segmentType);

            if (schema is not HollowObjectSchema objectSchema)
            {
                throw new FieldPathException(
                    FieldPathError.NotExpandable, dataset, type, segments, fieldSegments, schema);
            }

            if (objectSchema.FieldCount == 1)
            {
                segmentType = objectSchema.GetReferencedType(0);
                fieldSegments.Add(
                    new ObjectFieldSegment(objectSchema, objectSchema.GetFieldName(0), segmentType, 0));
            }
            else if (objectSchema.PrimaryKey is { FieldCount: 1 } key)
            {
                FieldPath<ObjectFieldSegment> expanded;
                try
                {
                    expanded = CreateFieldPathForPrimaryKey(dataset, key.Type, key.GetFieldPath(0));
                }
                catch (FieldPathException cause)
                {
                    throw new FieldPathException(
                        FieldPathError.NotExpandable, dataset, type, segments, fieldSegments, objectSchema, cause);
                }

                fieldSegments.AddRange(expanded.Segments);
                return;
            }
            else
            {
                throw new FieldPathException(
                    FieldPathError.NotExpandable, dataset, type, segments, fieldSegments, objectSchema);
            }
        }
    }
}

/// <summary>
/// The reason a field path could not be bound to a dataset.
/// </summary>
/// <remarks>
/// Java nests this as <c>FieldPaths.FieldPathException.ErrorKind</c>; C# has no nested enum inside an
/// exception convention, so it is a top-level enum named for what it describes.
/// </remarks>
public enum FieldPathError
{
    /// <summary>A type named by the path is not present in the dataset.</summary>
    NotBindable,

    /// <summary>A schema named by the path does not declare the field the path asks for.</summary>
    NotFound,

    /// <summary>The path stops at a reference field where a full path was required.</summary>
    NotFull,

    /// <summary>A segment of the path refers to something that cannot be traversed further.</summary>
    NotTraversable,

    /// <summary>The path stops at a reference field that cannot be expanded to a value field.</summary>
    NotExpandable,
}

/// <summary>
/// Thrown when a field path cannot be bound to a dataset.
/// </summary>
/// <remarks>
/// Java extends <c>IllegalArgumentException</c>; <see cref="ArgumentException"/> is the .NET equivalent.
/// </remarks>
public sealed class FieldPathException : ArgumentException
{
    internal FieldPathException(
        FieldPathError error,
        IHollowDataset dataset,
        string rootType,
        string[] segments,
        IReadOnlyList<FieldSegment> fieldSegments,
        HollowSchema? enclosingSchema = null,
        Exception? innerException = null)
        : this(error, dataset, rootType, segments, fieldSegments, enclosingSchema, segments.Length, innerException)
    {
    }

    internal FieldPathException(
        FieldPathError error,
        IHollowDataset dataset,
        string rootType,
        string[] segments,
        IReadOnlyList<FieldSegment> fieldSegments,
        HollowSchema? enclosingSchema,
        int segmentIndex,
        Exception? innerException = null)
        : base(
            BuildMessage(error, dataset, rootType, segments, fieldSegments, enclosingSchema, segmentIndex),
            innerException)
    {
        Error = error;
        RootType = rootType;
        Segments = segments;
        FieldSegments = fieldSegments;
        EnclosingSchema = enclosingSchema;
        SegmentIndex = segmentIndex;
    }

    /// <summary>Why the path could not be bound.</summary>
    public FieldPathError Error { get; }

    /// <summary>The type the path was bound from.</summary>
    public string RootType { get; }

    /// <summary>The symbolic path, split into segments.</summary>
    public IReadOnlyList<string> Segments { get; }

    /// <summary>The segments bound before the failure.</summary>
    public IReadOnlyList<FieldSegment> FieldSegments { get; }

    /// <summary>The schema being traversed when the failure occurred, where there was one.</summary>
    public HollowSchema? EnclosingSchema { get; }

    /// <summary>The index into <see cref="Segments"/> at which the failure occurred.</summary>
    public int SegmentIndex { get; }

    private static string BuildMessage(
        FieldPathError error,
        IHollowDataset dataset,
        string rootType,
        string[] segments,
        IReadOnlyList<FieldSegment> fieldSegments,
        HollowSchema? enclosingSchema,
        int segmentIndex)
    {
        string path = ToPathString(segments);
        string prefix = ToPathString(segments, segmentIndex + 1);

        return error switch
        {
            FieldPathError.NotBindable =>
                $"Field path \"{path}\" cannot be bound to data set {dataset}. A schema of type named "
                + $"\"{LastTypeName(rootType, fieldSegments)}\" cannot be found for the last segment of the "
                + $"path prefix \"{prefix}\".",

            FieldPathError.NotFound =>
                $"Field path \"{path}\" not found in data set {dataset}. A schema of type named "
                + $"\"{enclosingSchema?.Name}\" does not contain a field for the last segment of the path "
                + $"prefix \"{prefix}\".",

            FieldPathError.NotTraversable when enclosingSchema is not null
                    && enclosingSchema.SchemaType != SchemaType.Object =>
                $"Field path \"{path}\" is not traversable in data set {dataset}. A non-object schema of "
                + $"type named \"{enclosingSchema.Name}\" and of schema type {enclosingSchema.SchemaType} "
                + $"cannot be traversed for the last segment of the path prefix \"{prefix}\".",

            FieldPathError.NotTraversable =>
                $"Field path \"{path}\" is not traversable in data set {dataset}. An object schema of type "
                + $"named \"{enclosingSchema?.Name}\" cannot be traversed for the last segment of the path "
                + $"prefix \"{prefix}\". The last segment of the path prefix refers to a value "
                + "(non-reference) field.",

            FieldPathError.NotFull =>
                $"Field path \"{path}\" is not a full path in data set {dataset}. The last segment of the "
                + "path is not a value (non-reference) field and refers to a reference field whose schema "
                + $"is of type named \"{fieldSegments[^1].TypeName}\"",

            FieldPathError.NotExpandable when enclosingSchema is HollowObjectSchema objectSchema
                    && (objectSchema.FieldCount != 1 || objectSchema.PrimaryKey is not { FieldCount: 1 }) =>
                $"Field path \"{path}\" is not expandable in data set {dataset}. An object schema of type "
                + $"named \"{objectSchema.Name}\" cannot be traversed for the last segment of the partially "
                + $"expanded path \"{ToPathString(fieldSegments)}\". The schema contains more than one "
                + "field, or has no primary key, or has a primary key with more than one field path.",

            FieldPathError.NotExpandable =>
                $"Field path \"{path}\" is not expandable in data set {dataset}. A non-object schema of type "
                + $"named \"{enclosingSchema?.Name}\" and of schema type {enclosingSchema?.SchemaType} cannot "
                + $"be traversed for the last segment of the partially expanded path "
                + $"\"{ToPathString(fieldSegments)}\".",

            _ => $"Field path \"{path}\" cannot be bound to data set {dataset}.",
        };
    }

    private static string LastTypeName(string rootType, IReadOnlyList<FieldSegment> fieldSegments) =>
        fieldSegments.Count == 0 ? rootType : fieldSegments[^1].TypeName ?? rootType;

    private static string ToPathString(IEnumerable<FieldSegment> segments) =>
        string.Join('.', segments.Select(segment => segment.Name));

    private static string ToPathString(string[] segments) => ToPathString(segments, segments.Length);

    private static string ToPathString(string[] segments, int count) =>
        string.Join('.', segments.Take(count));
}

/// <summary>
/// A field path bound to a dataset's schemas.
/// </summary>
/// <typeparam name="TSegment">The kind of segment this path is made of.</typeparam>
public sealed class FieldPath<TSegment> : IEquatable<FieldPath<TSegment>>
    where TSegment : FieldSegment
{
    internal FieldPath(string rootType, IReadOnlyList<TSegment> segments, bool noAutoExpand)
    {
        RootType = rootType;
        Segments = segments;
        NoAutoExpand = noAutoExpand;
    }

    /// <summary>The type this path is bound from.</summary>
    public string RootType { get; }

    /// <summary>The bound segments, in order.</summary>
    public IReadOnlyList<TSegment> Segments { get; }

    /// <summary>Whether this path was bound without auto-expansion.</summary>
    public bool NoAutoExpand { get; }

    /// <inheritdoc />
    public bool Equals(FieldPath<TSegment>? other) =>
        other is not null
        && (ReferenceEquals(this, other)
            || (NoAutoExpand == other.NoAutoExpand
                && RootType == other.RootType
                && Segments.SequenceEqual(other.Segments)));

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as FieldPath<TSegment>);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        System.HashCode hash = default;
        hash.Add(RootType);
        hash.Add(NoAutoExpand);
        foreach (TSegment segment in Segments)
        {
            hash.Add(segment);
        }

        return hash.ToHashCode();
    }

    /// <summary>Renders the path back into its symbolic form.</summary>
    public override string ToString()
    {
        string path = string.Join('.', Segments.Select(segment => segment.Name));
        return NoAutoExpand ? path + "!" : path;
    }
}

/// <summary>
/// One segment of a bound field path.
/// </summary>
public class FieldSegment : IEquatable<FieldSegment>
{
    internal FieldSegment(HollowSchema enclosingSchema, string name, string? typeName)
    {
        EnclosingSchema = enclosingSchema;
        Name = name;
        TypeName = typeName;
    }

    /// <summary>The schema declaring the field this segment names.</summary>
    public HollowSchema EnclosingSchema { get; }

    /// <summary>The segment's name.</summary>
    public string Name { get; }

    /// <summary>
    /// The type this segment refers to, or <see langword="null"/> when it names a value field.
    /// </summary>
    public string? TypeName { get; }

    /// <inheritdoc />
    public virtual bool Equals(FieldSegment? other) =>
        other is not null
        && other.GetType() == GetType()
        && Equals(EnclosingSchema, other.EnclosingSchema)
        && Name == other.Name
        && TypeName == other.TypeName;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as FieldSegment);

    /// <inheritdoc />
    public override int GetHashCode() => System.HashCode.Combine(EnclosingSchema, Name, TypeName);

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>
/// A path segment naming a field of an object schema.
/// </summary>
public sealed class ObjectFieldSegment : FieldSegment, IEquatable<ObjectFieldSegment>
{
    internal ObjectFieldSegment(HollowObjectSchema enclosingSchema, string name, string? typeName, int index)
        : base(enclosingSchema, name, typeName)
    {
        Index = index;
        Type = enclosingSchema.GetFieldType(index);
    }

    /// <summary>The schema declaring this field.</summary>
    public new HollowObjectSchema EnclosingSchema => (HollowObjectSchema)base.EnclosingSchema;

    /// <summary>The field's position within its enclosing schema.</summary>
    public int Index { get; }

    /// <summary>The field's type.</summary>
    public FieldType Type { get; }

    /// <inheritdoc />
    public bool Equals(ObjectFieldSegment? other) =>
        base.Equals(other) && Index == other.Index && Type == other.Type;

    /// <inheritdoc />
    public override bool Equals(FieldSegment? other) => Equals(other as ObjectFieldSegment);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ObjectFieldSegment);

    /// <inheritdoc />
    public override int GetHashCode() => System.HashCode.Combine(base.GetHashCode(), Index, Type);
}
