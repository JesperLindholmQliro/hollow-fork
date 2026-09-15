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
using Hollow.Core.Write;

namespace Hollow.Benchmarks;

/// <summary>
/// Moves a dataset from a write state engine into a read state engine, the way a producer and a
/// consumer would.
/// </summary>
/// <remarks>
/// Java ships this as <c>core.util.StateEngineRoundTripper</c> in the main artifact, which is how its
/// benchmarks reach it. This port keeps it out of the library — it is only ever used by tests and by
/// these benchmarks — so both have their own copy of the same ten lines.
/// </remarks>
internal static class RoundTripper
{
    internal static HollowReadStateEngine RoundTripSnapshot(HollowWriteStateEngine writeEngine)
    {
        HollowReadStateEngine readEngine = new();
        RoundTripSnapshot(writeEngine, readEngine);

        return readEngine;
    }

    internal static void RoundTripSnapshot(
        HollowWriteStateEngine writeEngine, HollowReadStateEngine readEngine)
    {
        using MemoryStream blob = new();
        new HollowBlobWriter(writeEngine).WriteSnapshot(blob);
        writeEngine.PrepareForNextCycle();

        blob.Position = 0;
        new HollowBlobReader(readEngine).ReadSnapshot(blob);
    }

    internal static void RoundTripDelta(
        HollowWriteStateEngine writeEngine, HollowReadStateEngine readEngine)
    {
        using MemoryStream blob = new();
        new HollowBlobWriter(writeEngine).WriteDelta(blob);

        blob.Position = 0;
        new HollowBlobReader(readEngine).ApplyDelta(blob);

        writeEngine.PrepareForNextCycle();
    }
}
