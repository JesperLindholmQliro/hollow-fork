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

using Hollow.Core.Util;

namespace Hollow.Core.Schema;

/// <summary>
/// The four kinds of Hollow schema.
/// </summary>
/// <remarks>
/// Java nests this as <c>HollowSchema.SchemaType</c> with two <c>int</c> fields per constant; those
/// are exposed through <see cref="SchemaTypeExtensions"/> here. Note that the enum member values are
/// <em>not</em> the serialised type ids — use <see cref="SchemaTypeExtensions.GetTypeId"/>.
/// </remarks>
public enum SchemaType
{
    /// <summary>A fixed set of strongly typed fields.</summary>
    Object,

    /// <summary>An unordered collection without duplicates.</summary>
    Set,

    /// <summary>An ordered collection.</summary>
    List,

    /// <summary>A key/value mapping.</summary>
    Map,
}

/// <summary>
/// The serialised type ids Java attaches to <c>SchemaType</c> enum constants.
/// </summary>
public static class SchemaTypeExtensions
{
    /// <summary>The type id written for a schema of this kind that has no primary or hash key.</summary>
    public static int GetTypeId(this SchemaType schemaType) => schemaType switch
    {
        SchemaType.Object => 0,
        SchemaType.Set => 1,
        SchemaType.List => 2,
        SchemaType.Map => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(schemaType), schemaType, "unknown schema type"),
    };

    /// <summary>
    /// The type id written for a schema of this kind that carries a primary or hash key.
    /// </summary>
    /// <remarks><see cref="SchemaType.List"/> cannot carry a key, and yields -1.</remarks>
    public static int GetTypeIdWithPrimaryKey(this SchemaType schemaType) => schemaType switch
    {
        SchemaType.Object => 6,
        SchemaType.Set => 4,
        SchemaType.List => -1,
        SchemaType.Map => 5,
        _ => throw new ArgumentOutOfRangeException(nameof(schemaType), schemaType, "unknown schema type"),
    };

    /// <summary>
    /// Maps a serialised type id back to its schema kind.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="typeId"/> is not a known type id.</exception>
    public static SchemaType FromTypeId(int typeId) => typeId switch
    {
        0 or 6 => SchemaType.Object,
        1 or 4 => SchemaType.Set,
        2 => SchemaType.List,
        3 or 5 => SchemaType.Map,
        _ => throw new ArgumentException(
            $"Cannot recognize HollowSchema type id {typeId.Invariant()}", nameof(typeId)),
    };

    /// <summary>
    /// Whether a serialised type id indicates that a primary or hash key follows.
    /// </summary>
    public static bool HasKey(int typeId) => typeId is 4 or 5 or 6;
}

/// <summary>
/// Thrown when a schema turns out to be of a kind the caller does not handle.
/// </summary>
public sealed class UnrecognizedSchemaTypeException : InvalidOperationException
{
    /// <summary>
    /// Initialises the exception for the named type and its schema kind.
    /// </summary>
    public UnrecognizedSchemaTypeException(string name, SchemaType schemaType)
        : base($"unrecognized schema type; name={name} type={schemaType}")
    {
    }

    /// <summary>Initialises the exception with a message.</summary>
    public UnrecognizedSchemaTypeException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises the exception with a message and an inner exception.</summary>
    public UnrecognizedSchemaTypeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initialises the exception.</summary>
    public UnrecognizedSchemaTypeException()
    {
    }
}
