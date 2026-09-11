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
using Hollow.Core.Index.Key;
using Hollow.Core.Schema;

namespace Hollow.Tests.Core.Index;

/// <summary>
/// Binding a field path is where a declared key stops being a string and starts being a walk over the
/// schemas. The interesting cases are the ones where the declared path stops short of a value field and
/// has to be auto-expanded, and the ones where it cannot be.
/// </summary>
public class FieldPathsTests
{
    /// <summary>
    /// A dataset shaped like a typical data model: a record referencing a boxed value, a wrapper type
    /// with one field, and a type whose key names a nested field.
    /// </summary>
    private static SimpleHollowDataset Dataset()
    {
        // The single-field wrapper Hollow generates for a boxed int.
        HollowObjectSchema integer = new("Integer", 1);
        integer.AddField("value", FieldType.Int);

        HollowObjectSchema str = new("String", 1);
        str.AddField("value", FieldType.String);

        HollowObjectSchema country = new("Country", 2, new PrimaryKey("Country", "code"));
        country.AddField("code", FieldType.Reference, "String");
        country.AddField("name", FieldType.Reference, "String");

        HollowObjectSchema movie = new("Movie", 3);
        movie.AddField("id", FieldType.Reference, "Integer");
        movie.AddField("title", FieldType.Reference, "String");
        movie.AddField("country", FieldType.Reference, "Country");

        // Two fields and no primary key, so a path stopping here cannot be expanded.
        HollowObjectSchema ambiguous = new("Ambiguous", 2);
        ambiguous.AddField("left", FieldType.Int);
        ambiguous.AddField("right", FieldType.Int);

        HollowObjectSchema holder = new("Holder", 1);
        holder.AddField("ambiguous", FieldType.Reference, "Ambiguous");

        HollowListSchema movies = new("ListOfMovie", "Movie");
        HollowMapSchema ratings = new("MapOfStringToInteger", "String", "Integer");

        return new SimpleHollowDataset([integer, str, country, movie, ambiguous, holder, movies, ratings]);
    }

    [Fact]
    public void AValueFieldBindsToItsOwnPosition()
    {
        Assert.Equal([0], PrimaryKey.GetFieldPathIndex(Dataset(), "Integer", "value"));
    }

    /// <summary>
    /// A path that stops at a single-field record keeps going into it, which is what makes
    /// <c>movie.id</c> mean <c>movie.id.value</c>.
    /// </summary>
    [Fact]
    public void APathStoppingAtASingleFieldRecordIsExpanded()
    {
        SimpleHollowDataset dataset = Dataset();

        Assert.Equal([0, 0], PrimaryKey.GetFieldPathIndex(dataset, "Movie", "id"));
        Assert.Equal(["id", "value"], PrimaryKey.GetCompleteFieldPathParts(dataset, "Movie", "id"));
        Assert.Equal(FieldType.Int, PrimaryKey.GetFieldType(dataset, "Movie", "id"));
    }

    [Fact]
    public void AnAlreadyCompletePathIsLeftAlone()
    {
        SimpleHollowDataset dataset = Dataset();

        Assert.Equal([0, 0], PrimaryKey.GetFieldPathIndex(dataset, "Movie", "id.value"));
        Assert.Equal(FieldType.String, PrimaryKey.GetFieldType(dataset, "Movie", "title.value"));
    }

    /// <summary>
    /// A record with several fields but a single-field primary key expands through that key, so
    /// <c>movie.country</c> means the country's code.
    /// </summary>
    [Fact]
    public void APathStoppingAtARecordWithASingleFieldKeyExpandsThroughTheKey()
    {
        SimpleHollowDataset dataset = Dataset();

        Assert.Equal([2, 0, 0], PrimaryKey.GetFieldPathIndex(dataset, "Movie", "country"));
        Assert.Equal(
            ["country", "code", "value"], PrimaryKey.GetCompleteFieldPathParts(dataset, "Movie", "country"));
        Assert.Equal(FieldType.String, PrimaryKey.GetFieldType(dataset, "Movie", "country"));
    }

    /// <summary>
    /// A trailing <c>!</c> turns auto-expansion off, which is how a key names a reference rather than
    /// the value behind it.
    /// </summary>
    [Fact]
    public void ATrailingBangSuppressesExpansion()
    {
        SimpleHollowDataset dataset = Dataset();

        Assert.Equal([0], PrimaryKey.GetFieldPathIndex(dataset, "Movie", "id!"));
        Assert.Equal(FieldType.Reference, PrimaryKey.GetFieldType(dataset, "Movie", "id!"));

        FieldPath<ObjectFieldSegment> path = FieldPaths.CreateFieldPathForPrimaryKey(dataset, "Movie", "id!");
        Assert.True(path.NoAutoExpand);
        Assert.Equal("id!", path.ToString());
    }

    [Fact]
    public void APathThatCannotBeExpandedIsRejected()
    {
        FieldPathException e = Assert.Throws<FieldPathException>(
            () => PrimaryKey.GetFieldPathIndex(Dataset(), "Holder", "ambiguous"));

        Assert.Equal(FieldPathError.NotExpandable, e.Error);
    }

