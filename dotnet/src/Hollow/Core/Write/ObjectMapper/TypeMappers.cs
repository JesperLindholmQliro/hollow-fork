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

using System.Collections;
using System.Globalization;
using System.Reflection;
using Hollow.Core.Index.Key;
using Hollow.Core.Schema;
using Hollow.Core.Write.ObjectMapper.FlatRecords;
using Hollow.Core.Write.ObjectMapper.FlatRecords.Traversal;

namespace Hollow.Core.Write.ObjectMapper;

/// <summary>
/// Maps a CLR class or struct onto a Hollow object type.
/// </summary>
public sealed class HollowObjectTypeMapper : HollowTypeMapper
{
    private readonly HollowObjectMapper _parentMapper;
    private readonly Type _type;
    private readonly HollowObjectSchema _schema;
    private readonly List<MappedFieldInfo> _fields = [];
    private readonly int _numShards;

    private int[][]? _primaryKeyFieldPathIndexes;

    internal HollowObjectTypeMapper(HollowObjectMapper parentMapper, Type type, string? typeName)
    {
        _parentMapper = parentMapper;
        _type = type;

        TypeName = HollowObjectMapper.ResolveTypeName(type, typeName);

        _numShards = type.GetCustomAttribute<HollowShardLargeTypeAttribute>()?.NumShards ?? -1;

        string[]? primaryKey = type.GetCustomAttribute<HollowPrimaryKeyAttribute>()?.Fields;

        if (IsScalarWrapper(type))
        {
            // A scalar stored by reference becomes a one-field wrapper type, matching Java's
            // Integer/Long/String and friends.
            _schema = new HollowObjectSchema(TypeName, 1, primaryKey);
            _schema.AddField("value", ScalarFieldType(type));
            _fields.Add(MappedFieldInfo.Scalar(
                "value", ScalarFieldType(type), type, static instance => instance));
        }
        else if (type.IsEnum)
        {
            // An enum becomes a type carrying its member name, so the data stays readable if the
            // enum's numeric values are ever renumbered.
            _schema = new HollowObjectSchema(TypeName, 1, primaryKey);
            _schema.AddField("_name", FieldType.String);
            _fields.Add(MappedFieldInfo.Scalar(
                "_name",
                FieldType.String,
                typeof(string),
                static instance => instance.ToString() ?? string.Empty));
        }
        else
        {
            List<MappedMember> members = MappedMember.ForType(type);
            _schema = new HollowObjectSchema(TypeName, members.Count, primaryKey);

            foreach (MappedMember member in members)
            {
                _fields.Add(AddField(member));
            }
        }
    }

    /// <inheritdoc />
    public override string TypeName { get; }

    /// <inheritdoc />
    public override HollowSchema Schema => _schema;

    /// <inheritdoc />
    public override int Write(object value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return _parentMapper.StateEngine.Add(TypeName, ToWriteRecord(value, null));
    }

    /// <inheritdoc />
    public override int WriteFlat(object value, FlatRecordWriter writer)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(writer);

