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

namespace Hollow.Tests.Core.Io;

/// <summary>
/// Blob primitives have to match <c>java.io.DataInput</c> and <c>java.io.DataOutput</c> byte for byte,
/// or a .NET reader cannot read a blob a Java producer wrote. These tests pin the encodings against
/// bytes taken from the Java specification rather than from the port itself.
/// </summary>
public class BlobIoTests
{
    [Fact]
    public void IntegersAreBigEndian()
    {
        using MemoryStream stream = new();
        using (HollowBlobOutput output = HollowBlobOutput.Serial(stream, leaveOpen: true))
        {
            output.WriteInt16(0x0102);
            output.WriteInt32(0x01020304);
            output.WriteInt64(0x0102030405060708L);
        }

        Assert.Equal(
            [0x01, 0x02, 0x01, 0x02, 0x03, 0x04, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08],
            stream.ToArray());
    }

    [Theory]
    [InlineData((short)0)]
    [InlineData((short)1)]
    [InlineData((short)-1)]
    [InlineData(short.MaxValue)]
    [InlineData(short.MinValue)]
    public void Int16RoundTrips(short value) =>
        Assert.Equal(value, RoundTrip(output => output.WriteInt16(value), input => input.ReadInt16()));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void Int32RoundTrips(int value) =>
        Assert.Equal(value, RoundTrip(output => output.WriteInt32(value), input => input.ReadInt32()));

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void Int64RoundTrips(long value) =>
        Assert.Equal(value, RoundTrip(output => output.WriteInt64(value), input => input.ReadInt64()));

    /// <summary>
    /// Modified UTF-8 differs from standard UTF-8 in exactly two places, and both matter for Hollow:
    /// a NUL is two bytes, and an astral character is a surrogate pair of three-byte sequences.
    /// </summary>
    [Theory]
    [InlineData("", new byte[] { 0x00, 0x00 })]
    [InlineData("A", new byte[] { 0x00, 0x01, 0x41 })]
    [InlineData("å", new byte[] { 0x00, 0x02, 0xC3, 0xA5 })]
    [InlineData("€", new byte[] { 0x00, 0x03, 0xE2, 0x82, 0xAC })]
    [InlineData("😀", new byte[] { 0x00, 0x06, 0xED, 0xA0, 0xBD, 0xED, 0xB8, 0x80 })]
    public void StringsUseJavaModifiedUtf8(string value, byte[] expected)
    {
        using MemoryStream stream = new();
        using (HollowBlobOutput output = HollowBlobOutput.Serial(stream, leaveOpen: true))
        {
            output.WriteUtf(value);
        }

        Assert.Equal(expected, stream.ToArray());

        using HollowBlobInput input = HollowBlobInput.Serial(stream.ToArray());
        Assert.Equal(value, input.ReadUtf());
    }

    /// <summary>
    /// A NUL is encoded as the two bytes <c>0xC0 0x80</c> rather than a single <c>0x00</c>, so that an
    /// encoded string never contains an embedded zero byte.
    /// </summary>
    [Fact]
    public void NulIsEncodedAsTwoBytes()
    {
        string value = ((char)0).ToString();

        using MemoryStream stream = new();
        using (HollowBlobOutput output = HollowBlobOutput.Serial(stream, leaveOpen: true))
        {
            output.WriteUtf(value);
        }

        Assert.Equal([0x00, 0x02, 0xC0, 0x80], stream.ToArray());

        using HollowBlobInput input = HollowBlobInput.Serial(stream.ToArray());
        Assert.Equal(value, input.ReadUtf());
    }

    [Fact]
    public void ReadingPastTheEndThrows()
    {
        using HollowBlobInput input = HollowBlobInput.Serial([0x01, 0x02]);

        Assert.Throws<EndOfStreamException>(() => input.ReadInt32());
    }

    [Fact]
    public void ReadReturnsMinusOneAtTheEnd()
    {
        using HollowBlobInput input = HollowBlobInput.Serial([0x07]);

        Assert.Equal(7, input.Read());
        Assert.Equal(-1, input.Read());
    }

    [Fact]
    public void SkipBytesStopsAtTheEnd()
    {
        using HollowBlobInput input = HollowBlobInput.Serial([1, 2, 3, 4]);

        Assert.Equal(2, input.SkipBytes(2));
        Assert.Equal(2, input.SkipBytes(100));
        Assert.Equal(0, input.SkipBytes(1));
    }

    [Fact]
    public void SkipBytesWorksOnANonSeekableStream()
    {
        using ForwardOnlyStream stream = new([1, 2, 3, 4]);
        using HollowBlobInput input = HollowBlobInput.Serial(stream);

        Assert.False(input.CanSeek);
        Assert.Equal(2, input.SkipBytes(2));
        Assert.Equal(3, input.Read());
    }

    [Fact]
    public void WriteUtfRejectsStringsTooLongToLengthPrefix()
    {
        using MemoryStream stream = new();
        using HollowBlobOutput output = HollowBlobOutput.Serial(stream, leaveOpen: true);

        Assert.Throws<ArgumentException>(() => output.WriteUtf(new string('x', ushort.MaxValue + 1)));
    }

    private static T RoundTrip<T>(Action<HollowBlobOutput> write, Func<HollowBlobInput, T> read)
    {
        using MemoryStream stream = new();
        using (HollowBlobOutput output = HollowBlobOutput.Serial(stream, leaveOpen: true))
        {
            write(output);
        }

        using HollowBlobInput input = HollowBlobInput.Serial(stream.ToArray());
        return read(input);
    }

    /// <summary>
    /// A stream that reports itself as non-seekable, so that the fallback paths get exercised.
    /// </summary>
    private sealed class ForwardOnlyStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
