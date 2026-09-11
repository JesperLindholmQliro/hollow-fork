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

using Hollow.Api.Error;
using Hollow.Core;
using Hollow.Core.Index.Key;
using Hollow.Core.Read;
using Hollow.Core.Read.Filter;
using Hollow.Core.Schema;
using Hollow.Core.Write;

namespace Hollow.Tests.Core.Schema;

/// <summary>
/// Ported from <c>HollowObjectSchemaTest</c>, <c>HollowSetSchemaTest</c>, <c>HollowMapSchemaTest</c>
/// and <c>HollowSchemaTest</c>.
/// </summary>
public class SchemaTests
{
    [Fact]
    public void ObjectSchemaTracksItsFields()
    {
        HollowObjectSchema schema = new("Movie", 3);
        Assert.Equal(0, schema.AddField("id", FieldType.Int));
        Assert.Equal(1, schema.AddField("title", FieldType.String));
        Assert.Equal(2, schema.AddField("country", FieldType.Reference, "Country"));

        Assert.Equal(3, schema.FieldCount);
        Assert.Equal(SchemaType.Object, schema.SchemaType);

        Assert.Equal(1, schema.GetPosition("title"));
        Assert.Equal(-1, schema.GetPosition("nope"));

        Assert.Equal(FieldType.String, schema.GetFieldType("title"));
        Assert.Null(schema.GetFieldType("nope"));

        Assert.Equal("Country", schema.GetReferencedType(2));
        Assert.Null(schema.GetReferencedType(0));
    }

    [Fact]
    public void AddingAReferenceFieldWithoutATargetThrows()
    {
        HollowObjectSchema schema = new("Movie", 1);

        Assert.Throws<ArgumentException>(() => schema.AddField("country", FieldType.Reference));
    }

    [Fact]
    public void SchemaNamesCannotBeNullOrEmpty()
    {
        Assert.Throws<ArgumentException>(() => new HollowObjectSchema(null!, 1));
        Assert.Throws<ArgumentException>(() => new HollowObjectSchema(string.Empty, 1));
    }

    [Fact]
    public void FindCommonSchemaKeepsOnlySharedFields()
    {
        HollowObjectSchema first = new("Movie", 3);
        first.AddField("id", FieldType.Int);
        first.AddField("title", FieldType.String);
        first.AddField("year", FieldType.Int);

        HollowObjectSchema second = new("Movie", 2);
        second.AddField("id", FieldType.Int);
        second.AddField("year", FieldType.Int);

        HollowObjectSchema common = first.FindCommonSchema(second);

        Assert.Equal(2, common.FieldCount);
        Assert.Equal("id", common.GetFieldName(0));
        Assert.Equal("year", common.GetFieldName(1));
    }

    [Fact]
    public void FindCommonSchemaRejectsFieldsWithDifferentTypes()
    {
        HollowObjectSchema first = new("Movie", 1);
        first.AddField("id", FieldType.Int);

        HollowObjectSchema second = new("Movie", 1);
        second.AddField("id", FieldType.Long);

        IncompatibleSchemaException exception =
            Assert.Throws<IncompatibleSchemaException>(() => first.FindCommonSchema(second));

        Assert.Equal("Movie", exception.TypeName);
        Assert.Equal("id", exception.FieldName);
        Assert.Equal("int", exception.FieldType);
        Assert.Equal("long", exception.OtherFieldType);
    }

    [Fact]
    public void FindUnionSchemaKeepsEveryField()
    {
        HollowObjectSchema first = new("Movie", 2);
        first.AddField("id", FieldType.Int);
        first.AddField("title", FieldType.String);

        HollowObjectSchema second = new("Movie", 2);
        second.AddField("id", FieldType.Int);
        second.AddField("year", FieldType.Int);

        HollowObjectSchema union = first.FindUnionSchema(second);

        Assert.Equal(3, union.FieldCount);
        Assert.Equal(["id", "title", "year"], Enumerable.Range(0, 3).Select(union.GetFieldName));
    }

    [Fact]
    public void SchemasOfDifferentTypesCannotBeCombined()
    {
        HollowObjectSchema movie = new("Movie", 1);
        movie.AddField("id", FieldType.Int);

        HollowObjectSchema show = new("Show", 1);
        show.AddField("id", FieldType.Int);

        Assert.Throws<ArgumentException>(() => movie.FindCommonSchema(show));
        Assert.Throws<ArgumentException>(() => movie.FindUnionSchema(show));
    }

