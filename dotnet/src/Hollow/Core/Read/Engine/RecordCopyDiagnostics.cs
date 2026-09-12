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
/// Which way records were moved between shards, for a test to look at.
/// </summary>
/// <remarks>
/// <para>
/// Applying a delta and resharding both have two paths — records whose bits are unchanged are copied
/// wholesale when the layout allows it, and anything else is re-encoded — and the two produce identical
/// output by design. That is what makes the choice invisible from outside, and why a test that compares
/// states cannot tell whether the bulk path ran at all. This is how it tells.
/// </para>
/// <para>
/// Per thread, because the work happens on one thread, and a count shared across threads would only be
/// readable by a test that ran alone.
/// </para>
/// </remarks>
internal static class RecordCopyDiagnostics
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

    /// <summary>Records a reshard moved without re-encoding them.</summary>
    [ThreadStatic]
    internal static long BulkResharded;

    /// <summary>Records a reshard had to re-encode.</summary>
    [ThreadStatic]
    internal static long ReencodedByReshard;

    /// <summary>Zeroes every count, so a test can measure one stretch of work.</summary>
    internal static void Reset() =>
        BulkCopiedObjects = BulkCopiedLists = BulkCopiedSets = BulkCopiedMaps =
            BulkResharded = ReencodedByReshard = 0;
}
