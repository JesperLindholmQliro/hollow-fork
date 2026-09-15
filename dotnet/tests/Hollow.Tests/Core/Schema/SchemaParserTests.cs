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

using System.Reflection;
using Hollow.Core.Index.Key;
using Hollow.Core.Schema;

namespace Hollow.Tests.Core.Schema;

/// <summary>
/// Ported from <c>HollowSchemaParserTest</c>, keeping its schema text verbatim, and extended with the
/// two schema files the Java repository holds — <c>schema1.txt</c> and
/// <c>hollow_code_gen_test.schema</c>, both copied into this project's resources — so that the parser
/// is held to text written for Java rather than to text written to suit it.
/// </summary>
public class SchemaParserTests
{
    /// <summary>Java's <c>parsesObjectSchema</c>.</summary>
    [Fact]
    public void AnObjectSchemaIsRead()
    {
        HollowObjectSchema schema = (HollowObjectSchema)HollowSchemaParser.Parse(
            """
            /* This is a comment
               consisting of multiple lines */
             TypeA {
                int a1;
                	string a2; //This is a comment
                String a3;
            }
            """);

        Assert.Equal("TypeA", schema.Name);
        Assert.Equal(3, schema.FieldCount);

        Assert.Equal(FieldType.Int, schema.GetFieldType(0));
        Assert.Equal("a1", schema.GetFieldName(0));

        Assert.Equal(FieldType.String, schema.GetFieldType(1));
        Assert.Equal("a2", schema.GetFieldName(1));

        // Anything that is not one of the built-in names is a reference to the type it names — which
        // is why the capitalised String is a reference and the lower-case string is a field.
        Assert.Equal(FieldType.Reference, schema.GetFieldType(2));
        Assert.Equal("String", schema.GetReferencedType(2));
        Assert.Equal("a3", schema.GetFieldName(2));

        Assert.Null(schema.PrimaryKey);
        Assert.Equal(schema, HollowSchemaParser.Parse(schema.ToString()!));
    }

    /// <summary>Java's <c>parsesObjectSchemaWithKey</c>.</summary>
    [Fact]
    public void APrimaryKeyIsRead()
    {
        HollowObjectSchema schema = (HollowObjectSchema)HollowSchemaParser.Parse(
            """
             TypeA @PrimaryKey(a1) {
                int a1;
                string a2;
                String a3;
            }
            """);

        Assert.Equal(new PrimaryKey("TypeA", "a1"), schema.PrimaryKey);
        Assert.Equal(schema, HollowSchemaParser.Parse(schema.ToString()!));
    }

    /// <summary>Java's <c>parsesObjectSchemaMultipleWithKey</c>.</summary>
    [Fact]
    public void APrimaryKeyOfSeveralPathsIsRead()
    {
        HollowObjectSchema schema = (HollowObjectSchema)HollowSchemaParser.Parse(
            """
             TypeA @PrimaryKey(a1, a3.value) {
                int a1;
                string a2;
                String a3;
            }
            """);

        // A dotted path is one token, which is why the tokenizer treats '.' as a word character.
        Assert.Equal(new PrimaryKey("TypeA", "a1", "a3.value"), schema.PrimaryKey);
        Assert.Equal(schema, HollowSchemaParser.Parse(schema.ToString()!));
    }

    [Fact]
    public void EveryBuiltInFieldTypeIsRead()
    {
        HollowObjectSchema schema = (HollowObjectSchema)HollowSchemaParser.Parse(
            """
            Everything {
                int i; long l; float f; double d;
                boolean b; string s; bytes y; decimal m;
            }
            """);

        // decimal is this port's own field type. Without it in the table a schema this port wrote
        // would read back with its decimal fields turned into references to a type called "decimal".
        Assert.Equal(
            new[]
            {
                FieldType.Int, FieldType.Long, FieldType.Float, FieldType.Double,
                FieldType.Boolean, FieldType.String, FieldType.Bytes, FieldType.Decimal,
            },
            Enumerable.Range(0, schema.FieldCount).Select(schema.GetFieldType));

        Assert.Equal(schema, HollowSchemaParser.Parse(schema.ToString()!));
    }

