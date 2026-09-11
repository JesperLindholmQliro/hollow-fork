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

namespace Hollow.Api.Producer;

/// <summary>
/// Thrown by a listener that means to stop the cycle.
/// </summary>
/// <remarks>
/// A listener's exception is normally swallowed — a producer's job is to publish data, not to run
/// other people's code — so a listener that genuinely needs to stop the cycle either throws this or
/// implements <see cref="IVetoableListener"/>.
/// </remarks>
public sealed class ListenerVetoException : Exception
{
    /// <summary>Initialises a veto with no message.</summary>
    public ListenerVetoException()
    {
    }

    /// <summary>Initialises a veto.</summary>
    public ListenerVetoException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises a veto.</summary>
    public ListenerVetoException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The listeners registered on a producer, and the dispatch to them.
/// </summary>
/// <remarks>
/// <para>
/// A cycle takes a snapshot of the listener list once at the start, so that a registration made during
/// a cycle takes effect on the next one rather than part way through this one.
/// </para>
/// <para>
/// An exception from a listener is reported through <see cref="ListenerFailed"/> and otherwise ignored,
/// unless the listener implements <see cref="IVetoableListener"/> or threw a
/// <see cref="ListenerVetoException"/>, in which case it propagates and fails the cycle.
/// </para>
/// </remarks>
internal sealed class ProducerListenerSupport
{
    private readonly Lock _lock = new();

    private volatile IHollowProducerEventListener[] _listeners = [];

    /// <summary>
    /// Raised when a listener throws without meaning to veto the cycle.
    /// </summary>
    /// <remarks>
    /// The port takes no logging dependency; Java logs a warning here. See <c>PORTING.md</c>.
    /// </remarks>
    internal event EventHandler<Exception>? ListenerFailed;

    internal void AddListener(IHollowProducerEventListener listener)
    {
        lock (_lock)
        {
            if (!_listeners.Contains(listener))
            {
                _listeners = [.. _listeners, listener];
            }
        }
    }

    internal void RemoveListener(IHollowProducerEventListener listener)
    {
        lock (_lock)
        {
            _listeners = [.. _listeners.Where(existing => !Equals(existing, listener))];
        }
    }

    /// <summary>
    /// Takes the listener list for one cycle or restore.
    /// </summary>
    internal Snapshot Listeners() => new(this, _listeners);

    /// <summary>
    /// The listeners as they stood when a cycle started, and the dispatch to them.
    /// </summary>
    internal sealed class Snapshot(ProducerListenerSupport support, IHollowProducerEventListener[] listeners)
    {
        /// <summary>The registered listeners of a given kind.</summary>
        internal IEnumerable<T> OfType<T>()
            where T : class => listeners.OfType<T>();

        /// <summary>
        /// Calls <paramref name="notify"/> on every listener of type <typeparamref name="T"/>.
        /// </summary>
        internal void Fire<T>(Action<T> notify)
            where T : class
        {
            foreach (T listener in listeners.OfType<T>())
            {
                try
                {
                    notify(listener);
                }
                catch (Exception e) when (listener is not IVetoableListener && e is not ListenerVetoException)
                {
                    support.ListenerFailed?.Invoke(support, e);
                }
            }
        }
    }
}
