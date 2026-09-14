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

using Hollow.Core.Util;

namespace Hollow.Core.Read.Engine;

/// <summary>
/// Lifts the records a delta removed out of a type's current storage and into storage of their own.
/// </summary>
/// <remarks>
/// <para>
/// A delta leaves the removed records where they were — the ordinals simply stop being populated, and
/// the space is reclaimed the next time the type is resharded. A history has to keep those records, so
/// before they go it copies each of them into a compact block of its own and records where each one
/// went. That block becomes the historical state's storage, and the mapping is how a caller holding an
/// old ordinal finds the copy.
/// </para>
/// <para>
/// Java has no class here: each record kind carries its own standalone
/// <c>Hollow&lt;Kind&gt;DeltaHistoricalStateCreator</c> with the same four members copied out by hand,
/// and <c>HollowHistoricalStateCreator</c> picks between them with a chain of <c>instanceof</c> tests.
/// Naming the shared shape lets the caller dispatch on the type instead.
/// </para>
/// </remarks>
public abstract class HollowDeltaHistoricalStateCreator
{
    /// <summary>
    /// Prepares to lift what the last transition removed from <paramref name="typeState"/>.
    /// </summary>
    /// <param name="typeState">The type to take the removed records from.</param>
    /// <param name="reverse">
    /// Takes the records the transition <em>added</em> instead, which is what a history walking
    /// backwards through a reverse delta needs.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The type is not tracking which of its ordinals are populated, so what it removed cannot be
    /// worked out.
    /// </exception>
    protected HollowDeltaHistoricalStateCreator(HollowTypeReadState typeState, bool reverse)
    {
        ArgumentNullException.ThrowIfNull(typeState);

        PopulatedOrdinalListener listener = typeState.GetListener<PopulatedOrdinalListener>()
            ?? throw new ArgumentException(
                $"{typeState.TypeName} has no {nameof(PopulatedOrdinalListener)}, so the records it "
                + "removed cannot be recovered.",
                nameof(typeState));

        Iterator = new RemovedOrdinalIterator(listener, reverse);
    }

    /// <summary>
    /// Where each removed record was copied to: old ordinal to ordinal in the historical storage.
    /// </summary>
    /// <remarks>Empty until <see cref="PopulateHistory"/> has run.</remarks>
    public IntMap OrdinalMapping { get; protected set; } = new(0);

    /// <summary>The removed ordinals, in the order they are copied.</summary>
    /// <remarks>Null once <see cref="DereferenceTypeState"/> has run.</remarks>
    protected RemovedOrdinalIterator? Iterator { get; private set; }

    /// <summary>The ordinal the next copied record will be given.</summary>
    protected int NextOrdinal { get; set; }

    /// <summary>
    /// The iterator, or a failure saying that the type state has already been let go.
    /// </summary>
    protected RemovedOrdinalIterator RemovedOrdinals =>
        Iterator ?? throw new InvalidOperationException(
            $"{nameof(DereferenceTypeState)} has already run, so there is nothing left to read.");

    /// <summary>
    /// Copies every removed record into storage of its own, filling <see cref="OrdinalMapping"/>.
    /// </summary>
    public abstract void PopulateHistory();

    /// <summary>
    /// Drops the references into the live type state, so that it can be collected once every
    /// historical state has been built.
    /// </summary>
    public virtual void DereferenceTypeState() => Iterator = null;

    /// <summary>
    /// Wraps the copied records in a read state of their own, belonging to <paramref name="into"/>.
    /// </summary>
    /// <param name="into">
    /// The engine the historical state belongs to. Java passes no engine at all here and leaves the
    /// field null; a state that knows which engine it is part of is cheaper than one that has to
    /// tolerate not having one.
    /// </param>
    public abstract HollowTypeReadState CreateHistoricalTypeReadState(HollowReadStateEngine into);
}
