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

namespace Hollow.Core.Write;

/// <summary>
/// Writes the header block that begins every Hollow blob.
/// </summary>
public sealed class HollowBlobHeaderWriter
{
    /// <summary>
    /// Writes <paramref name="header"/> to <paramref name="output"/>.
    /// </summary>
    public void WriteHeader(HollowBlobHeader header, HollowBlobOutput output)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(output);

        // Four bytes identifying the blob format version; a change signals backwards incompatibility.
        output.WriteInt32(HollowBlobHeader.HollowBlobVersionHeader);

        // Every state gets a random 64-bit tag. Applying a delta compares the originating state's tag
        // against the current one, so a delta cannot be applied to the wrong state.
        output.WriteInt64(header.OriginRandomizedTag);
        output.WriteInt64(header.DestinationRandomizedTag);

        // The schemas go inside the pre-2.2.0 backwards-compatibility envelope: a length prefix an
        // older reader uses to skip the whole block.
        using MemoryStream schemasStream = new();
        using (HollowBlobOutput schemasOutput = HollowBlobOutput.Serial(schemasStream, leaveOpen: true))
        {
            VarInt.WriteVInt(schemasOutput, header.Schemas.Count);
            foreach (HollowSchema schema in header.Schemas)
            {
                schema.WriteTo(schemasOutput);
            }
        }

        byte[] schemasData = schemasStream.ToArray();

        // Plus one byte for the forwards-compatibility block that follows.
        VarInt.WriteVInt(output, schemasData.Length + 1);
        output.Write(schemasData);

        // Forwards compatibility: new data can be added here behind its own byte count, which existing
        // readers skip.
        VarInt.WriteVInt(output, 0);

        output.WriteInt16((short)header.HeaderTags.Count);
        foreach ((string key, string value) in header.HeaderTags)
        {
            output.WriteUtf(key);
            output.WriteUtf(value);
        }
    }

    /// <summary>
    /// Writes the header of an optional blob part to <paramref name="output"/>.
    /// </summary>
    /// <remarks>
    /// Shorter than a main blob's header: a part carries no header tags, and its schemas need no
    /// backwards-compatibility envelope because no reader older than optional parts will ever see one.
    /// </remarks>
    public void WritePartHeader(HollowBlobOptionalPartHeader header, HollowBlobOutput output)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(output);

        output.WriteInt32(HollowBlobOptionalPartHeader.HollowBlobPartVersionHeader);
        output.WriteUtf(header.PartName);

        // Repeated from the main blob, so that a part cannot be applied against the wrong state.
        output.WriteInt64(header.OriginRandomizedTag);
        output.WriteInt64(header.DestinationRandomizedTag);

        VarInt.WriteVInt(output, header.Schemas.Count);
        foreach (HollowSchema schema in header.Schemas)
        {
            schema.WriteTo(output);
        }

        // Forwards compatibility: new data can be added here behind its own byte count.
        VarInt.WriteVInt(output, 0);
    }
}
