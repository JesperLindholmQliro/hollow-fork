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

using Hollow.Core.Memory.Encoding;
using Hollow.Core.Schema;

namespace Hollow.Core.Read.Engine;

/// <summary>
/// Hashes key values the way a set or map record's hash table was laid out by the producer, so a
/// consumer can probe for a record by its declared hash key.
/// </summary>
public static class SetMapKeyHasher
{
    /// <summary>
    /// Hashes a composite key. <paramref name="fieldTypes"/> gives the type of each key field.
    /// </summary>
    public static int Hash(object?[] key, IReadOnlyList<FieldType> fieldTypes)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(fieldTypes);

        int hash = 0;
        for (int i = 0; i < key.Length; i++)
        {
            hash *= 31;
            hash ^= Hash(key[i], fieldTypes[i]);
        }

        return hash;
    }

    /// <summary>
    /// Hashes a single key field.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="fieldType"/> cannot be hashed, or <paramref name="key"/> is not of that type.
    /// </exception>
    public static int Hash(object? key, FieldType fieldType) =>
        fieldType switch
        {
            FieldType.Int or FieldType.Reference => HashCodes.HashInt(Unbox<int>(key, fieldType)),
            FieldType.Long => HashCodes.HashInt(HollowReadFieldUtils.LongHashCode(Unbox<long>(key, fieldType))),
            FieldType.Bytes => HashCodes.HashInt(HashCodes.Compute(Unbox<byte[]>(key, fieldType))),

            // The producer hashes a string by its natural Java hash code, not by the Murmur hash used
            // for variable-length field data.
            FieldType.String => HashCodes.HashInt(JavaStringHashCode(Unbox<string>(key, fieldType))),

            FieldType.Boolean => HashCodes.HashInt(Unbox<bool>(key, fieldType) ? 1231 : 1237),
            FieldType.Double => HashCodes.HashInt(
                HollowReadFieldUtils.LongHashCode(BitConverter.DoubleToInt64Bits(Unbox<double>(key, fieldType)))),
            FieldType.Float => HashCodes.HashInt(
                BitConverter.SingleToInt32Bits(Unbox<float>(key, fieldType))),
            FieldType.Decimal => HashCodes.HashInt(
                DecimalBits.CanonicalHashCode(Unbox<decimal>(key, fieldType))),
            _ => throw new ArgumentException($"cannot hash a {fieldType} field", nameof(fieldType)),
        };

    /// <summary>
    /// Reproduces <c>java.lang.String.hashCode</c>, which the producer uses when laying out a hash key's
    /// buckets. .NET's own string hash is randomised per process and would not agree with the blob.
    /// </summary>
    internal static int JavaStringHashCode(string value)
    {
        int hash = 0;
        foreach (char c in value)
        {
            hash = (hash * 31) + c;
        }

        return hash;
    }

    private static T Unbox<T>(object? key, FieldType fieldType) =>
        key is T typed
            ? typed
            : throw new ArgumentException(
                $"a {fieldType} key field requires a {typeof(T).Name}, got {key?.GetType().Name ?? "null"}",
                nameof(key));
}
