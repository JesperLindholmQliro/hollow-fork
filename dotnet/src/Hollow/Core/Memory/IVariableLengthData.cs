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

using Hollow.Core.Read;

namespace Hollow.Core.Memory;

/// <summary>
/// A growable range of bytes holding the variable-length portion of records.
/// </summary>
/// <remarks>
/// Named <c>VariableLengthData</c> in Java; the <c>I</c> prefix follows the .NET interface naming
/// convention.
/// </remarks>
public interface IVariableLengthData : IByteData
{
    /// <summary>
    /// Loads <paramref name="length"/> bytes from <paramref name="input"/> into this data.
    /// </summary>
    void LoadFrom(HollowBlobInput input, long length);

    /// <summary>
    /// Copies bytes from another range of byte data.
    /// </summary>
    /// <param name="source">The source data.</param>
    /// <param name="sourcePosition">Position in the source data to begin copying from.</param>
    /// <param name="destinationPosition">Position in this data to begin copying to.</param>
    /// <param name="length">Length of data to copy, in bytes.</param>
    void Copy(IByteData source, long sourcePosition, long destinationPosition, long length);

    /// <summary>
    /// Copies data from <paramref name="source"/>, guaranteeing that if the update is seen by another
    /// thread then all writes prior to this call are also visible to that thread.
    /// </summary>
    /// <param name="source">The source data.</param>
    /// <param name="sourcePosition">Position in the source data to begin copying from.</param>
    /// <param name="destinationPosition">Position in this data to begin copying to.</param>
    /// <param name="length">Length of data to copy, in bytes.</param>
    void OrderedCopy(IVariableLengthData source, long sourcePosition, long destinationPosition, long length);

    /// <summary>The size of this data, in bytes.</summary>
    long Size { get; }
}