    [Fact]
    public void AFieldThatDoesNotExistIsRejected()
    {
        FieldPathException e = Assert.Throws<FieldPathException>(
            () => PrimaryKey.GetFieldPathIndex(Dataset(), "Movie", "director"));

        Assert.Equal(FieldPathError.NotFound, e.Error);
        Assert.Equal(0, e.SegmentIndex);
    }

    /// <summary>
    /// A path can only run out of types at a segment that is not the last one, because the last segment
    /// is allowed to name a value field.
    /// </summary>
    [Fact]
    public void APathContinuingPastAValueFieldIsRejected()
    {
        FieldPathException e = Assert.Throws<FieldPathException>(
            () => PrimaryKey.GetFieldPathIndex(Dataset(), "Integer", "value.nonsense"));

        Assert.Equal(FieldPathError.NotTraversable, e.Error);
    }

    [Fact]
    public void APathRootedInAnAbsentTypeIsRejected()
    {
        FieldPathException e = Assert.Throws<FieldPathException>(
            () => PrimaryKey.GetFieldPathIndex(Dataset(), "NoSuchType", "id"));

        Assert.Equal(FieldPathError.NotBindable, e.Error);
        Assert.Equal("NoSuchType", e.RootType);
    }

    /// <summary>
    /// A primary key path may not traverse collections, because a key has to identify exactly one
    /// record.
    /// </summary>
    [Fact]
    public void APrimaryKeyPathCannotTraverseACollection()
    {
        FieldPathException e = Assert.Throws<FieldPathException>(
            () => PrimaryKey.GetFieldPathIndex(Dataset(), "ListOfMovie", "element"));

        Assert.Equal(FieldPathError.NotTraversable, e.Error);
    }

    [Fact]
    public void AHashIndexPathTraversesCollections()
    {
        SimpleHollowDataset dataset = Dataset();

        FieldPath<FieldSegment> listPath =
            FieldPaths.CreateFieldPathForHashIndex(dataset, "ListOfMovie", "element.id.value");
        Assert.Equal(["element", "id", "value"], listPath.Segments.Select(segment => segment.Name));

        FieldPath<FieldSegment> mapPath =
            FieldPaths.CreateFieldPathForHashIndex(dataset, "MapOfStringToInteger", "key.value");
        Assert.Equal(["key", "value"], mapPath.Segments.Select(segment => segment.Name));
    }

    [Fact]
    public void AHashIndexPathDoesNotAutoExpand()
    {
        FieldPath<FieldSegment> path =
            FieldPaths.CreateFieldPathForHashIndex(Dataset(), "Movie", "id");

        // No expansion, so the path stops at the reference itself.
        Assert.Equal(["id"], path.Segments.Select(segment => segment.Name));
    }

    /// <summary>
    /// A prefix index path can be told it must already be complete, which turns a path that would
    /// otherwise be expanded into an error.
    /// </summary>
    [Fact]
    public void APrefixIndexPathCanRequireAFullPath()
    {
        FieldPathException e = Assert.Throws<FieldPathException>(
            () => FieldPaths.CreateFieldPathForPrefixIndex(Dataset(), "Movie", "id", autoExpand: false));

        Assert.Equal(FieldPathError.NotFull, e.Error);
        Assert.Equal("Integer", Assert.Single(e.FieldSegments).TypeName);
    }

    [Fact]
    public void SegmentsCarryTheirEnclosingSchemaAndFieldType()
    {
        SimpleHollowDataset dataset = Dataset();

        FieldPath<ObjectFieldSegment> path =
            FieldPaths.CreateFieldPathForPrimaryKey(dataset, "Movie", "country");

        Assert.Equal(3, path.Segments.Count);
        Assert.Equal("Movie", path.Segments[0].EnclosingSchema.Name);
        Assert.Equal(FieldType.Reference, path.Segments[0].Type);
        Assert.Equal("Country", path.Segments[1].EnclosingSchema.Name);
        Assert.Equal("String", path.Segments[2].EnclosingSchema.Name);
        Assert.Equal(FieldType.String, path.Segments[2].Type);
        Assert.Null(path.Segments[2].TypeName);

        Assert.Equal("country.code.value", path.ToString());
    }

    [Fact]
    public void EquivalentPathsCompareEqual()
    {
        SimpleHollowDataset dataset = Dataset();

        FieldPath<ObjectFieldSegment> expanded =
            FieldPaths.CreateFieldPathForPrimaryKey(dataset, "Movie", "id");
        FieldPath<ObjectFieldSegment> spelledOut =
            FieldPaths.CreateFieldPathForPrimaryKey(dataset, "Movie", "id.value");

        Assert.Equal(expanded, spelledOut);
        Assert.Equal(expanded.GetHashCode(), spelledOut.GetHashCode());

        Assert.NotEqual(expanded, FieldPaths.CreateFieldPathForPrimaryKey(dataset, "Movie", "title"));
    }

    /// <summary>
    /// A key with no field paths falls back to the one the schema declares.
    /// </summary>
    [Fact]
    public void CreateFallsBackToTheSchemasDeclaredKey()
    {
        SimpleHollowDataset dataset = Dataset();

        Assert.Equal(new PrimaryKey("Country", "code"), PrimaryKey.Create(dataset, "Country"));
        Assert.Equal(new PrimaryKey("Movie", "id"), PrimaryKey.Create(dataset, "Movie", "id"));

        // Movie declares no key of its own.
        Assert.Null(PrimaryKey.Create(dataset, "Movie"));
    }
}
