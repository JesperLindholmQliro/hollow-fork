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

using Hollow.Api.Producer.Validation;

namespace Hollow.Api.Producer.Listener;

/// <summary>
/// The base every producer listener implements, so that one object can register for several kinds of
/// event and a producer can hold them all in one list.
/// </summary>
public interface IHollowProducerEventListener
{
}

/// <summary>
/// A listener whose failure should abort the cycle rather than merely be reported.
/// </summary>
/// <remarks>
/// By default an exception from a listener is swallowed: a producer's job is to publish data, not to
/// run other people's code, and a broken metrics listener should not stop a cycle. A listener that
/// implements this says its failure genuinely means the cycle must not continue.
/// </remarks>
public interface IVetoableListener
{
}

/// <summary>Notified when the producer's data model is initialised.</summary>
public interface IDataModelInitializationListener : IHollowProducerEventListener
{
    /// <summary>Called after the data model has been registered with the write state.</summary>
    void OnProducerInit(TimeSpan elapsed);
}

/// <summary>Notified when the producer restores from a published state.</summary>
public interface IRestoreListener : IHollowProducerEventListener
{
    /// <summary>Called before the restore begins.</summary>
    void OnProducerRestoreStart(long restoreVersion);

    /// <summary>
    /// Called when the restore finishes.
    /// </summary>
    /// <param name="status">Whether it worked.</param>
    /// <param name="versionDesired">The version asked for.</param>
    /// <param name="versionReached">The version actually restored, which may be an earlier one.</param>
    /// <param name="elapsed">How long it took.</param>
    void OnProducerRestoreComplete(Status status, long versionDesired, long versionReached, TimeSpan elapsed);
}

/// <summary>Why a cycle did not run.</summary>
public enum CycleSkipReason
{
    /// <summary>This producer is not the primary, so another one is producing.</summary>
    NotPrimaryProducer,
}

/// <summary>Notified about the cycle as a whole.</summary>
public interface ICycleListener : IHollowProducerEventListener
{
    /// <summary>Called instead of <see cref="OnCycleStart"/> when a cycle is skipped.</summary>
    void OnCycleSkip(CycleSkipReason reason);

    /// <summary>
    /// Called when a cycle starts a new delta chain rather than continuing one — which is the first
    /// cycle after a producer starts without restoring.
    /// </summary>
    void OnNewDeltaChain(long version);

    /// <summary>Called when a cycle begins.</summary>
    void OnCycleStart(long version);

    /// <summary>
    /// Called when a cycle finishes, whether or not it published anything.
    /// </summary>
    /// <param name="status">Whether it worked.</param>
    /// <param name="readState">
    /// The state the cycle produced, or <see langword="null"/> when it produced none.
    /// </param>
    /// <param name="version">The version the cycle was assigned.</param>
    /// <param name="elapsed">How long it took.</param>
    void OnCycleComplete(Status status, IReadState? readState, long version, TimeSpan elapsed);
}

/// <summary>Notified about the populate stage.</summary>
public interface IPopulateListener : IHollowProducerEventListener
{
    /// <summary>Called before the populator runs.</summary>
    void OnPopulateStart(long version);

    /// <summary>Called after the populator runs.</summary>
    void OnPopulateComplete(Status status, long version, TimeSpan elapsed);
}

/// <summary>Notified about staging and publishing blobs.</summary>
public interface IPublishListener : IHollowProducerEventListener
{
    /// <summary>
    /// Called when a cycle produced no delta because nothing changed, in which case nothing is
    /// published at all.
    /// </summary>
    void OnNoDeltaAvailable(long version);

    /// <summary>Called before any blob is staged.</summary>
    void OnPublishStart(long version);

    /// <summary>Called once per blob, after it has been written to staging.</summary>
    void OnBlobStage(Status status, Blob blob, TimeSpan elapsed);

    /// <summary>Called once per blob, after the publisher has taken it.</summary>
    void OnBlobPublish(Status status, Blob blob, TimeSpan elapsed);

    /// <summary>Called when the publish stage finishes.</summary>
    void OnPublishComplete(Status status, long version, TimeSpan elapsed);
}

