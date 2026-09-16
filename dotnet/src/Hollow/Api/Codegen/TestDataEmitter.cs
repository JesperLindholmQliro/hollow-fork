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

/// <summary>What a test data emitter needs to know, independent of the runtime.</summary>
/// <param name="Namespace">The namespace the emitted files declare.</param>
/// <param name="DatasetClassName">The name of the emitted dataset class.</param>
internal sealed record TestDataEmitterOptions(string Namespace, string DatasetClassName);

/// <summary>
/// Emits a builder per type, so that a test can describe a dataset in code and hand it to a consumer.
/// </summary>
/// <remarks>
/// <para>
/// The builders are fluent and typed by their parent, so <c>Up()</c> walks back out of a nested
/// record to the one that holds it and the compiler knows what that is.
/// </para>
/// <para>
/// A field whose type has exactly one non-reference field gets a shortcut: rather than
/// <c>.Title().Value("Heat")</c>, just <c>.Title("Heat")</c>. That covers the string and boxed-scalar
/// wrapper types a model is mostly made of.
/// </para>
/// </remarks>
internal sealed class TestDataEmitter(TestDataEmitterOptions options)
{
    /// <summary>Emits the builders, as file name to source.</summary>
    internal IReadOnlyDictionary<string, string> Emit(IEnumerable<ModelSchema> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);

        List<ModelSchema> ordered = [.. schemas.OrderBy(schema => schema.Name, StringComparer.Ordinal)];
        Dictionary<string, ModelSchema> byName =
            ordered.ToDictionary(schema => schema.Name, StringComparer.Ordinal);

        Dictionary<string, string> files = new(StringComparer.Ordinal)
        {
            [options.DatasetClassName + ".cs"] = GenerateDataset(ordered),
        };

