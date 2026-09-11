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

using Hollow.Core.Util;
using System.Text;

namespace Hollow.Core.Io;

/// <summary>
/// Encodes and decodes strings in Java's "modified UTF-8", the wire format used by
/// <c>java.io.DataOutput.writeUTF</c> / <c>java.io.DataInput.readUTF</c>.
/// </summary>
/// <remarks>
/// <para>
/// This has no BCL equivalent and must be hand-rolled: Hollow blob headers and schemas are written
/// with <c>writeUTF</c>, so a .NET reader has to match it byte for byte to stay compatible with blobs
/// produced by the Java implementation. It differs from standard UTF-8 in two ways:
/// </para>
/// <list type="bullet">
///   <item><description>U+0000 is encoded as the two bytes <c>0xC0 0x80</c> rather than a single <c>0x00</c>.</description></item>
///   <item><description>
///     Characters outside the BMP are encoded as a surrogate <em>pair</em> of three-byte sequences
///     (CESU-8) rather than a single four-byte sequence.
///   </description></item>
/// </list>
/// <para>
/// Because .NET strings are already UTF-16, encoding operates per <see cref="char"/> and surrogate
/// pairs fall out naturally.
/// </para>
/// </remarks>
internal static class ModifiedUtf8
{
    /// <summary>
    /// Returns the number of bytes <paramref name="value"/> occupies when modified-UTF-8 encoded.
    /// </summary>
    internal static int GetByteCount(string value)
    {
        int count = 0;
        foreach (char c in value)
        {
            count += c switch
            {
                >= (char)0x0001 and <= (char)0x007F => 1,
                <= (char)0x07FF => 2,
                _ => 3,
            };
        }

        return count;
    }

    /// <summary>
    /// Encodes <paramref name="value"/> into <paramref name="destination"/>, which must be at least
    /// <see cref="GetByteCount"/> bytes long. Returns the number of bytes written.
    /// </summary>
    internal static int GetBytes(string value, Span<byte> destination)
    {
        int pos = 0;
        foreach (char c in value)
        {
            if (c is >= (char)0x0001 and <= (char)0x007F)
            {
                destination[pos++] = (byte)c;
            }
            else if (c <= (char)0x07FF)
            {
                destination[pos++] = (byte)(0xC0 | ((c >> 6) & 0x1F));
                destination[pos++] = (byte)(0x80 | (c & 0x3F));
            }
            else
            {
                destination[pos++] = (byte)(0xE0 | ((c >> 12) & 0x0F));
                destination[pos++] = (byte)(0x80 | ((c >> 6) & 0x3F));
                destination[pos++] = (byte)(0x80 | (c & 0x3F));
            }
        }

        return pos;
    }

    /// <summary>
    /// Decodes a modified-UTF-8 byte sequence.
    /// </summary>
    /// <exception cref="InvalidDataException">The input is not well-formed modified UTF-8.</exception>
    internal static string GetString(ReadOnlySpan<byte> bytes)
    {
        StringBuilder sb = new(bytes.Length);

        int i = 0;
        while (i < bytes.Length)
        {
            int b = bytes[i];
            switch (b >> 4)
            {
                case <= 7:
                    // 0xxxxxxx
                    i++;
                    sb.Append((char)b);
                    break;

                case 12 or 13:
                    // 110xxxxx 10xxxxxx
                    if (i + 2 > bytes.Length)
                    {
                        throw new InvalidDataException("malformed input: partial character at end");
                    }

                    int b2 = bytes[i + 1];
                    if ((b2 & 0xC0) != 0x80)
                    {
                        throw new InvalidDataException($"malformed input around byte {(i + 1).Invariant()}");
                    }

                    sb.Append((char)(((b & 0x1F) << 6) | (b2 & 0x3F)));
                    i += 2;
                    break;

                case 14:
                    // 1110xxxx 10xxxxxx 10xxxxxx
                    if (i + 3 > bytes.Length)
                    {
                        throw new InvalidDataException("malformed input: partial character at end");
                    }

                    int c2 = bytes[i + 1];
                    int c3 = bytes[i + 2];
                    if ((c2 & 0xC0) != 0x80 || (c3 & 0xC0) != 0x80)
                    {
                        throw new InvalidDataException($"malformed input around byte {(i + 2).Invariant()}");
                    }

                    sb.Append((char)(((b & 0x0F) << 12) | ((c2 & 0x3F) << 6) | (c3 & 0x3F)));
                    i += 3;
                    break;

                default:
                    throw new InvalidDataException($"malformed input around byte {i.Invariant()}");
            }
        }

        return sb.ToString();
    }
}
