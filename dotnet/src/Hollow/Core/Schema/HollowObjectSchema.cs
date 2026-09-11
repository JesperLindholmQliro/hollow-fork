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

using System.Text;
using Hollow.Api.Error;
using Hollow.Core.Index.Key;
using Hollow.Core.Memory.Encoding;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Filter;
using Hollow.Core.Write;

namespace Hollow.Core.Schema;

/// <summary>
/// The schema of an Object record type: a fixed, ordered set of strongly typed fields.
/// </summary>
/// <seealso cref="HollowSchema" />
public sealed class HollowObjectSchema : HollowSchema
{
    private readonly Dictionary<string, int> _fieldPositions;
    private readonly string[] _fieldNames;
    private readonly FieldType[] _fieldTypes;
    private readonly string?[] _referencedTypes;
    private readonly HollowTypeReadState?[] _referencedFieldTypeStates;

    /// <summary>
    /// Initialises a schema with room for <paramref name="numFields"/> fields, keyed by
    /// <paramref name="keyFieldPaths"/>.
    /// </summary>
    public HollowObjectSchema(string schemaName, int numFields, params string[]? keyFieldPaths)
        : this(
            schemaName,
            numFields,
            keyFieldPaths is null || keyFieldPaths.Length == 0 ? null : new PrimaryKey(schemaName, keyFieldPaths))
    {
    }

    /// <summary>
    /// Initialises a schema with room for <paramref name="numFields"/> fields and the given primary key.
    /// </summary>
    public HollowObjectSchema(string schemaName, int numFields, PrimaryKey? primaryKey)
        : base(schemaName)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(numFields);