        foreach (ModelSchema schema in ordered)
        {
            files[CodeNames.TestData(schema.Name) + ".cs"] = schema switch
            {
                ModelObjectSchema objectSchema => GenerateObject(objectSchema, byName),
                ModelListSchema listSchema => GenerateList(listSchema, byName),
                ModelSetSchema setSchema => GenerateSet(setSchema, byName),
                ModelMapSchema mapSchema => GenerateMap(mapSchema, byName),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(schemas), schema.Kind, "unknown record kind"),
            };
        }

        return files;
    }

    private string GenerateDataset(IReadOnlyList<ModelSchema> schemas)
    {
        CodeWriter writer = new();

        Preamble(writer);

        writer.Line("/// <summary>");
        writer.Line("/// A dataset described in code, which a test can hand to a consumer.");
        writer.Line("/// </summary>");

        using (writer.Open($"public sealed class {options.DatasetClassName} : HollowTestDataset"))
        {
            bool first = true;

            foreach (ModelSchema schema in schemas)
            {
                if (!first)
                {
                    writer.Blank();
                }

                first = false;

                string builder = CodeNames.TestData(schema.Name);

                writer.Doc($"Adds a <c>{schema.Name}</c> record, and returns it to be filled in.");

                using (writer.Open($"public {builder}<object?> {CodeNames.Pascal(schema.Name)}()"))
                {
                    writer.Line($"{builder}<object?> record = new(null);");
                    writer.Blank();
                    writer.Line("Add(record);");
                    writer.Blank();
                    writer.Line("return record;");
                }
            }
        }

        return writer.ToString();
    }

    private string GenerateObject(ModelObjectSchema schema, IReadOnlyDictionary<string, ModelSchema> byName)
    {
        CodeWriter writer = new();
        string self = CodeNames.TestData(schema.Name) + "<TParent>";

        Preamble(writer);

        writer.Line("/// <summary>");
        writer.Line($"/// A <c>{schema.Name}</c> record being described in code.");
        writer.Line("/// </summary>");
        writer.Line("/// <typeparam name=\"TParent\">What <c>Up()</c> returns.</typeparam>");

        using (writer.Open(
            $"public sealed class {CodeNames.TestData(schema.Name)}<TParent>(TParent parent)"
            + $" : HollowTestObjectRecord<TParent>(parent)"))
        {
            foreach (ModelField field in schema.Fields)
            {
                string name = CodeNames.Pascal(field.Name);

                writer.Doc($"Sets <c>{schema.Name}.{field.Name}</c>.");

                if (field.Type == ModelFieldType.Reference)
                {
                    string child = $"{CodeNames.TestData(field.ReferencedType!)}<{self}>";

                    writer.Doc($"Adds <c>{schema.Name}.{field.Name}</c>, and returns it to be filled in.");

                    using (writer.Open($"public {child} {name}()"))
                    {
                        writer.Line($"{child} record = new(this);");
                        writer.Blank();
                        writer.Line($"SetField({Quote(field.Name)}, record);");
                        writer.Blank();
                        writer.Line("return record;");
                    }

                    if (Shortcut(field.ReferencedType!, byName) is (string valueType, string valueField))
                    {
                        writer.Blank();
                        writer.Doc(
                            $"Sets <c>{schema.Name}.{field.Name}</c> to a <c>{field.ReferencedType}</c>"
                            + $" holding <paramref name=\"value\"/>.");

                        using (writer.Open($"public {self} {name}({valueType} value)"))
                        {
                            writer.Line($"{name}().{CodeNames.Pascal(valueField)}(value);");
                            writer.Blank();
                            writer.Line("return this;");
                        }
                    }
                }
                else
                {
                    using (writer.Open($"public {self} {name}({ValueTypeOf(field.Type)} value)"))
                    {
                        writer.Line($"SetField({Quote(field.Name)}, value);");
                        writer.Blank();
                        writer.Line("return this;");
                    }
                }

                writer.Blank();
            }

            EmitObjectSchema(writer, schema);
        }

        return writer.ToString();
    }

    private static void EmitObjectSchema(CodeWriter writer, ModelObjectSchema schema)
    {
        writer.Doc("The schema these records are written against.");
        writer.Line("public static readonly HollowObjectSchema RecordSchema = CreateSchema();");
        writer.Blank();
        writer.Line("/// <inheritdoc />");
        writer.Line("public override HollowObjectSchema Schema => RecordSchema;");
        writer.Blank();

        using (writer.Open("private static HollowObjectSchema CreateSchema()"))
        {
            string key = schema.PrimaryKeyFieldPaths is { Count: > 0 } paths
                ? ", " + string.Join(", ", paths.Select(Quote))
                : string.Empty;

            writer.Line(
                $"HollowObjectSchema schema = new({Quote(schema.Name)}, {schema.Fields.Count}{key});");
            writer.Blank();

            foreach (ModelField field in schema.Fields)
            {
                string referenced = field.Type == ModelFieldType.Reference
                    ? $", {Quote(field.ReferencedType!)}"
                    : string.Empty;

                writer.Line(
                    $"schema.AddField({Quote(field.Name)}, FieldType.{field.Type}{referenced});");
            }

            writer.Blank();
            writer.Line("return schema;");
        }
    }

    private string GenerateList(ModelListSchema schema, IReadOnlyDictionary<string, ModelSchema> byName) =>
        GenerateCollection(
            schema,
            schema.ElementType,
            "HollowTestListRecord",
            $"new HollowListSchema({Quote(schema.Name)}, {Quote(schema.ElementType)})",
            "HollowListSchema",
            byName);

    private string GenerateSet(ModelSetSchema schema, IReadOnlyDictionary<string, ModelSchema> byName)
    {
        string hashKey = schema.HashKeyFieldPaths is { Count: > 0 } paths
            ? ", " + string.Join(", ", paths.Select(Quote))
            : string.Empty;

        return GenerateCollection(
            schema,
            schema.ElementType,
            "HollowTestSetRecord",
            $"new HollowSetSchema({Quote(schema.Name)}, {Quote(schema.ElementType)}{hashKey})",
            "HollowSetSchema",
            byName);
    }

    private string GenerateCollection(
        ModelSchema schema,
        string elementType,
        string baseClass,
        string schemaExpression,
        string schemaType,
        IReadOnlyDictionary<string, ModelSchema> byName)
    {
        CodeWriter writer = new();
        string self = CodeNames.TestData(schema.Name) + "<TParent>";
        string child = $"{CodeNames.TestData(elementType)}<{self}>";

        Preamble(writer);

        writer.Line("/// <summary>");
        writer.Line($"/// A <c>{schema.Name}</c> record being described in code.");
        writer.Line("/// </summary>");
        writer.Line("/// <typeparam name=\"TParent\">What <c>Up()</c> returns.</typeparam>");

        using (writer.Open(
            $"public sealed class {CodeNames.TestData(schema.Name)}<TParent>(TParent parent)"
            + $" : {baseClass}<TParent>(parent)"))
        {
            writer.Doc("Adds an element, and returns it to be filled in.");

            using (writer.Open($"public {child} {CodeNames.Pascal(elementType)}()"))
            {
                writer.Line($"{child} element = new(this);");
                writer.Blank();
                writer.Line("AddElement(element);");
                writer.Blank();
                writer.Line("return element;");
            }

            if (Shortcut(elementType, byName) is (string valueType, string valueField))
            {
                writer.Blank();
                writer.Doc($"Adds an element holding <paramref name=\"value\"/>.");

                using (writer.Open($"public {self} {CodeNames.Pascal(elementType)}({valueType} value)"))
                {
                    writer.Line(
                        $"{CodeNames.Pascal(elementType)}().{CodeNames.Pascal(valueField)}(value);");
                    writer.Blank();
                    writer.Line("return this;");
                }
            }

            writer.Blank();
            writer.Doc("The schema these records are written against.");
            writer.Line($"public static readonly {schemaType} RecordSchema = {schemaExpression};");
            writer.Blank();
            writer.Line("/// <inheritdoc />");
            writer.Line("public override HollowSchema Schema => RecordSchema;");
        }

        return writer.ToString();
    }

    private string GenerateMap(ModelMapSchema schema, IReadOnlyDictionary<string, ModelSchema> byName)
    {
        CodeWriter writer = new();
        string self = CodeNames.TestData(schema.Name) + "<TParent>";
        string keyBuilder = $"{CodeNames.TestData(schema.KeyType)}<{self}>";
        string valueBuilder = $"{CodeNames.TestData(schema.ValueType)}<{self}>";

        Preamble(writer);

        writer.Line("/// <summary>");
        writer.Line($"/// A <c>{schema.Name}</c> record being described in code.");
        writer.Line("/// </summary>");
        writer.Line("/// <remarks>");
        writer.Line("/// Java nests an <c>Entry</c> record builder here, because its runtime models an");
        writer.Line("/// entry as a record whose schema and write record both throw. This port's runtime");
        writer.Line("/// models it as the pair it is, so an entry is added whole — the key and the value");
        writer.Line("/// are filled in by the callbacks rather than by walking back out of a half-built");
        writer.Line("/// entry.");
        writer.Line("/// </remarks>");
        writer.Line("/// <typeparam name=\"TParent\">What <c>Up()</c> returns.</typeparam>");

        using (writer.Open(
            $"public sealed class {CodeNames.TestData(schema.Name)}<TParent>(TParent parent)"
            + $" : HollowTestMapRecord<TParent>(parent)"))
        {
            writer.Doc("Adds an entry, filling its key and value in with the callbacks.");

            using (writer.Open(
                $"public {self} Entry(Action<{keyBuilder}> key, Action<{valueBuilder}> value)"))
            {
                writer.Line("ArgumentNullException.ThrowIfNull(key);");
                writer.Line("ArgumentNullException.ThrowIfNull(value);");
                writer.Blank();
                writer.Line($"{keyBuilder} entryKey = new(this);");
                writer.Line($"{valueBuilder} entryValue = new(this);");
                writer.Blank();
                writer.Line("key(entryKey);");
                writer.Line("value(entryValue);");
                writer.Blank();
                writer.Line("AddEntry(entryKey, entryValue);");
                writer.Blank();
                writer.Line("return this;");
            }

            (string, string)? keyShortcut = Shortcut(schema.KeyType, byName);
            (string, string)? valueShortcut = Shortcut(schema.ValueType, byName);

            if (keyShortcut is (string keyType, string keyField)
                && valueShortcut is (string valueType, string valueField))
            {
                writer.Blank();
                writer.Doc("Adds an entry holding the two values.");

                writer.Line($"public {self} Entry({keyType} key, {valueType} value) =>");
                writer.Line("    Entry(");
                writer.Line($"        entryKey => entryKey.{CodeNames.Pascal(keyField)}(key),");
                writer.Line($"        entryValue => entryValue.{CodeNames.Pascal(valueField)}(value));");
            }

            writer.Blank();
            writer.Doc("The schema these records are written against.");

            string hashKey = schema.HashKeyFieldPaths is { Count: > 0 } paths
                ? ", " + string.Join(", ", paths.Select(Quote))
                : string.Empty;

            writer.Line(
                "public static readonly HollowMapSchema RecordSchema = new("
                + $"{Quote(schema.Name)}, {Quote(schema.KeyType)}, {Quote(schema.ValueType)}{hashKey});");
            writer.Blank();
            writer.Line("/// <inheritdoc />");
            writer.Line("public override HollowSchema Schema => RecordSchema;");
        }

        return writer.ToString();
    }

    /// <summary>
    /// The value type and field name a type can be shortened to, where it holds exactly one
    /// non-reference field.
    /// </summary>
    private static (string ValueType, string FieldName)? Shortcut(
        string typeName, IReadOnlyDictionary<string, ModelSchema> byName) =>
        byName.GetValueOrDefault(typeName) is ModelObjectSchema { Fields.Count: 1 } schema
        && schema.Fields[0].Type != ModelFieldType.Reference
            ? (ValueTypeOf(schema.Fields[0].Type), schema.Fields[0].Name)
            : null;

    private static string ValueTypeOf(ModelFieldType fieldType) => fieldType switch
    {
        ModelFieldType.Int => "int?",
        ModelFieldType.Long => "long?",
        ModelFieldType.Float => "float?",
        ModelFieldType.Double => "double?",
        ModelFieldType.Boolean => "bool?",
        ModelFieldType.Decimal => "decimal?",
        ModelFieldType.String => "string?",
        ModelFieldType.Bytes => "byte[]?",
        _ => throw new ArgumentOutOfRangeException(nameof(fieldType), fieldType, "not a value field"),
    };

    private static string Quote(string value) => "\"" + value + "\"";

    private void Preamble(CodeWriter writer)
    {
        writer.Line("// <auto-generated />");
        writer.Line("// Generated by Hollow.Api.Codegen. Changes here are lost on the next run.");
        writer.Line("#nullable enable");
        writer.Line();
        writer.Line("using System;");
        writer.Line("using Hollow.Api.TestData;");
        writer.Line("using Hollow.Core.Schema;");
        writer.Line();
        writer.Line($"namespace {options.Namespace};");
        writer.Line();
    }
}
