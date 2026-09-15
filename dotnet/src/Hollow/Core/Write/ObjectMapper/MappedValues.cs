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

namespace Hollow.Core.Write.ObjectMapper;

/// <summary>
/// Turns the values read back out of a record into the CLR types a model declares.
/// </summary>
/// <remarks>
/// <para>
/// Writing a model out throws information away: a <c>short</c>, a <c>uint</c> and an <c>int</c> are all
/// an int field, a <c>char[]</c> and a <c>string</c> are both a string field, and an enum is its member
/// name. Reading one back has to put that information in again from the model, which is the whole of
/// what this does.
/// </para>
/// <para>
/// Java has no equivalent: its <c>parseFlatRecord</c> reflects over fields directly and assigns what it
/// read, relying on the JVM's widening rules and on boxing to make it fit. .NET will not assign an
/// <c>int</c> to a <c>short</c> field, so the conversion has to be written out — and Java has no
/// unsigned types to convert at all.
/// </para>
/// </remarks>
internal static class MappedValues
{
    /// <summary>
    /// Converts a value read out of a record to <paramref name="targetType"/>.
    /// </summary>
    /// <exception cref="HollowMappingException">The value cannot be that type.</exception>
    internal static object? ConvertScalar(object? value, Type targetType)
    {
        if (value is null)
        {
            return null;
        }

        Type underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (underlying.IsInstanceOfType(value))
        {
            return value;
        }

        if (underlying.IsEnum)
        {
            // Enums go out as their member name, so that renumbering the enum does not silently change
            // what the data means.
            return Enum.Parse(underlying, (string)value, ignoreCase: false);
        }

        if (underlying == typeof(char[]))
        {
            return ((string)value).ToCharArray();
        }

        if (underlying == typeof(char))
        {
            return ((string)value)[0];
        }

        // The unsigned types went out by their bits rather than by their value, because half of each
        // range does not fit the signed field it is stored in. They come back the same way.
        if (underlying == typeof(uint) && value is int signed)
        {
            return unchecked((uint)signed);
        }

        if (underlying == typeof(ulong) && value is long signedLong)
        {
            return unchecked((ulong)signedLong);
        }

        try
        {
            return Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture);
        }
        catch (Exception failure) when (failure is InvalidCastException or FormatException or OverflowException)
        {
            throw new HollowMappingException(
                $"A {value.GetType().Name} read out of a record is not a {targetType.Name}", failure);
        }
    }

    /// <summary>
    /// Builds a sequence of <paramref name="targetType"/> holding <paramref name="elements"/>.
    /// </summary>
    /// <remarks>
    /// An array, anything a <see cref="List{T}"/> can stand in for, and anything with a constructor
    /// taking a sequence — which between them covers what the mapper accepts on the way in.
    /// </remarks>
    internal static object CreateSequence(Type targetType, Type elementType, List<object?> elements)
    {
        Array typed = ToArray(elementType, elements);

        if (targetType.IsArray)
        {
            return typed;
        }

        Type list = typeof(List<>).MakeGenericType(elementType);

        return targetType.IsAssignableFrom(list)
            ? Activator.CreateInstance(list, typed)!
            : FromSequence(targetType, elementType, typed);
    }

    /// <summary>
    /// Builds a set of <paramref name="targetType"/> holding <paramref name="elements"/>.
    /// </summary>
    internal static object CreateSet(Type targetType, Type elementType, List<object?> elements)
    {
        Array typed = ToArray(elementType, elements);
        Type set = typeof(HashSet<>).MakeGenericType(elementType);

        return targetType.IsAssignableFrom(set)
            ? Activator.CreateInstance(set, typed)!
            : FromSequence(targetType, elementType, typed);
    }

    /// <summary>
    /// Builds a dictionary of <paramref name="targetType"/> holding <paramref name="entries"/>.
    /// </summary>
    internal static object CreateDictionary(
        Type targetType, Type keyType, Type valueType, List<(object? Key, object? Value)> entries)
    {
        Type dictionary = typeof(Dictionary<,>).MakeGenericType(keyType, valueType);

        object built = targetType.IsAssignableFrom(dictionary)
            ? Activator.CreateInstance(dictionary)!
            : Activator.CreateInstance(targetType)
              ?? throw new HollowMappingException(
                  $"{targetType.Name} has no constructor taking nothing, so a map cannot be read into it");

        if (built is not IDictionary target)
        {
            throw new HollowMappingException(
                $"{targetType.Name} is not a dictionary, so a map cannot be read into it");
        }

        foreach ((object? key, object? value) in entries)
        {
            target[key!] = value;
        }

        return target;
    }

    private static Array ToArray(Type elementType, List<object?> elements)
    {
        Array typed = Array.CreateInstance(elementType, elements.Count);

        for (int i = 0; i < elements.Count; i++)
        {
            typed.SetValue(elements[i], i);
        }

        return typed;
    }

    /// <summary>
    /// Hands a built array to a collection type that takes one, which is how an immutable or otherwise
    /// custom collection is reached.
    /// </summary>
    private static object FromSequence(Type targetType, Type elementType, Array elements)
    {
        Type sequence = typeof(IEnumerable<>).MakeGenericType(elementType);

        ConstructorInfo? constructor = targetType.GetConstructor([sequence])
            ?? targetType.GetConstructor([elements.GetType()]);

        return constructor is not null
            ? constructor.Invoke([elements])
            : throw new HollowMappingException(
                $"{targetType.Name} is neither an array nor something a list or set can be assigned to, "
                + $"and has no constructor taking an IEnumerable<{elementType.Name}>, so a collection "
                + "cannot be read into it");
    }
}
