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

namespace Hollow.Core.Read.Engine;

/// <summary>
/// Reads the trailing bit set of a type's snapshot, which records the ordinals the producer populated,
/// and replays it to the type's listeners as a sequence of additions.
/// </summary>
public static class SnapshotPopulatedOrdinalsReader
{
    /// <summary>
    /// Reads the populated ordinals and notifies <paramref name="listeners"/> of each.
    /// </summary>
    public static void ReadOrdinals(HollowBlobInput input, IReadOnlyList<IHollowTypeStateListener> listeners)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(listeners);

        int numLongs = input.ReadInt32();
        int currentOrdinal = 0;

        for (int i = 0; i < numLongs; i++)
        {
            NotifyPopulatedOrdinals(input.ReadInt64(), currentOrdinal, listeners);
            currentOrdinal += 64;
        }
    }

    /// <summary>
    /// Skips the populated ordinals without materialising them.
    /// </summary>
    public static void DiscardOrdinals(HollowBlobInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        long bytesToSkip = (long)input.ReadInt32() * 8;
        while (bytesToSkip > 0)
        {
            long skipped = input.SkipBytes(bytesToSkip);
            if (skipped <= 0)
            {
                throw new EndOfStreamException("unexpected end of populated ordinals");
            }

            bytesToSkip -= skipped;
        }
    }

    private static void NotifyPopulatedOrdinals(
        long word, int ordinal, IReadOnlyList<IHollowTypeStateListener> listeners)
    {
        if (word == 0)
        {
            return;
        }

        int stopOrdinal = ordinal + 64;

        while (ordinal < stopOrdinal)
        {
            if ((word & (1L << ordinal)) != 0)
            {
                foreach (IHollowTypeStateListener listener in listeners)
                {
                    listener.AddedOrdinal(ordinal);
                }
            }

            ordinal++;
        }
    }
}