    /// <summary>Java's <c>parsesListSchema</c>.</summary>
    [Fact]
    public void AListSchemaIsRead()
    {
        HollowListSchema schema = (HollowListSchema)HollowSchemaParser.Parse("ListOfTypeA List<TypeA>;\n");

        Assert.Equal("ListOfTypeA", schema.Name);
        Assert.Equal("TypeA", schema.ElementType);
        Assert.Equal(schema, HollowSchemaParser.Parse(schema.ToString()!));
    }

    /// <summary>Java's <c>parsesSetSchema</c>.</summary>
    [Fact]
    public void ASetSchemaIsRead()
    {
        HollowSetSchema schema = (HollowSetSchema)HollowSchemaParser.Parse("SetOfTypeA Set<TypeA>;\n");

        Assert.Equal("SetOfTypeA", schema.Name);
        Assert.Equal("TypeA", schema.ElementType);
        Assert.Null(schema.HashKey);
        Assert.Equal(schema, HollowSchemaParser.Parse(schema.ToString()!));
    }

    /// <summary>Java's <c>parsesSetSchemaWithKey</c>.</summary>
    [Fact]
    public void ASetsHashKeyIsRead()
    {
        HollowSetSchema schema =
            (HollowSetSchema)HollowSchemaParser.Parse("SetOfTypeA Set<TypeA> @HashKey(id.value);\n");

        Assert.Equal(new PrimaryKey("TypeA", "id.value"), schema.HashKey);
        Assert.Equal(schema, HollowSchemaParser.Parse(schema.ToString()!));
    }

    /// <summary>Java's <c>parsesSetSchemaWithMultiFieldKey</c>.</summary>
    [Fact]
    public void ASetsHashKeyOfSeveralPathsIsRead()
    {
        HollowSetSchema schema = (HollowSetSchema)HollowSchemaParser.Parse(
            "SetOfTypeA Set<TypeA> @HashKey(id.value, region.country.id, key);\n");

        Assert.Equal(
            new PrimaryKey("TypeA", "id.value", "region.country.id", "key"), schema.HashKey);
        Assert.Equal(schema, HollowSchemaParser.Parse(schema.ToString()!));
    }

    /// <summary>Java's <c>parsesMapSchema</c>.</summary>
    [Fact]
    public void AMapSchemaIsRead()
    {
        // A space after the comma, which is what Java's test writes and what this port's ToString does
        // not — so the two forms have to parse the same.
        HollowMapSchema schema =
            (HollowMapSchema)HollowSchemaParser.Parse("MapOfStringToTypeA Map<String, TypeA>;\n");

        Assert.Equal("MapOfStringToTypeA", schema.Name);
        Assert.Equal("String", schema.KeyType);
        Assert.Equal("TypeA", schema.ValueType);
        Assert.Null(schema.HashKey);
        Assert.Equal(schema, HollowSchemaParser.Parse(schema.ToString()!));
    }

    /// <summary>Java's <c>parsesMapSchemaWithPrimaryKey</c>.</summary>
    [Fact]
    public void AMapsHashKeyIsRead()
    {
        HollowMapSchema schema = (HollowMapSchema)HollowSchemaParser.Parse(
            "MapOfStringToTypeA Map<String, TypeA> @HashKey(value);\n");

        // The hash key belongs to the key type, not to the map.
        Assert.Equal(new PrimaryKey("String", "value"), schema.HashKey);
        Assert.Equal(schema, HollowSchemaParser.Parse(schema.ToString()!));
    }

    /// <summary>Java's <c>parsesMapSchemaWithMultiFieldPrimaryKey</c>.</summary>
    [Fact]
    public void AMapsHashKeyOfSeveralPathsIsRead()
    {
        HollowMapSchema schema = (HollowMapSchema)HollowSchemaParser.Parse(
            "MapOfStringToTypeA Map<String, TypeA> @HashKey(id.value, region.country.id, key);\n");

        Assert.Equal(
            new PrimaryKey("String", "id.value", "region.country.id", "key"), schema.HashKey);
        Assert.Equal(schema, HollowSchemaParser.Parse(schema.ToString()!));
    }

