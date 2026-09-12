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

using Hollow.Core;
using Hollow.Core.Index;
using Hollow.Core.Schema;

namespace Hollow.Api.Consumer.Index;

/// <summary>
/// A bound field path of either kind, so that one extractor can be written against both.
/// </summary>
/// <remarks>
/// Java writes <c>FieldPath&lt;? extends FieldSegment&gt;</c> and lets the wildcard cover a primary key
/// path and a hash index path at once. C# generics are invariant, so a
/// <see cref="FieldPath{ObjectFieldSegment}"/> is not a <see cref="FieldPath{FieldSegment}"/> and the
/// two are brought together here instead.
/// </remarks>
internal sealed class BoundFieldPath : IEquatable<BoundFieldPath>
{
    private BoundFieldPath(string rootType, IReadOnlyList<FieldSegment> segments, bool noAutoExpand)
    {
        RootType = rootType;
        Segments = segments;
        NoAutoExpand = noAutoExpand;
    }

    internal string RootType { get; }

    internal IReadOnlyList<FieldSegment> Segments { get; }

    internal bool NoAutoExpand { get; }

    /// <summary>The last segment, or <see langword="null"/> for the empty path.</summary>
    internal FieldSegment? LastSegment => Segments.Count == 0 ? null : Segments[^1];

    /// <summary>
    /// The field type the path resolves to, taking a path into a collection as a reference.
    /// </summary>
    internal FieldType? ResolvedFieldType =>
        LastSegment switch
        {
            null => null,
            ObjectFieldSegment objectSegment => objectSegment.Type,
            _ => FieldType.Reference,
        };

    /// <summary>The path in the symbolic form the underlying indexes take.</summary>
    internal string Text => string.Join('.', Segments.Select(segment => segment.Name));

    internal static BoundFieldPath From<TSegment>(FieldPath<TSegment> path)
        where TSegment : FieldSegment =>
        new(path.RootType, [.. path.Segments], path.NoAutoExpand);

    public bool Equals(BoundFieldPath? other) =>
        other is not null
        && (ReferenceEquals(this, other)
            || (NoAutoExpand == other.NoAutoExpand
                && RootType == other.RootType
                && Segments.SequenceEqual(other.Segments)));

    public override bool Equals(object? obj) => Equals(obj as BoundFieldPath);

    public override int GetHashCode()
    {
        HashCode hash = default;
        hash.Add(RootType);
        hash.Add(NoAutoExpand);

        foreach (FieldSegment segment in Segments)
        {
            hash.Add(segment);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => NoAutoExpand ? Text + "!" : Text;
}

/// <summary>
/// Binds a symbolic field path for one kind of index.
/// </summary>
/// <remarks>
/// Java's <c>MatchFieldPathArgumentExtractor.FieldPathResolver</c>, which is a functional interface and
/// so becomes a delegate.
/// </remarks>
internal delegate BoundFieldPath FieldPathResolver(IHollowDataset dataset, string type, string fieldPath);

/// <summary>The two resolvers the typed indexes bind their paths with.</summary>
internal static class FieldPathResolvers
{
    /// <summary>Binds a path the way <c>HollowPrimaryKeyIndex</c> does.</summary>
    internal static readonly FieldPathResolver PrimaryKey =
        (dataset, type, path) => BoundFieldPath.From(
            FieldPaths.CreateFieldPathForPrimaryKey(dataset, type, path));

    /// <summary>Binds a path the way <c>HollowHashIndex</c> does.</summary>
    internal static readonly FieldPathResolver HashIndex =
        (dataset, type, path) => BoundFieldPath.From(
            FieldPaths.CreateFieldPathForHashIndex(dataset, type, path));
}
