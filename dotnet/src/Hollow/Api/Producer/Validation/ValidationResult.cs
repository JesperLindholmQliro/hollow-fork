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

using Hollow.Api.Producer.Listener;
using Hollow.Core.Util;

namespace Hollow.Api.Producer.Validation;

/// <summary>
/// How a validation turned out.
/// </summary>
public enum ValidationResultType
{
    /// <summary>The data is acceptable.</summary>
    Passed,

    /// <summary>The data is not acceptable, and the validator says why.</summary>
    Failed,

    /// <summary>The validator itself broke, so it cannot say whether the data is acceptable.</summary>
    Error,
}

/// <summary>
/// What one validator concluded about a state.
/// </summary>
/// <remarks>
/// Build one with <see cref="From(IValidatorListener)"/>. A validator that throws is turned into an
/// <see cref="ValidationResultType.Error"/> result rather than being allowed to abort the stage, so
/// that every validator's verdict is reported even when one of them is broken.
/// </remarks>
public sealed class ValidationResult
{
    private ValidationResult(
        ValidationResultType resultType,
        string name,
        Exception? exception,
        string? message,
        IReadOnlyDictionary<string, string> details)
    {
        ResultType = resultType;
        Name = name;
        Exception = exception;
        Message = message;
        Details = details;
    }

    /// <summary>How the validation turned out.</summary>
    public ValidationResultType ResultType { get; }

    /// <summary>The validator that produced this.</summary>
    public string Name { get; }

    /// <summary>
    /// What the validator threw, for an <see cref="ValidationResultType.Error"/> result.
    /// </summary>
    public Exception? Exception { get; }

    /// <summary>What the validator has to say, if anything.</summary>
    public string? Message { get; }

    /// <summary>
    /// Whatever the validator measured, for a caller reporting on the cycle.
    /// </summary>
    public IReadOnlyDictionary<string, string> Details { get; }

    /// <summary>Whether this result allows the cycle to continue.</summary>
    public bool IsPassed => ResultType == ValidationResultType.Passed;

    /// <summary>Starts building a result for <paramref name="validator"/>.</summary>
    public static Builder From(IValidatorListener validator)
    {
        ArgumentNullException.ThrowIfNull(validator);

        return new Builder(validator.Name);
    }

    /// <summary>Starts building a result attributed to <paramref name="name"/>.</summary>
    public static Builder From(string name) => new(name);

    /// <inheritdoc />
    public override string ToString() =>
        $"{Name}: {ResultType}{(Message is null ? string.Empty : $" — {Message}")}";

    /// <summary>
    /// Collects a validator's findings, then closes them into a <see cref="ValidationResult"/>.
    /// </summary>
    public sealed class Builder
    {
        private readonly Dictionary<string, string> _details = new(StringComparer.Ordinal);
        private readonly string _name;

        internal Builder(string name)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);

            _name = name;
        }

        /// <summary>Records something the validator measured.</summary>
        public Builder Detail(string name, object? value)
        {
            ArgumentNullException.ThrowIfNull(name);

            _details[name] = value.Invariant();

            return this;
        }

        /// <summary>Concludes that the data is acceptable.</summary>
        public ValidationResult Passed(string? message = null) =>
            new(ValidationResultType.Passed, _name, exception: null, message, Snapshot());

        /// <summary>Concludes that the data is not acceptable.</summary>
        public ValidationResult Failed(string message)
        {
            ArgumentNullException.ThrowIfNull(message);

            return new ValidationResult(ValidationResultType.Failed, _name, exception: null, message, Snapshot());
        }

        /// <summary>Concludes that the validator itself broke.</summary>
        public ValidationResult Error(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);

            return new ValidationResult(
                ValidationResultType.Error, _name, exception, exception.Message, Snapshot());
        }

        private IReadOnlyDictionary<string, string> Snapshot() =>
            new Dictionary<string, string>(_details, StringComparer.Ordinal);
    }
}

/// <summary>
/// What every validator concluded about one state.
/// </summary>
public sealed class ValidationStatus
{
    /// <summary>
    /// Collects <paramref name="results"/>.
    /// </summary>
    public ValidationStatus(IEnumerable<ValidationResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        Results = [.. results];
    }

    /// <summary>Every validator's verdict, in the order they ran.</summary>
    public IReadOnlyList<ValidationResult> Results { get; }

    /// <summary>Whether every validator passed, which is what lets the cycle continue.</summary>
    public bool Passed => Results.All(result => result.IsPassed);

    /// <summary>Whether any validator did not pass.</summary>
    public bool Failed => !Passed;

    /// <inheritdoc />
    public override string ToString() => string.Join("; ", Results);
}

/// <summary>
/// Thrown when a cycle's data is rejected by a validator, which stops it being announced.
/// </summary>
public sealed class ValidationStatusException : Exception
{
    /// <summary>
    /// Initialises an exception carrying <paramref name="status"/>.
    /// </summary>
    public ValidationStatusException(ValidationStatus status, string message)
        : base(message)
    {
        ArgumentNullException.ThrowIfNull(status);

        ValidationStatus = status;
    }

    /// <summary>
    /// Initialises an exception with no validation status, for serialisation compatibility.
    /// </summary>
    public ValidationStatusException()
        : this(new ValidationStatus([]), "Validation failed.")
    {
    }

    /// <summary>
    /// Initialises an exception with no validation status.
    /// </summary>
    public ValidationStatusException(string message)
        : this(new ValidationStatus([]), message)
    {
    }

    /// <summary>
    /// Initialises an exception with no validation status.
    /// </summary>
    public ValidationStatusException(string message, Exception innerException)
        : base(message, innerException) => ValidationStatus = new ValidationStatus([]);

    /// <summary>What every validator concluded.</summary>
    public ValidationStatus ValidationStatus { get; }
}

/// <summary>
/// Inspects the state a cycle produced and decides whether it may be announced.
/// </summary>
/// <remarks>
/// A validator runs after the blobs have been published but before consumers are told about them, so
/// rejecting a state costs some storage but no consumer ever sees it.
/// </remarks>
public interface IValidatorListener : IHollowProducerEventListener
{
    /// <summary>What to attribute this validator's results to.</summary>
    string Name { get; }

    /// <summary>
    /// Decides whether <paramref name="readState"/> is acceptable.
    /// </summary>
    /// <remarks>
    /// Throwing is equivalent to returning <see cref="ValidationResult.Builder.Error"/>, which fails
    /// the stage — but returning a failed result says more about why.
    /// </remarks>
    ValidationResult OnValidate(IReadState readState);
}
