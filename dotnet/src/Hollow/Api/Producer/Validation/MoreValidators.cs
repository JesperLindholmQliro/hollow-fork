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

using Hollow.Api.Objects;
using Hollow.Core;
using Hollow.Core.Index.Key;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Schema;
using Hollow.Core.Util;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Api.Producer.Validation;

/// <summary>
/// Fails a cycle in which a type holds fewer records than it should.
/// </summary>
/// <remarks>
/// The floor under <see cref="RecordCountVarianceValidator"/>: a variance bound says the count may not
/// move far, which is no help on the first cycle of a chain or after a legitimate large change. A
/// minimum says the count may never be absurd, whatever it was before.
/// </remarks>
public sealed class MinimumRecordCountValidator : IValidatorListener
{
    /// <summary>
    /// The most records a Hollow type can hold, which bounds a sensible threshold from above.
    /// </summary>
    private const int MaximumRecordCount = 1 << 29;

    private readonly string _typeName;
    private readonly Func<int> _minimumRecordCount;

    /// <summary>
    /// Requires <paramref name="typeName"/> to hold at least <paramref name="minimumRecordCount"/>
    /// records.
    /// </summary>
    public MinimumRecordCountValidator(string typeName, int minimumRecordCount)
        : this(typeName, () => minimumRecordCount)
    {
    }

    /// <summary>
    /// Requires <paramref name="typeName"/> to hold at least whatever
    /// <paramref name="minimumRecordCount"/> returns, read afresh each cycle so the threshold can be
    /// changed without restarting the producer.
    /// </summary>
    public MinimumRecordCountValidator(string typeName, Func<int> minimumRecordCount)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeName);
        ArgumentNullException.ThrowIfNull(minimumRecordCount);

        _typeName = typeName;
        _minimumRecordCount = minimumRecordCount;
    }

    /// <inheritdoc />
    public string Name => $"{nameof(MinimumRecordCountValidator)}_{_typeName}";

    /// <inheritdoc />
    public ValidationResult OnValidate(IReadState readState)
    {
        ArgumentNullException.ThrowIfNull(readState);

        ValidationResult.Builder result = ValidationResult.From(this).Detail("Typename", _typeName);

        int minimum = _minimumRecordCount();

        if (minimum < 0 || minimum > MaximumRecordCount)
        {
            return result.Error(new ArgumentOutOfRangeException(
                nameof(minimum),
                minimum,
                $"the minimum record count for type {_typeName} has to be between 0 and "
                + MaximumRecordCount.Invariant()));
        }

        result.Detail("AllowableMinRecordCount", minimum);

        if (readState.StateEngine.GetTypeState(_typeName) is not { } typeState)
        {
            return result.Failed(
                $"{nameof(MinimumRecordCountValidator)} is defined for type {_typeName}, which is not present.");
        }

        int recordCount = typeState.PopulatedOrdinals.Cardinality();
        result.Detail("RecordCount", recordCount);

        return recordCount < minimum
            ? result.Failed(
                $"Type {_typeName} holds {recordCount.Invariant()} records, fewer than the required "
                + $"{minimum.Invariant()}.")
            : result.Passed(
                $"{Name} holds {recordCount.Invariant()} records, at or above the required "
                + $"{minimum.Invariant()}.");
    }
}

/// <summary>
/// Fails a cycle in which any record of a type has a null in one of its primary key fields.
/// </summary>
/// <remarks>
/// A null key field is not a duplicate, so <see cref="DuplicateDataDetectionValidator"/> does not catch
/// it, and it is not a count change, so the count validators do not either. What it does is make the
/// record unfindable: a key lookup cannot be given a null to match, so the record is in the dataset and
/// unreachable by the means the model says identifies it.
/// </remarks>
public sealed class NullPrimaryKeyFieldValidator : IValidatorListener
{
    /// <summary>How many offending records to name before summarising.</summary>
    private const int MaximumDisplayedKeys = 100;

    private readonly string _typeName;