        return writer.Write(_schema, ToWriteRecord(value, writer));
    }

    /// <summary>
    /// Builds the record for <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The object to write.</param>
    /// <param name="flatWriter">
    /// Where a referenced object goes, or <see langword="null"/> to put it in the state engine. It is
    /// the only difference between the two ways of writing: a reference field holds an index into a
    /// flat record one way and a dataset ordinal the other, and everything else about a record is the
    /// same either way.
    /// </param>
    private HollowObjectWriteRecord ToWriteRecord(object value, FlatRecordWriter? flatWriter)
    {
        HollowObjectWriteRecord record = new(_schema);

        foreach (MappedFieldInfo field in _fields)
        {
            object? memberValue = field.GetValue(value);
            if (memberValue is null)
            {
                continue;
            }

            switch (field.FieldType)
            {
                case FieldType.Int:
                    record.SetInt(field.Name, ToInt32(field.Name, memberValue));
                    break;
                case FieldType.Long:
                    record.SetLong(field.Name, ToInt64(field.Name, memberValue));
                    break;
                case FieldType.Float:
                    record.SetFloat(field.Name, Convert.ToSingle(memberValue, CultureInfo.InvariantCulture));
                    break;
                case FieldType.Double:
                    record.SetDouble(field.Name, Convert.ToDouble(memberValue, CultureInfo.InvariantCulture));
                    break;
                case FieldType.Decimal:
                    record.SetDecimal(field.Name, Convert.ToDecimal(memberValue, CultureInfo.InvariantCulture));
                    break;
                case FieldType.Boolean:
                    record.SetBoolean(field.Name, Convert.ToBoolean(memberValue, CultureInfo.InvariantCulture));
                    break;
                case FieldType.String:
                    record.SetString(field.Name, ToHollowString(memberValue));
                    break;
                case FieldType.Bytes:
                    record.SetBytes(field.Name, (byte[])memberValue);
                    break;
                case FieldType.Reference:
                {
                    // Resolve by the declared type, not the runtime type, so that the ordinal lands in
                    // the type the schema says this field points at.
                    HollowTypeMapper referenced = _parentMapper.GetTypeMapper(
                        field.DeclaredType!,
                        field.TypeNameOverride,
                        field.HashKeyFieldPaths,
                        field.CollectionTypeNames);

                    record.SetReference(
                        field.Name,
                        flatWriter is null
                            ? referenced.Write(memberValue)
                            : referenced.WriteFlat(memberValue, flatWriter));

                    break;
                }

                default:
                    throw new HollowMappingException(
                        $"Field {field.Name} of type {TypeName} has unmappable field type {field.FieldType}");
            }
        }

        return record;
    }

    /// <inheritdoc />
    public override object? ParseFlatRecord(IFlatRecordTraversalNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is not FlatRecordTraversalObjectNode objectNode)
        {
            throw new HollowMappingException(
                $"{TypeName} is an object type, and the record is a {node.Schema.SchemaType}");
        }

        // A wrapper and an enum are the value they wrap, not an object with a field: the field is an
        // artefact of there being no other way to store a bare scalar.
        if (IsScalarWrapper(_type))
        {
            return MappedValues.ConvertScalar(objectNode.GetFieldValue("value"), _type);
        }

        if (_type.IsEnum)
        {
            return MappedValues.ConvertScalar(objectNode.GetString("_name"), _type);
        }

        Dictionary<string, object?> values = new(_fields.Count, StringComparer.Ordinal);

        foreach (MappedFieldInfo field in _fields)
        {
            values[field.Name] = field.FieldType == FieldType.Reference
                ? _parentMapper
                    .GetTypeMapper(
                        field.DeclaredType!,
                        field.TypeNameOverride,
                        field.HashKeyFieldPaths,
                        field.CollectionTypeNames)
                    .ParseFlatRecord(objectNode.GetFieldNode(field.Name))
                : MappedValues.ConvertScalar(objectNode.GetFieldValue(field.Name), field.MemberType!);
        }

        return Build(values);
    }

    /// <summary>
    /// Builds an instance of the mapped type from the values read out of a record.
    /// </summary>
    /// <remarks>
    /// A constructor whose parameters all name mapped members is preferred, which is what makes a
    /// record type or a primary constructor work. Failing that the type is constructed empty and its
    /// members assigned, and anything left unassignable is an error rather than a silent null: a model
    /// that cannot be filled in is a model this cannot round-trip, and saying so beats handing back a
    /// half-built object.
    /// </remarks>
    private object Build(Dictionary<string, object?> values)
    {
        if (MatchingConstructor() is { } constructor)
        {
            return constructor.Invoke(
                [.. constructor.GetParameters().Select(parameter => values[Named(parameter.Name!)])]);
        }

        object instance = Activator.CreateInstance(_type)
            ?? throw new HollowMappingException(
                $"{_type.Name} could not be created, so a record of {TypeName} cannot be read back");

        foreach (MappedFieldInfo field in _fields)
        {
            if (field.SetValue is not { } set)
            {
                throw new HollowMappingException(
                    $"{_type.Name}.{field.Name} cannot be assigned and is not a constructor parameter, "
                    + $"so a record of {TypeName} cannot be read back");
            }

            set(instance, values[field.Name]);
        }

        return instance;

        string Named(string parameter) =>
            _fields.FirstOrDefault(field =>
                string.Equals(field.Name, parameter, StringComparison.OrdinalIgnoreCase))?.Name
            ?? parameter;
    }

    /// <summary>
    /// The constructor whose every parameter names a mapped member, preferring the one that covers the
    /// most of them.
    /// </summary>
    private ConstructorInfo? MatchingConstructor() =>
        _type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(constructor =>
                constructor.GetParameters().Length > 0
                && constructor.GetParameters().All(parameter =>
                    _fields.Any(field =>
                        string.Equals(field.Name, parameter.Name, StringComparison.OrdinalIgnoreCase))))
            .MaxBy(constructor => constructor.GetParameters().Length);

    /// <summary>
    /// Reads the primary key of <paramref name="value"/> out of the CLR object, as the values the
    /// schema's key field paths point at.
    /// </summary>
    /// <remarks>
    /// The key is what identifies a record to an incremental cycle, which has to find the record in
    /// the previous state without serialising the object first.
    /// </remarks>
    /// <exception cref="ArgumentException">This type has no primary key.</exception>
    internal object?[] ExtractPrimaryKey(object value)
    {
        int[][] fieldPathIndexes = _primaryKeyFieldPathIndexes ??= CalculatePrimaryKeyFieldPathIndexes();

        object?[] key = new object?[fieldPathIndexes.Length];
        for (int i = 0; i < key.Length; i++)
        {
            key[i] = RetrieveFieldValue(value, fieldPathIndexes[i], 0);
        }

        return key;
    }

    private int[][] CalculatePrimaryKeyFieldPathIndexes()
    {
        PrimaryKey primaryKey = _schema.PrimaryKey
            ?? throw new ArgumentException($"Type {TypeName} does not have a primary key defined.");

        int[][] fieldPathIndexes = new int[primaryKey.FieldCount][];
        for (int i = 0; i < fieldPathIndexes.Length; i++)
        {
            fieldPathIndexes[i] = primaryKey.GetFieldPathIndex(_parentMapper.StateEngine, i);
        }

        return fieldPathIndexes;
    }

    /// <summary>
    /// Follows one key field path into the CLR object, returning the value at its end.
    /// </summary>
    private object? RetrieveFieldValue(object value, int[] fieldPathIndex, int depth)
    {
        MappedFieldInfo field = _fields[fieldPathIndex[depth]];
        object? fieldValue = field.GetValue(value);

        if (fieldValue is null)
        {
            return null;
        }

        if (depth < fieldPathIndex.Length - 1)
        {
            if (field.FieldType != FieldType.Reference)
            {
                throw new ArgumentException(
                    $"The primary key of type {TypeName} steps through field {field.Name}, which is a "
                    + $"{field.FieldType} field rather than a reference.");
            }

            HollowObjectTypeMapper referenced = (HollowObjectTypeMapper)_parentMapper.GetTypeMapper(
                field.DeclaredType!, field.TypeNameOverride, field.HashKeyFieldPaths,
                field.CollectionTypeNames);

            return referenced.RetrieveFieldValue(fieldValue, fieldPathIndex, depth + 1);
        }

        // The index compares a key against the stored field, so the value has to arrive as the CLR type
        // that field's Hollow type reads back as — an enum or a short reaching an Int field, say.
        return field.FieldType switch
        {
            FieldType.Int => Convert.ToInt32(fieldValue, CultureInfo.InvariantCulture),
            FieldType.Long => Convert.ToInt64(fieldValue, CultureInfo.InvariantCulture),
            FieldType.Float => Convert.ToSingle(fieldValue, CultureInfo.InvariantCulture),
            FieldType.Double => Convert.ToDouble(fieldValue, CultureInfo.InvariantCulture),
            FieldType.Decimal => Convert.ToDecimal(fieldValue, CultureInfo.InvariantCulture),
            FieldType.Boolean => Convert.ToBoolean(fieldValue, CultureInfo.InvariantCulture),
            FieldType.String => ToHollowString(fieldValue),
            _ => fieldValue,
        };
    }

    /// <inheritdoc />
    protected override HollowTypeWriteState CreateWriteState() =>
        new HollowObjectTypeWriteState(
            _schema, _numShards, _parentMapper.StateEngine.PartitionedOrdinalMap);

    /// <inheritdoc />
    protected internal override void RegisterReferencedTypes(HollowObjectMapper parentMapper)
    {
        foreach (MappedFieldInfo field in _fields)
        {
            if (field.FieldType == FieldType.Reference)
            {
                parentMapper.GetTypeMapper(
                    field.DeclaredType!,
                    field.TypeNameOverride,
                    field.HashKeyFieldPaths,
                    field.CollectionTypeNames);
            }
        }
    }

    /// <summary>
    /// Whether a CLR type is one Hollow stores directly rather than as a record of its own members.
    /// </summary>
    internal static bool IsScalarWrapper(Type type)
    {
        Type underlying = Nullable.GetUnderlyingType(type) ?? type;

        return underlying == typeof(string)
            || underlying == typeof(byte[])
            || underlying == typeof(char[])
            || underlying.IsPrimitive
            || underlying == typeof(decimal);
    }

    /// <summary>
    /// The schema field type a scalar maps to.
    /// </summary>
    internal static FieldType ScalarFieldType(Type type)
    {
        Type underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying == typeof(bool))
        {
            return FieldType.Boolean;
        }

        if (underlying == typeof(float))
        {
            return FieldType.Float;
        }

        if (underlying == typeof(double))
        {
            return FieldType.Double;
        }

        if (underlying == typeof(long) || underlying == typeof(ulong))
        {
            return FieldType.Long;
        }

        if (underlying == typeof(byte[]))
        {
            return FieldType.Bytes;
        }

        if (underlying == typeof(string) || underlying == typeof(char[]) || underlying == typeof(char))
        {
            return FieldType.String;
        }

        if (underlying == typeof(int) || underlying == typeof(uint)
            || underlying == typeof(short) || underlying == typeof(ushort)
            || underlying == typeof(byte) || underlying == typeof(sbyte))
        {
            return FieldType.Int;
        }

        if (underlying == typeof(decimal))
        {
            // Format extension: Netflix Hollow has no decimal field type. See PORTING.md.
            return FieldType.Decimal;
        }

        throw new HollowMappingException($"{underlying.Name} is not a Hollow scalar type");
    }

    /// <summary>
    /// The string a record holds for a member that maps to a string field.
    /// </summary>
    /// <remarks>
    /// A lone <see cref="char"/> maps to one too — <c>ScalarFieldType</c> says so — and is a string of
    /// one character rather than a number, so that what comes back out reads as what went in.
    /// </remarks>
    private static string ToHollowString(object value) =>
        value switch
        {
            char[] chars => new string(chars),
            char character => character.ToString(),
            _ => (string)value,
        };

    /// <summary>
    /// Narrows a value to the int a record holds.
    /// </summary>
    /// <remarks>
    /// A <see cref="uint"/> goes in by its bits rather than by its value: half of them are larger than
    /// <see cref="int.MaxValue"/>, and converting by value would refuse those rather than store them.
    /// The one it cannot store is the bit pattern the format spends on null.
    /// </remarks>
    private static int ToInt32(string field, object value) =>
        value switch
        {
            uint bits when bits == unchecked((uint)int.MinValue) => throw new HollowMappingException(
                $"{field} is {bits}, whose bits are the pattern the format uses for a null int"),
            uint bits => unchecked((int)bits),
            _ => Convert.ToInt32(value, CultureInfo.InvariantCulture),
        };

    /// <summary>Narrows a value to the long a record holds, as <see cref="ToInt32"/> does.</summary>
    private static long ToInt64(string field, object value) =>
        value switch
        {
            ulong bits when bits == unchecked((ulong)long.MinValue) => throw new HollowMappingException(
                $"{field} is {bits}, whose bits are the pattern the format uses for a null long"),
            ulong bits => unchecked((long)bits),
            _ => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        };

    /// <summary>
    /// Whether a member's value is stored directly in the record rather than as a reference to a type
    /// of its own.
    /// </summary>
    /// <remarks>
    /// A non-nullable primitive is always inlined: there is no null to represent, and a reference would
    /// cost an indirection for nothing. <see cref="decimal"/> counts as one here even though the CLR
    /// does not classify it as primitive, since it behaves like the other numeric value types.
    /// Everything else follows <see cref="HollowInlineAttribute"/>, defaulting to a reference so that
    /// repeated values deduplicate.
    /// </remarks>
    internal static bool IsInlinedScalar(MappedMember member)
    {
        Type memberType = member.MemberType;
        Type underlying = Nullable.GetUnderlyingType(memberType) ?? memberType;
        bool isNullableValue = Nullable.GetUnderlyingType(memberType) is not null;
        bool isValueScalar = underlying.IsPrimitive || underlying == typeof(decimal);

        return IsScalarWrapper(memberType)
            && (member.IsInlined || (isValueScalar && !isNullableValue));
    }

    private MappedFieldInfo AddField(MappedMember member)
    {
        Type underlying = Nullable.GetUnderlyingType(member.MemberType) ?? member.MemberType;

        if (IsInlinedScalar(member))
        {
            FieldType fieldType = ScalarFieldType(member.MemberType);
            _schema.AddField(member.Name, fieldType);
            return MappedFieldInfo.Scalar(
                member.Name, fieldType, member.MemberType, member.GetValue, member.SetValue);
        }

        // The name has to be derived rather than taken from the referenced mapper, which may not exist
        // yet; RegisterReferencedTypes creates it once this mapper is published.
        string referencedTypeName = HollowObjectMapper.ResolveTypeName(underlying, member.TypeNameOverride);

        _schema.AddField(member.Name, FieldType.Reference, referencedTypeName);
        return MappedFieldInfo.Reference(
            member.Name,
            underlying,
            member.MemberType,
            member.TypeNameOverride,
            member.HashKeyFieldPaths,
            member.CollectionTypeNames,
            member.GetValue,
            member.SetValue);
    }

    private sealed class MappedFieldInfo
    {
        private readonly Func<object, object?> _getValue;

        private MappedFieldInfo(
            string name,
            FieldType fieldType,
            Type? declaredType,
            Type? memberType,
            string? typeNameOverride,
            string[]? hashKeyFieldPaths,
            CollectionTypeNames collectionTypeNames,
            Func<object, object?> getValue,
            Action<object, object?>? setValue)
        {
            Name = name;
            FieldType = fieldType;
            DeclaredType = declaredType;
            MemberType = memberType;
            TypeNameOverride = typeNameOverride;
            HashKeyFieldPaths = hashKeyFieldPaths;
            CollectionTypeNames = collectionTypeNames;
            _getValue = getValue;
            SetValue = setValue;
        }

        internal string Name { get; }

        internal FieldType FieldType { get; }

        /// <summary>
        /// The CLR type of the member as declared, nullability and all, which is what a value read back
        /// out of a record has to be converted to.
        /// </summary>
        internal Type? MemberType { get; }

        /// <summary>Assigns this member, where the model allows it.</summary>
        internal Action<object, object?>? SetValue { get; }

        /// <summary>The declared CLR type of a reference field, which decides its Hollow type.</summary>
        internal Type? DeclaredType { get; }

        internal string? TypeNameOverride { get; }

        /// <summary>
        /// The hash key declared on this member, which applies to the set or map it references.
        /// </summary>
        internal string[]? HashKeyFieldPaths { get; }

        /// <summary>
        /// The type names this member declares for what the collection it references holds.
        /// </summary>
        internal CollectionTypeNames CollectionTypeNames { get; }

        internal object? GetValue(object instance) => _getValue(instance);

        internal static MappedFieldInfo Scalar(
            string name,
            FieldType fieldType,
            Type memberType,
            Func<object, object?> getValue,
            Action<object, object?>? setValue = null) =>
            new(
                name,
                fieldType,
                null,
                memberType,
                null,
                null,
                CollectionTypeNames.None,
                getValue,
                setValue);

        internal static MappedFieldInfo Reference(
            string name,
            Type declaredType,
            Type memberType,
            string? typeNameOverride,
            string[]? hashKeyFieldPaths,
            CollectionTypeNames collectionTypeNames,
            Func<object, object?> getValue,
            Action<object, object?>? setValue) =>
            new(
                name,
                FieldType.Reference,
                declaredType,
                memberType,
                typeNameOverride,
                hashKeyFieldPaths,
                collectionTypeNames,
                getValue,
                setValue);
    }
}

