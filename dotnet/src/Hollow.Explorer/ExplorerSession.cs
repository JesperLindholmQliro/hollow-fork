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
using Hollow.Explorer.Models;

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
}

/// <summary>
/// The explorer's sessions, found from the cookie naming one.
/// </summary>
public sealed class ExplorerSessionStore : UISessionStore<ExplorerSession>
{
    /// <inheritdoc />
    protected override string CookieName => "hollow-explorer-session";
}