    /// <summary>
    /// Watches the type <paramref name="dataType"/> maps to, which has to declare a primary key.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="dataType"/> carries no <see cref="HollowPrimaryKeyAttribute"/>.
    /// </exception>
    public NullPrimaryKeyFieldValidator(Type dataType)
    {
        ArgumentNullException.ThrowIfNull(dataType);

        if (Attribute.GetCustomAttribute(dataType, typeof(HollowPrimaryKeyAttribute)) is null)
        {
            throw new ArgumentException(
                $"{dataType} declares no [{nameof(HollowPrimaryKeyAttribute)}], so there are no key fields "
                + "to check for nulls",
                nameof(dataType));
        }

        _typeName = HollowObjectMapper.DefaultTypeName(dataType);
    }

    /// <summary>
    /// Watches <paramref name="typeName"/>, whose schema has to declare a primary key.
    /// </summary>
    /// <remarks>
    /// A type that declares none fails validation rather than throwing here, since whether it does is
    /// a property of the data the producer has yet to write.
    /// </remarks>
    public NullPrimaryKeyFieldValidator(string typeName)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeName);

        _typeName = typeName;
    }

    /// <inheritdoc />
    public string Name => $"{nameof(NullPrimaryKeyFieldValidator)}_{_typeName}";

    /// <inheritdoc />
    public ValidationResult OnValidate(IReadState readState)
    {
        ArgumentNullException.ThrowIfNull(readState);

        ValidationResult.Builder result = ValidationResult.From(this).Detail("Typename", _typeName);

        if (readState.StateEngine.GetTypeState(_typeName) is not HollowObjectTypeReadState typeState)
        {
            return result.Failed(
                $"{nameof(NullPrimaryKeyFieldValidator)} is defined for type {_typeName}, which is not an "
                + "object type of this dataset.");
        }

        if (((HollowObjectSchema)typeState.Schema).PrimaryKey is not { } primaryKey)
        {
            return result.Failed(
                $"{nameof(NullPrimaryKeyFieldValidator)} is defined for type {_typeName}, which declares no "
                + "primary key.");
        }

        result.Detail("FieldPaths", string.Join(", ", primaryKey.FieldPaths));

        HollowPrimaryKeyValueDeriver deriver = new(primaryKey, readState.StateEngine);

        List<string> offenders = [];
        int total = 0;

        for (int ordinal = typeState.PopulatedOrdinals.NextSetBit(0);
            ordinal != HollowConstants.OrdinalNone;
            ordinal = typeState.PopulatedOrdinals.NextSetBit(ordinal + 1))
        {
            object?[] key = deriver.GetRecordKey(ordinal);

            if (Array.TrueForAll(key, value => value is not null))
            {
                continue;
            }

            total++;

            if (offenders.Count < MaximumDisplayedKeys)
            {
                offenders.Add($"(ordinal={ordinal.Invariant()}, key=[{string.Join(", ", key)}])");
            }
        }

        if (total == 0)
        {
            return result.Passed($"{Name} found no record with a null primary key field.");
        }

        string listed = string.Join(", ", offenders);

        if (total > MaximumDisplayedKeys)
        {
            listed +=
                $" … (showing {MaximumDisplayedKeys.Invariant()} of {total.Invariant()} null keys)";
        }

        return result
            .Detail("NullKeyRecordCount", total)
            .Failed(
                $"Type {_typeName} has records with a null in its primary key "
                + $"({string.Join(", ", primaryKey.FieldPaths)}): {listed}");
    }
}