/// <summary>
/// Maps a CLR sequence onto a Hollow list type.
/// </summary>
public sealed class HollowListTypeMapper : HollowTypeMapper
{
    private readonly HollowObjectMapper _parentMapper;
    private readonly Type _type;
    private readonly Type _elementType;
    private readonly string? _elementTypeName;
    private readonly HollowListSchema _schema;

    internal HollowListTypeMapper(
        HollowObjectMapper parentMapper,
        Type type,
        Type elementType,
        string? typeName,
        CollectionTypeNames collectionTypeNames = default)
    {
        _parentMapper = parentMapper;
        _type = type;
        _elementType = elementType;
        _elementTypeName = collectionTypeNames.ElementOrKey;

        // The list's own name is derived from the CLR type, whatever its elements are called. So a
        // List<int> whose elements are named MovieId is still a ListOfInteger unless HollowTypeName
        // says otherwise, which is what Java does and what its documentation tells you to compose.
        TypeName = HollowObjectMapper.ResolveTypeName(type, typeName);
        _schema = new HollowListSchema(
            TypeName, HollowObjectMapper.ResolveTypeName(elementType, _elementTypeName));
    }

    /// <inheritdoc />
    public override string TypeName { get; }

    /// <inheritdoc />
    public override HollowSchema Schema => _schema;

