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
using Hollow.Core.Schema;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Api.Codegen;

/// <summary>
/// Emits a typed C# client for a Hollow data model, as text.
/// </summary>
/// <remarks>
/// <para>
/// Instead of <c>stateEngine.GetTypeState("Movie")</c> and reading field 3 of ordinal 17, a caller
/// writes <c>api.GetMovie(17).Title</c>. What comes out is ordinary C# source: readable, steppable in a
/// debugger, and compiled by the caller's own build.
/// </para>
/// <code>
/// HollowCodeGenerator generator = new(new HollowCodeGeneratorOptions { Namespace = "Acme.Movies" });
///
/// HollowCodeGenerator.WriteTo("obj/generated", generator.Generate(typeof(Movie)));
/// </code>
/// <para>
/// <c>Hollow.SourceGenerator</c> does the same job inside the compiler, with nothing to check in. Reach
/// for this form when the model is a dataset rather than a set of declared types, or when reading the
/// generated source matters.
/// </para>
/// <para>
/// <strong>Port note.</strong> Java's <c>api.codegen</c> emits Java, and emits a good deal more of it:
/// a wrapper, a type API, a delegate interface and two implementations, a factory and a data accessor
/// for each of six built-in scalar types, which here are already in <c>Hollow.Core.Types</c>. Java's
/// separate POJO, "performance API" and test-data-builder generators are not ported; nothing depends on
/// them.
/// </para>
/// </remarks>
/// <param name="options">What to emit.</param>
public sealed class HollowCodeGenerator(HollowCodeGeneratorOptions options)
{
    private readonly CodeEmitter _emitter =
        new((options ?? throw new ArgumentNullException(nameof(options))).ToEmitterOptions());

    /// <summary>
    /// Emits a client for the model <paramref name="modelTypes"/> describe, as file name to source.
    /// </summary>
    /// <remarks>
    /// The schemas are derived exactly as the object mapper derives them when writing, so the generated
    /// client reads what a producer mapping the same types writes.
    /// </remarks>
    /// <param name="modelTypes">The CLR types at the root of the model.</param>
    public IReadOnlyDictionary<string, string> Generate(params Type[] modelTypes)
    {
        ArgumentNullException.ThrowIfNull(modelTypes);

        HollowWriteStateEngine writeEngine = new();
        HollowObjectMapper mapper = new(writeEngine);

        foreach (Type modelType in modelTypes)
        {
            mapper.InitializeTypeState(modelType);
        }

        return Generate(writeEngine);
    }

    /// <summary>
    /// Emits a client for the model <paramref name="dataset"/> declares, as file name to source.
    /// </summary>
    /// <remarks>
    /// Anything that carries schemas will do: a write state engine, a read state engine loaded from a
    /// blob, or a <c>SimpleHollowDataset</c> built from parsed schema text.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Two types would generate the same class name.
    /// </exception>
    public IReadOnlyDictionary<string, string> Generate(IHollowDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        return _emitter.Emit(dataset.Schemas.Select(ToModelSchema));
    }

    /// <summary>
    /// Writes <paramref name="files"/> into <paramref name="directory"/>, creating it if need be.
    /// </summary>
    /// <remarks>
    /// A file whose content has not changed is left alone, so a build that generates into its source
    /// tree does not make every generated file look modified.
    /// </remarks>
    public static void WriteTo(string directory, IReadOnlyDictionary<string, string> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        Directory.CreateDirectory(directory);

        foreach ((string name, string source) in files)
        {
            string path = Path.Combine(directory, name);

            if (File.Exists(path) && File.ReadAllText(path) == source)
            {
                continue;
            }

            File.WriteAllText(path, source);
        }
    }

    /// <summary>
    /// Describes a runtime schema in the form the emitters take.
    /// </summary>
    /// <remarks>
    /// The other side of this is the source generator's symbol reader, which has to describe the same
    /// model the same way — a client generated one way reads a blob written by a producer mapping the
    /// same types the other.
    /// </remarks>
    internal static ModelSchema ToModelSchema(HollowSchema schema) =>
        schema switch
        {
            HollowObjectSchema objectSchema => new ModelObjectSchema(
                objectSchema.Name,
                [
                    .. Enumerable.Range(0, objectSchema.FieldCount)
                        .Select(index => new ModelField(
                            objectSchema.GetFieldName(index),
                            ToModelFieldType(objectSchema.GetFieldType(index)),
                            objectSchema.GetReferencedType(index))),
                ],
                objectSchema.PrimaryKey?.FieldPaths),

            HollowListSchema listSchema => new ModelListSchema(listSchema.Name, listSchema.ElementType),

            HollowSetSchema setSchema => new ModelSetSchema(
                setSchema.Name, setSchema.ElementType, setSchema.HashKey?.FieldPaths),

            HollowMapSchema mapSchema => new ModelMapSchema(
                mapSchema.Name, mapSchema.KeyType, mapSchema.ValueType, mapSchema.HashKey?.FieldPaths),

            _ => throw new ArgumentOutOfRangeException(
                nameof(schema), schema.SchemaType, "unknown record kind"),
        };

    private static ModelFieldType ToModelFieldType(FieldType fieldType) =>
        fieldType switch
        {
            FieldType.Reference => ModelFieldType.Reference,
            FieldType.Int => ModelFieldType.Int,
            FieldType.Long => ModelFieldType.Long,
            FieldType.Float => ModelFieldType.Float,
            FieldType.Double => ModelFieldType.Double,
            FieldType.Boolean => ModelFieldType.Boolean,
            FieldType.Decimal => ModelFieldType.Decimal,
            FieldType.String => ModelFieldType.String,
            FieldType.Bytes => ModelFieldType.Bytes,
            _ => throw new ArgumentOutOfRangeException(nameof(fieldType), fieldType, "unknown field type"),
        };
}
