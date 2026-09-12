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
using Hollow.Explorer.Models;
using Microsoft.AspNetCore.Http;

namespace Hollow.Explorer;

/// <summary>
/// What one reader is in the middle of: the search they are building and the schema branches they
/// have opened.
/// </summary>
/// <remarks>
/// Neither is data — both are a place in the data that took several requests to get to, and that a URL
/// is the wrong size to carry.
/// </remarks>
public sealed class ExplorerSession
{
    private readonly ConcurrentDictionary<string, SchemaDisplay> _schemaDisplays = new(StringComparer.Ordinal);

    /// <summary>The search being built, or <see langword="null"/> when there is none.</summary>
    public QueryResult? QueryResult { get; set; }

    /// <summary>Forgets the search, so the next page shows everything again.</summary>
    public void ClearQueryResult() => QueryResult = null;

    /// <summary>Which branches of <paramref name="type"/>'s schema this reader has opened.</summary>
    public SchemaDisplay? GetSchemaDisplay(string type) => _schemaDisplays.GetValueOrDefault(type);

    /// <summary>Remembers which branches of <paramref name="type"/>'s schema are open.</summary>
    public void SetSchemaDisplay(string type, SchemaDisplay display) => _schemaDisplays[type] = display;

    internal DateTimeOffset LastAccessed { get; set; }
}

/// <summary>
/// The sessions currently in flight, found from the cookie naming one.
/// </summary>
/// <remarks>
/// <para>
/// ASP.NET Core's own session state stores bytes, so using it would mean serialising a search result
/// and a schema tree on every request and deserialising them on the next. Java keeps the objects
/// themselves in the servlet session, and so does this — behind a cookie of its own so that a host
/// embedding the explorer does not have to wire up session middleware to get a working page.
/// </para>
/// <para>
/// This keeps a reader's state in the process serving them, which is what the explorer is for: one
/// person looking at one dataset. Behind a load balancer without sticky sessions they would lose their
/// place on whichever request landed elsewhere.
/// </para>
/// </remarks>
public sealed class ExplorerSessionStore
{
    private const string CookieName = "hollow-explorer-session";

    /// <summary>How long a session survives without a request before it is dropped.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, ExplorerSession> _sessions = new(StringComparer.Ordinal);

    private DateTimeOffset _lastSweep;

    /// <summary>
    /// The session <paramref name="context"/> belongs to, starting one if it does not have it yet.
    /// </summary>
    public ExplorerSession Get(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Sweep(now);

        if (context.Request.Cookies.TryGetValue(CookieName, out string? id)
            && id is not null
            && _sessions.TryGetValue(id, out ExplorerSession? existing))
        {
            existing.LastAccessed = now;

            return existing;
        }

        // A session id only has to be unguessable by whoever shares the browser's origin, which is what
        // makes it worth generating the same way a token would be.
        string newId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        ExplorerSession session = new() { LastAccessed = now };

        _sessions[newId] = session;

        context.Response.Cookies.Append(
            CookieName,
            newId,
            new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, IsEssential = true });

        return session;
    }

    private void Sweep(DateTimeOffset now)
    {
        // Sweeping on the way past costs nothing when there is nothing to drop, and saves the explorer
        // from needing a timer of its own.
        if (now - _lastSweep < IdleTimeout)
        {
            return;
        }

        _lastSweep = now;

        foreach ((string id, ExplorerSession session) in _sessions)
        {
            if (now - session.LastAccessed > IdleTimeout)
            {
                _sessions.TryRemove(id, out _);
            }
        }
    }
}
