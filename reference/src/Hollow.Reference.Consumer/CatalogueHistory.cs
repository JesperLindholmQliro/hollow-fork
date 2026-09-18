/*
 *  Copyright 2016 Netflix, Inc.
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

using Hollow.Api.Consumer;
using Hollow.Core.Read.Engine;
using Hollow.Core.Tools.History;
using Microsoft.Extensions.Logging;

namespace Hollow.Reference.Consumer;

/// <summary>
/// Keeps a <see cref="HollowHistory"/> in step with the consumer it was built from, and says out loud
/// what it saw.
/// </summary>
/// <remarks>
/// <para>
/// The Java reference implementation hands its consumer straight to
/// <c>HollowHistoryUIServer(consumer, port)</c> and lets Jetty do this wiring out of sight. The port
/// has no such constructor, so it is here in the open: a history is built on a read state as that state
/// moves, and something has to tell it each time the state moved and where to.
/// </para>
/// <para>
/// A history remembers what each transition changed, which means holding on to records the new version
/// no longer has. That memory is the reason <c>MaxHistoricalStates</c> exists.
/// </para>
/// <para>
/// It also logs a line per version picked up. That is not something a real service would want — it
/// would count the transition and move on — but a reference implementation whose console goes quiet
/// after start-up reads like one that has stopped watching, and it is the thing worth watching happen.
/// </para>
/// </remarks>
internal sealed class CatalogueHistory : HollowRefreshListener
{
    private readonly HollowHistory _history;
    private readonly ILogger _logger;
    private readonly string _historyBasePath;

    internal CatalogueHistory(HollowHistory history, ILogger logger, string historyBasePath)
    {
        _history = history;
        _logger = logger;
        _historyBasePath = historyBasePath;
    }

    public override void DeltaUpdateOccurred(HollowReadStateEngine stateEngine, long version)
    {
        // A delta is applied to the state engine the history already holds, so it only needs telling
        // which version that state is now of.
        if (version > _history.LatestVersion)
        {
            _history.DeltaOccurred(version);
            return;
        }

        // Backwards, which happens when an operator pins consumers to an earlier version. Recording it
        // needs a second state engine walking the chain the other way — see
        // HollowHistory.InitializeReverseStateEngine — which this reference implementation does not set
        // up. The consumer is on the right version either way; only the history stops short.
        _logger.LogInformation(
            "Moved back to version {Version}; the history stays at {LatestVersion}.",
            version,
            _history.LatestVersion);
    }

    public override void SnapshotUpdateOccurred(HollowReadStateEngine stateEngine, long version)
    {
        // A snapshot replaces the state engine rather than updating it, so the history has to be handed
        // the new one. It cannot say what changed across that gap — there was no delta to read it from
        // — so it starts counting again from here.
        _history.DoubleSnapshotOccurred(stateEngine, version);

        _logger.LogInformation(
            "Loaded a whole snapshot of version {Version}; the history restarts from it.", version);
    }

    public override void RefreshSuccessful(long beforeVersion, long afterVersion, long requestedVersion)
    {
        if (beforeVersion == afterVersion)
        {
            // A refresh that found nothing new. The watcher fires on any change to the folder it is
            // watching, so this is the common case and not worth a line.
            return;
        }

        int states = _history.NumberOfHistoricalStates;

        _logger.LogInformation(
            "Picked up version {Version} ({Records}). The history now holds {States} — see {HistoryPath}.",
            afterVersion,
            RecordCounts(),
            states == 1 ? "1 past version" : $"{states} past versions",
            _historyBasePath);
    }

    private string RecordCounts() =>
        string.Join(
            ", ",
            _history.LatestState.TypeStates.Values
                .OrderBy(type => type.Schema.Name, StringComparer.Ordinal)
                .Select(type => $"{type.Schema.Name} {type.PopulatedOrdinals.Cardinality()}"));
}
