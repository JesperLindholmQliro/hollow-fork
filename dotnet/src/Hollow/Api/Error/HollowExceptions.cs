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

namespace Hollow.Api.Error;

/// <summary>
/// Thrown when two schemas for the same type declare a field with incompatible types, so that no
/// common schema exists.
/// </summary>
public sealed class IncompatibleSchemaException : InvalidOperationException
{
    /// <summary>
    /// Initialises the exception for a field that differs between two schemas of the same type.
    /// </summary>
    public IncompatibleSchemaException(string typeName, string fieldName, string fieldType, string otherFieldType)
        : base($"Field '{fieldName}' in type '{typeName}' is declared as '{fieldType}' in one schema "
            + $"and '{otherFieldType}' in the other")
    {
        TypeName = typeName;
        FieldName = fieldName;
        FieldType = fieldType;
        OtherFieldType = otherFieldType;
    }

    /// <summary>Initialises the exception with a message.</summary>
    public IncompatibleSchemaException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises the exception with a message and an inner exception.</summary>
    public IncompatibleSchemaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initialises the exception.</summary>
    public IncompatibleSchemaException()
    {
    }

    /// <summary>The name of the type whose schemas disagree.</summary>
    public string? TypeName { get; }

    /// <summary>The name of the field whose declarations disagree.</summary>
    public string? FieldName { get; }

    /// <summary>The field's type in the first schema.</summary>
    public string? FieldType { get; }

    /// <summary>The field's type in the second schema.</summary>
    public string? OtherFieldType { get; }
}

/// <summary>
/// Thrown when a dataset is asked for a schema it does not contain.
/// </summary>
public sealed class SchemaNotFoundException : InvalidOperationException
{
    /// <summary>
    /// Initialises the exception for <paramref name="typeName"/>, listing the types that do exist.
    /// </summary>
    public SchemaNotFoundException(string typeName, IEnumerable<string> availableTypes)
        : base($"Schema for type '{typeName}' not found; available types: "
            + string.Join(", ", availableTypes ?? []))
    {
        TypeName = typeName;
    }

    /// <summary>Initialises the exception with a message.</summary>
    public SchemaNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises the exception with a message and an inner exception.</summary>
    public SchemaNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initialises the exception.</summary>
    public SchemaNotFoundException()
    {
    }

    /// <summary>The name of the type whose schema is missing.</summary>
    public string? TypeName { get; }
}