    /// <inheritdoc />
    public override int Write(object value)
    {
        ArgumentNullException.ThrowIfNull(value);

        // A model that hands the same collection instance to many records would otherwise serialise it
        // once per record, and find out they were identical only when the write engine deduplicates.
        long cycleBits = _parentMapper.StateEngine.RandomizedTag & MemoizedRecord.AssignedOrdinalCycleMask;
        int remembered = MemoizedRecord.RememberedOrdinal(value, cycleBits);

        if (remembered != -1)
        {
            return remembered;
        }

        int ordinal = _parentMapper.StateEngine.Add(TypeName, ToWriteRecord(value, null));

        MemoizedRecord.Remember(value, ordinal, cycleBits);

        return ordinal;
    }

    /// <inheritdoc />
    public override int WriteFlat(object value, FlatRecordWriter writer)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(writer);

        return writer.Write(_schema, ToWriteRecord(value, writer));
    }

    /// <inheritdoc />
    public override object? ParseFlatRecord(IFlatRecordTraversalNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is not FlatRecordTraversalListNode listNode)
        {
            throw new HollowMappingException(
                $"{TypeName} is a list type, and the record is a {node.Schema.SchemaType}");
        }

        HollowTypeMapper elements = _parentMapper.GetTypeMapper(_elementType, _elementTypeName, null);

        return MappedValues.CreateSequence(
            _type, _elementType, [.. listNode.Select(elements.ParseFlatRecord)]);
    }

    private HollowListWriteRecord ToWriteRecord(object value, FlatRecordWriter? flatWriter)
    {
        HollowListWriteRecord record = new();
        HollowTypeMapper elements = _parentMapper.GetTypeMapper(_elementType, _elementTypeName, null);

        foreach (object? element in (IEnumerable)value)
        {
            if (element is null)
            {
                throw new HollowMappingException(
                    $"List type {TypeName} contains a null element; Hollow collections cannot hold nulls.");
            }

            record.AddElement(
                flatWriter is null ? elements.Write(element) : elements.WriteFlat(element, flatWriter));
        }

        return record;
    }

    /// <inheritdoc />
    protected override HollowTypeWriteState CreateWriteState() =>
        new HollowListTypeWriteState(
            _schema, usePartitionedOrdinalMap: _parentMapper.StateEngine.PartitionedOrdinalMap);

    /// <inheritdoc />
    protected internal override void RegisterReferencedTypes(HollowObjectMapper parentMapper) =>
        parentMapper.GetTypeMapper(_elementType, _elementTypeName, null);
}

