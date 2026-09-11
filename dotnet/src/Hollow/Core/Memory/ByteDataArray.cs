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

using Hollow.Core.Memory.Pool;

namespace Hollow.Core.Memory;

/// <summary>
/// Writes data to a <see cref="SegmentedByteArray"/>, tracking the index it writes to.
/// </summary>
public sealed class ByteDataArray
{
    private readonly SegmentedByteArray _buffer;

    /// <summary>
    /// Initialises an array backed by the default (non-pooling) recycler.
    /// </summary>
    public ByteDataArray()
        : this(WastefulRecycler.DefaultInstance)
    {
    }

    /// <summary>
    /// Initialises an array backed by segments from <paramref name="memoryRecycler"/>.
    /// </summary>
    public ByteDataArray(IArraySegmentRecycler memoryRecycler)
    {
        _buffer = new SegmentedByteArray(memoryRecycler);
    }

    /// <summary>
    /// The write position, which is also the number of bytes written since the last reset.
    /// </summary>
    /// <remarks>
    /// Java exposes <c>length()</c> and <c>setPosition(long)</c> over the same field; a settable
    /// property covers both.
    /// </remarks>
    public long Length { get; set; }

    /// <summary>The array this buffer writes into.</summary>
    public SegmentedByteArray UnderlyingArray => _buffer;

    /// <summary>Appends a single byte.</summary>
    public void Write(byte value) => _buffer.Set(Length++, value);

    /// <summary>Rewinds the write position to the start.</summary>
    public void Reset() => Length = 0;

    /// <summary>Gets the byte at <paramref name="index"/>.</summary>
    public byte Get(long index) => _buffer.Get(index);

    /// <summary>
    /// Appends everything written to this array to <paramref name="other"/>.
    /// </summary>
    public void CopyTo(ByteDataArray other)
    {
        ArgumentNullException.ThrowIfNull(other);

        other._buffer.Copy(_buffer, 0, other.Length, Length);
        other.Length += Length;
    }

    /// <summary>
    /// Appends <paramref name="length"/> bytes of <paramref name="data"/> starting at
    /// <paramref name="startPosition"/>.
    /// </summary>
    public void CopyFrom(IByteData data, long startPosition, int length)
    {
        ArgumentNullException.ThrowIfNull(data);

        _buffer.Copy(data, startPosition, Length, length);
        Length += length;
    }
}
