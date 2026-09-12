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
using Hollow.Core;
using Hollow.Core.Index;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Iterator;

namespace Hollow.Tests.Api.Codegen;

/// <summary>
/// The routes the generator writes so that an index can be handed a path through the model rather than
/// a string.
/// </summary>
/// <remarks>
/// What has to hold is that a route reads the way the model does, arrives where the compiler says it
/// does, and spells the same text the string form spelled — because everything under the indexes still
/// works in text, and a route that resolved to something else would be a silent change of meaning.
/// </remarks>
public class FieldPathTests
{
    /// <summary>The path value a generated route produces, reached by property name.</summary>
    private static FieldPath Walk(Assembly assembly, string root, params string[] steps)
    {
        object current = assembly.GetType($"{CodeGeneratorTests.GeneratedNamespace}.MoviesPaths")!
            .GetProperty(root)!
            .GetValue(null)!;

        foreach (string step in steps)
        {
            current = current.GetType().GetProperty(step)!.GetValue(current)!;
        }

        return (FieldPath)current;
    }

    /// <summary>
    /// A route spells the text the string form spelled, which is what makes it a drop-in for one.
    /// </summary>
    [Theory]
    [InlineData("Id", "Id")]
    [InlineData("Title|Value", "Title.value")]
    [InlineData("Studio|Name|Value", "Studio.Name.value")]
    [InlineData("Cast|Element", "Cast.element")]
    [InlineData("Cast|Element|Name|Value", "Cast.element.Name.value")]
    [InlineData("Tags|Element|Value", "Tags.element.value")]
    [InlineData("Roles|Key|Value", "Roles.key.value")]
    [InlineData("Roles|Value|Name|Value", "Roles.value.Name.value")]
    public void ARouteSpellsThePathItStandsFor(string steps, string expected)
    {
        Assembly assembly = CodeGeneratorTests.CompiledClient();
        FieldPath path = Walk(assembly, "Movie", steps.Split('|'));

        Assert.Equal(expected, path.Path);
        Assert.Equal("Movie", path.RootTypeName);
    }

    /// <summary>
    /// Where a route arrives is a type argument, so an index built from it types itself rather than
    /// taking the caller's word for it.
    /// </summary>
    [Theory]
    [InlineData("Id", typeof(int))]
    [InlineData("Year", typeof(int))]
    [InlineData("IsColour", typeof(bool))]
    [InlineData("Title|Value", typeof(string))]
    [InlineData("Studio|Name|Value", typeof(string))]

    // A nullable decimal or byte array is a reference to a shared record rather than a value stored in
    // the record, so the route reaches the value one step further along than the model reads.
    [InlineData("Budget|Value", typeof(decimal))]
    [InlineData("Poster|Value", typeof(byte[]))]
    public void ARouteCarriesWhatItArrivesAt(string steps, Type expected)
    {
        Assembly assembly = CodeGeneratorTests.CompiledClient();
        FieldPath path = Walk(assembly, "Movie", steps.Split('|'));

        Assert.Equal(expected, ValueTypeOf(path));
    }

    /// <summary>
    /// A route that stops at a record is a path to that record's wrapper, which is what a select path
    /// needs — and it is still a route, so it carries on.
    /// </summary>
    [Fact]
    public void ARouteThatStopsAtARecordIsAPathToItsWrapper()
    {
        Assembly assembly = CodeGeneratorTests.CompiledClient();

        Type actor = assembly.GetType($"{CodeGeneratorTests.GeneratedNamespace}.Actor")!;
        Type studio = assembly.GetType($"{CodeGeneratorTests.GeneratedNamespace}.Studio")!;

        Assert.Equal(actor, ValueTypeOf(Walk(assembly, "Movie", "Cast", "Element")));
        Assert.Equal(studio, ValueTypeOf(Walk(assembly, "Movie", "Studio")));

        // The step that arrived is the step that continues.
        Assert.Equal("Studio.Name.value", Walk(assembly, "Movie", "Studio", "Name", "Value").Path);
    }