/// <summary>
/// Fails a cycle in which a record changed in a way the caller says it may not.
/// </summary>
/// <remarks>
/// <para>
/// The other validators judge a state on its own. This one judges the <em>transition</em>: it is
/// handed the old and the new version of every record that was replaced this cycle, and fails if the
/// caller's predicate rejects the pair. That catches the class of bug the counts cannot — a field
/// that was never supposed to change, changing.
/// </para>
/// <code>
/// new ObjectModificationValidator&lt;Movie&gt;(
///     "Movie",
///     (before, after) =&gt; before.ReleaseYear == after.ReleaseYear,
///     (dataAccess, ordinal) =&gt; new CatalogueApi(dataAccess).GetMovie(ordinal)!);
/// </code>
/// <para>
/// Added and removed records are not offered to the predicate; only replacements are, so the type has
/// to declare a primary key for one version to be matched with the other.
/// </para>
/// <para>
/// Java takes two functions, one building the API and one reading a record out of it, and is generic in
/// the API type as well. One function from data access and ordinal to record says the same thing, and
/// a generated API's accessor fits it directly.
/// </para>
/// </remarks>
/// <typeparam name="T">The record wrapper the predicate is given.</typeparam>
public sealed class ObjectModificationValidator<T> : IValidatorListener
    where T : IHollowRecord
{
    private readonly string _typeName;
    private readonly Func<T, T, bool> _isAcceptable;
    private readonly Func<IHollowDataAccess, int, T> _readRecord;

    /// <summary>
    /// Requires every replacement of a <paramref name="typeName"/> record to satisfy
    /// <paramref name="isAcceptable"/>.
    /// </summary>
    /// <param name="typeName">The type to watch.</param>
    /// <param name="isAcceptable">
    /// Given the record as it was and as it now is, whether the change is allowed.
    /// </param>
    /// <param name="readRecord">Reads one record of the type out of a dataset.</param>
    public ObjectModificationValidator(
        string typeName,
        Func<T, T, bool> isAcceptable,
        Func<IHollowDataAccess, int, T> readRecord)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeName);
        ArgumentNullException.ThrowIfNull(isAcceptable);
        ArgumentNullException.ThrowIfNull(readRecord);

        _typeName = typeName;
        _isAcceptable = isAcceptable;
        _readRecord = readRecord;
    }

    /// <inheritdoc />
    public string Name => $"{nameof(ObjectModificationValidator<T>)}_{_typeName}";

    /// <inheritdoc />
    public ValidationResult OnValidate(IReadState readState)
    {
        ArgumentNullException.ThrowIfNull(readState);

        ValidationResult.Builder result = ValidationResult.From(this).Detail("Typename", _typeName);

        if (readState.StateEngine.GetTypeState(_typeName) is not HollowObjectTypeReadState typeState)
        {
            return result.Failed(
                $"{nameof(ObjectModificationValidator<T>)} is defined for type {_typeName}, which is not an "
                + "object type of this dataset.");
        }

        if (((HollowObjectSchema)typeState.Schema).PrimaryKey is null)
        {
            return result.Failed(
                $"{nameof(ObjectModificationValidator<T>)} is defined for type {_typeName}, which declares no "
                + "primary key, so a replaced record cannot be matched to the one it replaced.");
        }

        RecordChangeSet changes = RecordChangeSet.Compute(readState.StateEngine, _typeName);

        if (!changes.HasPriorState)
        {
            return result.Detail("skipped", true).Passed(
                $"Type {_typeName} held no records in the previous cycle, so nothing was replaced.");
        }

        result.Detail("ReplacedRecordCount", changes.Updated.Count);

        HollowPrimaryKeyValueDeriver deriver = new(changes.PrimaryKey, readState.StateEngine);

        foreach (UpdatedRecord replacement in changes.Updated)
        {
            T before = _readRecord(readState.StateEngine, replacement.FromOrdinal);
            T after = _readRecord(readState.StateEngine, replacement.ToOrdinal);

            if (_isAcceptable(before, after))
            {
                continue;
            }

            return result
                .Detail("fromOrdinal", replacement.FromOrdinal)
                .Detail("toOrdinal", replacement.ToOrdinal)
                .Detail("key", string.Join(", ", deriver.GetRecordKey(replacement.ToOrdinal)))
                .Failed($"A {_typeName} record changed in a way this validator does not allow.");
        }

        return result.Passed(
            $"{Name} accepted all {changes.Updated.Count.Invariant()} replaced records.");
    }
}

/// <summary>
/// Fails a cycle in which too large a share of a type's records were added, removed or replaced.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RecordCountVarianceValidator"/> watches the net count, which a cycle that replaces every
/// record leaves untouched. This watches the three kinds of change separately, so a cycle that swapped
/// the whole dataset for a same-sized one is caught.
/// </para>
/// <para>
/// A threshold left unset is not checked. The type has to declare a primary key, since telling a
/// replacement from an add and a remove is what the whole validator is about.
/// </para>
/// </remarks>
public sealed class RecordCountPercentChangeValidator : IValidatorListener
{
    private readonly string _typeName;
    private readonly ChangeThreshold _threshold;

