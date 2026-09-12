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

using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Hollow.Api.Codegen;
using Hollow.SourceGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace Hollow.Tests.Api.Codegen;

/// <summary>
/// Runs the source generator over a model declared in source and hands back what it produced.
/// </summary>
/// <remarks>
/// The generator runs in the compiler, so the honest way to test it is to be the compiler: build a
/// compilation from the model source, drive the generator over it, then compile the result and load it.
/// What comes back is a real assembly holding the real generated client.
/// </remarks>
internal static class SourceGeneratorDriver
{
    /// <summary>
    /// The generator's added trees have to be parsed the way the model was, or the compilation ends up
    /// holding trees at two language versions and refuses to combine them.
    /// </summary>
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    /// <summary>
    /// Compiles <paramref name="modelSource"/> with the generator attached.
    /// </summary>
    /// <param name="modelSource">The model declaration.</param>
    /// <param name="expectGeneratedFiles">
    /// Whether the generator is expected to produce anything, which decides whether an assembly comes
    /// back or only the diagnostics.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The generator reported an error, or what it produced does not compile.
    /// </exception>
    internal static (Assembly Assembly, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<string> Files)
        Run(string modelSource, bool expectGeneratedFiles = true)
    {
        (Compilation output, IReadOnlyList<Diagnostic> diagnostics, IReadOnlyList<string> files) =
            Drive(modelSource);

        Refuse(diagnostics, "the generator reported:");

        return expectGeneratedFiles
            ? (Emit(output), diagnostics, files)
            : (typeof(SourceGeneratorDriver).Assembly, diagnostics, files);
    }

    /// <summary>
    /// Runs the generator over a model it cannot map, returning what it reported.
    /// </summary>
    internal static IReadOnlyList<Diagnostic> RunExpectingFailure(string modelSource) =>
        Drive(modelSource).Diagnostics;

    /// <summary>
    /// The schemas the generator derives from <paramref name="modelSource"/>, in schema syntax.
    /// </summary>
    /// <remarks>
    /// Reaches past the generator into its symbol reader, because that derivation — not the emitted
    /// text — is the part that has to agree with the object mapper.
    /// </remarks>
    internal static IReadOnlyList<string> DescribeSchemas(string modelSource)
    {
        CSharpCompilation compilation = CompileModel(modelSource);

        List<INamedTypeSymbol> roots =
        [
            .. compilation.SyntaxTrees
                .SelectMany(tree => tree.GetRoot().DescendantNodes())
                .Select(node => compilation.GetSemanticModel(node.SyntaxTree).GetDeclaredSymbol(node))
                .OfType<INamedTypeSymbol>()
                .Where(type => type.GetAttributes().Any(attribute =>
                    attribute.AttributeClass?.ToDisplayString()
                    == "Hollow.Api.Codegen.HollowGeneratedApiAttribute")),
        ];

        return
        [
            .. SymbolModel.Describe(roots)
                .Select(schema => schema.ToString()!)
                .Order(StringComparer.Ordinal),
        ];
    }

    private static (Compilation Output, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<string> Files)
        Drive(string modelSource)
    {
        GeneratorDriver driver = CSharpGeneratorDriver
            .Create([new HollowApiSourceGenerator().AsSourceGenerator()], parseOptions: ParseOptions)
            .RunGeneratorsAndUpdateCompilation(
                CompileModel(modelSource),
                out Compilation output,
                out ImmutableArray<Diagnostic> diagnostics);

        GeneratorDriverRunResult result = driver.GetRunResult();

        return (
            output,
            [.. diagnostics],
            [.. result.Results.SelectMany(run => run.GeneratedSources).Select(source => source.HintName)]);
    }

    private static CSharpCompilation CompileModel(string modelSource) =>
        CSharpCompilation.Create(
            "Hollow.Generated.Model",
            [CSharpSyntaxTree.ParseText(modelSource, ParseOptions)],
            ReferenceAssemblies(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Debug,
                nullableContextOptions: NullableContextOptions.Enable));

    private static Assembly Emit(Compilation compilation)
    {
        using MemoryStream assembly = new();
        EmitResult result = compilation.Emit(assembly);

        Refuse(result.Diagnostics, "the generated code does not compile:");

        assembly.Position = 0;

        return new AssemblyLoadContext("generated", isCollectible: true).LoadFromStream(assembly);
    }

    private static void Refuse(IEnumerable<Diagnostic> diagnostics, string what)
    {
        string errors = string.Join(
            "\n",
            diagnostics
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Select(diagnostic => diagnostic.ToString()));

        if (errors.Length > 0)
        {
            throw new InvalidOperationException($"{what}\n{errors}");
        }
    }

    private static IEnumerable<MetadataReference> ReferenceAssemblies()
    {
        HashSet<string> locations = new(StringComparer.Ordinal);

        void Add(Assembly assembly)
        {
            if (!assembly.IsDynamic && assembly.Location.Length > 0)
            {
                locations.Add(assembly.Location);
            }
        }

        Add(typeof(HollowGeneratedApiAttribute).Assembly);
        Add(typeof(object).Assembly);

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Add(assembly);
        }

        // Facades the compiler needs even though nothing loads them directly.
        string runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

        foreach (string name in new[] { "System.Runtime.dll", "System.Collections.dll", "System.Linq.dll" })
        {
            string path = Path.Combine(runtimeDirectory, name);

            if (File.Exists(path))
            {
                locations.Add(path);
            }
        }

        return locations.Select(location => MetadataReference.CreateFromFile(location));
    }
}