    /// <summary>
    /// Every type a record can be reached from is a root, so a route need not start where the caller
    /// happens to be indexing.
    /// </summary>
    [Fact]
    public void EveryObjectTypeIsARoot()
    {
        Assembly assembly = CodeGeneratorTests.CompiledClient();
        Type roots = assembly.GetType($"{CodeGeneratorTests.GeneratedNamespace}.MoviesPaths")!;

        // Bytes among them because this port has no built-in wrapper for it, so it is a generated type
        // like any other — the same reason it appears in the client at all.
        Assert.Equal(
            ["Actor", "Bytes", "Movie", "Studio"],
            [.. roots.GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal)]);

        // A collection is not one: nothing holds a list without holding the record that references it.
        // Nor is a type read through one of Hollow's own scalar wrappers.
        Assert.Null(roots.GetProperty("ListOfActor"));
        Assert.Null(roots.GetProperty("MapOfStringToActor"));
        Assert.Null(roots.GetProperty("String"));
        Assert.Null(roots.GetProperty("Integer"));

        Assert.Equal("Name.value", Walk(assembly, "Actor", "Name", "Value").Path);
        Assert.Equal("Actor", Walk(assembly, "Actor", "Name", "Value").RootTypeName);
    }

    /// <summary>
    /// A root is the identity route, as <c>\Root.self</c> is in Swift — empty text, and the record
    /// itself at the end of it.
    /// </summary>
    [Fact]
    public void ARootIsTheEmptyRoute()
    {
        Assembly assembly = CodeGeneratorTests.CompiledClient();
        FieldPath root = Walk(assembly, "Movie");

        Assert.Equal(string.Empty, root.Path);
        Assert.Equal("Movie", root.RootTypeName);
        Assert.Equal("Movie", root.ToString());
        Assert.Equal(
            assembly.GetType($"{CodeGeneratorTests.GeneratedNamespace}.Movie"), ValueTypeOf(root));
    }

    /// <summary>
    /// The compiler stops a route being used on the wrong type, but not an index whose type name does
    /// not match the root it was built from — so that is checked when the path is handed over.
    /// </summary>
    [Fact]
    public void ARouteRefusesToIndexAnotherType()
    {
        FieldPath path = new FieldPath<string, int>("Movie", "Id");

        ArgumentException e = Assert.Throws<ArgumentException>(() => path.RequireRoot("Actor"));

        Assert.Contains("starts at Movie", e.Message, StringComparison.Ordinal);
        Assert.Contains("index Actor", e.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The point of all of it: an index built from a route finds the same records the string form
    /// found, because underneath it is the same path.
    /// </summary>
    /// <remarks>
    /// The shape tests above say a route spells what it should. This says the spelling resolves against
    /// a real dataset — which the schema, not the generator, has the last word on.
    /// </remarks>
    [Fact]
    public void AnIndexBuiltFromARouteFindsTheSameRecords()
    {
        Assembly assembly = CodeGeneratorTests.CompiledClient();
        HollowReadStateEngine readEngine = CodeGeneratorTests.TwoFilmsReadState();

        using HollowPrimaryKeyIndex byRoute = new(readEngine, Walk(assembly, "Movie", "Id"));
        using HollowPrimaryKeyIndex byText = new(readEngine, "Movie", "Id");

        Assert.Equal(byText.GetMatchingOrdinal(2), byRoute.GetMatchingOrdinal(2));
        Assert.NotEqual(HollowConstants.OrdinalNone, byRoute.GetMatchingOrdinal(2));

        // And one that crosses a reference, where the text is the part most easily got wrong.
        using HollowPrefixIndex titles = new(readEngine, Walk(assembly, "Movie", "Title", "Value"));

        Assert.Equal(
            [byText.GetMatchingOrdinal(1)],
            [.. Ordinals(titles.FindKeysWithPrefix("the matrix"))]);
    }

    private static IEnumerable<int> Ordinals(IHollowOrdinalIterator iterator)
    {
        for (int ordinal = iterator.Next();
            ordinal != IHollowOrdinalIterator.NoMoreOrdinals;
            ordinal = iterator.Next())
        {
            yield return ordinal;
        }
    }

    /// <summary>The <c>TValue</c> a generated route was declared with.</summary>
    private static Type ValueTypeOf(FieldPath path)
    {
        for (Type? type = path.GetType(); type is not null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(FieldPath<,>))
            {
                return type.GetGenericArguments()[1];
            }
        }

        throw new InvalidOperationException($"{path.GetType()} is not a typed field path");
    }
}
