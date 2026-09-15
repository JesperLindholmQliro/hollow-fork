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

namespace Hollow.Core.Memory;

/// <summary>
/// Makes somewhere to put variable-length record data, according to the memory mode.
/// </summary>
/// <remarks>
/// On-heap that is pooled segments the bytes are copied into; in shared memory it is a window onto the
/// mapped file, which copies nothing.
/// </remarks>
public static class VariableLengthDataFactory
{
    /// <summary>
    /// Returns an empty range of byte data, ready to be loaded from a blob.
    /// </summary>
    public static IVariableLengthData Get(MemoryMode memoryMode, IArraySegmentRecycler memoryRecycler) =>
        memoryMode == MemoryMode.OnHeap
            ? new SegmentedByteArray(memoryRecycler)
            : new EncodedByteBuffer();
}
