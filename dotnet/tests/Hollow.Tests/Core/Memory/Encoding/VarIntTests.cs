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

using Hollow.Core.Memory;
using Hollow.Core.Memory.Encoding;
using Hollow.Core.Read;

namespace Hollow.Tests.Core.Memory.Encoding;

/// <summary>
/// Ported from <c>VarIntTest</c>, plus round-trip coverage for the overloads Java exercises only
/// indirectly.
/// </summary>
public class VarIntTests
{
    private static readonly byte[] BytesValue129 = [0x81, 0x01];
    private static readonly byte[] BytesEmpty = [];
    private static readonly byte[] BytesTruncated = [0x81];

    [Fact]
    public void ReadVLongFromBlobInput()
    {
        using HollowBlobInput input = HollowBlobInput.Serial(BytesValue129);

        Assert.Equal(129L, VarInt.ReadVLong(input));
    }

    [Fact]
    public void ReadVLongFromEmptyBlobInputThrows()
    {
        using HollowBlobInput input = HollowBlobInput.Serial(BytesEmpty);

        Assert.Throws<EndOfStreamException>(() => VarInt.ReadVLong(input));
    }

    [Fact]
    public void ReadVLongFromTruncatedBlobInputThrows()
    {
        using HollowBlobInput input = HollowBlobInput.Serial(BytesTruncated);

        Assert.Throws<EndOfStreamException>(() => VarInt.ReadVLong(input));
    }

    [Fact]
    public void ReadVIntFromBlobInput()
    {
        using HollowBlobInput input = HollowBlobInput.Serial(BytesValue129);

        Assert.Equal(129, VarInt.ReadVInt(input));
    }

    [Fact]
    public void ReadVIntFromEmptyBlobInputThrows()
    {
        using HollowBlobInput input = HollowBlobInput.Serial(BytesEmpty);

        Assert.Throws<EndOfStreamException>(() => VarInt.ReadVInt(input));
    }

    [Fact]
    public void ReadVIntFromTruncatedBlobInputThrows()
    {
        using HollowBlobInput input = HollowBlobInput.Serial(BytesTruncated);

        Assert.Throws<EndOfStreamException>(() => VarInt.ReadVInt(input));
    }

    [Fact]
    public void VLongRoundTripsThroughByteArray()
    {
        const long Value = 129L;

        byte[] serialized = new byte[VarInt.SizeOfVLong(Value)];
        VarInt.WriteVLong(serialized, 0, Value);

        Assert.Equal(VarInt.SizeOfVLong(Value), VarInt.NextVLongSize(serialized, 0));
        Assert.Equal(Value, VarInt.ReadVLong(serialized, 0));
    }

    [Fact]
    public void VIntRoundTripsThroughByteArray()
    {
        const int Value = 129;

        byte[] serialized = new byte[VarInt.SizeOfVInt(Value)];
        VarInt.WriteVInt(serialized, 0, Value);

        Assert.Equal(Value, VarInt.ReadVInt(serialized, 0));
    }

    [Fact]
    public void VNullRoundTripsThroughByteArray()
    {
        byte[] serialized = new byte[1];
        VarInt.WriteVNull(serialized, 0);

        Assert.True(VarInt.ReadVNull(serialized, 0));
        Assert.False(VarInt.ReadVNull(new byte[1], 0));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(127L)]
    [InlineData(128L)]
    [InlineData(16383L)]
    [InlineData(16384L)]
    [InlineData(1L << 34)]
    [InlineData(long.MaxValue)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void VLongRoundTripsThroughEveryWriter(long value)
    {
        int expectedSize = VarInt.SizeOfVLong(value);

        byte[] viaArray = new byte[expectedSize];
        Assert.Equal(expectedSize, VarInt.WriteVLong(viaArray, 0, value));
        Assert.Equal(value, VarInt.ReadVLong(viaArray, 0));

        ByteDataArray viaBuffer = new();
        VarInt.WriteVLong(viaBuffer, value);
        Assert.Equal(expectedSize, viaBuffer.Length);
        Assert.Equal(value, VarInt.ReadVLong(viaBuffer.UnderlyingArray, 0));

        Assert.Equal(expectedSize, VarInt.NextVLongSize(viaBuffer.UnderlyingArray, 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(16383)]
    [InlineData(16384)]
    [InlineData(int.MaxValue)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void VIntRoundTripsThroughEveryWriter(int value)
    {
        int expectedSize = VarInt.SizeOfVInt(value);

        byte[] viaArray = new byte[expectedSize];
        Assert.Equal(expectedSize, VarInt.WriteVInt(viaArray, 0, value));
        Assert.Equal(value, VarInt.ReadVInt(viaArray, 0));

        ByteDataArray viaBuffer = new();
        VarInt.WriteVInt(viaBuffer, value);
        Assert.Equal(expectedSize, viaBuffer.Length);
        Assert.Equal(value, VarInt.ReadVInt(viaBuffer.UnderlyingArray, 0));
    }

    [Fact]
    public void ReadingANullAsAnIntegerThrows()
    {
        ByteDataArray buffer = new();
        VarInt.WriteVNull(buffer);

        Assert.True(VarInt.ReadVNull(buffer.UnderlyingArray, 0));
        Assert.Throws<InvalidOperationException>(() => VarInt.ReadVInt(buffer.UnderlyingArray, 0));
        Assert.Throws<InvalidOperationException>(() => VarInt.ReadVLong(buffer.UnderlyingArray, 0));
    }

    [Fact]
    public void ReadVIntsIntoReadsARunOfValues()
    {
        int[] values = [0, 1, 127, 128, 300, 16384];

        ByteDataArray buffer = new();
        foreach (int value in values)
        {
            VarInt.WriteVInt(buffer, value);
        }

        int[] output = new int[buffer.Length];
        int count = VarInt.ReadVIntsInto(buffer.UnderlyingArray, 0, (int)buffer.Length, output);

        Assert.Equal(values.Length, count);
        Assert.Equal(values, output[..count]);
    }

    [Fact]
    public void CountVarIntsInRangeCountsNullsAndValues()
    {
        ByteDataArray buffer = new();
        VarInt.WriteVInt(buffer, 1);
        VarInt.WriteVNull(buffer);
        VarInt.WriteVInt(buffer, 16384);
        VarInt.WriteVInt(buffer, 2);

        Assert.Equal(4, VarInt.CountVarIntsInRange(buffer.UnderlyingArray, 0, (int)buffer.Length));
    }
}