/// <summary>
/// Maps a CLR set onto a Hollow set type.
/// </summary>
public sealed class HollowSetTypeMapper : HollowTypeMapper
{
    private readonly HollowObjectMapper _parentMapper;
    private readonly Type _type;
    private readonly Type _elementType;
    private readonly string? _elementTypeName;
    private readonly HollowSetSchema _schema;

    internal HollowSetTypeMapper(
        HollowObjectMapper parentMapper,
        Type type,
        Type elementType,
        string? typeName,
        string[]? hashKeyFieldPaths,
        CollectionTypeNames collectionTypeNames = default)
    {
        _parentMapper = parentMapper;

        _type = type;
        _elementType = elementType;
        _elementTypeName = collectionTypeNames.ElementOrKey;

        TypeName = HollowObjectMapper.ResolveTypeName(type, typeName);
        _schema = new HollowSetSchema(
            TypeName,
            HollowObjectMapper.ResolveTypeName(elementType, _elementTypeName),
            parentMapper.ResolveHashKey(hashKeyFieldPaths, elementType));
    }

    /// <inheritdoc />
    public override string TypeName { get; }

    /// <inheritdoc />
    public override HollowSchema Schema => _schema;

    /// <inheritdoc />
    public override int Write(object value)
    {
        ArgumentNullException.ThrowIfNull(value);

        // A model that hands the same collection instance to many records would otherwise serialise it
        // once per record, and find out they were identical only when the write engine deduplicates.
        long cycleBits = _parentMapper.StateEngine.RandomizedTag & MemoizedRecord.AssignedOrdinalCycleMask;
        int remembered = MemoizedRecord.RememberedOrdinal(value, cycleBits);

        if (remembered != -1)
        {
            return remembered;
        }

        int ordinal = _parentMapper.StateEngine.Add(TypeName, ToWriteRecord(value, null));

        MemoizedRecord.Remember(value, ordinal, cycleBits);

        return ordinal;
    }

