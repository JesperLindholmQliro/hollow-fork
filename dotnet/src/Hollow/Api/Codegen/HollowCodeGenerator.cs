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
using Hollow.Core.Index.Key;
using Hollow.Core.Schema;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Api.Codegen;

/// <summary>
/// Emits a typed C# client for a Hollow data model.
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
/// generator.WriteTo("obj/generated", generator.Generate(typeof(Movie)));
/// </code>
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
    private readonly HollowCodeGeneratorOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

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
    public IReadOnlyDictionary<string, string> Generate(IHollowDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        GeneratedModel model = GeneratedModel.From(dataset);

        if (model.Types.GroupBy(type => type.RecordType, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Count() > 1) is { } clash)
        {
            throw new InvalidOperationException(
                $"the types {string.Join(", ", clash.Select(type => type.TypeName))} would all generate a "
                + $"class named {clash.Key}; rename one of them in the data model");
        }

        Dictionary<string, string> files = new(StringComparer.Ordinal);

        foreach (GeneratedType type in model.Types.Where(type => type.IsGenerated))
        {
            files[type.RecordType + ".cs"] = GenerateType(model, type);

            if (_options.GenerateUniqueKeyIndexes
                && type.Schema is HollowObjectSchema { PrimaryKey: not null })
            {
                files[CodeNames.UniqueKeyIndex(type.TypeName) + ".cs"] = GenerateUniqueKeyIndex(type);
            }
        }

        files[_options.ResolvedApiClassName + ".cs"] = GenerateApi(model);

        return files;
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

    // ---- Per-type emitters ----

    private string GenerateType(GeneratedModel model, GeneratedType type) =>
        type.Kind switch
        {
            SchemaType.Object => GenerateObjectType(model, type),
            SchemaType.List => GenerateListType(model, type),
            SchemaType.Set => GenerateSetType(model, type),
            SchemaType.Map => GenerateMapType(model, type),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type.Kind, "unknown record kind"),
        };

    private string GenerateObjectType(GeneratedModel model, GeneratedType type)
    {
        CodeWriter writer = new();
        string api = _options.ResolvedApiClassName;
        string record = type.RecordType;
        string typeApi = CodeNames.TypeApi(type.TypeName);
        string delegateInterface = CodeNames.DelegateInterface(type.TypeName);
        string lookupDelegate = CodeNames.LookupDelegate(type.TypeName);
        string cachedDelegate = CodeNames.CachedDelegate(type.TypeName);
        string factory = CodeNames.Factory(type.TypeName);

        Preamble(writer);

        // ---- Type API ----
        writer.Doc($"Reads the fields of a <c>{type.TypeName}</c> record by ordinal.");
        using (writer.Open($"public sealed class {typeApi} : HollowObjectTypeApi"))
        {
            writer.Doc("The field names this API expects, in the order its accessors index them.");
            writer.Line(
                "public static readonly string[] FieldNames = "
                + $"[{string.Join(", ", type.Fields.Select(field => Quote(field.Name)))}];");
            writer.Blank();

            writer.Doc($"Reads the <c>{type.TypeName}</c> records of <paramref name=\"typeDataAccess\"/>.");
            writer.Line($"public {typeApi}({api} api, IHollowObjectTypeDataAccess typeDataAccess)");
            writer.Line("    : base(api, typeDataAccess, FieldNames)");
            using (writer.Open())
            {
            }

            writer.Blank();
            writer.Doc("The API this type belongs to.");
            writer.Line($"public new {api} Api => ({api})base.Api;");

            foreach (GeneratedField field in type.Fields)
            {
                writer.Blank();
                EmitTypeApiAccessor(writer, type, field);
            }
        }

        // ---- Delegate interface ----
        writer.Blank();
        writer.Doc($"Where a <c>{type.TypeName}</c> record reads its fields from.");
        using (writer.Open($"public interface {delegateInterface} : IHollowObjectDelegate"))
        {
            writer.Doc("The type API this delegate reads through.");
            writer.Line($"{typeApi} {typeApi} {{ get; }}");

            foreach (GeneratedField field in type.Fields)
            {
                writer.Blank();
                writer.Doc($"Reads the <c>{field.Name}</c> field.");
                writer.Line($"{AccessorReturnType(field)} {AccessorName(field)}(int ordinal);");
            }
        }

        // ---- Lookup delegate ----
        writer.Blank();
        writer.Doc($"Reads a <c>{type.TypeName}</c> record straight out of the blob.");
        writer.Line($"public sealed class {lookupDelegate}({typeApi} typeApi)");
        writer.Line($"    : HollowObjectAbstractDelegate, {delegateInterface}");
        using (writer.Open())
        {
            writer.Line("/// <inheritdoc />");
            writer.Line("public override HollowObjectSchema Schema => typeApi.Schema;");
            writer.Blank();
            writer.Line("/// <inheritdoc />");
            writer.Line(
                "public override IHollowObjectTypeDataAccess TypeDataAccess => typeApi.TypeDataAccess;");
            writer.Blank();
            writer.Line("/// <inheritdoc />");
            writer.Line("public override HollowObjectTypeApi? TypeApi => typeApi;");
            writer.Blank();
            writer.Line("/// <inheritdoc />");
            writer.Line($"public {typeApi} {typeApi} => typeApi;");

            foreach (GeneratedField field in type.Fields)
            {
                writer.Blank();
                writer.Line("/// <inheritdoc />");
                writer.Line(
                    $"public {AccessorReturnType(field)} {AccessorName(field)}(int ordinal) => "
                    + $"typeApi.{AccessorName(field)}(ordinal);");
            }
        }

        // ---- Cached delegate ----
        if (_options.GenerateCachedDelegates)
        {
            writer.Blank();
            EmitCachedDelegate(writer, type, typeApi, delegateInterface, cachedDelegate);
        }

        // ---- Record wrapper ----
        writer.Blank();
        writer.Doc($"One <c>{type.TypeName}</c> record.");
        writer.Line($"public sealed class {record}({delegateInterface} recordDelegate, int ordinal)");
        writer.Line("    : HollowObject(recordDelegate, ordinal)");
        using (writer.Open())
        {
            writer.Doc("Where this record reads its fields from.");
            writer.Line($"public new {delegateInterface} Delegate => recordDelegate;");
            writer.Blank();
            writer.Doc("The API this record was read through.");
            writer.Line($"public {api} Api => recordDelegate.{typeApi}.Api;");

            foreach (GeneratedField field in type.Fields)
            {
                writer.Blank();
                EmitRecordProperty(writer, model, type, field);
            }
        }

        // ---- Factory ----
        writer.Blank();
        writer.Doc($"Builds <c>{type.TypeName}</c> wrappers for a provider to hand out.");
        writer.Line($"public sealed class {factory}({typeApi} typeApi) : HollowFactory<{record}>");
        using (writer.Open())
        {
            writer.Line("/// <inheritdoc />");
            writer.Line($"public override {record} NewHollowObject(");
            writer.Line("    IHollowTypeDataAccess ignoredDataAccess, HollowTypeApi? ignoredTypeApi, int ordinal) =>");
            writer.Line($"    new(new {lookupDelegate}(typeApi), ordinal);");

            if (_options.GenerateCachedDelegates)
            {
                writer.Blank();
                writer.Line("/// <inheritdoc />");
                writer.Line($"public override {record} NewCachedHollowObject(");
                writer.Line(
                    "    IHollowTypeDataAccess ignoredDataAccess, HollowTypeApi? ignoredTypeApi, int ordinal) =>");
                writer.Line($"    new(new {cachedDelegate}(typeApi, ordinal), ordinal);");
            }
        }

        return writer.ToString();
    }

    private static void EmitCachedDelegate(
        CodeWriter writer,
        GeneratedType type,
        string typeApi,
        string delegateInterface,
        string cachedDelegate)
    {
        writer.Doc($"Holds one <c>{type.TypeName}</c> record's field values, read once.");
        writer.Line($"public sealed class {cachedDelegate} : HollowObjectAbstractDelegate, "
            + $"{delegateInterface}, IHollowCachedDelegate");
        using (writer.Open())
        {
            foreach (GeneratedField field in type.Fields)
            {
                writer.Line(
                    $"private readonly {AccessorReturnType(field)} _{CodeNames.Camel(field.Name)};");
            }

            writer.Blank();
            writer.Line($"private {typeApi} _typeApi;");
            writer.Blank();

            writer.Doc($"Reads and holds the <c>{type.TypeName}</c> record at <paramref name=\"ordinal\"/>.");
            using (writer.Open($"public {cachedDelegate}({typeApi} typeApi, int ordinal)"))
            {
                writer.Line("_typeApi = typeApi;");

                foreach (GeneratedField field in type.Fields)
                {
                    writer.Line(
                        $"_{CodeNames.Camel(field.Name)} = typeApi.{AccessorName(field)}(ordinal);");
                }
            }

            writer.Blank();
            writer.Line("/// <inheritdoc />");
            writer.Line("public override HollowObjectSchema Schema => _typeApi.Schema;");
            writer.Blank();
            writer.Line("/// <inheritdoc />");
            writer.Line(
                "public override IHollowObjectTypeDataAccess TypeDataAccess => _typeApi.TypeDataAccess;");
            writer.Blank();
            writer.Line("/// <inheritdoc />");
            writer.Line("public override HollowObjectTypeApi? TypeApi => _typeApi;");
            writer.Blank();
            writer.Line("/// <inheritdoc />");
            writer.Line($"public {typeApi} {typeApi} => _typeApi;");
            writer.Blank();
            writer.Line("/// <inheritdoc />");
            writer.Line($"public void UpdateTypeApi(HollowTypeApi typeApi) => _typeApi = ({typeApi})typeApi;");

            foreach (GeneratedField field in type.Fields)
            {
                writer.Blank();
                writer.Line("/// <inheritdoc />");
                writer.Line(
                    $"public {AccessorReturnType(field)} {AccessorName(field)}(int ordinal) => "
                    + $"_{CodeNames.Camel(field.Name)};");
            }
        }
    }

    private static void EmitTypeApiAccessor(CodeWriter writer, GeneratedType type, GeneratedField field)
    {
        string name = AccessorName(field);
        int position = field.Position;

        if (field.IsReference)
        {
            writer.Doc(
                $"The ordinal of the <c>{field.ReferencedType}</c> record the <c>{field.Name}</c> field "
                + "points at.");
            writer.Line($"public int {name}(int ordinal) => ReadOrdinalField(ordinal, {position});");

            return;
        }

        writer.Doc($"Reads the <c>{field.Name}</c> field.");

        switch (field.Type)
        {
            case FieldType.Int:
                writer.Line(
                    $"public int? {name}(int ordinal) => "
                    + $"IsNullField(ordinal, {position}) ? null : ReadIntField(ordinal, {position});");
                break;

            case FieldType.Long:
                writer.Line(
                    $"public long? {name}(int ordinal) => "
                    + $"IsNullField(ordinal, {position}) ? null : ReadLongField(ordinal, {position});");
                break;

            case FieldType.Float:
                writer.Line(
                    $"public float? {name}(int ordinal) => "
                    + $"IsNullField(ordinal, {position}) ? null : ReadFloatField(ordinal, {position});");
                break;

            case FieldType.Double:
                writer.Line(
                    $"public double? {name}(int ordinal) => "
                    + $"IsNullField(ordinal, {position}) ? null : ReadDoubleField(ordinal, {position});");
                break;

            case FieldType.Boolean:
                writer.Line($"public bool? {name}(int ordinal) => ReadBooleanField(ordinal, {position});");
                break;

            case FieldType.Decimal:
                writer.Line(
                    $"public decimal? {name}(int ordinal) => ReadDecimalField(ordinal, {position});");
                break;

            case FieldType.String:
                writer.Line($"public string? {name}(int ordinal) => ReadStringField(ordinal, {position});");
                writer.Blank();
                writer.Doc(
                    $"Whether the <c>{field.Name}</c> field holds <paramref name=\"testValue\"/>, without "
                    + "materialising the stored string.");
                writer.Line(
                    $"public bool Is{field.PropertyName}Equal(int ordinal, string? testValue) => "
                    + $"IsStringFieldEqual(ordinal, {position}, testValue);");
                break;

            case FieldType.Bytes:
                writer.Line($"public byte[]? {name}(int ordinal) => ReadBytesField(ordinal, {position});");
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(field), field.Type, $"unknown field type on {type.TypeName}.{field.Name}");
        }
    }

    private void EmitRecordProperty(
        CodeWriter writer, GeneratedModel model, GeneratedType type, GeneratedField field)
    {
        string property = field.PropertyName;
        string accessor = AccessorName(field);

        if (!field.IsReference)
        {
            writer.Doc($"The <c>{field.Name}</c> field.");
            writer.Line(
                $"public {AccessorReturnType(field)} {property} => Delegate.{accessor}(Ordinal);");

            if (field.Type == FieldType.String)
            {
                writer.Blank();
                writer.Doc(
                    $"Whether <c>{field.Name}</c> holds <paramref name=\"testValue\"/>, without "
                    + "materialising the stored string.");
                writer.Line(
                    $"public bool Is{property}Equal(string? testValue) => "
                    + $"Delegate.{CodeNames.TypeApi(type.TypeName)}."
                    + $"Is{property}Equal(Ordinal, testValue);");
            }

            return;
        }

        GeneratedType? referenced = field.ReferencedType is null ? null : model.Find(field.ReferencedType);
        string referencedRecord = model.RecordTypeOf(field.ReferencedType);
        string referencedAccessor = "Get" + referencedRecord;

        bool shortcut = _options.UseErgonomicShortcuts && referenced?.ShortcutValueType is not null;
        string recordProperty = shortcut ? property + "Record" : property;

        if (shortcut)
        {
            writer.Doc(
                $"The value of the <c>{referenced!.TypeName}</c> record the <c>{field.Name}</c> field "
                + "points at.");
            writer.Line(
                $"public {referenced.ShortcutValueType} {property} => {recordProperty}?.Value;");
            writer.Blank();
        }

        writer.Doc($"The <c>{field.ReferencedType}</c> record the <c>{field.Name}</c> field points at.");
        writer.Line(
            $"public {referencedRecord}? {recordProperty} => Api.{referencedAccessor}("
            + $"Delegate.{accessor}(Ordinal));");

        // A reference to the shared String type is where the comparison that avoids materialising the
        // stored string pays off, so it is worth a method of its own.
        if (referenced?.BuiltIn is { FieldType: FieldType.String } builtInString)
        {
            writer.Blank();
            writer.Doc(
                $"Whether <c>{field.Name}</c> holds <paramref name=\"testValue\"/>, without "
                + "materialising the stored string.");
            using (writer.Open($"public bool Is{property}Equal(string? testValue)"))
            {
                writer.Line($"int referenced = Delegate.{accessor}(Ordinal);");
                writer.Blank();
                writer.Line("return referenced == HollowConstants.OrdinalNone");
                writer.Line("    ? testValue is null");
                writer.Line(
                    $"    : Api.{builtInString.RecordType}TypeApi.IsValueEqual(referenced, testValue);");
            }
        }
    }

    private string GenerateListType(GeneratedModel model, GeneratedType type) =>
        GenerateCollectionType(
            model,
            type,
            baseType: $"HollowList<{model.RecordTypeOf(type.ElementType)}>",
            typeApiBase: "HollowListTypeApi",
            typeApiDataAccess: "IHollowListTypeDataAccess",
            lookupDelegate: $"HollowListLookupDelegate<{model.RecordTypeOf(type.ElementType)}>",
            cachedDelegate: $"HollowListCachedDelegate<{model.RecordTypeOf(type.ElementType)}>");

    private string GenerateSetType(GeneratedModel model, GeneratedType type) =>
        GenerateCollectionType(
            model,
            type,
            baseType: $"HollowSet<{model.RecordTypeOf(type.ElementType)}>",
            typeApiBase: "HollowSetTypeApi",
            typeApiDataAccess: "IHollowSetTypeDataAccess",
            lookupDelegate: $"HollowSetLookupDelegate<{model.RecordTypeOf(type.ElementType)}>",
            cachedDelegate: $"HollowSetCachedDelegate<{model.RecordTypeOf(type.ElementType)}>");

    private string GenerateCollectionType(
        GeneratedModel model,
        GeneratedType type,
        string baseType,
        string typeApiBase,
        string typeApiDataAccess,
        string lookupDelegate,
        string cachedDelegate)
    {
        CodeWriter writer = new();
        string api = _options.ResolvedApiClassName;
        string record = type.RecordType;
        string typeApi = CodeNames.TypeApi(type.TypeName);
        string element = model.RecordTypeOf(type.ElementType);
        string elementAccessor = "Get" + element;
        string delegateType = type.Kind == SchemaType.List
            ? $"IHollowListDelegate<{element}>"
            : $"IHollowSetDelegate<{element}>";

        Preamble(writer);

        writer.Doc($"Reads the <c>{type.TypeName}</c> records by ordinal.");
        writer.Line(
            $"public sealed class {typeApi}({api} api, {typeApiDataAccess} typeDataAccess)");
        writer.Line($"    : {typeApiBase}(api, typeDataAccess)");
        using (writer.Open())
        {
            writer.Doc("The API this type belongs to.");
            writer.Line($"public new {api} Api => ({api})base.Api;");
        }

        writer.Blank();
        writer.Doc($"One <c>{type.TypeName}</c> record.");
        writer.Line($"public sealed class {record}({delegateType} recordDelegate, int ordinal, {api} api)");
        writer.Line($"    : {baseType}(recordDelegate, ordinal)");
        using (writer.Open())
        {
            writer.Line("/// <inheritdoc />");
            writer.Line(
                $"public override {element} InstantiateElement(int elementOrdinal) => "
                + $"api.{elementAccessor}(elementOrdinal)!;");
            writer.Blank();
            writer.Line("/// <inheritdoc />");
            writer.Line(
                "public override bool EqualsElement(int elementOrdinal, object? other) => "
                + $"other is IHollowRecord record && record.Ordinal == elementOrdinal && "
                + $"record.Schema.Name == {Quote(type.ElementType!)};");
        }

        writer.Blank();
        writer.Doc($"Builds <c>{type.TypeName}</c> wrappers for a provider to hand out.");
        writer.Line(
            $"public sealed class {CodeNames.Factory(type.TypeName)}({typeApi} typeApi) "
            + $": HollowFactory<{record}>");
        using (writer.Open())
        {
            writer.Line("/// <inheritdoc />");
            writer.Line($"public override {record} NewHollowObject(");
            writer.Line("    IHollowTypeDataAccess ignoredDataAccess, HollowTypeApi? ignoredTypeApi, int ordinal) =>");
            writer.Line($"    new(new {lookupDelegate}(typeApi), ordinal, typeApi.Api);");
            writer.Blank();
            writer.Line("/// <inheritdoc />");
            writer.Line($"public override {record} NewCachedHollowObject(");
            writer.Line("    IHollowTypeDataAccess ignoredDataAccess, HollowTypeApi? ignoredTypeApi, int ordinal) =>");
            writer.Line($"    new(new {cachedDelegate}(typeApi.TypeDataAccess, ordinal), ordinal, typeApi.Api);");
        }

        return writer.ToString();
    }

    private string GenerateMapType(GeneratedModel model, GeneratedType type)
    {
        CodeWriter writer = new();
        string api = _options.ResolvedApiClassName;
        string record = type.RecordType;
        string typeApi = CodeNames.TypeApi(type.TypeName);
        string key = model.RecordTypeOf(type.KeyType);
        string value = model.RecordTypeOf(type.ValueType);
        string keyAccessor = "Get" + key;
        string valueAccessor = "Get" + value;

        Preamble(writer);

        writer.Doc($"Reads the <c>{type.TypeName}</c> records by ordinal.");
        writer.Line($"public sealed class {typeApi}({api} api, IHollowMapTypeDataAccess typeDataAccess)");
        writer.Line("    : HollowMapTypeApi(api, typeDataAccess)");
        using (writer.Open())
        {
            writer.Doc("The API this type belongs to.");
            writer.Line($"public new {api} Api => ({api})base.Api;");
        }

        writer.Blank();
        writer.Doc($"One <c>{type.TypeName}</c> record.");
        writer.Line(
            $"public sealed class {record}(IHollowMapDelegate<{key}, {value}> recordDelegate, int ordinal, "
            + $"{api} api)");
        writer.Line($"    : HollowMap<{key}, {value}>(recordDelegate, ordinal)");
        using (writer.Open())
        {
            writer.Line("/// <inheritdoc />");
            writer.Line(
                $"public override {key} InstantiateKey(int keyOrdinal) => api.{keyAccessor}(keyOrdinal)!;");
            writer.Blank();
            writer.Line("/// <inheritdoc />");
            writer.Line(
                $"public override {value} InstantiateValue(int valueOrdinal) => "
                + $"api.{valueAccessor}(valueOrdinal)!;");
            writer.Blank();
            writer.Line("/// <inheritdoc />");
            writer.Line(
                "public override bool EqualsKey(int keyOrdinal, object? other) => "
                + $"other is IHollowRecord record && record.Ordinal == keyOrdinal && "
                + $"record.Schema.Name == {Quote(type.KeyType!)};");
            writer.Blank();
            writer.Line("/// <inheritdoc />");
            writer.Line(
                "public override bool EqualsValue(int valueOrdinal, object? other) => "
                + $"other is IHollowRecord record && record.Ordinal == valueOrdinal && "
                + $"record.Schema.Name == {Quote(type.ValueType!)};");
        }

        writer.Blank();
        writer.Doc($"Builds <c>{type.TypeName}</c> wrappers for a provider to hand out.");
        writer.Line(
            $"public sealed class {CodeNames.Factory(type.TypeName)}({typeApi} typeApi) "
            + $": HollowFactory<{record}>");
        using (writer.Open())
        {
            writer.Line("/// <inheritdoc />");
            writer.Line($"public override {record} NewHollowObject(");
            writer.Line("    IHollowTypeDataAccess ignoredDataAccess, HollowTypeApi? ignoredTypeApi, int ordinal) =>");
            writer.Line(
                $"    new(new HollowMapLookupDelegate<{key}, {value}>(typeApi), ordinal, typeApi.Api);");
            writer.Blank();
            writer.Line("/// <inheritdoc />");
            writer.Line($"public override {record} NewCachedHollowObject(");
            writer.Line("    IHollowTypeDataAccess ignoredDataAccess, HollowTypeApi? ignoredTypeApi, int ordinal) =>");
            writer.Line(
                $"    new(new HollowMapCachedDelegate<{key}, {value}>(typeApi.TypeDataAccess, ordinal), "
                + "ordinal, typeApi.Api);");
        }

        return writer.ToString();
    }

    // ---- The API ----

    private string GenerateApi(GeneratedModel model)
    {
        CodeWriter writer = new();
        string api = _options.ResolvedApiClassName;

        Preamble(writer);

        writer.Line("/// <summary>");
        writer.Line("/// A typed view over the dataset, with one accessor per type.");
        writer.Line("/// </summary>");
        writer.Line("/// <remarks>");
        writer.Line("/// Pass the factory below to a consumer to have it hand this out as");
        writer.Line("/// <c>HollowConsumer.Api</c>, or construct it directly over a read state engine.");
        writer.Line("/// </remarks>");
        using (writer.Open($"public sealed class {api} : HollowApi"))
        {
            foreach (GeneratedType type in model.Types)
            {
                writer.Line(
                    $"private readonly HollowObjectProvider<{type.RecordType}> "
                    + $"_{CodeNames.Camel(type.TypeName)}Provider;");
            }

            writer.Blank();
            writer.Doc("Reads <paramref name=\"dataAccess\"/>, caching nothing.");
            writer.Line($"public {api}(IHollowDataAccess dataAccess)");
            writer.Line("    : this(dataAccess, new HashSet<string>(StringComparer.Ordinal))");
            using (writer.Open())
            {
            }

            writer.Blank();
            writer.Doc(
                "Reads <paramref name=\"dataAccess\"/>, holding a wrapper per record of each type in "
                + "<paramref name=\"cachedTypes\"/>.");
            writer.Line($"public {api}(IHollowDataAccess dataAccess, ISet<string> cachedTypes)");
            writer.Line("    : base(dataAccess)");
            using (writer.Open())
            {
                writer.Line("ArgumentNullException.ThrowIfNull(cachedTypes);");
                writer.Blank();

                foreach (GeneratedType type in model.Types)
                {
                    EmitApiTypeConstruction(writer, type);
                }
            }

            foreach (GeneratedType type in model.Types)
            {
                writer.Blank();
                EmitApiTypeMembers(writer, type);
            }

            writer.Blank();
            writer.Line("/// <inheritdoc />");
            using (writer.Open("public override void DetachCaches()"))
            {
                foreach (GeneratedType type in model.Types)
                {
                    writer.Line(
                        $"(_{CodeNames.Camel(type.TypeName)}Provider as "
                        + $"HollowObjectCacheProvider<{type.RecordType}>)?.Detach();");
                }
            }

            writer.Blank();
            writer.Doc("The types this API caches by default when none are named.");
            writer.Line(
                "public static readonly string[] DefaultCachedTypes = ["
                + string.Join(", ", _options.DefaultCachedTypes.Select(Quote)) + "];");
        }

        writer.Blank();
        writer.Doc($"Builds a <see cref=\"{api}\"/> for a consumer.");
        using (writer.Open($"public sealed class {api}Factory : IHollowApiFactory"))
        {
            writer.Doc("Caches every record of the named types.");
            writer.Line($"public {api}Factory(params string[] cachedTypes)");
            using (writer.Open())
            {
                writer.Line("CachedTypes = [.. cachedTypes.Length == 0 "
                    + $"? {api}.DefaultCachedTypes : cachedTypes];");
            }

            writer.Blank();
            writer.Doc("The types this factory's APIs cache.");
            writer.Line("public HashSet<string> CachedTypes { get; }");
            writer.Blank();
            writer.Line("/// <inheritdoc />");
            writer.Line(
                $"public HollowApi CreateApi(IHollowDataAccess dataAccess) => "
                + $"new {api}(dataAccess, CachedTypes);");
        }

        return writer.ToString();
    }

    private static void EmitApiTypeConstruction(CodeWriter writer, GeneratedType type)
    {
        string parameter = CodeNames.Camel(type.TypeName);
        string typeApi = TypeApiPropertyName(type);

        writer.Line($"{typeApi} = {TypeApiConstruction(type)};");
        writer.Line($"AddTypeApi({typeApi});");

        // A cache walks the type's populated ordinals, which a type the dataset does not have cannot
        // supply, so a missing type always reads through the factory instead.
        writer.Line(
            $"_{parameter}Provider = cachedTypes.Contains({Quote(type.TypeName)}) && {typeApi}.IsTypePresent");
        writer.Line(
            $"    ? new HollowObjectCacheProvider<{type.RecordType}>("
            + $"{typeApi}.TypeDataAccess, {typeApi}, {FactoryConstruction(type)})");
        writer.Line(
            $"    : new HollowObjectFactoryProvider<{type.RecordType}>("
            + $"{typeApi}.TypeDataAccess, {typeApi}, {FactoryConstruction(type)});");
        writer.Blank();
    }

    private static void EmitApiTypeMembers(CodeWriter writer, GeneratedType type)
    {
        string parameter = CodeNames.Camel(type.TypeName);
        string typeApiProperty = TypeApiPropertyName(type);
        string accessor = "Get" + type.RecordType;

        writer.Doc($"Reads the <c>{type.TypeName}</c> records by ordinal.");
        writer.Line($"public {TypeApiTypeName(type)} {typeApiProperty} {{ get; }}");
        writer.Blank();

        writer.Doc(
            $"The <c>{type.TypeName}</c> record at <paramref name=\"ordinal\"/>, or "
            + "<see langword=\"null\"/> when there is none.");
        writer.Line(
            $"public {type.RecordType}? {accessor}(int ordinal) => ordinal == HollowConstants.OrdinalNone");
        writer.Line("    ? null");
        writer.Line($"    : _{parameter}Provider.GetHollowObject(ordinal);");
        writer.Blank();

        writer.Doc($"Every <c>{type.TypeName}</c> record in the dataset.");
        writer.Line($"public IEnumerable<{type.RecordType}> All{type.RecordType} =>");
        writer.Line($"    {typeApiProperty}.IsTypePresent");
        writer.Line(
            $"        ? {typeApiProperty}.TypeDataAccess.TypeState.PopulatedOrdinals.EnumerateSetBits()");
        writer.Line($"            .Select({accessor})");
        writer.Line("            .Where(record => record is not null)");
        writer.Line("            .Select(record => record!)");
        writer.Line("        : [];");
    }

    // ---- The unique-key index ----

    private string GenerateUniqueKeyIndex(GeneratedType type)
    {
        HollowObjectSchema schema = (HollowObjectSchema)type.Schema;
        PrimaryKey key = schema.PrimaryKey!;
        CodeWriter writer = new();
        string api = _options.ResolvedApiClassName;
        string index = CodeNames.UniqueKeyIndex(type.TypeName);

        Preamble(writer);

        writer.Line("/// <summary>");
        writer.Line(
            $"/// Finds the one <c>{type.TypeName}</c> record holding a given "
            + "<c>" + string.Join(", ", key.FieldPaths) + "</c>.");
        writer.Line("/// </summary>");
        writer.Line("/// <remarks>");
        writer.Line("/// Add it to the consumer as a refresh listener to have it follow the data.");
        writer.Line("/// </remarks>");
        writer.Line($"public sealed class {index} : IRefreshListener, IRefreshRegistrationListener, IDisposable");
        using (writer.Open())
        {
            writer.Line("private readonly HollowConsumer _consumer;");
            writer.Line("private HollowPrimaryKeyIndex _index;");
            writer.Blank();

            writer.Doc($"Indexes the <c>{type.TypeName}</c> records <paramref name=\"consumer\"/> holds.");
            using (writer.Open($"public {index}(HollowConsumer consumer)"))
            {
                writer.Line("ArgumentNullException.ThrowIfNull(consumer);");
                writer.Blank();
                writer.Line("_consumer = consumer;");
                writer.Line("_index = new HollowPrimaryKeyIndex(");
                writer.Line(
                    "    consumer.StateEngine ?? throw new InvalidOperationException("
                    + "\"the consumer holds no data yet\"),");
                writer.Line($"    {Quote(type.TypeName)},");
                writer.Line($"    {string.Join(", ", key.FieldPaths.Select(Quote))});");
            }

            writer.Blank();
            writer.Doc($"The <c>{type.TypeName}</c> holding the given key, or <see langword=\"null\"/>.");
            writer.Line(
                $"public {type.RecordType}? FindMatch({string.Join(", ", KeyParameters(type, key))})");
            using (writer.Open())
            {
                writer.Line(
                    "int ordinal = _index.GetMatchingOrdinal("
                    + string.Join(", ", key.FieldPaths.Select(path => CodeNames.Parameter(LastSegment(path))))
                    + ");");
                writer.Blank();
                writer.Line(
                    $"return (({api})(_consumer.Api ?? throw new InvalidOperationException("
                    + "\"the consumer holds no data yet\")))");
                writer.Line($"    .Get{type.RecordType}(ordinal);");
            }

            writer.Blank();
            writer.Line("/// <inheritdoc />");
            writer.Line("public void RefreshStarted(long currentVersion, long requestedVersion)");
            using (writer.Open())
            {
            }

            writer.Blank();
            writer.Line("/// <inheritdoc />");
            using (writer.Open(
                "public void SnapshotUpdateOccurred(HollowReadStateEngine stateEngine, long version)"))
            {
                writer.Line("HollowPrimaryKeyIndex previous = _index;");
                writer.Line("previous.DetachFromDeltaUpdates();");
                writer.Blank();
                writer.Line("HollowPrimaryKeyIndex rebuilt = new(stateEngine, previous.PrimaryKey);");
                writer.Line("rebuilt.ListenForDeltaUpdates();");
                writer.Line("_index = rebuilt;");
                writer.Line("previous.Dispose();");
            }

            writer.Blank();
            writer.Line("/// <inheritdoc />");
            writer.Line("public void DeltaUpdateOccurred(HollowReadStateEngine stateEngine, long version)");
            using (writer.Open())
            {
            }

            writer.Blank();
            writer.Line("/// <inheritdoc />");
            writer.Line("public void BlobLoaded(Blob transition)");
            using (writer.Open())
            {
            }

            writer.Blank();
            writer.Line("/// <inheritdoc />");
            writer.Line(
                "public void RefreshSuccessful(long beforeVersion, long afterVersion, long requestedVersion)");
            using (writer.Open())
            {
            }

            writer.Blank();
            writer.Line("/// <inheritdoc />");
            writer.Line("public void RefreshFailed(");
            writer.Line(
                "    long beforeVersion, long afterVersion, long requestedVersion, Exception failureCause)");
            using (writer.Open())
            {
            }

            writer.Blank();
            writer.Line("/// <inheritdoc />");
            writer.Line("public void OnBeforeAddition(HollowConsumer consumer) => _index.ListenForDeltaUpdates();");
            writer.Blank();
            writer.Line("/// <inheritdoc />");
            writer.Line(
                "public void OnAfterRemoval(HollowConsumer consumer) => _index.DetachFromDeltaUpdates();");
            writer.Blank();
            writer.Line("/// <inheritdoc />");
            writer.Line("public void Dispose() => _index.Dispose();");
        }

        return writer.ToString();
    }

    private static IEnumerable<string> KeyParameters(GeneratedType type, PrimaryKey key)
    {
        HollowObjectSchema schema = (HollowObjectSchema)type.Schema;

        foreach (string path in key.FieldPaths)
        {
            string first = path.Split('.')[0].TrimEnd('!');
            FieldType? fieldType = schema.GetFieldType(first);

            // A key path that stays inside this type names its own field's type; one that crosses a
            // reference is matched on whatever the underlying index resolves it to, which is loosest as
            // object.
            string parameterType = path.Contains('.', StringComparison.Ordinal) || fieldType is null
                ? "object"
                : fieldType == FieldType.Reference
                    ? "object"
                    : CodeNames.ValueTypeOf(fieldType.Value).TrimEnd('?');

            yield return $"{parameterType} {CodeNames.Parameter(LastSegment(path))}";
        }
    }

    private static string LastSegment(string path) => path.TrimEnd('!').Split('.')[^1];

    // ---- Shared pieces ----

    private static string TypeApiPropertyName(GeneratedType type) =>
        type.BuiltIn is not null ? type.BuiltIn.RecordType + "TypeApi" : CodeNames.TypeApi(type.TypeName);

    private static string TypeApiTypeName(GeneratedType type) =>
        type.BuiltIn?.TypeApiType ?? CodeNames.TypeApi(type.TypeName);

    private static string TypeApiConstruction(GeneratedType type)
    {
        string dataAccess =
            $"({DataAccessInterfaceOf(type.Kind)})(dataAccess.GetTypeDataAccess({Quote(type.TypeName)}) "
            + $"?? new {MissingDataAccessOf(type.Kind)}(dataAccess, {Quote(type.TypeName)}))";

        return $"new {TypeApiTypeName(type)}(this, {dataAccess})";
    }

    private static string FactoryConstruction(GeneratedType type) =>
        type.BuiltIn is not null
            ? $"HollowScalarTypes.Factory<{type.BuiltIn.RecordType}>()"
            : $"new {CodeNames.Factory(type.TypeName)}({TypeApiPropertyName(type)})";

    private static string DataAccessInterfaceOf(SchemaType kind) =>
        kind switch
        {
            SchemaType.Object => "IHollowObjectTypeDataAccess",
            SchemaType.List => "IHollowListTypeDataAccess",
            SchemaType.Set => "IHollowSetTypeDataAccess",
            SchemaType.Map => "IHollowMapTypeDataAccess",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown record kind"),
        };

    private static string MissingDataAccessOf(SchemaType kind) =>
        kind switch
        {
            SchemaType.Object => "HollowObjectMissingDataAccess",
            SchemaType.List => "HollowListMissingDataAccess",
            SchemaType.Set => "HollowSetMissingDataAccess",
            SchemaType.Map => "HollowMapMissingDataAccess",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown record kind"),
        };

    private static string AccessorName(GeneratedField field) =>
        field.IsReference ? $"Get{field.PropertyName}Ordinal" : $"Get{field.PropertyName}";

    private static string AccessorReturnType(GeneratedField field) =>
        field.IsReference
            ? "int"
            : field.Type switch
            {
                FieldType.Int => "int?",
                FieldType.Long => "long?",
                FieldType.Float => "float?",
                FieldType.Double => "double?",
                _ => CodeNames.ValueTypeOf(field.Type),
            };

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private void Preamble(CodeWriter writer)
    {
        writer.Line("// <auto-generated />");
        writer.Line("// Generated by Hollow.Api.Codegen. Changes here are lost on the next run.");
        writer.Line("#nullable enable");
        writer.Line();
        writer.Line("using System;");
        writer.Line("using System.Collections.Generic;");
        writer.Line("using System.Linq;");
        writer.Line("using Hollow.Api.Client;");
        writer.Line("using Hollow.Api.Consumer;");
        writer.Line("using Hollow.Api.Custom;");
        writer.Line("using Hollow.Api.Objects;");
        writer.Line("using Hollow.Api.Objects.Delegate;");
        writer.Line("using Hollow.Api.Objects.Provider;");
        writer.Line("using Hollow.Core;");
        writer.Line("using Hollow.Core.Index;");
        writer.Line("using Hollow.Core.Read.DataAccess;");
        writer.Line("using Hollow.Core.Read.Engine;");
        writer.Line("using Hollow.Core.Schema;");
        writer.Line("using Hollow.Core.Types;");
        writer.Line();
        writer.Line($"namespace {_options.Namespace};");
        writer.Line();
    }
}
