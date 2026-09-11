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

using Hollow.Core.Memory.Encoding;
using Hollow.Core.Schema;

namespace Hollow.Core.Read.Engine;

/// <summary>
/// Reads the header block that begins every Hollow blob.
/// </summary>
public sealed class HollowBlobHeaderReader
{
    /// <summary>
    /// Reads a blob header from <paramref name="input"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">The blob's format version is not readable.</exception>
    public HollowBlobHeader ReadHeader(HollowBlobInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        int headerVersion = input.ReadInt32();
        if (headerVersion != HollowBlobHeader.HollowBlobVersionHeader)
        {
            throw new InvalidDataException(
                "The HollowBlob you are trying to read is incompatible. The expected Hollow blob version "
                + $"was {HollowBlobHeader.HollowBlobVersionHeader} but the actual version was {headerVersion}.");
        }

        HollowBlobHeader header = new()
        {
            BlobFormatVersion = headerVersion,
            OriginRandomizedTag = input.ReadInt64(),
            DestinationRandomizedTag = input.ReadInt64(),
        };

        // The pre-2.2.0 envelope: a byte count an older reader used to skip the schema block wholesale.
        int oldBytesToSkip = VarInt.ReadVInt(input);
        if (oldBytesToSkip != 0)
        {
            header.Schemas = ReadSchemas(input);
            SkipForwardCompatibilityBytes(input);
        }

        header.HeaderTags = ReadHeaderTags(input);

        return header;
    }

    private static IReadOnlyList<HollowSchema> ReadSchemas(HollowBlobInput input)
    {
        int numSchemas = VarInt.ReadVInt(input);

        HollowSchema[] schemas = new HollowSchema[numSchemas];
        for (int i = 0; i < numSchemas; i++)
        {
            schemas[i] = HollowSchema.ReadFrom(input);
        }

        return schemas;
    }

    private static Dictionary<string, string> ReadHeaderTags(HollowBlobInput input)
    {
        int numTags = input.ReadInt16();

        Dictionary<string, string> headerTags = new(numTags, StringComparer.Ordinal);
        for (int i = 0; i < numTags; i++)
        {
            headerTags[input.ReadUtf()] = input.ReadUtf();
        }

        return headerTags;
    }

    /// <summary>
    /// Skips a forwards-compatibility block, which is a byte count followed by that many bytes this
    /// version does not understand.
    /// </summary>
    internal static void SkipForwardCompatibilityBytes(HollowBlobInput input)
    {
        int bytesToSkip = VarInt.ReadVInt(input);
        while (bytesToSkip > 0)
        {
            long skipped = input.SkipBytes(bytesToSkip);
            if (skipped <= 0)
            {
                throw new EndOfStreamException("unexpected end of forwards-compatibility block");
            }

            bytesToSkip -= (int)skipped;
        }
    }
}
