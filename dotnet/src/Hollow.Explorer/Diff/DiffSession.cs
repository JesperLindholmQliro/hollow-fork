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
using System.Globalization;

namespace Hollow.Explorer.Diff;

/// <summary>
/// What one reader is in the middle of: where they are in each list, and the record pair they have
/// open.
/// </summary>
/// <remarks>
/// The open pair has to be kept because opening a row is a request of its own. The row tree is built
/// lazily and its visibility is changed in place, so the request that asks for a row's children has to
/// reach the same tree the page was drawn from.
/// </remarks>
public sealed class DiffSession
{
    private readonly ConcurrentDictionary<string, string> _parameters = new(StringComparer.Ordinal);

    /// <summary>The record pair this reader has open, if any.</summary>
    public HollowDiffView? DiffView { get; private set; }

    /// <summary>
    /// The view over <paramref name="type"/>'s records at the two ordinals, building one if this
    /// reader does not already have it open.
    /// </summary>
    public HollowDiffView GetOrCreateDiffView(
        string type, int fromOrdinal, int toOrdinal, Func<HollowDiffView> create)
    {
        ArgumentNullException.ThrowIfNull(create);

        if (DiffView is { } existing
            && string.Equals(existing.Type, type, StringComparison.Ordinal)
            && existing.FromOrdinal == fromOrdinal
            && existing.ToOrdinal == toOrdinal)
        {
            return existing;
        }

        HollowDiffView view = create();
        view.ResetView();
        DiffView = view;

        return view;
    }

    /// <summary>
    /// A page parameter, which the request may supply and the session otherwise remembers.
    /// </summary>
    /// <remarks>
    /// This is what makes the paging links on the type page work: a link that moves one list forward
    /// says nothing about the other two, and the reader expects those to stay where they were.
    /// </remarks>
    /// <param name="context">What the parameter is remembered per — usually the type being shown.</param>
    /// <param name="name">The parameter's name.</param>
    /// <param name="requestValue">What this request said, if it said anything.</param>
    /// <param name="defaultValue">What to use when neither the request nor the session has it.</param>
    public string Parameter(string context, string name, string? requestValue, string defaultValue)
    {
        string key = context + "_" + name;

        if (requestValue is not null)
        {
            _parameters[key] = requestValue;

            return requestValue;
        }

        return _parameters.GetValueOrDefault(key, defaultValue);
    }

    /// <summary>A page parameter that counts, defaulting where it is absent or not a number.</summary>
    public int IntParameter(string context, string name, string? requestValue, int defaultValue) =>
        int.TryParse(
            Parameter(context, name, requestValue, defaultValue.ToString(CultureInfo.InvariantCulture)),
            CultureInfo.InvariantCulture,
            out int value)
            ? value
            : defaultValue;

    /// <summary>A page parameter that switches something on or off.</summary>
    public bool BoolParameter(string context, string name, string? requestValue, bool defaultValue) =>
        bool.TryParse(
            Parameter(context, name, requestValue, defaultValue ? "true" : "false"),
            out bool value)
            ? value
            : defaultValue;
}

/// <summary>
/// The diff UI's sessions, found from the cookie naming one.
/// </summary>
public sealed class DiffSessionStore : UISessionStore<DiffSession>
{
    /// <inheritdoc />
    protected override string CookieName => "hollow-diff-session";
}
