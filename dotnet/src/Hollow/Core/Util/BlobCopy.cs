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
using Hollow.Core.Memory.Encoding;
using Hollow.Core.Read;
using Hollow.Core.Write;

namespace Hollow.Core.Util;

/// <summary>
/// Copies pieces of a blob from an input to several outputs at once, reading each piece once.
/// </summary>
/// <remarks>
/// <para>
/// What a blob-rewriting tool needs and a blob reader does not: to move a length-prefixed run of
/// bytes from one stream to several others without understanding it. Every method here reads the
/// piece's own framing, so the caller advances through the blob correctly whether or not it knows
/// what the piece means.
/// </para>
/// <para>
/// Java calls this <c>IOUtils</c> and puts it in <c>core.util</c>. The name says nothing, so this one
/// says what it copies.
/// </para>
/// </remarks>
public static class BlobCopy
{
    /// <summary>How much is moved per read when copying an arbitrary run of bytes.</summary>
    private const int CopyBufferSize = 4096;

    /// <summary>
    /// Copies one variable-length int, returning the value copied.
    /// </summary>
    /// <remarks>
    /// The value is returned because a caller nearly always needs it — it is a count or a maximum
    /// ordinal that decides what to read next — and reading it again would mean seeking back.
    /// </remarks>
    public static int CopyVInt(HollowBlobInput input, params HollowBlobOutput[] outputs)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(outputs);

        int value = VarInt.ReadVInt(input);

        foreach (HollowBlobOutput output in outputs)
        {
            VarInt.WriteVInt(output, value);
        }

        return value;
    }

    /// <summary>Copies one variable-length long, returning the value copied.</summary>
    public static long CopyVLong(HollowBlobInput input, params HollowBlobOutput[] outputs)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(outputs);

        long value = VarInt.ReadVLong(input);

        foreach (HollowBlobOutput output in outputs)
        {
            VarInt.WriteVLong(output, value);
        }

        return value;
    }

    /// <summary>Copies exactly <paramref name="count"/> bytes.</summary>
    /// <exception cref="EndOfStreamException">The input ends first.</exception>
    public static void CopyBytes(HollowBlobInput input, HollowBlobOutput[] outputs, long count)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(outputs);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);

        try
        {
            while (count > 0)
            {
                int wanted = (int)Math.Min(count, buffer.Length);
                int read = input.Read(buffer.AsSpan(0, wanted));

                if (read <= 0)
                {
                    throw new EndOfStreamException(
                        $"the blob ended with {count} byte(s) of a run still to copy");
                }

                foreach (HollowBlobOutput output in outputs)
                {
                    output.Write(buffer.AsSpan(0, read));
                }

                count -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Copies a long array written as a count followed by that many 64-bit words.
    /// </summary>
    /// <remarks>
    /// The fixed-length field storage and the collection pointer arrays are both written this way, so
    /// a tool that does not care what the bits mean can move one without decoding it.
    /// </remarks>
    public static void CopySegmentedLongArray(HollowBlobInput input, params HollowBlobOutput[] outputs)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(outputs);

        long numLongs = CopyVLong(input, outputs);

        CopyBytes(input, outputs, numLongs * sizeof(long));
    }

    /// <summary>
    /// Copies a gap-encoded ordinal sequence, of the kind a delta carries for its removals and
    /// additions.
    /// </summary>
    /// <remarks>
    /// Java puts this on <c>GapEncodedVariableLengthIntegerReader</c> as a static. It belongs with
    /// the other copies: it decodes nothing, and the reader has no part in it.
    /// </remarks>
    public static void CopyEncodedDeltaOrdinals(
        HollowBlobInput input, params HollowBlobOutput[] outputs)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(outputs);

        long numBytes = CopyVLong(input, outputs);

        CopyBytes(input, outputs, numBytes);
    }

    /// <summary>Copies a 32-bit count followed by that many 64-bit words.</summary>
    /// <remarks>
    /// The populated-ordinal bit set of a snapshot, whose length is a plain int rather than a varint.
    /// </remarks>
    public static void CopyPopulatedOrdinals(HollowBlobInput input, params HollowBlobOutput[] outputs)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(outputs);

        int numLongs = input.ReadInt32();

        foreach (HollowBlobOutput output in outputs)
        {
            output.WriteInt32(numLongs);
        }

        CopyBytes(input, outputs, (long)numLongs * sizeof(long));
    }
}
