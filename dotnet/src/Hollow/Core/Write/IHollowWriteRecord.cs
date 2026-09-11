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

using Hollow.Core.Memory;

namespace Hollow.Core.Write;

/// <summary>
/// A single record staged for addition to a <see cref="HollowWriteStateEngine"/>.
/// </summary>
/// <remarks>
/// Named <c>HollowWriteRecord</c> in Java; the <c>I</c> prefix follows the .NET interface naming
/// convention.
/// </remarks>
public interface IHollowWriteRecord
{
    /// <summary>
    /// Writes this record's fields to <paramref name="buffer"/> in the intermediate form the write
    /// state engine deduplicates on.
    /// </summary>
    void WriteDataTo(ByteDataArray buffer);

    /// <summary>
    /// Clears every field, so the record can be reused for the next addition.
    /// </summary>
    void Reset();
}
