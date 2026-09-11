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

namespace Hollow.Api.Producer.Enforcer;

/// <summary>
/// Decides whether this process is the one allowed to produce.
/// </summary>
/// <remarks>
/// A delta chain has exactly one writer. Two producers publishing to the same blob store would fork
/// the chain, and every consumer following it would have to take a snapshot to recover. In a
/// distributed deployment an implementation of this backs onto whatever the deployment uses for
/// leader election; a single-process deployment can use <see cref="BasicSingleProducerEnforcer"/>.
/// </remarks>
public interface ISingleProducerEnforcer
{
    /// <summary>Whether this producer may run a cycle.</summary>
    bool IsPrimary { get; }

    /// <summary>Allows this producer to run cycles.</summary>
    void Enable();

    /// <summary>
    /// Gives up primary status, so that another producer may take it.
    /// </summary>
    /// <remarks>
    /// A cycle already in flight carries on and fails at the announcement, which is the last point at
    /// which giving up is still safe: the blobs are published but nothing has been told to read them.
    /// </remarks>
    void Disable();

    /// <summary>
    /// Holds this producer's primary status until <see cref="Unlock"/>, so that it cannot be revoked
    /// during the announcement.
    /// </summary>
    void Lock()
    {
    }

    /// <summary>Releases the hold taken by <see cref="Lock"/>.</summary>
    void Unlock()
    {
    }
}

/// <summary>
/// The enforcer a producer uses when nothing else decides: primary unless told otherwise.
/// </summary>
/// <remarks>
/// Suitable when exactly one producer process exists by construction. It enforces nothing across
/// processes — it only lets a caller pause and resume this one.
/// </remarks>
public sealed class BasicSingleProducerEnforcer : ISingleProducerEnforcer
{
    private readonly Lock _lock = new();

    private volatile bool _isPrimary = true;
    private bool _isLocked;

    /// <inheritdoc />
    public bool IsPrimary => _isPrimary;

    /// <inheritdoc />
    public void Enable()
    {
        lock (_lock)
        {
            if (!_isLocked)
            {
                _isPrimary = true;
            }
        }
    }

    /// <inheritdoc />
    public void Disable()
    {
        lock (_lock)
        {
            if (!_isLocked)
            {
                _isPrimary = false;
            }
        }
    }

    /// <summary>Takes primary status regardless of any hold.</summary>
    public void Force() => _isPrimary = true;

    /// <inheritdoc />
    public void Lock()
    {
        lock (_lock)
        {
            _isLocked = true;
        }
    }

    /// <inheritdoc />
    public void Unlock()
    {
        lock (_lock)
        {
            _isLocked = false;
        }
    }
}
