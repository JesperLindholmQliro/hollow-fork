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

namespace Hollow.Core.Memory;

/// <summary>
/// The memory mode in which a read state engine holds its data.
/// </summary>
public enum MemoryMode
{
    /// <summary>Record data is held in pooled managed arrays.</summary>
    OnHeap,

    /// <summary>
    /// Record data is lazily paged in from a memory-mapped blob file.
    /// </summary>
    /// <remarks>Not yet implemented by the .NET port; see <c>PORTING.md</c>.</remarks>
    SharedMemoryLazy,
}

/// <summary>
/// Extension methods for <see cref="MemoryMode"/>.
/// </summary>
/// <remarks>
/// Java models <c>MemoryMode</c> as an enum with instance methods. C# enums cannot carry behaviour,
/// so the Java instance method <c>supportsFiltering()</c> becomes an extension method here.
/// </remarks>
public static class MemoryModeExtensions
{
    /// <summary>
    /// Returns whether a memory mode supports type filtering.
    /// </summary>
    public static bool SupportsFiltering(this MemoryMode mode) => mode == MemoryMode.OnHeap;
}
