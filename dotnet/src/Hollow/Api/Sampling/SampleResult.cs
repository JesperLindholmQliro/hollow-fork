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

using System.Globalization;
using Hollow.Core.Read.Filter;

namespace Hollow.Api.Sampling;

/// <summary>
/// How many times one thing was read — a field, or an operation on a collection type.
/// </summary>
/// <remarks>
/// Ordered by count descending and then by identifier, so that sorting a set of results puts the hot
/// ones first and keeps the cold ones in a stable order rather than an arbitrary one.
/// </remarks>
/// <param name="Identifier">What was read, as <c>Type.field</c> or <c>Type.operation()</c>.</param>
/// <param name="SampleCount">How many reads were counted.</param>
public readonly record struct SampleResult(string Identifier, long SampleCount)
    : IComparable<SampleResult>
{
    /// <summary>Orders by count descending, then by identifier.</summary>
    public static bool operator <(SampleResult left, SampleResult right) => left.CompareTo(right) < 0;

    /// <summary>Orders by count descending, then by identifier.</summary>
    public static bool operator >(SampleResult left, SampleResult right) => left.CompareTo(right) > 0;

    /// <summary>Orders by count descending, then by identifier.</summary>
    public static bool operator <=(SampleResult left, SampleResult right) => left.CompareTo(right) <= 0;

    /// <summary>Orders by count descending, then by identifier.</summary>
    public static bool operator >=(SampleResult left, SampleResult right) => left.CompareTo(right) >= 0;

    /// <inheritdoc />
    public int CompareTo(SampleResult other) =>
        SampleCount == other.SampleCount
            ? string.CompareOrdinal(Identifier, other.Identifier)
            : other.SampleCount.CompareTo(SampleCount);

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Identifier}: {SampleCount}");
}

/// <summary>
/// Counts the reads of one type, under a director that says which reads to count.
/// </summary>
/// <remarks>
/// Named <c>HollowSampler</c> in Java; the <c>I</c> marks it as the interface it has always been.
/// </remarks>
public interface IHollowSampler
{
    /// <summary>Whether anything has been counted since the last <see cref="Reset"/>.</summary>
    bool HasSampleResults { get; }

    /// <summary>Puts every one of this type's counters under <paramref name="director"/>.</summary>
    void SetSamplingDirector(HollowSamplingDirector director);

    /// <summary>
    /// Puts only the counters <paramref name="fieldSpec"/> names under <paramref name="director"/>.
    /// </summary>
    /// <remarks>
    /// Sampling a few suspect fields at a high rate costs far less than sampling everything at one, so
    /// the director is settable per field rather than only per type. Java passes a
    /// <c>HollowFilterConfig</c> here, which is this port's <see cref="ITypeFilter"/>.
    /// </remarks>
    void SetFieldSpecificSamplingDirector(ITypeFilter fieldSpec, HollowSamplingDirector director);

    /// <summary>Tells every director which thread applies transitions.</summary>
    void SetUpdateThread(Thread? thread);

    /// <summary>What has been counted, one result per field or operation.</summary>
    IReadOnlyList<SampleResult> GetSampleResults();

    /// <summary>Sets every counter back to zero.</summary>
    void Reset();
}