    /// <summary>
    /// Bounds how much of <paramref name="typeName"/> may change in one cycle.
    /// </summary>
    public RecordCountPercentChangeValidator(string typeName, ChangeThreshold threshold)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeName);
        ArgumentNullException.ThrowIfNull(threshold);

        _typeName = typeName;
        _threshold = threshold;
    }

    /// <inheritdoc />
    public string Name => $"{nameof(RecordCountPercentChangeValidator)}_{_typeName}";

    /// <inheritdoc />
    public ValidationResult OnValidate(IReadState readState)
    {
        ArgumentNullException.ThrowIfNull(readState);

        ValidationResult.Builder result = ValidationResult.From(this).Detail("Typename", _typeName);

        ResolvedThresholds thresholds;

        try
        {
            thresholds = _threshold.Resolve();
        }
        catch (ArgumentOutOfRangeException e)
        {
            // A threshold that is read afresh each cycle can only be checked once it has been read, and
            // a nonsensical one is a fault in the producer's configuration rather than in its data.
            return result.Error(e);
        }

        if (readState.StateEngine.GetTypeState(_typeName) is not HollowObjectTypeReadState typeState)
        {
            return result.Failed(
                $"{nameof(RecordCountPercentChangeValidator)} is defined for type {_typeName}, which is not "
                + "an object type of this dataset.");
        }

        if (((HollowObjectSchema)typeState.Schema).PrimaryKey is null)
        {
            return result.Failed(
                $"{nameof(RecordCountPercentChangeValidator)} is defined for type {_typeName}, which declares "
                + "no primary key, so a replacement cannot be told from an add and a remove.");
        }

        RecordChangeSet changes = RecordChangeSet.Compute(readState.StateEngine, _typeName);

        if (!changes.HasPriorState)
        {
            return result.Detail("skipped", true).Passed(
                $"Type {_typeName} held no records in the previous cycle, so there is nothing to compare "
                + "against.");
        }

        int previousCount = typeState.PreviousOrdinals.Cardinality();
        int added = changes.Added.Cardinality();
        int removed = changes.Removed.Cardinality();
        int updated = changes.Updated.Count;

        result
            .Detail("PreviousRecordCount", previousCount)
            .Detail("AddedRecordCount", added)
            .Detail("RemovedRecordCount", removed)
            .Detail("UpdatedRecordCount", updated);

        List<string> breaches =
        [
            .. Breach("added", added, previousCount, thresholds.Added, result),
            .. Breach("removed", removed, previousCount, thresholds.Removed, result),
            .. Breach("updated", updated, previousCount, thresholds.Updated, result),
        ];

        return breaches.Count > 0
            ? result.Failed(
                $"Type {_typeName} changed by more than its thresholds allow: {string.Join(", ", breaches)}.")
            : result.Passed($"{Name} stayed within every threshold it was given.");
    }

    private static IEnumerable<string> Breach(
        string what, int count, int previousCount, float? threshold, ValidationResult.Builder result)
    {
        if (threshold is not { } allowed)
        {
            yield break;
        }

        float actual = count * 100f / previousCount;

        result.Detail($"{what}Percent", actual).Detail($"{what}PercentThreshold", allowed * 100f);

        if (actual >= allowed * 100f)
        {
            yield return $"{what} {actual.Invariant()}% of {previousCount.Invariant()} (allowed under "
                + $"{(allowed * 100f).Invariant()}%)";
        }
    }
}

/// <summary>
/// What a <see cref="ChangeThreshold"/>'s three bounds are for one cycle, with an unset one left unset.
/// </summary>
internal readonly record struct ResolvedThresholds(float? Added, float? Removed, float? Updated);