    /// <inheritdoc />
    public override int WriteFlat(object value, FlatRecordWriter writer)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(writer);

        return writer.Write(_schema, ToWriteRecord(value, writer));
    }

    /// <inheritdoc />
    public override object? ParseFlatRecord(IFlatRecordTraversalNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is not FlatRecordTraversalSetNode setNode)
        {
            throw new HollowMappingException(
                $"{TypeName} is a set type, and the record is a {node.Schema.SchemaType}");
        }

        HollowTypeMapper elements = _parentMapper.GetTypeMapper(_elementType, _elementTypeName, null);

        return MappedValues.CreateSet(_type, _elementType, [.. setNode.Select(elements.ParseFlatRecord)]);
    }

    private HollowSetWriteRecord ToWriteRecord(object value, FlatRecordWriter? flatWriter)
    {
        HollowSetWriteRecord record = new();
        HollowTypeMapper elements = _parentMapper.GetTypeMapper(_elementType, _elementTypeName, null);

        foreach (object? element in (IEnumerable)value)
        {
            if (element is null)
            {
                throw new HollowMappingException(
                    $"Set type {TypeName} contains a null element; Hollow collections cannot hold nulls.");
            }

            record.AddElement(
                flatWriter is null ? elements.Write(element) : elements.WriteFlat(element, flatWriter));
        }

        return record;
    }

    /// <inheritdoc />
    protected override HollowTypeWriteState CreateWriteState() =>
        new HollowSetTypeWriteState(
            _schema, usePartitionedOrdinalMap: _parentMapper.StateEngine.PartitionedOrdinalMap);

    /// <inheritdoc />
    protected internal override void RegisterReferencedTypes(HollowObjectMapper parentMapper) =>
        parentMapper.GetTypeMapper(_elementType, _elementTypeName, null);
}