    /// <summary>Java's <c>parsesManySchemas</c>.</summary>
    [Fact]
    public void SeveralSchemasAreReadInOrder()
    {
        IReadOnlyList<HollowSchema> schemas = HollowSchemaParser.ParseCollection(
            """
            /* This is a comment
               consisting of multiple lines */
             TypeA {
                int a1;
                	string a2; //This is a comment
                String a3;
            }

            MapOfStringToTypeA Map<String, TypeA>;
            ListOfTypeA List<TypeA>;
            TypeB { float b1; double b2; boolean b3; }
            """);

        Assert.Equal(4, schemas.Count);
        Assert.Equal(["TypeA", "MapOfStringToTypeA", "ListOfTypeA", "TypeB"], schemas.Select(s => s.Name));
        Assert.Equal(
            [SchemaType.Object, SchemaType.Map, SchemaType.List, SchemaType.Object],
            schemas.Select(s => s.SchemaType));
    }

    /// <summary>
    /// Java's <c>testParseCollectionOfSchemas_reader</c>, over the same <c>schema1.txt</c>.
    /// </summary>
    [Fact]
    public void TheRepositorysMinionSchemaIsRead()
    {
        using TextReader reader = OpenResource("schema1.txt");
        IReadOnlyList<HollowSchema> schemas = HollowSchemaParser.ParseCollection(reader);

        Assert.Equal(2, schemas.Count);
        Assert.Equal("Minion", schemas[0].Name);
        Assert.Equal("String", schemas[1].Name);

        HollowObjectSchema minion = (HollowObjectSchema)schemas[0];
        Assert.Equal(new PrimaryKey("Minion", "minionId"), minion.PrimaryKey);
        Assert.Equal(FieldType.Long, minion.GetFieldType(0));
        Assert.Equal(FieldType.Reference, minion.GetFieldType(1));
        Assert.Equal("String", minion.GetReferencedType(1));
    }

    /// <summary>
    /// The other schema file the Java repository holds, which the code generator's tests read.
    /// </summary>
    /// <remarks>
    /// It is the awkward one on purpose: a licence-style block comment before the first type, tabs for
    /// indentation, a comment between two declarations, and every kind of schema in one file.
    /// </remarks>
    [Fact]
    public void TheRepositorysCodeGeneratorSchemaIsRead()
    {
        using TextReader reader = OpenResource("hollow_code_gen_test.schema");
        IReadOnlyList<HollowSchema> schemas = HollowSchemaParser.ParseCollection(reader);

        Assert.Equal(
            [
                "TopLevelObject", "ListOfObject1", "SetOfObject1", "MapOfStringToObject1", "Object1",
                "String", "ListOfObject2", "SetOfObject2", "MapOfStringToObject2", "MapOfObject2ToString",
            ],
            schemas.Select(schema => schema.Name));

        HollowObjectSchema top = (HollowObjectSchema)schemas[0];
        Assert.Equal(10, top.FieldCount);

        // Every one of them a reference, the last included: `String validStringField` points at the
        // String type the file goes on to declare, not at a string field. Telling those two apart is
        // what the file is for.
        Assert.All(
            Enumerable.Range(0, top.FieldCount),
            i => Assert.Equal(FieldType.Reference, top.GetFieldType(i)));
        Assert.Equal("MapOfObject2ToString", top.GetReferencedType(7));
        Assert.Equal("String", top.GetReferencedType(9));

        HollowObjectSchema stringType = (HollowObjectSchema)schemas[5];
        Assert.Equal("value", stringType.GetFieldName(0));
        Assert.Equal(FieldType.String, stringType.GetFieldType(0));

        HollowMapSchema mapOfObject2 = (HollowMapSchema)schemas[9];
        Assert.Equal("Object2", mapOfObject2.KeyType);
        Assert.Equal("String", mapOfObject2.ValueType);
    }

    /// <summary>
    /// The schema text <c>HollowDatasetTest</c> writes on one line, which is how a test that only
    /// cares about the shape of a model tends to write it.
    /// </summary>
    [Fact]
    public void AWholeModelOnOneLineIsRead()
    {
        IReadOnlyList<HollowSchema> schemas = HollowSchemaParser.ParseCollection(
            "TypeA { int a1; string a2; TypeB a3; MapOfTypeB a4; }  MapOfTypeB Map<TypeB, TypeB>; "
            + "TypeB { float b1; bytes b2; }");

        Assert.Equal(["TypeA", "MapOfTypeB", "TypeB"], schemas.Select(schema => schema.Name));
    }

