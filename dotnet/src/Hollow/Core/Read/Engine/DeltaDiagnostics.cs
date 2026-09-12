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

namespace Hollow.Core.Read.Engine;

/// <summary>
/// What the delta applicators did, for a test to look at.
/// </summary>
/// <remarks>
/// <para>
/// Every applicator has two paths — a run of records the delta leaves alone is copied wholesale when
/// the layout allows it, and anything else is re-encoded record by record — and the two produce
/// identical output by design. That is what makes the choice invisible from outside, and why a test
/// that compares states cannot tell whether the bulk path ran at all. This is how it tells.
/// </para>
/// <para>
/// Per thread, because a delta is applied on one thread, and a count shared across threads would only
/// be readable by a test that ran alone.
/// </para>
/// </remarks>
internal static class DeltaDiagnostics
{
    /// <summary>Object records this thread has carried across in bulk rather than re-encoding.</summary>
    [ThreadStatic]
    internal static long BulkCopiedObjects;

    /// <summary>List records this thread has carried across in bulk.</summary>
    [ThreadStatic]
    internal static long BulkCopiedLists;

    /// <summary>Set records this thread has carried across in bulk.</summary>
    [ThreadStatic]
    internal static long BulkCopiedSets;

    /// <summary>Map records this thread has carried across in bulk.</summary>
    [ThreadStatic]
    internal static long BulkCopiedMaps;

    /// <summary>Zeroes all four, so a test can measure one stretch of work.</summary>
    internal static void Reset() =>
        BulkCopiedObjects = BulkCopiedLists = BulkCopiedSets = BulkCopiedMaps = 0;
}
