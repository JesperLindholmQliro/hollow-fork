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

using Hollow.Core.Schema;

namespace Hollow.Core.Memory.Encoding;

/// <summary>
/// Zig-zag encoding, used for <see cref="FieldType.Int"/> and <see cref="FieldType.Long"/> so that
/// smaller absolute values can be encoded using fewer bits.
/// </summary>
public static class ZigZag
{
    /// <summary>Zig-zag encodes a 64-bit value.</summary>
    public static long EncodeLong(long value) => (value << 1) ^ (value >> 63);

    /// <summary>Decodes a zig-zag encoded 64-bit value.</summary>
    public static long DecodeLong(long value) => (long)((ulong)value >> 1) ^ ((value << 63) >> 63);

    /// <summary>Zig-zag encodes a 32-bit value.</summary>
    public static int EncodeInt(int value) => (value << 1) ^ (value >> 31);

    /// <summary>Decodes a zig-zag encoded 32-bit value.</summary>
    public static int DecodeInt(int value) => (int)((uint)value >> 1) ^ ((value << 31) >> 31);
}
