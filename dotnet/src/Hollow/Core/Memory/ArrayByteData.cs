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
}
