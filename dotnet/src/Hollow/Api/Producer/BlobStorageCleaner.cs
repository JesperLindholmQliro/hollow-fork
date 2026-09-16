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

using BlobType = Hollow.Api.Consumer.BlobType;

namespace Hollow.Api.Producer;

/// <summary>
/// A chance to remove old blobs from the store, taken after each one a producer publishes.
/// </summary>
/// <remarks>
/// <para>
/// A blob store grows without bound otherwise: every cycle adds a snapshot, a delta and a reverse
/// delta, and nothing ever removes them. What to keep is a deployment's decision — how far back a
/// consumer may be, how much storage costs — so this is where it is made.
/// </para>
/// <para>
/// Called after the publish rather than before, and in a <c>finally</c>, so a failed publish still
/// gets the chance to tidy up after itself.
/// </para>
/// <para>
/// Java makes the three methods abstract and ships a <c>DummyBlobStorageCleaner</c> that implements
/// them as no-ops. They are virtual no-ops here, so a cleaner that only prunes snapshots overrides
/// one method and there is no second type.
/// </para>
/// </remarks>
public class BlobStorageCleaner
{
    /// <summary>A cleaner that removes nothing, which is what a producer uses unless told otherwise.</summary>
    public static BlobStorageCleaner None { get; } = new();

    /// <summary>Takes the chance to remove old blobs of the kind just published.</summary>
    public void Clean(BlobType blobType)
    {
        switch (blobType)
        {
            case BlobType.Snapshot:
                CleanSnapshots();
                break;

            case BlobType.Delta:
                CleanDeltas();
                break;

            case BlobType.ReverseDelta:
                CleanReverseDeltas();
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(blobType), blobType, "unknown blob type");
        }
    }

    /// <summary>Removes old snapshots.</summary>
    protected virtual void CleanSnapshots()
    {
    }

    /// <summary>Removes old deltas.</summary>
    protected virtual void CleanDeltas()
    {
    }

    /// <summary>Removes old reverse deltas.</summary>
    protected virtual void CleanReverseDeltas()
    {
    }
}
