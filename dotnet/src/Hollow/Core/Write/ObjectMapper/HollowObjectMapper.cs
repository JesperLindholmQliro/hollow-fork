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
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Hollow.Core.Schema;

namespace Hollow.Core.Write.ObjectMapper;

/// <summary>
/// Maps ordinary CLR objects onto Hollow records, deriving the schemas from their types.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Port note.</strong> Java maps every declared field, reaching private state through
/// <c>sun.misc.Unsafe</c>. This port maps <em>public instance properties with a getter, and public
/// instance fields</em>, which is the idiomatic .NET surface and avoids reflecting over private state.
/// A type's schema therefore follows its public shape; mark anything to exclude with
/// <see cref="HollowTransientAttribute"/>.
/// </para>
/// <para>
/// Members are mapped in declaration order, which fixes the field order of the generated schema.
/// </para>
/// </remarks>
public sealed class HollowObjectMapper
{
    private readonly HollowWriteStateEngine _stateEngine;
    private readonly ConcurrentDictionary<MapperKey, HollowTypeMapper> _typeMappers = new();

    /// <summary>
    /// Initialises a mapper writing into <paramref name="stateEngine"/>.
    /// </summary>
    public HollowObjectMapper(HollowWriteStateEngine stateEngine)
    {
        ArgumentNullException.ThrowIfNull(stateEngine);
        _stateEngine = stateEngine;
    }

    /// <summary>The engine this mapper writes into.</summary>
    public HollowWriteStateEngine StateEngine => _stateEngine;

    /// <summary>
    /// Registers <typeparamref name="T"/> and everything reachable from it, without adding a record.
    /// </summary>
    public void InitializeTypeState<T>() => InitializeTypeState(typeof(T));

    /// <summary>
    /// Registers <paramref name="type"/> and everything reachable from it, without adding a record.
    /// </summary>
    public void InitializeTypeState(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        GetTypeMapper(type, typeName: null);
    }

    /// <summary>
    /// Adds <paramref name="value"/> and everything it references, returning the ordinal assigned to
    /// it.
    /// </summary>
    public int Add(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return GetTypeMapper(value.GetType(), typeName: null).Write(value);
    }

    /// <summary>
    /// Resolves, creating if necessary, the mapper for a CLR type.
    /// </summary>
    internal HollowTypeMapper GetTypeMapper(Type type, string? typeName)
    {
        MapperKey key = new(type, typeName);

        if (_typeMappers.TryGetValue(key, out HollowTypeMapper? existing))
        {
            return existing;
        }

        // Construct, publish, then resolve referenced types. Publishing before recursing is what makes
        // a self-referencing or mutually-referencing model terminate.
        HollowTypeMapper mapper = CreateMapper(key);
        mapper = _typeMappers.GetOrAdd(key, mapper);

        mapper.EnsureRegistered(_stateEngine);
        mapper.RegisterReferencedTypes(this);

        return mapper;
    }

    private HollowTypeMapper CreateMapper(MapperKey key)
    {
        Type type = key.Type;

        if (TryGetDictionaryTypes(type, out Type? keyType, out Type? valueType))
        {
            return new HollowMapTypeMapper(this, type, keyType!, valueType!, key.TypeName);
        }

        if (TryGetSetElementType(type, out Type? setElementType))
        {
            return new HollowSetTypeMapper(this, type, setElementType!, key.TypeName);
        }

        if (TryGetListElementType(type, out Type? listElementType))
        {
            return new HollowListTypeMapper(this, type, listElementType!, key.TypeName);
        }

        return new HollowObjectTypeMapper(this, type, key.TypeName);
    }

    /// <summary>
    /// The Hollow type name a CLR type maps to.
    /// </summary>
    /// <remarks>
    /// This has to be derivable without constructing a mapper, because an object type's schema names
    /// the types its reference fields point at while those mappers may not exist yet. Deriving the name
    /// in one place keeps the schema and the mapper that fills it in agreement.
    /// </remarks>
    internal static string ResolveTypeName(Type type, string? overrideName)
    {
        if (overrideName is not null)
        {
            return overrideName;
        }

        if (type.GetCustomAttribute<HollowTypeNameAttribute>()?.Name is { } attributeName)
        {
            return attributeName;
        }

        if (TryGetDictionaryTypes(type, out Type? keyType, out Type? valueType))
        {
            return $"MapOf{ResolveTypeName(keyType, null)}To{ResolveTypeName(valueType, null)}";
        }

        if (TryGetSetElementType(type, out Type? setElementType))
        {
            return $"SetOf{ResolveTypeName(setElementType, null)}";
        }

        if (TryGetListElementType(type, out Type? listElementType))
        {
            return $"ListOf{ResolveTypeName(listElementType, null)}";
        }

        return DefaultTypeName(type);
    }

    /// <summary>
    /// The Hollow type name for a CLR type, before any <see cref="HollowTypeNameAttribute"/> override.
    /// </summary>
    /// <remarks>
    /// The scalar wrapper names match Java's, so that a .NET-produced blob describes the same types a
    /// Java-produced one would.
    /// </remarks>
    internal static string DefaultTypeName(Type type)
    {
        Type underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying == typeof(string))
        {
            return "String";
        }

        if (underlying == typeof(int))
        {
            return "Integer";
        }

        if (underlying == typeof(long))
        {
            return "Long";
        }

        if (underlying == typeof(short))
        {
            return "Short";
        }

        if (underlying == typeof(bool))
        {
            return "Boolean";
        }

        if (underlying == typeof(float))
        {
            return "Float";
        }

        if (underlying == typeof(double))
        {
            return "Double";
        }

        if (underlying == typeof(byte) || underlying == typeof(sbyte))
        {
            return "Byte";
        }

