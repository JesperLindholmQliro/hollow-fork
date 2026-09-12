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

using Hollow.Explorer.Models;

namespace Hollow.Explorer.ViewModels;

/// <summary>
/// One node of a schema tree, together with what its links have to carry.
/// </summary>
/// <remarks>
/// Java writes the tree with a recursive Velocity macro reading the page's context for the rest. A
/// partial view recursing on itself is the same shape, and a partial has no context to read — hence
/// carrying the base path and the page's types down with the node.
/// </remarks>
/// <param name="Display">The node.</param>
/// <param name="BasePath">Where the explorer is mounted.</param>
/// <param name="Types">
/// Every type on the page, which an expand or collapse link has to repeat so that following it does
/// not drop the other schemas.
/// </param>
public sealed record SchemaDisplayModel(
    SchemaDisplay Display, string BasePath, IReadOnlyList<string> Types)
{
    /// <summary>The same page, showing <paramref name="child"/> instead.</summary>
    public SchemaDisplayModel For(SchemaDisplay child) => this with { Display = child };

    /// <summary>The types of this page, as the query string every link on it starts with.</summary>
    public string TypesQuery =>
        string.Join("&amp;", Types.Select(type => "type=" + Uri.EscapeDataString(type)));
}
