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

using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;

namespace Hollow.Explorer;

/// <summary>
/// The sessions currently in flight, found from the cookie naming one.
/// </summary>
/// <remarks>
/// <para>
/// ASP.NET Core's own session state stores bytes, so using it would mean serialising whatever a reader
/// is in the middle of on every request and deserialising it on the next. Java keeps the objects
/// themselves in the servlet session, and so does this — behind a cookie of its own so that a host
/// embedding one of these UIs does not have to wire up session middleware to get a working page.
/// </para>
/// <para>
/// This keeps a reader's state in the process serving them, which is what these UIs are for: one
/// person looking at one dataset. Behind a load balancer without sticky sessions they would lose their
/// place on whichever request landed elsewhere.
/// </para>
/// <para>
/// Java has one <c>HollowUISession</c> for every UI, keyed by attribute name. Two stores with separate
/// cookies say the same thing without the explorer and the diff having to agree on names — and mean
/// that mounting one of them does not hand the other a session it never asked for.
/// </para>
/// </remarks>
/// <typeparam name="TSession">What one reader's place in the UI is held in.</typeparam>
public abstract class UISessionStore<TSession>
    where TSession : class, new()
{
    /// <summary>How long a session survives without a request before it is dropped.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, Entry> _sessions = new(StringComparer.Ordinal);

    private DateTimeOffset _lastSweep;

    /// <summary>The name of the cookie this store's sessions are found by.</summary>
    protected abstract string CookieName { get; }

    /// <summary>
    /// The session <paramref name="context"/> belongs to, starting one if it does not have it yet.
    /// </summary>
    public TSession Get(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Sweep(now);

        if (context.Request.Cookies.TryGetValue(CookieName, out string? id)
            && id is not null
            && _sessions.TryGetValue(id, out Entry? existing))
        {
            existing.LastAccessed = now;

            return existing.Session;
        }

        // A session id only has to be unguessable by whoever shares the browser's origin, which is what
        // makes it worth generating the same way a token would be.
        string newId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        Entry entry = new(new TSession()) { LastAccessed = now };

        _sessions[newId] = entry;

        context.Response.Cookies.Append(
            CookieName,
            newId,
            new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, IsEssential = true });

        return entry.Session;
    }

    private void Sweep(DateTimeOffset now)
    {
        // Sweeping on the way past costs nothing when there is nothing to drop, and saves the store
        // from needing a timer of its own.
        if (now - _lastSweep < IdleTimeout)
        {
            return;
        }

        _lastSweep = now;

        foreach ((string id, Entry entry) in _sessions)
        {
            if (now - entry.LastAccessed > IdleTimeout)
            {
                _sessions.TryRemove(id, out _);
            }
        }
    }

    /// <summary>
    /// A session and when it was last asked for, which is what the sweep goes by.
    /// </summary>
    /// <remarks>
    /// Kept beside the session rather than on it, so that what a UI puts in a session is only what the
    /// UI is about.
    /// </remarks>
    private sealed class Entry(TSession session)
    {
        public TSession Session { get; } = session;

        public DateTimeOffset LastAccessed { get; set; }
    }
}