/// <summary>
/// How much of a type may be added, removed or replaced in one cycle.
/// </summary>
/// <remarks>
/// Each is a fraction rather than a percentage — one per cent is <c>0.01f</c> — matching Java. A
/// threshold left unset is not checked at all, where Java encodes "unset" as a negative number the
/// caller could also pass by accident.
/// </remarks>
public sealed class ChangeThreshold
{
    private ChangeThreshold(Func<float>? added, Func<float>? removed, Func<float>? updated)
    {
        AddedPercent = added;
        RemovedPercent = removed;
        UpdatedPercent = updated;
    }

    private Func<float>? AddedPercent { get; }

    private Func<float>? RemovedPercent { get; }

    private Func<float>? UpdatedPercent { get; }

    /// <summary>Starts building a threshold, with nothing checked.</summary>
    public static Builder Create() => new();

    /// <summary>
    /// Reads all three thresholds for this cycle, leaving unset ones unset.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">One of them is out of range.</exception>
    internal ResolvedThresholds Resolve() =>
        new(AddedPercent?.Invoke(), RemovedPercent?.Invoke(), UpdatedPercent?.Invoke());

    /// <summary>Configures a <see cref="ChangeThreshold"/>.</summary>
    public sealed class Builder
    {
        private Func<float>? _added;
        private Func<float>? _removed;
        private Func<float>? _updated;

        /// <summary>Allows under <paramref name="fraction"/> of the previous count to be added.</summary>
        /// <remarks>Unbounded above: a cycle may add any number of records to an existing dataset.</remarks>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="fraction"/> is negative.</exception>
        public Builder WithAdded(float fraction)
        {
            NonNegative(fraction, nameof(fraction));
            _added = () => fraction;

            return this;
        }

        /// <inheritdoc cref="WithAdded(float)" />
        /// <remarks>
        /// Read afresh each cycle, so the bound can be changed without restarting the producer — and
        /// checked afresh each cycle too, since there is nothing to check at the time it is given.
        /// </remarks>
        public Builder WithAdded(Func<float> fraction)
        {
            ArgumentNullException.ThrowIfNull(fraction);

            _added = () => NonNegative(fraction(), nameof(fraction));

            return this;
        }

        /// <summary>Allows under <paramref name="fraction"/> of the previous count to be removed.</summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="fraction"/> is not between 0 and 1.
        /// </exception>
        public Builder WithRemoved(float fraction)
        {
            AtMostEverything(fraction, nameof(fraction));
            _removed = () => fraction;

            return this;
        }

        /// <inheritdoc cref="WithRemoved(float)" />
        /// <inheritdoc cref="WithAdded(Func{float})" path="/remarks" />
        public Builder WithRemoved(Func<float> fraction)
        {
            ArgumentNullException.ThrowIfNull(fraction);

            _removed = () => AtMostEverything(fraction(), nameof(fraction));

            return this;
        }

        /// <summary>Allows under <paramref name="fraction"/> of the previous count to be replaced.</summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="fraction"/> is not between 0 and 1.
        /// </exception>
        public Builder WithUpdated(float fraction)
        {
            AtMostEverything(fraction, nameof(fraction));
            _updated = () => fraction;

            return this;
        }

        /// <inheritdoc cref="WithUpdated(float)" />
        /// <inheritdoc cref="WithAdded(Func{float})" path="/remarks" />
        public Builder WithUpdated(Func<float> fraction)
        {
            ArgumentNullException.ThrowIfNull(fraction);

            _updated = () => AtMostEverything(fraction(), nameof(fraction));

            return this;
        }

        /// <summary>Builds the threshold.</summary>
        public ChangeThreshold Build() => new(_added, _removed, _updated);

        private static float NonNegative(float fraction, string parameterName) =>
            fraction >= 0
                ? fraction
                : throw new ArgumentOutOfRangeException(
                    parameterName, fraction, "a change threshold cannot be negative");

        private static float AtMostEverything(float fraction, string parameterName) =>
            fraction is >= 0 and <= 1
                ? fraction
                : throw new ArgumentOutOfRangeException(
                    parameterName,
                    fraction,
                    "a removal or replacement threshold is a fraction of what is already there, so it "
                    + "has to be between 0 and 1 — one per cent is 0.01f, not 1f");
    }
}
