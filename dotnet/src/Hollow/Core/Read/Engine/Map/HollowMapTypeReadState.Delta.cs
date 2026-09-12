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

using Hollow.Core.Memory.Encoding;
using Hollow.Core.Memory.Pool;

namespace Hollow.Core.Read.Engine.Map;

/// <summary>
/// Delta support for a map type's read state.
/// </summary>
public sealed partial class HollowMapTypeReadState
{
    /// <summary>
    /// Applies one delta transition to this type, replacing its records with the successor state's.
    /// </summary>
    /// <param name="input">The delta blob, positioned at this type's records.</param>
    /// <param name="memoryRecycler">The pool to draw the new records' storage from.</param>
    public void ApplyDelta(HollowBlobInput input, IArraySegmentRecycler memoryRecycler)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(memoryRecycler);

        Shard[] shards = [.. _shardsVolatile.TypedShards];

        if (shards.Length > 1)
        {
            _maxOrdinal = VarInt.ReadVInt(input);
        }

        for (int i = 0; i < shards.Length; i++)
        {
            HollowMapTypeDataElements deltaData = new(memoryRecycler);
            deltaData.ReadDelta(input);

            HollowMapTypeDataElements fromData = shards[i].DataElements;
            HollowMapTypeDataElements nextData = HollowMapTypeDataElements.ApplyDelta(fromData, deltaData);

            shards[i] = new Shard(nextData, shards[i].ShardOrdinalShift);

            if (shards.Length == 1)
            {
                _maxOrdinal = nextData.MaxOrdinal;
            }

            // Published before the listeners are told and before the storage it replaced is released:
            // a listener may read the records it is being told about, and a concurrent reader must not
            // be left pointing at storage that has gone back to the recycler.
            _shardsVolatile = new ShardsHolder<Shard>([.. shards]);

            NotifyListenersAboutDeltaChanges(
                deltaData.EncodedRemovals, deltaData.EncodedAdditions, i, shards.Length);

            fromData.Destroy();
            deltaData.Destroy();
        }
    }

    /// <summary>
    /// Replays a shard's delta as add and remove callbacks, translating each shard-local ordinal back
    /// into the global ordinal the listener expects.
    /// </summary>
    private void NotifyListenersAboutDeltaChanges(
        GapEncodedVariableLengthIntegerReader? removals,
        GapEncodedVariableLengthIntegerReader? additions,
        int shardNumber,
        int numShards)
    {
        if (Listeners.Count == 0)
        {
            return;
        }

        if (removals is not null)
        {
            foreach (int ordinal in removals.EnumerateOrdinals())
            {
                NotifyRemovedOrdinal((ordinal * numShards) + shardNumber);
            }
        }

        if (additions is not null)
        {
            foreach (int ordinal in additions.EnumerateOrdinals())
            {
                NotifyAddedOrdinal((ordinal * numShards) + shardNumber);
            }
        }
    }
}
