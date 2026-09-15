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

namespace Hollow.Core.Memory.Encoding;

/// <summary>
/// Variable-length record data read straight out of a memory-mapped blob.
/// </summary>
/// <remarks>
/// <para>
/// The shared-memory counterpart of <see cref="SegmentedByteArray"/>. Where that one copies the blob's
/// bytes into pooled segments, this one remembers where they are and reads them in place.
/// </para>
/// <para>
/// Unlike the fixed-length data, nothing is byte-swapped here. A bit string is written as a run of
/// 64-bit words whose byte order has to be undone; variable-length data is a plain byte stream, and
/// byte <c>n</c> of the stream is byte <c>n</c> of the file.
/// </para>
/// <para>
/// Named <c>EncodedByteBuffer</c> in Java.
/// </para>
/// </remarks>
public sealed class EncodedByteBuffer : IVariableLengthData
{
    private MemoryMappedBlob? _blob;
    private long _byteOffset;

    /// <inheritdoc />
    public long Size { get; private set; }

    /// <inheritdoc />
    public long Length => Size;

    /// <inheritdoc />
    public byte Get(long index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Size);

        if (_blob is null)
        {
            throw new InvalidOperationException("no data has been loaded into this buffer");
        }

        return _blob.GetByte(_byteOffset + index);
    }

    /// <summary>
    /// Takes the next <paramref name="length"/> bytes of <paramref name="input"/> without reading them.
    /// </summary>
    /// <remarks>
    /// Only where they start is recorded; the operating system pages them in if and when a record that
    /// needs them is read.
    /// </remarks>
    public void LoadFrom(HollowBlobInput input, long length)
    {
        ArgumentNullException.ThrowIfNull(input);

        _blob = input.RequireMappedBlob();
        _byteOffset = input.Position;
        Size = length;

        input.Skip(length);
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always; a mapped blob is read-only.</exception>
    public void Copy(IByteData source, long sourcePosition, long destinationPosition, long length) =>
        throw ReadOnly();

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always; a mapped blob is read-only.</exception>
    public void OrderedCopy(
        IVariableLengthData source, long sourcePosition, long destinationPosition, long length) =>
        throw ReadOnly();

    private static NotSupportedException ReadOnly() =>
        new("memory-mapped record data is read-only; shared-memory mode cannot write or apply deltas");
}