/// <summary>
/// Maps a CLR dictionary onto a Hollow map type.
/// </summary>
public sealed class HollowMapTypeMapper : HollowTypeMapper
{
    private readonly HollowObjectMapper _parentMapper;
    private readonly Type _type;
    private readonly Type _keyType;
    private readonly Type _valueType;
    private readonly string? _keyTypeName;
    private readonly string? _valueTypeName;
    private readonly HollowMapSchema _schema;

    internal HollowMapTypeMapper(
        HollowObjectMapper parentMapper,
        Type type,
        Type keyType,
        Type valueType,
        string? typeName,
        string[]? hashKeyFieldPaths,
        CollectionTypeNames collectionTypeNames = default)
    {
        _parentMapper = parentMapper;

        _type = type;
        _keyType = keyType;
        _valueType = valueType;
        _keyTypeName = collectionTypeNames.ElementOrKey;
        _valueTypeName = collectionTypeNames.Value;

        TypeName = HollowObjectMapper.ResolveTypeName(type, typeName);
        _schema = new HollowMapSchema(
            TypeName,
            HollowObjectMapper.ResolveTypeName(keyType, _keyTypeName),
            HollowObjectMapper.ResolveTypeName(valueType, _valueTypeName),
            parentMapper.ResolveHashKey(hashKeyFieldPaths, keyType));
    }

