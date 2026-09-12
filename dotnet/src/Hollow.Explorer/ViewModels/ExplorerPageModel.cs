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

namespace Hollow.Explorer.ViewModels;

/// <summary>
/// What every page of the explorer needs, which is what the navigation bar across the top is built
/// from.
/// </summary>
/// <remarks>
/// Java puts these into the Velocity context in a base page class. A base model says the same thing
/// while letting the layout name what it reads.
/// </remarks>
public abstract class ExplorerPageModel
{
    /// <summary>Where the explorer is mounted, which every link on the page is relative to.</summary>
    public required string BasePath { get; init; }

    /// <summary>The version being shown, where the explorer is following a consumer.</summary>
    public long? StateVersion { get; init; }

    /// <summary>A line the embedder wanted shown, if any.</summary>
    public string? HeaderDisplayString { get; init; }

    /// <summary>Where <see cref="HeaderDisplayString"/> links to.</summary>
    public string? HeaderStringUrl { get; init; }

    /// <summary>Extra navigation cells the embedder supplied, as HTML they wrote.</summary>
    public IReadOnlyList<string> CommonHeaderEntries { get; init; } = [];

    /// <summary>
    /// The one type this page is about, which gets it links to its data and its schema.
    /// </summary>
    public string? TypeName { get; init; }

    /// <summary>
    /// The types nothing else references, offered as the place to start reading a schema.
    /// </summary>
    /// <remarks>Only the home page has these; elsewhere the bar has somewhere more specific to go.</remarks>
    public IReadOnlyList<string> TopLevelTypes { get; init; } = [];
}
