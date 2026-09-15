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
using Hollow.Core.Read;

namespace Hollow.Core.Memory;

/// <summary>
/// Reads a bit string in whichever way the memory mode calls for.
/// </summary>
/// <remarks>
/// On-heap the words are copied into pooled arrays; in shared memory they are left in the file and
/// read through the mapping. The two answers implement the same <see cref="IFixedLengthData"/>, so
/// nothing downstream of here knows which it got.
/// </remarks>
public static class FixedLengthDataFactory
{
    /// <summary>
    /// Reads a bit string whose serialised form begins with its length in 64-bit words.
    /// </summary>
    public static IFixedLengthData Get(
        HollowBlobInput input, MemoryMode memoryMode, IArraySegmentRecycler memoryRecycler)
    {
        ArgumentNullException.ThrowIfNull(input);

        return memoryMode == MemoryMode.OnHeap
            ? FixedLengthElementArray.NewFrom(input, memoryRecycler)
            : EncodedLongBuffer.NewFrom(input);
    }

    /// <summary>
    /// Reads a bit string of <paramref name="numLongs"/> 64-bit words.
    /// </summary>
    public static IFixedLengthData Get(
        HollowBlobInput input, MemoryMode memoryMode, IArraySegmentRecycler memoryRecycler, long numLongs)
    {
        ArgumentNullException.ThrowIfNull(input);

        return memoryMode == MemoryMode.OnHeap
            ? FixedLengthElementArray.NewFrom(input, memoryRecycler, numLongs)
            : EncodedLongBuffer.NewFrom(input, numLongs);
    }
}