    [Fact]
    public void FilterSchemaDropsExcludedFields()
    {
        HollowObjectSchema schema = new("Movie", 3);
        schema.AddField("id", FieldType.Int);
        schema.AddField("title", FieldType.String);
        schema.AddField("year", FieldType.Int);

        ITypeFilter filter = TypeFilter.Include(
            ["Movie"],
            new Dictionary<string, IReadOnlySet<string>> { ["Movie"] = new HashSet<string> { "id", "year" } });

        HollowObjectSchema filtered = schema.FilterSchema(filter);

        Assert.Equal(2, filtered.FieldCount);
        Assert.Equal(["id", "year"], Enumerable.Range(0, 2).Select(filtered.GetFieldName));
    }

    [Fact]
    public void ObjectSchemaEquality()
    {
        HollowObjectSchema first = new("Movie", 2, "id");
        first.AddField("id", FieldType.Int);
        first.AddField("title", FieldType.String);

        HollowObjectSchema same = new("Movie", 2, "id");
        same.AddField("id", FieldType.Int);
        same.AddField("title", FieldType.String);

        HollowObjectSchema differentKey = new("Movie", 2);
        differentKey.AddField("id", FieldType.Int);
        differentKey.AddField("title", FieldType.String);

        Assert.Equal(first, same);
        Assert.Equal(first.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(first, differentKey);
    }

    [Fact]
    public void ObjectSchemaToString()
    {
        HollowObjectSchema schema = new("Movie", 2, "id");
        schema.AddField("id", FieldType.Int);
        schema.AddField("country", FieldType.Reference, "Country");

        Assert.Equal(
            "Movie @PrimaryKey(id) {\n\tint id;\n\tCountry country;\n}",
            schema.ToString());
    }

    [Fact]
    public void CollectionSchemaToString()
    {
        Assert.Equal("Movies List<Movie>;", new HollowListSchema("Movies", "Movie").ToString());
        Assert.Equal("Movies Set<Movie>;", new HollowSetSchema("Movies", "Movie").ToString());
        Assert.Equal(
            "Movies Set<Movie> @HashKey(id);",
            new HollowSetSchema("Movies", "Movie", "id").ToString());
        Assert.Equal(
            "MovieMap Map<Movie,Actor>;",
            new HollowMapSchema("MovieMap", "Movie", "Actor").ToString());
        Assert.Equal(
            "MovieMap Map<Movie,Actor> @HashKey(id, name);",
            new HollowMapSchema("MovieMap", "Movie", "Actor", "id", "name").ToString());
    }

    [Fact]
    public void WithoutKeysStripsHashKeys()
    {
        HollowSetSchema keyedSet = new("Movies", "Movie", "id");
        HollowMapSchema keyedMap = new("MovieMap", "Movie", "Actor", "id");
        HollowListSchema list = new("Movies", "Movie");

        Assert.Null(Assert.IsType<HollowSetSchema>(HollowSchema.WithoutKeys(keyedSet)).HashKey);
        Assert.Null(Assert.IsType<HollowMapSchema>(HollowSchema.WithoutKeys(keyedMap)).HashKey);
        Assert.Same(list, HollowSchema.WithoutKeys(list));
    }

    [Theory]
    [MemberData(nameof(RoundTrippableSchemas))]
    public void SchemasRoundTripThroughTheirSerialisedForm(HollowSchema schema)
    {
        using MemoryStream stream = new();
        using (HollowBlobOutput output = HollowBlobOutput.Serial(stream, leaveOpen: true))
        {
            schema.WriteTo(output);
        }

        using HollowBlobInput input = HollowBlobInput.Serial(stream.ToArray());
        HollowSchema read = HollowSchema.ReadFrom(input);

        Assert.Equal(schema, read);
        Assert.Equal(schema.ToString(), read.ToString());
    }

    public static TheoryData<HollowSchema> RoundTrippableSchemas()
    {
        HollowObjectSchema plainObject = new("Movie", 6);
        plainObject.AddField("id", FieldType.Int);
        plainObject.AddField("title", FieldType.String);
        plainObject.AddField("rating", FieldType.Double);
        plainObject.AddField("released", FieldType.Boolean);
        plainObject.AddField("poster", FieldType.Bytes);
        plainObject.AddField("country", FieldType.Reference, "Country");

        HollowObjectSchema keyedObject = new("Country", 2, "id", "name");
        keyedObject.AddField("id", FieldType.Long);
        keyedObject.AddField("name", FieldType.String);

        return
        [
            plainObject,
            keyedObject,
            new HollowListSchema("Movies", "Movie"),
            new HollowSetSchema("MovieSet", "Movie"),
            new HollowSetSchema("KeyedMovieSet", "Movie", "id"),
            new HollowMapSchema("MovieMap", "Movie", "Country"),
            new HollowMapSchema("KeyedMovieMap", "Movie", "Country", "id", "title"),
        ];
    }

    [Fact]
    public void FieldTypeMetadataMatchesTheWireFormat()
    {
        Assert.Equal(1, FieldType.Boolean.GetFixedLength());
        Assert.Equal(4, FieldType.Float.GetFixedLength());
        Assert.Equal(8, FieldType.Double.GetFixedLength());
        Assert.Equal(-1, FieldType.Int.GetFixedLength());

        Assert.True(FieldType.String.IsVariableLength());
        Assert.True(FieldType.Bytes.IsVariableLength());
        Assert.False(FieldType.Int.IsVariableLength());

        foreach (FieldType fieldType in Enum.GetValues<FieldType>())
        {
            Assert.Equal(fieldType, FieldTypeExtensions.ParseWireName(fieldType.ToWireName()));
            Assert.Equal(fieldType, FieldTypeExtensions.ParseWireName(fieldType.ToSchemaName()));
        }

        Assert.Throws<ArgumentException>(() => FieldTypeExtensions.ParseWireName("NOT_A_TYPE"));
        Assert.False(FieldTypeExtensions.TryParseWireName("NOT_A_TYPE", out _));
    }

    [Fact]
    public void SchemaTypeIdsMatchTheWireFormat()
    {
        Assert.Equal(0, SchemaType.Object.GetTypeId());
        Assert.Equal(1, SchemaType.Set.GetTypeId());
        Assert.Equal(2, SchemaType.List.GetTypeId());
        Assert.Equal(3, SchemaType.Map.GetTypeId());

        Assert.Equal(6, SchemaType.Object.GetTypeIdWithPrimaryKey());
        Assert.Equal(4, SchemaType.Set.GetTypeIdWithPrimaryKey());
        Assert.Equal(5, SchemaType.Map.GetTypeIdWithPrimaryKey());

        foreach (int typeId in (int[])[0, 1, 2, 3, 4, 5, 6])
        {
            SchemaType schemaType = SchemaTypeExtensions.FromTypeId(typeId);
            Assert.Equal(
                typeId,
                SchemaTypeExtensions.HasKey(typeId) ? schemaType.GetTypeIdWithPrimaryKey() : schemaType.GetTypeId());
        }

        Assert.Throws<ArgumentException>(() => SchemaTypeExtensions.FromTypeId(7));
    }

    [Fact]
    public void PrimaryKeyEquality()
    {
        PrimaryKey first = new("Movie", "id", "title");
        PrimaryKey same = new("Movie", "id", "title");
        PrimaryKey differentOrder = new("Movie", "title", "id");
        PrimaryKey differentType = new("Show", "id", "title");

        Assert.Equal(first, same);
        Assert.Equal(first.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(first, differentOrder);
        Assert.NotEqual(first, differentType);

        Assert.Equal(2, first.FieldCount);
        Assert.Equal("title", first.GetFieldPath(1));
        Assert.Equal("PrimaryKey [type=Movie, fieldPaths=[id, title]]", first.ToString());
    }

    [Fact]
    public void PrimaryKeyRequiresAtLeastOneFieldPath() =>
        Assert.Throws<ArgumentException>(() => new PrimaryKey("Movie"));

    [Fact]
    public void SimpleHollowDatasetLooksUpSchemasByName()
    {
        HollowObjectSchema movie = new("Movie", 1);
        movie.AddField("id", FieldType.Int);
        HollowListSchema movies = new("Movies", "Movie");

        SimpleHollowDataset dataset = new([movie, movies]);

        Assert.Equal(2, dataset.Schemas.Count);
        Assert.Same(movie, dataset.GetSchema("Movie"));
        Assert.Null(dataset.GetSchema("Nope"));
        Assert.Throws<SchemaNotFoundException>(() => dataset.GetNonNullSchema("Nope"));

        Assert.True(dataset.HasIdenticalSchemas(new SimpleHollowDataset([movie, movies])));
        Assert.False(dataset.HasIdenticalSchemas(new SimpleHollowDataset([movie])));
    }
}
