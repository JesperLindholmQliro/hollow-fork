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

using System.Buffers;

namespace Hollow.Core.Memory;

/// <summary>
/// An <see cref="IByteData"/> backed by a simple array of bytes.
/// </summary>
public sealed class ArrayByteData : IByteData
{
    private readonly byte[] _data;

    /// <summary>
    /// Wraps <paramref name="data"/>. The array is not copied.
    /// </summary>
    public ArrayByteData(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;
    }

    /// <inheritdoc />
    public byte Get(long position) => _data[(int)position];

    /// <inheritdoc />
    public long Length => _data.Length;

    /// <inheritdoc />
    /// <remarks>One flat array, so every range within it has a contiguous view.</remarks>
    public bool TryGetSpan(long position, int length, out ReadOnlySpan<byte> span)
    {
        if (position < 0 || length < 0 || position + length > _data.Length)
        {
            span = default;

            return false;
        }

        span = _data.AsSpan((int)position, length);

        return true;
    }

    /// <inheritdoc />
    public void CopyTo(long position, Span<byte> destination) =>
        _data.AsSpan((int)position, destination.Length).CopyTo(destination);

    /// <inheritdoc />
    /// <remarks>Always one segment, over the backing array itself.</remarks>
    public ReadOnlySequence<byte> GetSequence(long position, int length) =>
        new(_data.AsMemory((int)position, length));
}
