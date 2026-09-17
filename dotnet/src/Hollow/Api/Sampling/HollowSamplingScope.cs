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

namespace Hollow.Api.Sampling;

/// <summary>
/// Marks a region of work as the dataset's own, so that the reads it makes are not counted as the
/// application's.
/// </summary>
/// <remarks>
/// <para>
/// Applying a transition reads records to build indexes and checksums. Counting those would report
/// fields as hot that no caller ever asked for, so the work that does it says so:
/// </para>
/// <code>
/// using (HollowSamplingScope.EnterUpdate())
/// {
///     // Every read from here is excluded, on whatever thread the work reaches.
/// }
/// </code>
/// <para>
/// <strong>Named for what it does rather than after Java.</strong> Java has
/// <c>HollowSamplingDirector.setUpdateThread(Thread)</c>, which registers a thread and compares the
/// reading thread against it. That holds only while the update stays on the thread that registered
/// itself, which on .NET it does not: an <c>await</c> resumes the continuation on another pool thread,
/// and a fan-out never ran on the registering thread at all, so those reads get counted as the
/// application's. The reverse is worse — a registered pool thread goes back to the pool and later
/// serves application work, whose reads are then silently excluded, so the sampler under-reports the
/// very fields it exists to find.
/// </para>
/// <para>
/// The scope lives in an <see cref="AsyncLocal{T}"/>, so it follows the work rather than the thread:
/// across <c>await</c>, <c>Task.Run</c>, <c>Parallel.For</c> and <c>Thread.Start</c> alike. Only an
/// explicit <see cref="ExecutionContext.SuppressFlow"/> severs it, which nothing here does.
/// </para>
/// <para>
/// Reading the flag costs a few nanoseconds more than comparing two thread references, and less than
/// that while no scope has ever been entered, because the execution context is then null. Neither
/// matters: every director tests its own state first and <c>&amp;&amp;</c> short-circuits, so a
/// time-sliced director asks this question only during the slice it is counting in.
/// </para>
/// </remarks>
public static class HollowSamplingScope
{
    private static readonly AsyncLocal<bool> Updating = new();

    /// <summary>
    /// Whether the calling work is the dataset applying a transition rather than the application
    /// reading.
    /// </summary>
    public static bool IsUpdate => Updating.Value;

    /// <summary>
    /// Marks everything the calling work reaches as the dataset's own, until the returned scope is
    /// disposed.
    /// </summary>
    /// <remarks>Scopes nest: disposing one restores whatever was in force around it.</remarks>
    public static UpdateScope EnterUpdate() => new();

    /// <summary>The region <see cref="EnterUpdate"/> opened.</summary>
    /// <remarks>
    /// A struct, so entering a scope allocates nothing. Disposing it twice is harmless: the second
    /// restores the same value the first did.
    /// </remarks>
    public readonly struct UpdateScope : IDisposable
    {
        private readonly bool _previous;

        /// <summary>Enters the scope, remembering what was in force around it.</summary>
        public UpdateScope()
        {
            _previous = Updating.Value;
            Updating.Value = true;
        }

        /// <summary>Restores whatever was in force around the scope.</summary>
        public void Dispose() => Updating.Value = _previous;
    }
}
