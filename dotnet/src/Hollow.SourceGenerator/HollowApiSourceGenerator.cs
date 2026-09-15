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
using Hollow.Api.Codegen;
using Microsoft.CodeAnalysis;

namespace Hollow.SourceGenerator;

/// <summary>
/// Emits a typed Hollow client at compile time for every model root marked
/// <c>[HollowGeneratedApi]</c>.
/// </summary>
/// <remarks>
/// <para>
/// The idiomatic .NET form of what <c>HollowCodeGenerator</c> does as a text emitter: the model is
/// declared with attributes, the client appears in the compilation, and there is nothing to check in
/// or keep in step. Both run the same emitters — see <see cref="SymbolModel"/> for the one piece that
/// cannot be shared, and why.
/// </para>
/// <para>
/// Roots naming the same namespace and API class are emitted as one client covering all of them, so a
/// model with several entry points does not produce two APIs that each know half of it.
/// </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class HollowApiSourceGenerator : IIncrementalGenerator
{
    private const string MarkerAttribute = "Hollow.Api.Codegen.HollowGeneratedApiAttribute";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<ModelRoot> roots = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                MarkerAttribute,
                static (node, _) => true,
                static (attributeContext, _) => ModelRoot.From(attributeContext))
            .Where(static root => root is not null)
            .Select(static (root, _) => root!);

        context.RegisterSourceOutput(roots.Collect(), static (production, collected) =>
            Emit(production, collected));
    }

    private static void Emit(SourceProductionContext production, ImmutableArray<ModelRoot> roots)
    {
        // Roots that agree on where the client goes are one client; anything else would leave each
        // half of a model with an API that cannot follow a reference into the other.
        foreach (IGrouping<(string Namespace, string ApiClassName), ModelRoot> client in
            roots.GroupBy(root => (root.Namespace, root.ApiClassName)))
        {
            EmitClient(production, client.Key.Namespace, client.Key.ApiClassName, [.. client]);
        }
    }

    private static void EmitClient(
        SourceProductionContext production,
        string generatedNamespace,
        string apiClassName,
        IReadOnlyList<ModelRoot> roots)
    {
        ModelRoot first = roots[0];

        EmitterOptions options = new()
        {
            Namespace = generatedNamespace,
            ApiClassName = apiClassName,
            DefaultCachedTypes =
                [.. roots.SelectMany(root => root.CachedTypes).Distinct(StringComparer.Ordinal)],
            UseErgonomicShortcuts = first.UseErgonomicShortcuts,
            GenerateUniqueKeyIndexes = first.GenerateUniqueKeyIndexes,
            GenerateDataAccessors = first.GenerateDataAccessors,
            GenerateCachedDelegates = first.GenerateCachedDelegates,
            GenerateFieldPaths = first.GenerateFieldPaths,
        };

        IReadOnlyDictionary<string, string> files;

        try
        {
            files = new CodeEmitter(options).Emit(SymbolModel.Describe(roots.Select(root => root.Type)));
        }
        catch (ModelException error)
        {
            production.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.ModelCannotBeMapped,
                error.Symbol.Locations.FirstOrDefault() ?? first.Location,
                error.Message));

            return;
        }
        catch (InvalidOperationException error)
        {
            production.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.ModelCannotBeMapped, first.Location, error.Message));

            return;
        }

        foreach (KeyValuePair<string, string> file in files)
        {
            // Qualified by the API class, so two clients in one compilation cannot collide on a file
            // name they both happen to generate.
            production.AddSource($"{apiClassName}.{file.Key}", file.Value);
        }
    }

    /// <summary>One type marked as a model root, with where its client should go.</summary>
    private sealed class ModelRoot(
        INamedTypeSymbol type,
        string generatedNamespace,
        string apiClassName,
        IReadOnlyList<string> cachedTypes,
        bool useErgonomicShortcuts,
        bool generateUniqueKeyIndexes,
        bool generateDataAccessors,
        bool generateCachedDelegates,
        bool generateFieldPaths,
        Location location)
    {
        internal INamedTypeSymbol Type { get; } = type;

        internal string Namespace { get; } = generatedNamespace;

        internal string ApiClassName { get; } = apiClassName;

        internal IReadOnlyList<string> CachedTypes { get; } = cachedTypes;

        internal bool UseErgonomicShortcuts { get; } = useErgonomicShortcuts;

        internal bool GenerateUniqueKeyIndexes { get; } = generateUniqueKeyIndexes;

        internal bool GenerateDataAccessors { get; } = generateDataAccessors;

        internal bool GenerateCachedDelegates { get; } = generateCachedDelegates;

        internal bool GenerateFieldPaths { get; } = generateFieldPaths;

        internal Location Location { get; } = location;

        internal static ModelRoot? From(GeneratorAttributeSyntaxContext context)
        {
            if (context.TargetSymbol is not INamedTypeSymbol type)
            {
                return null;
            }

            AttributeData attribute = context.Attributes[0];

            string modelNamespace = type.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : type.ContainingNamespace.ToDisplayString();

            // Its own namespace by default, not the model's: the wrapper generated for a type is named
            // after that type, so emitting into the model's namespace would collide with the model.
            string generatedNamespace =
                Named(attribute, "Namespace") as string
                ?? (modelNamespace.Length == 0 ? "Hollow.Generated" : modelNamespace + ".Generated");

            // Named for where the model lives rather than for where the client is emitted — the latter
            // ends in "Generated" by default, and every API called GeneratedApi helps nobody.
            string[] modelSegments = modelNamespace.Split('.');
            string apiName = modelNamespace.Length == 0
                ? type.Name
                : modelSegments[modelSegments.Length - 1];

            return new ModelRoot(
                type,
                generatedNamespace,
                Named(attribute, "ApiClassName") as string ?? CodeNames.Pascal(apiName) + "Api",
                NamedStrings(attribute, "CachedTypes"),
                Named(attribute, "UseErgonomicShortcuts") as bool? ?? true,
                Named(attribute, "GenerateUniqueKeyIndexes") as bool? ?? true,
                Named(attribute, "GenerateDataAccessors") as bool? ?? true,
                Named(attribute, "GenerateCachedDelegates") as bool? ?? true,
                Named(attribute, "GenerateFieldPaths") as bool? ?? true,
                context.TargetNode.GetLocation());
        }

        private static object? Named(AttributeData attribute, string name) =>
            attribute.NamedArguments.FirstOrDefault(argument => argument.Key == name).Value.Value;

        private static IReadOnlyList<string> NamedStrings(AttributeData attribute, string name)
        {
            TypedConstant argument =
                attribute.NamedArguments.FirstOrDefault(candidate => candidate.Key == name).Value;

            return argument.Kind == TypedConstantKind.Array
                ? [.. argument.Values
                    .Select(value => value.Value as string)
                    .Where(value => value is not null)
                    .Select(value => value!)]
                : [];
        }
    }
}

/// <summary>What the generator reports when it cannot emit a client.</summary>
internal static class Diagnostics
{
    /// <summary>The declared model cannot be mapped onto Hollow schemas.</summary>
    internal static readonly DiagnosticDescriptor ModelCannotBeMapped = new(
        id: "HOLLOW001",
        title: "The Hollow data model cannot be mapped",
        messageFormat: "{0}",
        category: "Hollow.Codegen",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "A type marked [HollowGeneratedApi] roots a data model that the mapper cannot describe, so "
            + "no client can be generated for it.");
}
