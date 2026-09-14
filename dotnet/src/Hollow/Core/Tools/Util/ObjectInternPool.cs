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

using System.Text;
using Hollow.Core.Memory;
using Hollow.Core.Memory.Encoding;
using Hollow.Core.Schema;

namespace Hollow.Core.Tools.Util;

/// <summary>
/// Stores each distinct key value once, and hands back an ordinal to refer to it by.
/// </summary>
/// <remarks>
/// <para>
/// The history's key index remembers the key of every record it has ever seen. Across hundreds of
/// states most of those keys repeat, and holding each one as a boxed value would cost more than the
/// index itself. So a value is serialised into the same byte-array ordinal map the write engine uses,
/// and the index keeps only the ordinal.
/// </para>
/// <para>
/// Named <c>ObjectInternPool</c> in Java, in <c>tools.util</c>.
/// </para>
/// <para>
/// <strong>Two Java bugs are not reproduced.</strong> Java writes a string's <em>character</em> count
/// and then its UTF-8 <em>bytes</em>, so any non-ASCII string reads back truncated; and it reads those
/// bytes from one past the length, which assumes the length varint is a single byte and so corrupts
/// any string of 128 characters or more. This writes the byte count and reads back from wherever the
/// varint actually ended.
/// </para>
/// </remarks>
public sealed class ObjectInternPool
{
    private readonly ByteArrayOrdinalMap _ordinalMap = new(1024);
    private readonly HashSet<int> _ordinalsInCycle = [];

    private bool _isReadyToRead;

    /// <summary>
    /// Makes everything written so far readable, and starts a new cycle.
    /// </summary>
    public void PrepareForRead()
    {
        if (!_isReadyToRead)
        {
            _ordinalMap.PrepareForWrite();
        }

        _ordinalsInCycle.Clear();
        _isReadyToRead = true;
    }

    /// <summary>
    /// Whether <paramref name="ordinal"/> was written during the cycle in progress.
    /// </summary>
    /// <remarks>
    /// The index uses this to rule out a match: two records written in the same cycle cannot be the
    /// same record, whatever their keys say.
    /// </remarks>
    public bool OrdinalInCurrentCycle(int ordinal) => _ordinalsInCycle.Contains(ordinal);

    /// <summary>
    /// The value stored at <paramref name="ordinal"/>, read back as <paramref name="type"/>.
    /// </summary>
    public object GetObject(int ordinal, FieldType type)
    {
        long pointer = _ordinalMap.GetPointerForData(ordinal);
        IByteData byteData = _ordinalMap.ByteData.UnderlyingArray;

        return type switch
        {
            FieldType.Boolean => VarInt.ReadVInt(byteData, pointer) == 1,
            FieldType.Float => BitConverter.Int32BitsToSingle(VarInt.ReadVInt(byteData, pointer)),
            FieldType.Double => BitConverter.Int64BitsToDouble(VarInt.ReadVLong(byteData, pointer)),
            FieldType.Int => VarInt.ReadVInt(byteData, pointer),
            FieldType.Long => VarInt.ReadVLong(byteData, pointer),
            FieldType.String => ReadString(byteData, pointer),
            _ => throw new ArgumentException($"Unknown type {type}", nameof(type)),
        };
    }

    /// <summary>
    /// Stores <paramref name="objectToIntern"/> if it is new, and returns the ordinal to refer to it
    /// by.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The value is of a type a key field cannot hold.
    /// </exception>
    public int WriteAndGetOrdinal(object objectToIntern)
    {
        ArgumentNullException.ThrowIfNull(objectToIntern);

        ByteDataArray buffer = new();
        _isReadyToRead = false;

        switch (objectToIntern)
        {
            case float value:
                VarInt.WriteVInt(buffer, BitConverter.SingleToInt32Bits(value));
                break;
            case double value:
                VarInt.WriteVLong(buffer, BitConverter.DoubleToInt64Bits(value));
                break;
            case int value:
                VarInt.WriteVInt(buffer, value);
                break;
            case long value:
                VarInt.WriteVLong(buffer, value);
                break;
            case bool value:
                VarInt.WriteVInt(buffer, value ? 1 : 0);
                break;
            case string value:
                WriteString(buffer, value);
                break;
            default:
                throw new ArgumentException(
                    $"Cannot intern object of type {objectToIntern.GetType().FullName}", nameof(objectToIntern));
        }

        int ordinal = _ordinalMap.GetOrAssignOrdinal(buffer);
        _ordinalsInCycle.Add(ordinal);

        return ordinal;
    }

    /// <summary>
    /// Writes a string as its UTF-8 byte count followed by those bytes.
    /// </summary>
    /// <remarks>
    /// The count is of bytes rather than characters, because that is what the read has to skip.
    /// </remarks>
    private static void WriteString(ByteDataArray buffer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);

        VarInt.WriteVInt(buffer, bytes.Length);

        foreach (byte b in bytes)
        {
            buffer.Write(b);
        }
    }

    private static string ReadString(IByteData byteData, long pointer)
    {
        int length = VarInt.ReadVInt(byteData, pointer);

        // Step past the length itself rather than assuming it took one byte, which is where Java goes
        // wrong for anything 128 bytes or longer.
        long position = pointer + VarInt.SizeOfVInt(length);
        byte[] bytes = new byte[length];

        for (int i = 0; i < length; i++)
        {
            bytes[i] = byteData.Get(position + i);
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
