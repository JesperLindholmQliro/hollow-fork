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

using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Filter;
using Hollow.Core.Write;

namespace Hollow.Tests;

/// <summary>
/// Moves a dataset from a write state engine into a read state engine the way a producer and consumer
/// would, so that a test can assert against what a consumer actually sees.
/// </summary>
/// <remarks>
/// Java ships this as <c>core.util.StateEngineRoundTripper</c> in the main artifact. It is only ever
/// used by tests, so this port keeps it in the test project.
/// </remarks>
internal static class StateEngineRoundTripper
{
    /// <summary>
    /// Writes a snapshot of <paramref name="writeEngine"/> and reads it into a fresh read state.
    /// </summary>
    internal static HollowReadStateEngine RoundTripSnapshot(
        HollowWriteStateEngine writeEngine, ITypeFilter? filter = null)
    {
        HollowReadStateEngine readEngine = new();
        RoundTripSnapshot(writeEngine, readEngine, filter);

        return readEngine;
    }

    /// <summary>
    /// Writes a snapshot of <paramref name="writeEngine"/> and reads it into
    /// <paramref name="readEngine"/>, rolling the write engine on to the next cycle.
    /// </summary>
    internal static void RoundTripSnapshot(
        HollowWriteStateEngine writeEngine, HollowReadStateEngine readEngine, ITypeFilter? filter = null)
    {
        using MemoryStream stream = new();
        new HollowBlobWriter(writeEngine).WriteSnapshot(stream);
        writeEngine.PrepareForNextCycle();

        stream.Position = 0;
        new HollowBlobReader(readEngine).ReadSnapshot(stream, filter);
    }

    /// <summary>
    /// Writes a delta from <paramref name="writeEngine"/>'s previous cycle to its current one and
    /// applies it to <paramref name="readEngine"/>, rolling the write engine on to the next cycle.
    /// </summary>
    internal static void RoundTripDelta(HollowWriteStateEngine writeEngine, HollowReadStateEngine readEngine)
    {
        using MemoryStream stream = new();
        new HollowBlobWriter(writeEngine).WriteDelta(stream);

        stream.Position = 0;
        new HollowBlobReader(readEngine).ApplyDelta(stream);

        writeEngine.PrepareForNextCycle();
    }
}