    [Fact]
    public void CommentsAreSkipped()
    {
        IReadOnlyList<HollowSchema> schemas = HollowSchemaParser.ParseCollection(
            """
            // The film itself.
            Movie {
                int id;      // its identifier
                /* and what
                   it is called */
                string title;
            }
            // Nothing after this.
            """);

        HollowObjectSchema movie = (HollowObjectSchema)Assert.Single(schemas);
        Assert.Equal(2, movie.FieldCount);
        Assert.Equal("title", movie.GetFieldName(1));
    }

    [Fact]
    public void NothingAtAllReadsAsNoSchemas()
    {
        Assert.Empty(HollowSchemaParser.ParseCollection("   // just a comment\n"));
        Assert.Empty(HollowSchemaParser.ParseCollection(string.Empty));
    }

    [Fact]
    public void TextHoldingNoSchemaIsRefusedRatherThanAnsweredWithNothing()
    {
        // ParseCollection answers "none"; Parse was asked for one and has to say it found none.
        Assert.Throws<FormatException>(() => HollowSchemaParser.Parse("// nothing here"));
    }

    [Theory]
    [InlineData("Movie { int id; }")]
    [InlineData("Movie @PrimaryKey(id, country.code) { int id; Country country; }")]
    [InlineData("Everything { int i; long l; float f; double d; boolean b; string s; bytes y; decimal m; }")]
    [InlineData("ListOfMovie List<Movie>;")]
    [InlineData("SetOfMovie Set<Movie>;")]
    [InlineData("SetOfMovie Set<Movie> @HashKey(id);")]
    [InlineData("MapOfStringToMovie Map<String,Movie>;")]
    [InlineData("MapOfMovieToInteger Map<Movie,Integer> @HashKey(id, country.code);")]
    public void WhatIsWrittenOutIsWhatIsReadBackIn(string source)
    {
        HollowSchema schema = HollowSchemaParser.Parse(source);
        HollowSchema again = HollowSchemaParser.Parse(schema.ToString()!);

        // Through the text and back twice: the second pass is what proves the text form is a fixed
        // point rather than merely parseable once.
        Assert.Equal(schema, again);
        Assert.Equal(schema.ToString(), again.ToString());
    }

    [Theory]
    [InlineData("schema1.txt")]
    [InlineData("hollow_code_gen_test.schema")]
    public void ARepositorySchemaFileSurvivesBeingWrittenOutAndReadBack(string resource)
    {
        using TextReader reader = OpenResource(resource);
        IReadOnlyList<HollowSchema> schemas = HollowSchemaParser.ParseCollection(reader);

        string written = string.Join("\n", schemas.Select(schema => schema.ToString()));

        Assert.Equal(schemas, HollowSchemaParser.ParseCollection(written));
    }

    [Theory]
    [InlineData("Movie", "the end of the text")]
    [InlineData("Movie {", "a field type")]
    [InlineData("Movie { int; }", "a field name")]
    [InlineData("Movie { int id }", "';'")]
    [InlineData("Movie Tuple<int>;", "'{', 'List', 'Set' or 'Map'")]
    [InlineData("ListOfMovie List Movie;", "'<'")]
    [InlineData("ListOfMovie List<Movie", "'>'")]
    [InlineData("ListOfMovie List<Movie>", "';'")]
    [InlineData("MapOfStringToMovie Map<String Movie>;", "','")]
    [InlineData("SetOfMovie Set<Movie> @PrimaryKey(id);", "@HashKey")]
    [InlineData("Movie @HashKey(id) { int id; }", "@PrimaryKey")]
    [InlineData("Movie @PrimaryKey id) { int id; }", "'('")]
    [InlineData("Movie @PrimaryKey(id country) { int id; }", "',' or ')'")]
    public void BadSyntaxSaysWhatItExpected(string source, string expected)
    {
        FormatException failure = Assert.Throws<FormatException>(() => HollowSchemaParser.Parse(source));

        Assert.Contains(expected, failure.Message, StringComparison.Ordinal);
    }

    private static TextReader OpenResource(string name) =>
        new StreamReader(
            Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"{name} is not embedded in the test assembly"));
}
