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
using Hollow.Api.Custom;
using Hollow.Api.Objects.Generic;
using Hollow.Core;
using Hollow.Core.Schema;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Api.Consumer.Index;

/// <summary>
/// Turns a matched ordinal into the record a query returns.
/// </summary>
/// <remarks>
/// For a generated type that means calling the API's accessor for it, which is the one place the typed
/// indexes need the generated API rather than the state engine. For a generic record it means building
/// the wrapper directly.
/// </remarks>
/// <typeparam name="T">The result type.</typeparam>
internal sealed class SelectFieldPathResultExtractor<T>
{
    private readonly Func<HollowApi, int, T> _extract;

    private SelectFieldPathResultExtractor(BoundFieldPath fieldPath, Func<HollowApi, int, T> extract)
    {
        FieldPath = fieldPath;
        _extract = extract;
    }

    internal BoundFieldPath FieldPath { get; }

    internal T Extract(HollowApi api, int ordinal) => _extract(api, ordinal);

    /// <summary>
    /// Binds <paramref name="fieldPath"/> from <paramref name="rootTypeName"/> and works out how a record of
    /// <typeparamref name="T"/> is built from an ordinal of what it resolves to.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The path resolves to a value field rather than a record, or to a record
    /// <typeparamref name="T"/> cannot hold.
    /// </exception>
    internal static SelectFieldPathResultExtractor<T> From(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type apiType,
        IHollowDataset dataset,
        string rootTypeName,
        string fieldPath)
    {
        BoundFieldPath path = FieldPathResolvers.HashIndex(dataset, rootTypeName, fieldPath);

        string typeName = rootTypeName;

        if (path.LastSegment is { } lastSegment)
        {
            typeName = lastSegment.TypeName ?? rootTypeName;

            // The underlying hash index selects the record enclosing a value field; this layer insists
            // the path names the record itself, so that what comes back is what the path says.
            if (path.ResolvedFieldType != FieldType.Reference)
            {
                throw new ArgumentException(
                    $"the select type {typeof(T)} cannot be selected by field path {fieldPath}, which "
                    + $"resolves to a field of type {path.ResolvedFieldType}");
            }
        }

        // A generic record reads any type by name, so it needs no accessor on the API.
        if (typeof(T).IsAssignableFrom(typeof(GenericHollowObject))
            || typeof(T).IsAssignableFrom(typeof(GenericHollowList))
            || typeof(T).IsAssignableFrom(typeof(GenericHollowSet))
            || typeof(T).IsAssignableFrom(typeof(GenericHollowMap)))
        {
            return new SelectFieldPathResultExtractor<T>(
                path,
                (api, ordinal) => GenericHollowRecord.Instantiate(api.DataAccess, typeName, ordinal) is T record
                    ? record
                    : throw new InvalidOperationException(
                        $"{typeName} does not read as a {typeof(T)}"));
        }

        if (typeName != "String" && HollowObjectMapper.DefaultTypeName(typeof(T)) != typeName)
        {
            throw new ArgumentException(
                $"the select type {typeof(T)} cannot be selected by field path {fieldPath}, which resolves "
                + $"to a reference to {typeName}");
        }

        // A generated API exposes one accessor per type, named for it. Java looks up
        // "get" + simple name; the port's generator emits the PascalCase form.
        MethodInfo accessor =
            apiType.GetMethod($"Get{typeof(T).Name}", BindingFlags.Instance | BindingFlags.Public, [typeof(int)])
            ?? throw new ArgumentException(
                $"the select type {typeof(T)} is not associated with the API {apiType}, which declares no "
                + $"Get{typeof(T).Name}(int)");

        return new SelectFieldPathResultExtractor<T>(
            path,
            (api, ordinal) => (T)accessor.Invoke(api, [ordinal])!);
    }
}
