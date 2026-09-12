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

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Hollow.Api.Objects;
using Hollow.Core;
using Hollow.Core.Schema;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Api.Consumer.Index;

/// <summary>
/// Pulls one match value out of a query object and puts it in the form the underlying index wants.
/// </summary>
/// <remarks>
/// The transformation that matters is for a reference field: the index matches on an ordinal, and the
/// query holds a record, so the extractor reads the record's ordinal. The rest is type checking, done
/// once when the index is built rather than on every query.
/// </remarks>
/// <typeparam name="TQuery">The query type.</typeparam>
internal sealed class MatchFieldPathArgumentExtractor<TQuery>
{
    private readonly Func<TQuery, object?> _extract;

    private MatchFieldPathArgumentExtractor(BoundFieldPath fieldPath, Func<TQuery, object?> extract)
    {
        FieldPath = fieldPath;
        _extract = extract;
    }

    internal BoundFieldPath FieldPath { get; }

    internal object? Extract(TQuery query) => _extract(query);

    /// <summary>
    /// Builds one extractor per <see cref="FieldPathAttribute"/>-annotated member of
    /// <typeparamref name="TQuery"/>, in <see cref="FieldPathAttribute.Order"/> order.
    /// </summary>
    /// <remarks>
    /// Java reads annotated fields and annotated zero-argument methods; the C# equivalent of both is a
    /// property, and a field is read too so that a plain data holder works either way. Only members
    /// declared on the type itself are read, as in Java.
    /// </remarks>
    internal static List<MatchFieldPathArgumentExtractor<TQuery>> FromHolderType(
        IHollowDataset dataset,
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties
            | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicFields)]
        Type queryType,
        string rootTypeName,
        FieldPathResolver resolver)
    {
        const BindingFlags Declared =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        List<(MemberInfo Member, FieldPathAttribute Attribute)> annotated =
        [
            .. queryType.GetProperties(Declared)
                .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
                .Select(property => ((MemberInfo)property, property.GetCustomAttribute<FieldPathAttribute>()!))
                .Where(pair => pair.Item2 is not null),
            .. queryType.GetFields(Declared)
                .Select(field => ((MemberInfo)field, field.GetCustomAttribute<FieldPathAttribute>()!))
                .Where(pair => pair.Item2 is not null),
        ];

        if (annotated.Count == 0)
        {
            throw new ArgumentException(
                $"the query type {queryType} declares no [{nameof(FieldPathAttribute)}] member, so there is "
                + "nothing to match on",
                nameof(queryType));
        }

        return
        [
            .. annotated
                .OrderBy(pair => pair.Attribute.Order)
                .Select(pair => FromMember(dataset, rootTypeName, pair.Member, pair.Attribute, resolver)),
        ];
    }

    /// <summary>
    /// Builds the one extractor for an index matching on a single path, where the query is the value
    /// itself rather than an object holding it.
    /// </summary>
    internal static MatchFieldPathArgumentExtractor<TQuery> FromPathAndType(
        IHollowDataset dataset, string rootTypeName, string fieldPath, FieldPathResolver resolver) =>
        FromAccessor(dataset, rootTypeName, fieldPath, typeof(TQuery), query => query, resolver);

    private static MatchFieldPathArgumentExtractor<TQuery> FromMember(
        IHollowDataset dataset,
        string rootTypeName,
        MemberInfo member,
        FieldPathAttribute attribute,
        FieldPathResolver resolver)
    {
        string fieldPath = attribute.Path.Length == 0 ? member.Name : attribute.Path;

        (Type memberType, Func<object?, object?> read) = member switch
        {
            PropertyInfo property => (property.PropertyType, (Func<object?, object?>)property.GetValue),
            _ => (((FieldInfo)member).FieldType, ((FieldInfo)member).GetValue),
        };

        return FromAccessor(dataset, rootTypeName, fieldPath, memberType, query => read(query), resolver);
    }

    private static MatchFieldPathArgumentExtractor<TQuery> FromAccessor(
        IHollowDataset dataset,
        string rootTypeName,
        string fieldPath,
        Type accessorType,
        Func<TQuery, object?> accessor,
        FieldPathResolver resolver)
    {
        BoundFieldPath path = resolver(dataset, rootTypeName, fieldPath);

        // A path that reaches into a collection lands on the element type, which is a reference.
        FieldType schemaFieldType = path.ResolvedFieldType ?? FieldType.Reference;

        // A nullable value type matches the field its underlying type matches; the index takes a null
        // key as "no constraint on this field", which is what an unset key member means.
        Type matchType = Nullable.GetUnderlyingType(accessorType) ?? accessorType;

        Func<TQuery, object?> extract = accessor;

        switch (schemaFieldType)
        {
            case FieldType.Boolean when matchType != typeof(bool):
            case FieldType.Double when matchType != typeof(double):
            case FieldType.Float when matchType != typeof(float):
            case FieldType.Long when matchType != typeof(long):
            case FieldType.Decimal when matchType != typeof(decimal):
            case FieldType.Bytes when matchType != typeof(byte[]):
                throw IncompatibleMatchType(accessorType, fieldPath, schemaFieldType);

            case FieldType.Int:
                // The index matches an int field against an int, so anything that widens to one without
                // losing information is converted here rather than refused.
                if (matchType == typeof(byte) || matchType == typeof(sbyte)
                    || matchType == typeof(short) || matchType == typeof(ushort)
                    || matchType == typeof(char))
                {
                    extract = query => accessor(query) is { } value
                        ? Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture)
                        : null;
                }
                else if (matchType != typeof(int))
                {
                    throw IncompatibleMatchType(accessorType, fieldPath, schemaFieldType);
                }

                break;

            case FieldType.String:
                if (matchType == typeof(char[]))
                {
                    extract = query => accessor(query) is char[] characters ? new string(characters) : null;
                }
                else if (matchType != typeof(string))
                {
                    throw IncompatibleMatchType(accessorType, fieldPath, schemaFieldType);
                }

                break;

            case FieldType.Reference:
            {
                string typeName = path.LastSegment?.TypeName ?? path.RootType;

                if (matchType == typeof(int))
                {
                    // An ordinal, given directly. Java does not allow this; it is free here and is what
                    // a caller holding a match result from another index already has.
                    break;
                }

                if (!typeof(IHollowRecord).IsAssignableFrom(matchType)
                    || (typeName != "String" && HollowObjectMapper.DefaultTypeName(matchType) != typeName))
                {
                    throw IncompatibleMatchType(accessorType, fieldPath, typeName);
                }

                extract = query => accessor(query) is IHollowRecord record ? record.Ordinal : null;
                break;
            }

            default:
                break;
        }

        return new MatchFieldPathArgumentExtractor<TQuery>(path, extract);
    }

    private static ArgumentException IncompatibleMatchType(
        Type matchType, string fieldPath, FieldType schemaFieldType) =>
        new($"the match type {matchType} cannot match field path {fieldPath}, which resolves to a field of "
            + $"type {schemaFieldType}");

    private static ArgumentException IncompatibleMatchType(Type matchType, string fieldPath, string typeName) =>
        new($"the match type {matchType} cannot match field path {fieldPath}, which resolves to a reference "
            + $"to {typeName}");
}
