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

using Hollow.Api.Objects.Generic;
using Hollow.Core.Index.Key;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.Engine;

namespace Hollow.Api.Consumer.Data;

/// <summary>
/// What a transition added, removed and replaced, as records read by field name rather than through a
/// generated API.
/// </summary>
/// <remarks>
/// <see cref="HollowDataAccessor{T}"/> answers what changed but leaves reading the records to a
/// subclass, which normally means generated code. This is the answer for a dataset that has none — a
/// tool, a diagnostic, or anything that knows the type name at run time and not at compile time.
/// </remarks>
public sealed class GenericHollowRecordDataAccessor : HollowDataAccessor<GenericHollowObject>
{
    /// <summary>Reads the <paramref name="type"/> records <paramref name="consumer"/> holds.</summary>
    public GenericHollowRecordDataAccessor(HollowConsumer consumer, string type)
        : base(consumer, type)
    {
    }

    /// <summary>
    /// Reads the <paramref name="type"/> records of <paramref name="stateEngine"/>, matching them by
    /// the key the type declares.
    /// </summary>
    public GenericHollowRecordDataAccessor(HollowReadStateEngine stateEngine, string type)
        : base(stateEngine, type)
    {
    }

    /// <summary>
    /// Reads them matching on <paramref name="fieldPaths"/> rather than on the declared key.
    /// </summary>
    public GenericHollowRecordDataAccessor(
        HollowReadStateEngine stateEngine, string type, params string[] fieldPaths)
        : base(stateEngine, type, fieldPaths)
    {
    }

    /// <summary>Reads them matching on <paramref name="primaryKey"/>.</summary>
    public GenericHollowRecordDataAccessor(
        HollowReadStateEngine stateEngine, string type, PrimaryKey? primaryKey)
        : base(stateEngine, type, primaryKey)
    {
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The dataset has no such object type.</exception>
    public override GenericHollowObject GetRecord(int ordinal)
    {
        IHollowTypeDataAccess dataAccess = StateEngine.GetTypeDataAccess(Type)
            ?? throw new InvalidOperationException($"{Type} is not a type of this dataset");

        return dataAccess is IHollowObjectTypeDataAccess objectDataAccess
            ? new GenericHollowObject(objectDataAccess, ordinal)
            : throw new InvalidOperationException($"{Type} is not an object type");
    }
}
