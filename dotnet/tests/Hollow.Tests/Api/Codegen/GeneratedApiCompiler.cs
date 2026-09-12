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
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace Hollow.Tests.Api.Codegen;

/// <summary>
/// Compiles generated source in process and loads it, so the tests can call it.
/// </summary>
/// <remarks>
/// Java's generator tests write the output to a temporary directory and shell out to the JDK compiler,
/// which tells them only that it compiles. Compiling in process costs less and gives an assembly back,
/// so a test can go on to read real records through the generated API — which is the claim worth
/// making.
/// </remarks>
internal static class GeneratedApiCompiler
{
    /// <summary>
    /// Compiles <paramref name="files"/> against the Hollow assembly and returns the result.
    /// </summary>
    internal static Assembly Compile(IReadOnlyDictionary<string, string> files)
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            "Hollow.Generated." + files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            files.Select(file => CSharpSyntaxTree.ParseText(
                file.Value,
                new CSharpParseOptions(LanguageVersion.Preview),
                path: file.Key)),
            ReferenceAssemblies(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Debug,
                nullableContextOptions: NullableContextOptions.Enable));

        if (Environment.GetEnvironmentVariable("HOLLOW_DUMP") is { Length: > 0 } dumpDirectory)
        {
            Directory.CreateDirectory(dumpDirectory);

            foreach ((string name, string source) in files)
            {
                File.WriteAllText(Path.Combine(dumpDirectory, name), source);
            }
        }

        using MemoryStream assembly = new();
        EmitResult result = compilation.Emit(assembly);

        if (!result.Success)
        {
            string errors = string.Join(
                "\n",
                result.Diagnostics
                    .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                    .Select(diagnostic => diagnostic.ToString()));

            throw new InvalidOperationException($"the generated code does not compile:\n{errors}");
        }

        // The generated code is emitted nullable-enabled and warning-free on purpose: a warning in
        // generated source is a warning the caller cannot fix.
        string warnings = string.Join(
            "\n",
            result.Diagnostics
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning)
                .Select(diagnostic => diagnostic.ToString()));

        if (warnings.Length > 0)
        {
            throw new InvalidOperationException($"the generated code compiles with warnings:\n{warnings}");
        }

        assembly.Position = 0;

        return new AssemblyLoadContext("generated", isCollectible: true).LoadFromStream(assembly);
    }

    /// <summary>
    /// Everything the generated code needs to compile: the Hollow assembly and the framework beneath
    /// it.
    /// </summary>
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

        Add(typeof(Hollow.Api.Custom.HollowApi).Assembly);
        Add(typeof(object).Assembly);

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Add(assembly);
        }

        // System.Runtime is a facade the compiler needs even though nothing loads it directly.
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
