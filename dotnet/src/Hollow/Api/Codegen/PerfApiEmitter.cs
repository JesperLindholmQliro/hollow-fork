/*
 *  Copyright 2021 Netflix, Inc.
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

namespace Hollow.Api.Codegen;

/// <summary>
/// What a performance API emitter needs to know, independent of the runtime.
/// </summary>
/// <param name="Namespace">The namespace the emitted files declare.</param>
/// <param name="ApiClassName">The name of the emitted API class.</param>
/// <param name="CheckFieldExistsMethods">
/// The <c>Type.field</c> names that also get a property saying whether the loaded dataset has the
/// field at all.
/// </param>
internal sealed record PerfApiEmitterOptions(
    string Namespace, string ApiClassName, IReadOnlySet<string> CheckFieldExistsMethods);

/// <summary>
/// Emits a performance API: one class per object type whose accessors read a field with a constant
/// index, and an API class holding one of each.
/// </summary>
/// <remarks>
/// <para>
/// The client API emitter next door emits record wrappers. This emits nothing that allocates: every
/// accessor takes a <c>HollowRef</c> — a struct over one packed long — and returns a value read
/// straight out of the type access.
/// </para>
/// <para>
/// Java emits a primitive accessor and a boxed one per numeric field, because only the boxed one can
/// be null and it allocates an <c>Integer</c> to say so. One nullable accessor says both here, and
/// <c>Nullable&lt;T&gt;</c> is a struct, so the pair would buy nothing.
/// </para>
/// </remarks>
internal sealed class PerfApiEmitter(PerfApiEmitterOptions options)
{
    /// <summary>Emits the API, as file name to source.</summary>
    internal IReadOnlyDictionary<string, string> Emit(IEnumerable<ModelSchema> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);

        // Sorted, so that the same model emits the same files whatever order the schemas arrive in.
        List<ModelSchema> ordered = [.. schemas.OrderBy(schema => schema.Name, StringComparer.Ordinal)];

        Dictionary<string, string> files = new(StringComparer.Ordinal)
        {
            [options.ApiClassName + ".cs"] = GenerateApi(ordered),
        };

        foreach (ModelObjectSchema schema in ordered.OfType<ModelObjectSchema>())
        {
            files[CodeNames.PerfApi(schema.Name) + ".cs"] = GenerateObjectType(schema);
        }

        return files;
    }

    private string GenerateApi(IReadOnlyList<ModelSchema> schemas)
    {
        CodeWriter writer = new();

        Preamble(writer);

        writer.Line("/// <summary>");
        writer.Line("/// A view over the dataset that allocates nothing, with one accessor per type.");
        writer.Line("/// </summary>");

        using (writer.Open($"public sealed class {options.ApiClassName} : HollowPerformanceApi"))
        {
            writer.Doc("Reads <paramref name=\"dataAccess\"/>.");
            writer.Line($"public {options.ApiClassName}(IHollowDataAccess dataAccess)");
            writer.Line("    : base(dataAccess)");

            using (writer.Open())
            {
                foreach (ModelSchema schema in schemas)
                {
                    writer.Line(
                        $"{CodeNames.Pascal(schema.Name)} = new {TypeApiOf(schema)}"
                        + $"(dataAccess, {Quote(schema.Name)}, this);");
                }
            }

            foreach (ModelSchema schema in schemas)
            {
                writer.Blank();
                writer.Doc($"Reads the <c>{schema.Name}</c> records.");
                writer.Line($"public {TypeApiOf(schema)} {CodeNames.Pascal(schema.Name)} {{ get; }}");
            }
        }

        return writer.ToString();
    }

    private string GenerateObjectType(ModelObjectSchema schema)
    {
        CodeWriter writer = new();

        Preamble(writer);

        writer.Line("/// <summary>");
        writer.Line($"/// Reads a <c>{schema.Name}</c> record's fields, by reference.");
        writer.Line("/// </summary>");

        using (writer.Open(
            $"public sealed class {CodeNames.PerfApi(schema.Name)} : HollowObjectTypePerfApi"))
        {
            writer.Doc("The fields this API reads, in the order its accessors index them.");
            writer.Line(
                "public static readonly string[] FieldNames = ["
                + string.Join(", ", schema.Fields.Select(field => Quote(field.Name))) + "];");

            writer.Blank();
            writer.Doc($"Reads the <c>{schema.Name}</c> records of <paramref name=\"dataAccess\"/>.");
            writer.Line(
                $"public {CodeNames.PerfApi(schema.Name)}"
                + "(IHollowDataAccess dataAccess, string typeName, HollowPerformanceApi api)");
            writer.Line("    : base(dataAccess, typeName, api, FieldNames)");

            using (writer.Open())
            {
            }

            for (int index = 0; index < schema.Fields.Count; index++)
            {
                EmitField(writer, schema, schema.Fields[index], index);
            }
        }

        return writer.ToString();
    }

    private void EmitField(CodeWriter writer, ModelObjectSchema schema, ModelField field, int index)
    {
        string name = CodeNames.Pascal(field.Name);
        string describes = field.Type == ModelFieldType.Reference
            ? $"{field.Type} ({field.ReferencedType})"
            : field.Type.ToString();

        writer.Blank();
        writer.Doc($"<c>{schema.Name}.{field.Name}</c>, which is a <c>{describes}</c> field.");

        switch (field.Type)
        {
            case ModelFieldType.Int:
                EmitSentinelRead(writer, name, index, "int", "ReadInt", "value == int.MinValue");
                break;

            case ModelFieldType.Long:
                EmitSentinelRead(writer, name, index, "long", "ReadLong", "value == long.MinValue");
                break;

            case ModelFieldType.Float:
                EmitSentinelRead(writer, name, index, "float", "ReadFloat", "float.IsNaN(value)");
                break;

            case ModelFieldType.Double:
                EmitSentinelRead(writer, name, index, "double", "ReadDouble", "double.IsNaN(value)");
                break;

            case ModelFieldType.Boolean:
                EmitDirectRead(writer, name, index, "bool?", "ReadBoolean", "null");
                break;

            case ModelFieldType.Decimal:
                EmitDirectRead(writer, name, index, "decimal?", "ReadDecimal", "null");
                break;

            case ModelFieldType.Bytes:
                EmitDirectRead(writer, name, index, "byte[]?", "ReadBytes", "null");
                break;

            case ModelFieldType.String:
                EmitDirectRead(writer, name, index, "string?", "ReadString", "null");

                writer.Blank();
                writer.Doc(
                    $"Whether <c>{schema.Name}.{field.Name}</c> equals "
                    + "<paramref name=\"testValue\"/>, without materialising it.");
                writer.Line($"public bool Is{name}Equal(HollowRef reference, string? testValue) =>");
                writer.Line($"    FieldIndexes[{index}] != -1");
                writer.Line(
                    $"    && TypeAccess.IsStringFieldEqual(Ordinal(reference), FieldIndexes[{index}], testValue);");
                break;

            case ModelFieldType.Reference:
                writer.Line($"public HollowRef Get{name}Ref(HollowRef reference) =>");
                writer.Line($"    FieldIndexes[{index}] == -1");
                writer.Line("        ? HollowRef.Null");
                writer.Line("        : HollowRef.Create(");
                writer.Line($"            ReferenceMaskedTypeIdentifiers[{index}],");
                writer.Line($"            TypeAccess.ReadOrdinal(Ordinal(reference), FieldIndexes[{index}]));");
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(field), field.Type, "unknown field type");
        }

        if (options.CheckFieldExistsMethods.Contains($"{schema.Name}.{field.Name}"))
        {
            writer.Blank();
            writer.Doc($"Whether the loaded dataset has <c>{schema.Name}.{field.Name}</c> at all.");
            writer.Line($"public bool {name}FieldExists => FieldIndexes[{index}] != -1;");
        }
    }

    /// <summary>A read whose null is a sentinel value, turned back into <see langword="null"/>.</summary>
    private static void EmitSentinelRead(
        CodeWriter writer, string name, int index, string valueType, string read, string isNull)
    {
        using (writer.Open($"public {valueType}? Get{name}(HollowRef reference)"))
        {
            using (writer.Open($"if (FieldIndexes[{index}] == -1)"))
            {
                writer.Line("return null;");
            }

            writer.Blank();
            writer.Line($"{valueType} value = TypeAccess.{read}(Ordinal(reference), FieldIndexes[{index}]);");
            writer.Blank();
            writer.Line($"return {isNull} ? null : value;");
        }
    }

    /// <summary>A read that already answers with the absent value the caller wants.</summary>
    private static void EmitDirectRead(
        CodeWriter writer, string name, int index, string valueType, string read, string absent)
    {
        writer.Line($"public {valueType} Get{name}(HollowRef reference) =>");
        writer.Line($"    FieldIndexes[{index}] == -1");
        writer.Line($"        ? {absent}");
        writer.Line($"        : TypeAccess.{read}(Ordinal(reference), FieldIndexes[{index}]);");
    }

    private static string TypeApiOf(ModelSchema schema) => schema.Kind switch
    {
        ModelSchemaKind.Object => CodeNames.PerfApi(schema.Name),
        ModelSchemaKind.List => "HollowListTypePerfApi",
        ModelSchemaKind.Set => "HollowSetTypePerfApi",
        ModelSchemaKind.Map => "HollowMapTypePerfApi",
        _ => throw new ArgumentOutOfRangeException(nameof(schema), schema.Kind, "unknown record kind"),
    };

    private static string Quote(string value) => "\"" + value + "\"";

    private void Preamble(CodeWriter writer)
    {
        writer.Line("// <auto-generated />");
        writer.Line("// Generated by Hollow.Api.Codegen. Changes here are lost on the next run.");
        writer.Line("#nullable enable");
        writer.Line();
        writer.Line("using System;");
        writer.Line("using Hollow.Api.PerfApi;");
        writer.Line("using Hollow.Core.Read.DataAccess;");
        writer.Line();
        writer.Line($"namespace {options.Namespace};");
        writer.Line();
    }
}
