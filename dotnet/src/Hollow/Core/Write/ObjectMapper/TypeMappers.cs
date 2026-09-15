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
            _fields.Add(MappedFieldInfo.Scalar("value", ScalarFieldType(type), static instance => instance));
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
                    record.SetInt(field.Name, Convert.ToInt32(memberValue, CultureInfo.InvariantCulture));
                    break;
                case FieldType.Long:
                    record.SetLong(field.Name, Convert.ToInt64(memberValue, CultureInfo.InvariantCulture));
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
                    // Resolve by the declared type, not the runtime type, so that the ordinal lands in
                    // the type the schema says this field points at.
                    record.SetReference(
                        field.Name,
                        _parentMapper
                            .GetTypeMapper(
                                field.DeclaredType!,
                                field.TypeNameOverride,
                                field.HashKeyFieldPaths,
                                field.CollectionTypeNames)
                            .Write(memberValue));
                    break;
                default:
                    throw new HollowMappingException(
                        $"Field {field.Name} of type {TypeName} has unmappable field type {field.FieldType}");
            }
        }

        return _parentMapper.StateEngine.Add(TypeName, record);
    }

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

    private static string ToHollowString(object value) =>
        value is char[] chars ? new string(chars) : (string)value;

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
            return MappedFieldInfo.Scalar(member.Name, fieldType, member.GetValue);
        }

        // The name has to be derived rather than taken from the referenced mapper, which may not exist
        // yet; RegisterReferencedTypes creates it once this mapper is published.
        string referencedTypeName = HollowObjectMapper.ResolveTypeName(underlying, member.TypeNameOverride);

        _schema.AddField(member.Name, FieldType.Reference, referencedTypeName);
        return MappedFieldInfo.Reference(
            member.Name,
            underlying,
            member.TypeNameOverride,
            member.HashKeyFieldPaths,
            member.CollectionTypeNames,
            member.GetValue);
    }

    private sealed class MappedFieldInfo
    {
        private readonly Func<object, object?> _getValue;

        private MappedFieldInfo(
            string name,
            FieldType fieldType,
            Type? declaredType,
            string? typeNameOverride,
            string[]? hashKeyFieldPaths,
            CollectionTypeNames collectionTypeNames,
            Func<object, object?> getValue)
        {
            Name = name;
            FieldType = fieldType;
            DeclaredType = declaredType;
            TypeNameOverride = typeNameOverride;
            HashKeyFieldPaths = hashKeyFieldPaths;
            CollectionTypeNames = collectionTypeNames;
            _getValue = getValue;
        }

        internal string Name { get; }

        internal FieldType FieldType { get; }

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

        internal static MappedFieldInfo Scalar(string name, FieldType fieldType, Func<object, object?> getValue) =>
            new(name, fieldType, null, null, null, CollectionTypeNames.None, getValue);

        internal static MappedFieldInfo Reference(
            string name,
            Type declaredType,
            string? typeNameOverride,
            string[]? hashKeyFieldPaths,
            CollectionTypeNames collectionTypeNames,
            Func<object, object?> getValue) =>
            new(
                name,
                FieldType.Reference,
                declaredType,
                typeNameOverride,
                hashKeyFieldPaths,
                collectionTypeNames,
                getValue);
    }
}

/// <summary>
/// Maps a CLR sequence onto a Hollow list type.
/// </summary>
public sealed class HollowListTypeMapper : HollowTypeMapper
{
    private readonly HollowObjectMapper _parentMapper;
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

        HollowListWriteRecord record = new();

        foreach (object? element in (IEnumerable)value)
        {
            if (element is null)
            {
                throw new HollowMappingException(
                    $"List type {TypeName} contains a null element; Hollow collections cannot hold nulls.");
            }

            record.AddElement(
                _parentMapper.GetTypeMapper(_elementType, _elementTypeName, null).Write(element));
        }

        return _parentMapper.StateEngine.Add(TypeName, record);
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

        HollowSetWriteRecord record = new();

        foreach (object? element in (IEnumerable)value)
        {
            if (element is null)
            {
                throw new HollowMappingException(
                    $"Set type {TypeName} contains a null element; Hollow collections cannot hold nulls.");
            }

            record.AddElement(
                _parentMapper.GetTypeMapper(_elementType, _elementTypeName, null).Write(element));
        }

        return _parentMapper.StateEngine.Add(TypeName, record);
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

        HollowMapWriteRecord record = new();

        foreach (DictionaryEntry entry in (IDictionary)value)
        {
            if (entry.Key is null || entry.Value is null)
            {
                throw new HollowMappingException(
                    $"Map type {TypeName} contains a null key or value; Hollow maps cannot hold nulls.");
            }

            int keyOrdinal = _parentMapper.GetTypeMapper(_keyType, _keyTypeName, null).Write(entry.Key);
            int valueOrdinal =
                _parentMapper.GetTypeMapper(_valueType, _valueTypeName, null).Write(entry.Value);
            record.AddEntry(keyOrdinal, valueOrdinal);
        }

        return _parentMapper.StateEngine.Add(TypeName, record);
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