        _fieldPositions = new Dictionary<string, int>(numFields, StringComparer.Ordinal);
        _fieldNames = new string[numFields];
        _fieldTypes = new FieldType[numFields];
        _referencedTypes = new string[numFields];
        _referencedFieldTypeStates = new HollowTypeReadState[numFields];
        PrimaryKey = primaryKey;
    }

    /// <summary>The number of fields added so far.</summary>
    public int FieldCount { get; private set; }

    /// <summary>The primary key of this type, or <see langword="null"/> when it has none.</summary>
    public PrimaryKey? PrimaryKey { get; }

    /// <inheritdoc />
    public override SchemaType SchemaType => SchemaType.Object;

    /// <summary>
    /// Appends a field, returning its position.
    /// </summary>
    public int AddField(string fieldName, FieldType fieldType) => AddField(fieldName, fieldType, null);

    /// <summary>
    /// Appends a field, returning its position.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="fieldType"/> is <see cref="FieldType.Reference"/> but no
    /// <paramref name="referencedType"/> was given.
    /// </exception>
    public int AddField(string fieldName, FieldType fieldType, string? referencedType)
    {
        ArgumentNullException.ThrowIfNull(fieldName);

        if (fieldType == FieldType.Reference && referencedType is null)
        {
            throw new ArgumentException(
                "When adding a REFERENCE field to a schema, the referenced type must be provided. "
                + $"Check type: {Name} field: {fieldName}",
                nameof(referencedType));
        }

        _fieldNames[FieldCount] = fieldName;
        _fieldTypes[FieldCount] = fieldType;
        _referencedTypes[FieldCount] = referencedType;
        _fieldPositions[fieldName] = FieldCount;

        return FieldCount++;
    }

    /// <summary>
    /// Returns the position of a previously added field, or -1 when the field has not been added.
    /// </summary>
    public int GetPosition(string fieldName) =>
        _fieldPositions.TryGetValue(fieldName, out int position) ? position : -1;

    /// <summary>Gets the name of the field at <paramref name="fieldPosition"/>.</summary>
    public string GetFieldName(int fieldPosition) => _fieldNames[fieldPosition];

    /// <summary>Gets the type of the field at <paramref name="fieldPosition"/>.</summary>
    public FieldType GetFieldType(int fieldPosition) => _fieldTypes[fieldPosition];

    /// <summary>
    /// Gets the type of the named field, or <see langword="null"/> when there is no such field.
    /// </summary>
    public FieldType? GetFieldType(string fieldName)
    {
        int fieldPosition = GetPosition(fieldName);
        return fieldPosition == -1 ? null : GetFieldType(fieldPosition);
    }

    /// <summary>
    /// Gets the referenced type of the field at <paramref name="fieldPosition"/>, or
    /// <see langword="null"/> when the field is not a reference.
    /// </summary>
    public string? GetReferencedType(int fieldPosition) => _referencedTypes[fieldPosition];

    /// <summary>
    /// Gets the referenced type of the named field, or <see langword="null"/> when there is no such
    /// field or it is not a reference.
    /// </summary>
    public string? GetReferencedType(string fieldName)
    {
        int fieldPosition = GetPosition(fieldName);
        return fieldPosition == -1 ? null : GetReferencedType(fieldPosition);
    }

    /// <summary>
    /// Records the read state of the type referenced by the field at
    /// <paramref name="fieldPosition"/>.
    /// </summary>
    public void SetReferencedTypeState(int fieldPosition, HollowTypeReadState? state) =>
        _referencedFieldTypeStates[fieldPosition] = state;

    /// <summary>
    /// Gets the read state of the type referenced by the field at <paramref name="fieldPosition"/>,
    /// populated during deserialisation.
    /// </summary>
    public HollowTypeReadState? GetReferencedTypeState(int fieldPosition) =>
        _referencedFieldTypeStates[fieldPosition];

    /// <summary>
    /// Returns a schema containing only the fields present in both this schema and
    /// <paramref name="otherSchema"/>.
    /// </summary>
    /// <exception cref="IncompatibleSchemaException">
    /// A field is present in both schemas but declared with different types.
    /// </exception>
    public HollowObjectSchema FindCommonSchema(HollowObjectSchema otherSchema)
    {
        ArgumentNullException.ThrowIfNull(otherSchema);

        if (Name != otherSchema.Name)
        {
            throw new ArgumentException(
                "Cannot find common schema of two schemas with different names!", nameof(otherSchema));
        }

        int commonFields = 0;
        for (int i = 0; i < FieldCount; i++)
        {
            if (otherSchema.GetPosition(_fieldNames[i]) != -1)
            {
                commonFields++;
            }
        }

        PrimaryKey? primaryKey = IsNullableObjectEquals(PrimaryKey, otherSchema.PrimaryKey) ? PrimaryKey : null;
        HollowObjectSchema commonSchema = new(Name, commonFields, primaryKey);

        for (int i = 0; i < FieldCount; i++)
        {
            int otherFieldIndex = otherSchema.GetPosition(_fieldNames[i]);
            if (otherFieldIndex == -1)
            {
                continue;
            }

            if (_fieldTypes[i] != otherSchema.GetFieldType(otherFieldIndex)
                || _referencedTypes[i] != otherSchema.GetReferencedType(otherFieldIndex))
            {
                throw new IncompatibleSchemaException(
                    Name,
                    _fieldNames[i],
                    DescribeFieldType(this, i),
                    DescribeFieldType(otherSchema, otherFieldIndex));
            }

            commonSchema.AddField(_fieldNames[i], _fieldTypes[i], _referencedTypes[i]);
        }

        return commonSchema;
    }

    /// <summary>
    /// Returns a schema containing the fields of this schema followed by any fields present only in
    /// <paramref name="otherSchema"/>.
    /// </summary>
    public HollowObjectSchema FindUnionSchema(HollowObjectSchema otherSchema)
    {
        ArgumentNullException.ThrowIfNull(otherSchema);

        if (Name != otherSchema.Name)
        {
            throw new ArgumentException(
                "Cannot find common schema of two schemas with different names!", nameof(otherSchema));
        }

        int totalFields = otherSchema.FieldCount;
        for (int i = 0; i < FieldCount; i++)
        {
            if (otherSchema.GetPosition(_fieldNames[i]) == -1)
            {
                totalFields++;
            }
        }

        PrimaryKey? primaryKey = IsNullableObjectEquals(PrimaryKey, otherSchema.PrimaryKey) ? PrimaryKey : null;
        HollowObjectSchema unionSchema = new(Name, totalFields, primaryKey);

        for (int i = 0; i < FieldCount; i++)
        {
            unionSchema.AddField(_fieldNames[i], _fieldTypes[i], _referencedTypes[i]);
        }

        for (int i = 0; i < otherSchema.FieldCount; i++)
        {
            if (GetPosition(otherSchema.GetFieldName(i)) == -1)
            {
                unionSchema.AddField(
                    otherSchema.GetFieldName(i), otherSchema.GetFieldType(i), otherSchema.GetReferencedType(i));
            }
        }

        return unionSchema;
    }

    /// <summary>
    /// Returns a schema containing only the fields <paramref name="filter"/> includes.
    /// </summary>
    public HollowObjectSchema FilterSchema(ITypeFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        int includedFields = 0;
        for (int i = 0; i < FieldCount; i++)
        {
            if (filter.Includes(Name, _fieldNames[i]))
            {
                includedFields++;
            }
        }

        HollowObjectSchema filteredSchema = new(Name, includedFields, PrimaryKey);

        for (int i = 0; i < FieldCount; i++)
        {
            if (filter.Includes(Name, _fieldNames[i]))
            {
                filteredSchema.AddField(_fieldNames[i], _fieldTypes[i], _referencedTypes[i]);
            }
        }

        return filteredSchema;
    }

    /// <inheritdoc />
    public override void WriteTo(HollowBlobOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);

        output.WriteByte(PrimaryKey is null
            ? SchemaType.Object.GetTypeId()
            : SchemaType.Object.GetTypeIdWithPrimaryKey());

        output.WriteUtf(Name);

        if (PrimaryKey is not null)
        {
            VarInt.WriteVInt(output, PrimaryKey.FieldCount);
            for (int i = 0; i < PrimaryKey.FieldCount; i++)
            {
                output.WriteUtf(PrimaryKey.GetFieldPath(i));
            }
        }

        output.WriteInt16((short)FieldCount);
        for (int i = 0; i < FieldCount; i++)
        {
            output.WriteUtf(_fieldNames[i]);
            output.WriteUtf(_fieldTypes[i].ToWireName());
            if (_fieldTypes[i] == FieldType.Reference)
            {
                output.WriteUtf(_referencedTypes[i]!);
            }
        }
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj))
        {
            return true;
        }

        if (obj is not HollowObjectSchema other
            || Name != other.Name
            || FieldCount != other.FieldCount
            || !IsNullableObjectEquals(PrimaryKey, other.PrimaryKey))
        {
            return false;
        }

        for (int i = 0; i < FieldCount; i++)
        {
            if (GetFieldType(i) != other.GetFieldType(i)
                || GetFieldName(i) != other.GetFieldName(i)
                || (GetFieldType(i) == FieldType.Reference && GetReferencedType(i) != other.GetReferencedType(i)))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        System.HashCode hash = default;
        hash.Add(Name);
        hash.Add(SchemaType);
        hash.Add(PrimaryKey);

        for (int i = 0; i < FieldCount; i++)
        {
            hash.Add(_fieldNames[i]);
            hash.Add(_fieldTypes[i]);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString()
    {
        StringBuilder builder = new(Name);

        if (PrimaryKey is not null)
        {
            builder.Append(" @PrimaryKey(").AppendJoin(", ", PrimaryKey.FieldPaths).Append(')');
        }

        builder.Append(" {\n");
        for (int i = 0; i < FieldCount; i++)
        {
            builder.Append('\t')
                .Append(GetFieldType(i) == FieldType.Reference ? GetReferencedType(i) : GetFieldType(i).ToSchemaName())
                .Append(' ')
                .Append(GetFieldName(i))
                .Append(";\n");
        }

        return builder.Append('}').ToString();
    }

    private static string DescribeFieldType(HollowObjectSchema schema, int fieldPosition) =>
        schema.GetFieldType(fieldPosition) == FieldType.Reference
            ? schema.GetReferencedType(fieldPosition)!
            : schema.GetFieldType(fieldPosition).ToSchemaName();
}
