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

using Hollow.Core.Index;
using Hollow.Core.Index.Key;
using Hollow.Core.Read.Engine;
using Hollow.Core.Schema;
using Hollow.Core.Util;

namespace Hollow.Api.Producer.Validation;

/// <summary>
/// Fails a cycle in which two records of a type share a primary key.
/// </summary>
/// <remarks>
/// <para>
/// A duplicate key is usually a bug in the data source rather than in Hollow, and it makes every
/// primary-key lookup for that key ambiguous. Catching it before the state is announced means
/// consumers never see it.
/// </para>
/// <para>
/// Records whose every field is part of the key cannot produce duplicates — two such records are
/// identical and deduplicate to one ordinal — so a validator over such a type never fires.
/// </para>
/// </remarks>
public sealed class DuplicateDataDetectionValidator : IValidatorListener
{
    private const int MaxReportedDuplicateKeys = 10;

    private readonly string _typeName;
    private readonly string[]? _fieldPaths;

    /// <summary>
    /// Validates <paramref name="typeName"/> against the primary key its schema declares.
    /// </summary>
    public DuplicateDataDetectionValidator(string typeName)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeName);

        _typeName = typeName;
    }

    /// <summary>
    /// Validates <paramref name="typeName"/> against an explicit key, for a type whose schema declares
    /// none or declares a different one.
    /// </summary>
    public DuplicateDataDetectionValidator(string typeName, params string[] fieldPaths)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeName);
        ArgumentNullException.ThrowIfNull(fieldPaths);

        _typeName = typeName;
        _fieldPaths = fieldPaths;
    }

    /// <inheritdoc />
    public string Name => $"{nameof(DuplicateDataDetectionValidator)}_{_typeName}";

    /// <inheritdoc />
    public ValidationResult OnValidate(IReadState readState)
    {
        ArgumentNullException.ThrowIfNull(readState);

        ValidationResult.Builder result = ValidationResult.From(this).Detail("Typename", _typeName);

        HollowReadStateEngine stateEngine = readState.StateEngine;

        if (stateEngine.GetTypeState(_typeName) is not { } typeState)
        {
            return result.Failed(
                $"{nameof(DuplicateDataDetectionValidator)} is defined for type {_typeName}, but that type is not "
                + "present. Check that the producer's data model was initialised with it.");
        }

        if (typeState.Schema is not HollowObjectSchema objectSchema)
        {
            return result.Failed(
                $"{nameof(DuplicateDataDetectionValidator)} is defined for type {_typeName}, which is a "
                + $"{typeState.Schema.SchemaType} type. Only an object type has a primary key.");
        }

        PrimaryKey? primaryKey = _fieldPaths is { } fieldPaths
            ? new PrimaryKey(_typeName, fieldPaths)
            : objectSchema.PrimaryKey;

        if (primaryKey is null)
        {
            return result.Failed(
                $"{nameof(DuplicateDataDetectionValidator)} is defined for type {_typeName}, but no primary key was "
                + "found. Declare one on the schema or pass the field paths to the validator.");
        }

        result.Detail("FieldPaths", string.Join(", ", primaryKey.FieldPaths));

        HollowPrimaryKeyIndex index = new(stateEngine, primaryKey);

        // Ask for one more than is reported, so the message can say whether it is showing all of them.
        IReadOnlyList<HollowPrimaryKeyIndex.DuplicateKeyInfo> duplicates =
            index.GetDuplicateKeys(MaxReportedDuplicateKeys + 1);

        if (duplicates.Count == 0)
        {
            return result.Passed($"{Name} found no duplicate keys.");
        }

        string reported = string.Join(", ", duplicates.Take(MaxReportedDuplicateKeys));
        string ellipsis = duplicates.Count > MaxReportedDuplicateKeys ? ", …" : string.Empty;

        return result
            .Detail("DuplicateKeyCount", Math.Min(duplicates.Count, MaxReportedDuplicateKeys))
            .Failed(
                $"Duplicate keys found for type {_typeName}. The primary key is "
                + $"[{string.Join(", ", primaryKey.FieldPaths)}]. Duplicates: {reported}{ellipsis}");
    }
}

/// <summary>
/// Fails a cycle in which a type's record count moved further than it should have.
/// </summary>
/// <remarks>
/// A data source that half-fails often produces a state that is structurally valid and badly
/// incomplete. A bound on how much a type may grow or shrink in one cycle is a crude check, but it is
/// the one that catches that case before consumers see it.
/// </remarks>
public sealed class RecordCountVarianceValidator : IValidatorListener
{
    private readonly string _typeName;
    private readonly Func<float> _allowableVariancePercent;

    /// <summary>
    /// Allows <paramref name="typeName"/>'s record count to move by at most
    /// <paramref name="allowableVariancePercent"/> per cycle.
    /// </summary>
    /// <param name="typeName">The type to watch.</param>
    /// <param name="allowableVariancePercent">
    /// The permitted change as a percentage of the previous cycle's count. Zero means the count must
    /// not change at all.
    /// </param>
    public RecordCountVarianceValidator(string typeName, float allowableVariancePercent)
        : this(typeName, () => allowableVariancePercent)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(allowableVariancePercent);
    }

    /// <summary>
    /// Allows <paramref name="typeName"/>'s record count to move by at most whatever
    /// <paramref name="allowableVariancePercent"/> returns, read afresh each cycle so that the
    /// threshold can be changed without restarting the producer.
    /// </summary>
    public RecordCountVarianceValidator(string typeName, Func<float> allowableVariancePercent)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeName);
        ArgumentNullException.ThrowIfNull(allowableVariancePercent);

        _typeName = typeName;
        _allowableVariancePercent = allowableVariancePercent;
    }

    /// <inheritdoc />
    public string Name => $"{nameof(RecordCountVarianceValidator)}_{_typeName}";

    /// <inheritdoc />
    public ValidationResult OnValidate(IReadState readState)
    {
        ArgumentNullException.ThrowIfNull(readState);

        float allowable = _allowableVariancePercent();

        ValidationResult.Builder result = ValidationResult.From(this)
            .Detail("Typename", _typeName)
            .Detail("AllowableVariancePercent", allowable);

        if (allowable < 0)
        {
            return result.Failed(
                $"The variance threshold for type {_typeName} is negative ({allowable.Invariant()}).");
        }

        if (readState.StateEngine.GetTypeState(_typeName) is not { } typeState)
        {
            return result.Failed(
                $"{nameof(RecordCountVarianceValidator)} is defined for type {_typeName}, which is not present.");
        }

        int latest = typeState.PopulatedOrdinals.Cardinality();
        int previous = typeState.PreviousOrdinals.Cardinality();

        result.Detail("LatestRecordCount", latest).Detail("PreviousRecordCount", previous);

        // Nothing to compare against on the first cycle of a chain, and dividing by it would not work
        // anyway.
        if (previous == 0)
        {
            return result.Detail("skipped", true).Passed(
                $"Type {_typeName} held no records in the previous cycle, so there is nothing to compare against.");
        }

        float changePercent = Math.Abs(latest - previous) * 100f / previous;
        result.Detail("ActualChangePercent", changePercent);

        return changePercent > allowable
            ? result.Failed(
                $"Record count for type {_typeName} changed by {changePercent.Invariant()}%, which is more than the "
                + $"allowed {allowable.Invariant()}%.")
            : result.Passed(
                $"{Name} changed by {changePercent.Invariant()}%, within the allowed {allowable.Invariant()}%.");
    }
}