    /// <inheritdoc />
    public override string TypeName { get; }

    /// <inheritdoc />
    public override HollowSchema Schema => _schema;

    /// <inheritdoc />
    public override int Write(object value)
    {
        ArgumentNullException.ThrowIfNull(value);

        // A model that hands the same collection instance to many records would otherwise serialise it
        // once per record, and find out they were identical only when the write engine deduplicates.
        long cycleBits = _parentMapper.StateEngine.RandomizedTag & MemoizedRecord.AssignedOrdinalCycleMask;
        int remembered = MemoizedRecord.RememberedOrdinal(value, cycleBits);

        if (remembered != -1)
        {
            return remembered;
        }

        int ordinal = _parentMapper.StateEngine.Add(TypeName, ToWriteRecord(value, null));

        MemoizedRecord.Remember(value, ordinal, cycleBits);

        return ordinal;
    }

    /// <inheritdoc />
    public override int WriteFlat(object value, FlatRecordWriter writer)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(writer);

        return writer.Write(_schema, ToWriteRecord(value, writer));
    }

    /// <inheritdoc />
    public override object? ParseFlatRecord(IFlatRecordTraversalNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is not FlatRecordTraversalMapNode mapNode)
        {
            throw new HollowMappingException(
                $"{TypeName} is a map type, and the record is a {node.Schema.SchemaType}");
        }

        HollowTypeMapper keys = _parentMapper.GetTypeMapper(_keyType, _keyTypeName, null);
        HollowTypeMapper values = _parentMapper.GetTypeMapper(_valueType, _valueTypeName, null);

        return MappedValues.CreateDictionary(
            _type,
            _keyType,
            _valueType,
            [.. mapNode.Select(entry =>
                (keys.ParseFlatRecord(entry.Key), values.ParseFlatRecord(entry.Value)))]);
    }

    private HollowMapWriteRecord ToWriteRecord(object value, FlatRecordWriter? flatWriter)
    {
        HollowMapWriteRecord record = new();
        HollowTypeMapper keys = _parentMapper.GetTypeMapper(_keyType, _keyTypeName, null);
        HollowTypeMapper values = _parentMapper.GetTypeMapper(_valueType, _valueTypeName, null);

        foreach (DictionaryEntry entry in (IDictionary)value)
        {
            if (entry.Key is null || entry.Value is null)
            {
                throw new HollowMappingException(
                    $"Map type {TypeName} contains a null key or value; Hollow maps cannot hold nulls.");
            }

            record.AddEntry(
                flatWriter is null ? keys.Write(entry.Key) : keys.WriteFlat(entry.Key, flatWriter),
                flatWriter is null ? values.Write(entry.Value) : values.WriteFlat(entry.Value, flatWriter));
        }

        return record;
    }

    /// <inheritdoc />
    protected override HollowTypeWriteState CreateWriteState() =>
        new HollowMapTypeWriteState(
            _schema, usePartitionedOrdinalMap: _parentMapper.StateEngine.PartitionedOrdinalMap);

    /// <inheritdoc />
    protected internal override void RegisterReferencedTypes(HollowObjectMapper parentMapper)
    {
        parentMapper.GetTypeMapper(_keyType, _keyTypeName, null);
        parentMapper.GetTypeMapper(_valueType, _valueTypeName, null);
    }
}
