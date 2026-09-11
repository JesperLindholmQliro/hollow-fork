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
using Hollow.Core.Write;

namespace Hollow.Core.Memory.Encoding;

/// <summary>
/// Variable-byte integer encoding and decoding.
/// </summary>
/// <remarks>
/// Values are written seven bits at a time, most significant group first, with the high bit of every
/// byte but the last set. A lone <c>0x80</c> byte encodes null. Negative values are always written in
/// their full width.
/// </remarks>
public static class VarInt
{
    /// <summary>The single byte that encodes a null value.</summary>
    private const byte NullMarker = 0x80;

    /// <summary>
    /// Writes a null variable-length integer into <paramref name="buffer"/>.
    /// </summary>
    public static void WriteVNull(ByteDataArray buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        buffer.Write(NullMarker);
    }

    /// <summary>
    /// Writes a null variable-length integer into <paramref name="data"/> at <paramref name="position"/>,
    /// returning the next position.
    /// </summary>
    public static int WriteVNull(byte[] data, int position)
    {
        ArgumentNullException.ThrowIfNull(data);
        data[position++] = NullMarker;
        return position;
    }

    /// <summary>
    /// Encodes <paramref name="value"/> as a variable-length integer into <paramref name="buffer"/>.
    /// </summary>
    public static void WriteVLong(ByteDataArray buffer, long value)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        Span<byte> encoded = stackalloc byte[MaxVLongSize];
        int length = EncodeVLong(encoded, value);
        for (int i = 0; i < length; i++)
        {
            buffer.Write(encoded[i]);
        }
    }

    /// <summary>
    /// Encodes <paramref name="value"/> as a variable-length integer into <paramref name="output"/>.
    /// </summary>
    public static void WriteVLong(HollowBlobOutput output, long value)
    {
        ArgumentNullException.ThrowIfNull(output);

        Span<byte> encoded = stackalloc byte[MaxVLongSize];
        int length = EncodeVLong(encoded, value);
        output.Write(encoded[..length]);
    }

    /// <summary>
    /// Encodes <paramref name="value"/> as a variable-length integer into <paramref name="data"/> at
    /// <paramref name="position"/>, returning the next position.
    /// </summary>
    public static int WriteVLong(byte[] data, int position, long value)
    {
        ArgumentNullException.ThrowIfNull(data);
        return position + EncodeVLong(data.AsSpan(position), value);
    }

    /// <summary>
    /// Encodes <paramref name="value"/> as a variable-length integer into <paramref name="buffer"/>.
    /// </summary>
    public static void WriteVInt(ByteDataArray buffer, int value)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        Span<byte> encoded = stackalloc byte[MaxVIntSize];
        int length = EncodeVInt(encoded, value);
        for (int i = 0; i < length; i++)
        {
            buffer.Write(encoded[i]);
        }
    }

    /// <summary>
    /// Encodes <paramref name="value"/> as a variable-length integer into <paramref name="output"/>.
    /// </summary>
    public static void WriteVInt(HollowBlobOutput output, int value)
    {
        ArgumentNullException.ThrowIfNull(output);

        Span<byte> encoded = stackalloc byte[MaxVIntSize];
        int length = EncodeVInt(encoded, value);
        output.Write(encoded[..length]);
    }

    /// <summary>
    /// Encodes <paramref name="value"/> as a variable-length integer into <paramref name="data"/> at
    /// <paramref name="position"/>, returning the next position.
    /// </summary>
    public static int WriteVInt(byte[] data, int position, int value)
    {
        ArgumentNullException.ThrowIfNull(data);
        return position + EncodeVInt(data.AsSpan(position), value);
    }

    /// <summary>
    /// Determines whether the value at <paramref name="position"/> is a null variable-length integer.
    /// </summary>
    public static bool ReadVNull(IByteData data, long position)
    {
        ArgumentNullException.ThrowIfNull(data);
        return data.Get(position) == NullMarker;
    }

    /// <summary>
    /// Determines whether the value at <paramref name="position"/> is a null variable-length integer.
    /// </summary>
    public static bool ReadVNull(byte[] data, int position)
    {
        ArgumentNullException.ThrowIfNull(data);
        return data[position] == NullMarker;
    }

    /// <summary>
    /// Reads a variable-length integer starting at <paramref name="position"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The encoded value is null.</exception>
    public static int ReadVInt(IByteData data, long position)
    {
        ArgumentNullException.ThrowIfNull(data);

        byte b = data.Get(position++);
        ThrowIfNull(b, "int");

        int value = b & 0x7F;
        while ((b & 0x80) != 0)
        {
            b = data.Get(position++);
            value <<= 7;
            value |= b & 0x7F;
        }

        return value;
    }

    /// <summary>
    /// Reads a variable-length integer starting at <paramref name="position"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The encoded value is null.</exception>
    public static int ReadVInt(byte[] data, int position)
    {
        ArgumentNullException.ThrowIfNull(data);

        byte b = data[position++];
        ThrowIfNull(b, "int");

        int value = b & 0x7F;
        while ((b & 0x80) != 0)
        {
            b = data[position++];
            value <<= 7;
            value |= b & 0x7F;
        }

        return value;
    }

    /// <summary>
    /// Reads a variable-length integer from <paramref name="input"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The encoded value is null.</exception>
    public static int ReadVInt(HollowBlobInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        byte b = ReadByteSafely(input);
        ThrowIfNull(b, "int");

        int value = b & 0x7F;
        while ((b & 0x80) != 0)
        {
            b = ReadByteSafely(input);
            value <<= 7;
            value |= b & 0x7F;
        }

        return value;
    }

    /// <summary>
    /// Reads a run of variable-length integers, returning the number of values written to
    /// <paramref name="output"/>.
    /// </summary>
    /// <param name="data">The byte data to read from.</param>
    /// <param name="position">The position to read from.</param>
    /// <param name="length">The number of bytes to consume.</param>
    /// <param name="output">
    /// The destination, which must be at least <paramref name="length"/> long and zeroed.
    /// </param>
    public static int ReadVIntsInto(IByteData data, long position, int length, Span<int> output)
    {
        ArgumentNullException.ThrowIfNull(data);

        // Two loops: the first handles the common single-byte encoding, falling back to the second
        // full-featured loop as soon as a continuation bit shows up.
        int i = 0;
        for (; i < length; i++)
        {
            int b = data.Get(position + i);
            if ((b & 0x80) != 0)
            {
                break;
            }

            output[i] = b;
        }

        int count = i;
        for (; i < length; i++)
        {
            int b = data.Get(position + i);

            output[count] = (output[count] << 7) | (b & 0x7F);
            count += (~b >> 7) & 0x1;
        }

        return count;
    }

    /// <summary>
    /// Reads a run of variable-length integers as UTF-16 code units, returning the number of values
    /// written to <paramref name="output"/>.
    /// </summary>
    /// <param name="data">The byte data to read from.</param>
    /// <param name="position">The position to read from.</param>
    /// <param name="length">The number of bytes to consume.</param>
    /// <param name="output">
    /// The destination, which must be at least <paramref name="length"/> long and zeroed.
    /// </param>
    public static int ReadVIntsInto(IByteData data, long position, int length, Span<char> output)
    {
        ArgumentNullException.ThrowIfNull(data);

        int i = 0;
        for (; i < length; i++)
        {
            int b = data.Get(position + i);
            if ((b & 0x80) != 0)
            {
                break;
            }

            output[i] = (char)b;
        }

        int count = i;
        for (; i < length; i++)
        {
            int b = data.Get(position + i);

            output[count] = (char)((output[count] << 7) | (b & 0x7F));
            count += (~b >> 7) & 0x1;
        }

        return count;
    }

    /// <summary>
    /// Reads a variable-length long starting at <paramref name="position"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The encoded value is null.</exception>
    public static long ReadVLong(IByteData data, long position)
    {
        ArgumentNullException.ThrowIfNull(data);

        byte b = data.Get(position++);
        ThrowIfNull(b, "long");

        long value = b & 0x7FL;
        while ((b & 0x80) != 0)
        {
            b = data.Get(position++);
            value <<= 7;
            value |= b & 0x7FL;
        }

        return value;
    }

    /// <summary>
    /// Reads a variable-length long starting at <paramref name="position"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The encoded value is null.</exception>
    public static long ReadVLong(byte[] data, int position)
    {
        ArgumentNullException.ThrowIfNull(data);

        byte b = data[position++];
        ThrowIfNull(b, "long");

        long value = b & 0x7FL;
        while ((b & 0x80) != 0)
        {
            b = data[position++];
            value <<= 7;
            value |= b & 0x7FL;
        }

        return value;
    }

    /// <summary>
    /// Reads a variable-length long from <paramref name="input"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The encoded value is null.</exception>
    public static long ReadVLong(HollowBlobInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        byte b = ReadByteSafely(input);
        ThrowIfNull(b, "long");

        long value = b & 0x7FL;
        while ((b & 0x80) != 0)
        {
            b = ReadByteSafely(input);
            value <<= 7;
            value |= b & 0x7FL;
        }

        return value;
    }

    /// <summary>
    /// Determines the size, in bytes, of the variable-length long at <paramref name="position"/>.
    /// </summary>
    public static int NextVLongSize(IByteData data, long position)
    {
        ArgumentNullException.ThrowIfNull(data);

        byte b = data.Get(position++);
        if (b == NullMarker)
        {
            return 1;
        }

        int length = 1;
        while ((b & 0x80) != 0)
        {
            b = data.Get(position++);
            length++;
        }

        return length;
    }

    /// <summary>
    /// Determines the size, in bytes, of the variable-length long at <paramref name="position"/>.
    /// </summary>
    public static int NextVLongSize(byte[] data, int position)
    {
        ArgumentNullException.ThrowIfNull(data);

        byte b = data[position++];
        if (b == NullMarker)
        {
            return 1;
        }

        int length = 1;
        while ((b & 0x80) != 0)
        {
            b = data[position++];
            length++;
        }

        return length;
    }

    /// <summary>
    /// Determines the size, in bytes, of <paramref name="value"/> when encoded.
    /// </summary>
    public static int SizeOfVInt(int value) => value switch
    {
        < 0 => 5,
        < 0x80 => 1,
        < 0x4000 => 2,
        < 0x200000 => 3,
        < 0x10000000 => 4,
        _ => 5,
    };

    /// <summary>
    /// Determines the size, in bytes, of <paramref name="value"/> when encoded.
    /// </summary>
    public static int SizeOfVLong(long value) => value switch
    {
        < 0L => 10,
        < 0x80L => 1,
        < 0x4000L => 2,
        < 0x200000L => 3,
        < 0x10000000L => 4,
        < 0x800000000L => 5,
        < 0x40000000000L => 6,
        < 0x2000000000000L => 7,
        < 0x100000000000000L => 8,
        _ => 9,
    };

    /// <summary>
    /// Counts the variable-length integers encoded over a range of byte data.
    /// </summary>
    public static int CountVarIntsInRange(IByteData data, long fieldPosition, int length)
    {
        ArgumentNullException.ThrowIfNull(data);

        int numInts = 0;
        bool insideInt = false;

        for (int i = 0; i < length; i++)
        {
            byte b = data.Get(fieldPosition + i);

            if ((b & 0x80) == 0)
            {
                numInts++;
                insideInt = false;
            }
            else if (!insideInt && b == NullMarker)
            {
                numInts++;
            }
            else
            {
                insideInt = true;
            }
        }

        return numInts;
    }

    /// <summary>
    /// Reads a single byte, failing rather than returning -1 at end of input.
    /// </summary>
    /// <exception cref="EndOfStreamException">The input ended mid-record.</exception>
    public static byte ReadByteSafely(HollowBlobInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        int b = input.Read();
        if (b == -1)
        {
            throw new EndOfStreamException("Unexpected end of VarInt record");
        }

        return (byte)b;
    }

    /// <summary>A negative long takes the full ten bytes; a null takes one.</summary>
    private const int MaxVLongSize = 10;

    /// <summary>A negative int takes the full five bytes; a null takes one.</summary>
    private const int MaxVIntSize = 5;

    /// <summary>
    /// Encodes <paramref name="value"/> into <paramref name="destination"/>, returning the byte count.
    /// </summary>
    /// <remarks>
    /// Java repeats this cascade of comparisons in four places, once per output type. Encoding into a
    /// stack buffer once and letting the callers copy it out keeps the format in a single place.
    /// </remarks>
    private static int EncodeVLong(Span<byte> destination, long value)
    {
        int pos = 0;

        if (value < 0)
        {
            destination[pos++] = 0x81;
        }

        if (value > 0xFFFFFFFFFFFFFFL || value < 0)
        {
            destination[pos++] = (byte)(0x80 | (((ulong)value >> 56) & 0x7FUL));
        }

        if (value > 0x1FFFFFFFFFFFFL || value < 0)
        {
            destination[pos++] = (byte)(0x80 | (((ulong)value >> 49) & 0x7FUL));
        }

        if (value > 0x3FFFFFFFFFFL || value < 0)
        {
            destination[pos++] = (byte)(0x80 | (((ulong)value >> 42) & 0x7FUL));
        }

        if (value > 0x7FFFFFFFFL || value < 0)
        {
            destination[pos++] = (byte)(0x80 | (((ulong)value >> 35) & 0x7FUL));
        }

        if (value > 0xFFFFFFFL || value < 0)
        {
            destination[pos++] = (byte)(0x80 | (((ulong)value >> 28) & 0x7FUL));
        }

        if (value > 0x1FFFFFL || value < 0)
        {
            destination[pos++] = (byte)(0x80 | (((ulong)value >> 21) & 0x7FUL));
        }

        if (value > 0x3FFFL || value < 0)
        {
            destination[pos++] = (byte)(0x80 | (((ulong)value >> 14) & 0x7FUL));
        }

        if (value > 0x7FL || value < 0)
        {
            destination[pos++] = (byte)(0x80 | (((ulong)value >> 7) & 0x7FUL));
        }

        destination[pos++] = (byte)(value & 0x7FL);

        return pos;
    }

    /// <summary>
    /// Encodes <paramref name="value"/> into <paramref name="destination"/>, returning the byte count.
    /// </summary>
    private static int EncodeVInt(Span<byte> destination, int value)
    {
        int pos = 0;

        if (value > 0x0FFFFFFF || value < 0)
        {
            destination[pos++] = (byte)(0x80 | ((uint)value >> 28));
        }

        if (value > 0x1FFFFF || value < 0)
        {
            destination[pos++] = (byte)(0x80 | (((uint)value >> 21) & 0x7F));
        }

        if (value > 0x3FFF || value < 0)
        {
            destination[pos++] = (byte)(0x80 | (((uint)value >> 14) & 0x7F));
        }

        if (value > 0x7F || value < 0)
        {
            destination[pos++] = (byte)(0x80 | (((uint)value >> 7) & 0x7F));
        }

        destination[pos++] = (byte)(value & 0x7F);

        return pos;
    }

    private static void ThrowIfNull(byte b, string type)
    {
        if (b == NullMarker)
        {
            throw new InvalidOperationException($"Attempting to read null value as {type}");
        }
    }
}