/// <summary>Notified about the integrity check.</summary>
public interface IIntegrityCheckListener : IHollowProducerEventListener
{
    /// <summary>Called before the check.</summary>
    void OnIntegrityCheckStart(long version);

    /// <summary>Called after the check.</summary>
    void OnIntegrityCheckComplete(Status status, IReadState? readState, long version, TimeSpan elapsed);
}

/// <summary>Notified about the validation stage as a whole.</summary>
/// <remarks>
/// A validator itself implements <see cref="IValidatorListener"/>; this reports the combined outcome.
/// </remarks>
public interface IValidationStatusListener : IHollowProducerEventListener
{
    /// <summary>Called before any validator runs.</summary>
    void OnValidationStatusStart(long version);

    /// <summary>Called once every validator has run.</summary>
    void OnValidationStatusComplete(ValidationStatus status, long version, TimeSpan elapsed);
}

/// <summary>Notified about the announcement.</summary>
public interface IAnnouncementListener : IHollowProducerEventListener
{
    /// <summary>Called before the announcement.</summary>
    void OnAnnouncementStart(long version);

    /// <summary>Called after the announcement.</summary>
    void OnAnnouncementComplete(Status status, IReadState? readState, long version, TimeSpan elapsed);
}

/// <summary>
/// A producer listener that does nothing, to be derived from and overridden selectively.
/// </summary>
/// <remarks>
/// Java has one interface per stage and expects a listener to implement the ones it cares about. That
/// still works here — the interfaces above are separate for exactly that reason — but a base class
/// covering all of them is usually less code.
/// </remarks>
public abstract class HollowProducerListener
    : IDataModelInitializationListener,
        IRestoreListener,
        ICycleListener,
        IPopulateListener,
        IPublishListener,
        IIntegrityCheckListener,
        IValidationStatusListener,
        IAnnouncementListener
{
    /// <inheritdoc />
    public virtual void OnProducerInit(TimeSpan elapsed)
    {
    }

    /// <inheritdoc />
    public virtual void OnProducerRestoreStart(long restoreVersion)
    {
    }

    /// <inheritdoc />
    public virtual void OnProducerRestoreComplete(
        Status status, long versionDesired, long versionReached, TimeSpan elapsed)
    {
    }

    /// <inheritdoc />
    public virtual void OnCycleSkip(CycleSkipReason reason)
    {
    }

    /// <inheritdoc />
    public virtual void OnNewDeltaChain(long version)
    {
    }

    /// <inheritdoc />
    public virtual void OnCycleStart(long version)
    {
    }

    /// <inheritdoc />
    public virtual void OnCycleComplete(Status status, IReadState? readState, long version, TimeSpan elapsed)
    {
    }

    /// <inheritdoc />
    public virtual void OnPopulateStart(long version)
    {
    }

    /// <inheritdoc />
    public virtual void OnPopulateComplete(Status status, long version, TimeSpan elapsed)
    {
    }

    /// <inheritdoc />
    public virtual void OnNoDeltaAvailable(long version)
    {
    }

    /// <inheritdoc />
    public virtual void OnPublishStart(long version)
    {
    }

    /// <inheritdoc />
    public virtual void OnBlobStage(Status status, Blob blob, TimeSpan elapsed)
    {
    }

    /// <inheritdoc />
    public virtual void OnBlobPublish(Status status, Blob blob, TimeSpan elapsed)
    {
    }

    /// <inheritdoc />
    public virtual void OnPublishComplete(Status status, long version, TimeSpan elapsed)
    {
    }

    /// <inheritdoc />
    public virtual void OnIntegrityCheckStart(long version)
    {
    }

    /// <inheritdoc />
    public virtual void OnIntegrityCheckComplete(
        Status status, IReadState? readState, long version, TimeSpan elapsed)
    {
    }

    /// <inheritdoc />
    public virtual void OnValidationStatusStart(long version)
    {
    }

    /// <inheritdoc />
    public virtual void OnValidationStatusComplete(ValidationStatus status, long version, TimeSpan elapsed)
    {
    }

    /// <inheritdoc />
    public virtual void OnAnnouncementStart(long version)
    {
    }

    /// <inheritdoc />
    public virtual void OnAnnouncementComplete(Status status, IReadState? readState, long version, TimeSpan elapsed)
    {
    }
}