        if (underlying == typeof(char))
        {
            return "Character";
        }

        if (underlying == typeof(byte[]))
        {
            return "Bytes";
        }

        // A generic type's CLR name carries an arity suffix, which is not a legal Hollow type name.
        string name = underlying.Name;
        int arityMarker = name.IndexOf('`', StringComparison.Ordinal);
        return arityMarker == -1 ? name : name[..arityMarker];
    }

    private static bool TryGetDictionaryTypes(
        Type type, [NotNullWhen(true)] out Type? keyType, [NotNullWhen(true)] out Type? valueType)
    {
        Type? dictionaryInterface = FindGenericInterface(type, typeof(IDictionary<,>));

        if (dictionaryInterface is null)
        {
            keyType = null;
            valueType = null;
            return false;
        }

        Type[] arguments = dictionaryInterface.GetGenericArguments();
        keyType = arguments[0];
        valueType = arguments[1];
        return true;
    }

    private static bool TryGetSetElementType(Type type, [NotNullWhen(true)] out Type? elementType)
    {
        Type? setInterface = FindGenericInterface(type, typeof(ISet<>));
        elementType = setInterface?.GetGenericArguments()[0];
        return elementType is not null;
    }

    private static bool TryGetListElementType(Type type, [NotNullWhen(true)] out Type? elementType)
    {
        // A string is enumerable but is a scalar as far as Hollow is concerned.
        if (type == typeof(string) || type == typeof(byte[]) || type == typeof(char[]))
        {
            elementType = null;
            return false;
        }

        Type? enumerableInterface = FindGenericInterface(type, typeof(IEnumerable<>));
        elementType = enumerableInterface?.GetGenericArguments()[0];
        return elementType is not null;
    }

    private static Type? FindGenericInterface(Type type, Type openGeneric)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == openGeneric)
        {
            return type;
        }

        foreach (Type candidate in type.GetInterfaces())
        {
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == openGeneric)
            {
                return candidate;
            }
        }

        return null;
    }

    private readonly record struct MapperKey(Type Type, string? TypeName);
}

/// <summary>
/// Maps one CLR type onto one Hollow type.
/// </summary>
public abstract class HollowTypeMapper
{
    private int _registered;

    /// <summary>The name of the Hollow type this mapper produces.</summary>
    public abstract string TypeName { get; }

    /// <summary>The schema of the Hollow type this mapper produces.</summary>
    public abstract HollowSchema Schema { get; }

    /// <summary>
    /// Writes <paramref name="value"/> as a record, returning the ordinal assigned to it.
    /// </summary>
    public abstract int Write(object value);

    /// <summary>
    /// The write state this mapper adds records to.
    /// </summary>
    protected abstract HollowTypeWriteState CreateWriteState();

    /// <summary>
    /// Resolves the mappers for the types this one references, after this mapper has been published so
    /// that cycles terminate.
    /// </summary>
    protected internal virtual void RegisterReferencedTypes(HollowObjectMapper parentMapper)
    {
        // Scalar types reference nothing.
    }

    /// <summary>
    /// Registers this mapper's type with the engine exactly once.
    /// </summary>
    internal void EnsureRegistered(HollowWriteStateEngine stateEngine)
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
        {
            return;
        }

        if (stateEngine.GetTypeState(TypeName) is null)
        {
            stateEngine.AddTypeState(CreateWriteState());
        }
    }
}

/// <summary>
/// Thrown when a CLR type cannot be expressed as a Hollow type.
/// </summary>
public sealed class HollowMappingException : InvalidOperationException
{
    /// <summary>Initialises the exception with a message.</summary>
    public HollowMappingException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises the exception with a message and an inner exception.</summary>
    public HollowMappingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initialises the exception.</summary>
    public HollowMappingException()
    {
    }
}

/// <summary>
/// Reads a member's value out of an instance, whether it is a property or a field.
/// </summary>
/// <remarks>
/// Java reaches fields through <c>Unsafe</c> field offsets; a compiled accessor is the .NET equivalent
/// and keeps the per-record cost to a delegate call.
/// </remarks>
internal sealed class MappedMember
{
    private readonly Func<object, object?> _getValue;

    internal MappedMember(MemberInfo member, Type memberType, Func<object, object?> getValue)
    {
        Name = member.Name;
        MemberType = memberType;
        _getValue = getValue;
        IsInlined = member.GetCustomAttribute<HollowInlineAttribute>() is not null;
        TypeNameOverride = member.GetCustomAttribute<HollowTypeNameAttribute>()?.Name;
    }

    internal string Name { get; }

    internal Type MemberType { get; }

    internal bool IsInlined { get; }

    internal string? TypeNameOverride { get; }

    internal object? GetValue(object instance) => _getValue(instance);

    /// <summary>
    /// The public instance properties and fields of <paramref name="type"/>, in declaration order,
    /// excluding anything marked <see cref="HollowTransientAttribute"/>.
    /// </summary>
    internal static List<MappedMember> ForType(Type type)
    {
        List<MappedMember> members = [];

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetMethod is null
                || property.GetIndexParameters().Length != 0
                || property.GetCustomAttribute<HollowTransientAttribute>() is not null)
            {
                continue;
            }

            members.Add(new MappedMember(property, property.PropertyType, property.GetValue!));
        }

        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (field.GetCustomAttribute<HollowTransientAttribute>() is not null)
            {
                continue;
            }

            members.Add(new MappedMember(field, field.FieldType, field.GetValue!));
        }

        if (members.Count == 0)
        {
            throw new HollowMappingException(
                $"Type {type.Name} has no public instance properties or fields to map. Hollow records "
                + "need at least one field.");
        }

        return members;
    }
}
