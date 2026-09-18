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

using System.Diagnostics;
using Hollow.Core.Memory;
using Hollow.Core.Memory.Encoding;
using Hollow.Core.Read;

namespace Hollow.Tests.Core.Memory.Encoding;

/// <summary>
/// What the decoders do when handed bytes that were never an encoded value.
/// </summary>
/// <remarks>
/// <para>
/// Java's <c>VarIntTest</c> only reads back what it wrote. A decoder is also the first thing a corrupt
/// or truncated blob reaches, and the interesting question there is not what it returns but that it
/// stops: every one of these formats has a size the encoder cannot exceed, so a reader following
/// continuation bits into an arbitrary buffer is scanning a buffer it has no business scanning.
/// </para>
/// <para>
/// Nothing here asserts a value. It asserts that a decoder handed nonsense fails, fails quickly, and
/// fails with the same exception each time rather than with whatever the first out-of-range access
/// happened to throw.
/// </para>
/// </remarks>
public sealed class MalformedInputTests
{
    /// <summary>
    /// Big enough that walking it would show up, small enough to allocate in every test run.
    /// </summary>
    private const int LargeBufferLength = 8 * 1024 * 1024;

    // ── Nothing to read ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ReadingAVarIntFromNoBytesIsRefused()
    {
        Assert.Throws<InvalidDataException>(() => VarInt.ReadVInt([], out _));
        Assert.Throws<InvalidDataException>(() => VarInt.ReadVLong([], out _));
    }

    [Fact]
    public void MeasuringADecimalInNoBytesIsRefused()
    {
        // The case that started this: a zero-length span is a length somewhere saying zero, and the
        // answer is an exception rather than whatever source[0] reads.
        Assert.Throws<InvalidDataException>(() => DecimalEncoding.EncodedLength([]));
    }

    [Fact]
    public void DecodingADecimalFromNoBytesIsRefused()
    {
        Assert.Throws<InvalidDataException>(() => DecimalEncoding.Decode([], out _));
        Assert.Throws<InvalidDataException>(() => DecimalEncoding.DecodeValue([]));
    }

    [Fact]
    public void ADecimalFieldWithNoLengthIsRefused()
    {
        ByteDataArray data = new();
        data.Write(0x01);

        Assert.Throws<InvalidDataException>(() => DecimalEncoding.Decode(data.UnderlyingArray, 0, 0));
        Assert.Throws<InvalidDataException>(() => DecimalEncoding.Decode(data.UnderlyingArray, 0, -1));
    }

    // ── A continuation that never ends ───────────────────────────────────────────────────────────

    [Fact]
    public void AVarIntThatNeverEndsIsRefusedRatherThanFollowed()
    {
        // Every byte says "more follows". Five is all an int can be, so the sixth is the answer.
        byte[] endless = AllContinuations(LargeBufferLength);

        Assert.Throws<InvalidDataException>(() => VarInt.ReadVInt(endless, out _));
        Assert.Throws<InvalidDataException>(() => VarInt.ReadVLong(endless, out _));
    }

    [Fact]
    public void AVarIntThatNeverEndsIsRefusedQuickly()
    {
        // The point of the bound, stated as the test it is: a decoder given eight megabytes of
        // continuation bytes reads a handful of them, not eight million.
        byte[] endless = AllContinuations(LargeBufferLength);

        Stopwatch elapsed = Stopwatch.StartNew();

        for (int attempt = 0; attempt < 1000; attempt++)
        {
            Assert.Throws<InvalidDataException>(() => VarInt.ReadVLong(endless, out _));
        }

        // A thousand scans of eight megabytes is seconds; a thousand reads of ten bytes is not
        // measurable. The threshold is loose enough to survive a slow machine and tight enough to
        // fail if the bound goes away.
        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(2),
            $"a thousand refusals took {elapsed.Elapsed}, which means the buffer is being walked");
    }

    [Fact]
    public void AVarIntThatNeverEndsInAByteArrayIsRefused()
    {
        byte[] endless = AllContinuations(1024);

        Assert.Throws<InvalidDataException>(() => VarInt.ReadVInt(endless, 0));
        Assert.Throws<InvalidDataException>(() => VarInt.ReadVLong(endless, 0));
        Assert.Throws<InvalidDataException>(() => VarInt.NextVLongSize(endless, 0));
    }

    [Fact]
    public void AVarIntThatNeverEndsInByteDataIsRefused()
    {
        // The overload with the least to stop it: byte data has no end to run into, so without the
        // bound this walks the whole blob.
        ByteDataArray data = new();

        for (int i = 0; i < 1024; i++)
        {
            data.Write(0xFF);
        }

        Assert.Throws<InvalidDataException>(() => VarInt.ReadVInt(data.UnderlyingArray, 0L));
        Assert.Throws<InvalidDataException>(() => VarInt.ReadVLong(data.UnderlyingArray, 0L));
        Assert.Throws<InvalidDataException>(() => VarInt.NextVLongSize(data.UnderlyingArray, 0L));
    }

    [Fact]
    public void AVarIntThatNeverEndsInABlobIsRefused()
    {
        using HollowBlobInput input = HollowBlobInput.Serial(new MemoryStream(AllContinuations(1024)));

        Assert.Throws<InvalidDataException>(() => VarInt.ReadVInt(input));
    }

    [Fact]
    public void ADecimalWhoseMantissaNeverEndsIsRefused()
    {
        // Form C — a 64-bit mantissa — so ten bytes is the most it could be.
        byte[] endless = AllContinuations(LargeBufferLength);
        endless[0] = 0x70;

        Assert.Throws<InvalidDataException>(() => DecimalEncoding.EncodedLength(endless));
        Assert.Throws<InvalidDataException>(() => DecimalEncoding.Decode(endless, out _));
    }

    [Fact]
    public void ADecimalWhoseMantissaNeverEndsIsRefusedQuickly()
    {
        byte[] endless = AllContinuations(LargeBufferLength);
        endless[0] = 0x70;

        Stopwatch elapsed = Stopwatch.StartNew();

        for (int attempt = 0; attempt < 1000; attempt++)
        {
            Assert.Throws<InvalidDataException>(() => DecimalEncoding.EncodedLength(endless));
        }

        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(2),
            $"a thousand refusals took {elapsed.Elapsed}, which means the buffer is being walked");
    }

    [Fact]
    public void ADecimalInTheThirtyTwoBitFormIsHeldToTheShorterLimit()
    {
        // Form B's mantissa is a 32-bit variable-length integer, so six bytes of continuation is
        // already one too many even though a 64-bit mantissa could carry ten.
        byte[] endless = AllContinuations(64);
        endless[0] = 0x50;

        Assert.Throws<InvalidDataException>(() => DecimalEncoding.EncodedLength(endless));
    }

    // ── Cut short ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AVarIntCutShortByTheEndOfItsSpanIsRefused()
    {
        // Two continuation bytes and nothing after them.
        byte[] truncated = [0x81, 0x81];

        Assert.Throws<InvalidDataException>(() => VarInt.ReadVInt(truncated, out _));
        Assert.Throws<InvalidDataException>(() => VarInt.ReadVLong(truncated, out _));
    }

    [Fact]
    public void ADecimalCutShortIsRefused()
    {
        // A form C marker with a mantissa that runs out.
        Assert.Throws<InvalidDataException>(() => DecimalEncoding.EncodedLength([0x70]));
        Assert.Throws<InvalidDataException>(() => DecimalEncoding.EncodedLength([0x70, 0x81]));
        Assert.Throws<InvalidDataException>(() => DecimalEncoding.Decode([0x70, 0x81], out _));
    }

    [Fact]
    public void ADecimalInTheSixteenByteFormWithFewerThanSixteenBytesIsRefused()
    {
        // Both readers, because a length reader that answered seventeen here would send its caller
        // seventeen bytes forward into a record that has ten.
        byte[] truncated = new byte[10];
        truncated[0] = 0xFE;

        Assert.Throws<InvalidDataException>(() => DecimalEncoding.Decode(truncated, out _));
        Assert.Throws<InvalidDataException>(() => DecimalEncoding.EncodedLength(truncated));
    }

    [Fact]
    public void SixteenBytesThatDescribeNoDecimalAreRefused()
    {
        // The flag word names a scale of 255, which no decimal has. The runtime's own constructor is
        // what decides that; this asserts the refusal arrives as malformed data rather than as an
        // ArgumentException from somewhere under the read path.
        byte[] bytes = new byte[DecimalEncoding.MaxEncodedLength];
        bytes[0] = 0xFE;
        bytes[14] = 0xFF;

        Assert.Throws<InvalidDataException>(() => DecimalEncoding.Decode(bytes, out _));
    }

    // ── A marker that means nothing ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0x10)]
    [InlineData(0x20)]
    [InlineData(0x30)]
    [InlineData(0x80)]
    [InlineData(0xA0)]
    [InlineData(0xC0)]
    [InlineData(0xE0)]
    [InlineData(0xFD)]
    public void AMarkerThatBeginsNoDecimalFormIsRefusedByBothReaders(int marker)
    {
        byte[] bytes = new byte[DecimalEncoding.MaxEncodedLength];
        bytes[0] = (byte)marker;

        Assert.Throws<InvalidDataException>(() => DecimalEncoding.EncodedLength(bytes));
        Assert.Throws<InvalidDataException>(() => DecimalEncoding.Decode(bytes, out _));
    }

    // ── Whatever happens to be in the buffer ─────────────────────────────────────────────────────

    [Fact]
    public void RandomBytesAreEitherDecodedOrRefused()
    {
        // No decoder should ever do anything but return a value or throw an exception of a kind the
        // read path expects. What is not acceptable is an IndexOutOfRangeException, a hang, or a
        // length that would send the caller off the end of the record.
        Random random = new(20160101);

        for (int attempt = 0; attempt < 20_000; attempt++)
        {
            byte[] bytes = new byte[random.Next(1, 24)];
            random.NextBytes(bytes);

            TryDecode(() =>
            {
                int length = DecimalEncoding.EncodedLength(bytes);

                Assert.InRange(length, 1, DecimalEncoding.MaxEncodedLength);
                Assert.True(length <= bytes.Length, $"{length} bytes claimed out of {bytes.Length}");
            });

            TryDecode(() =>
            {
                _ = DecimalEncoding.Decode(bytes, out int length);

                Assert.InRange(length, 1, DecimalEncoding.MaxEncodedLength);
            });

            TryDecode(() =>
            {
                _ = VarInt.ReadVInt(bytes, out int length);

                Assert.InRange(length, 1, VarInt.MaxVIntSize);
            });

            TryDecode(() =>
            {
                _ = VarInt.ReadVLong(bytes, out int length);

                Assert.InRange(length, 1, VarInt.MaxVLongSize);
            });
        }
    }

    [Fact]
    public void RandomBytesInAWholeBufferAreRefusedQuickly()
    {
        // The same sweep over one large buffer rather than many small ones: a decoder that scanned for
        // a terminator would take the length of the buffer per call.
        Random random = new(20160102);
        byte[] bytes = new byte[LargeBufferLength];
        random.NextBytes(bytes);

        Stopwatch elapsed = Stopwatch.StartNew();

        for (int attempt = 0; attempt < 10_000; attempt++)
        {
            // Every offset starts a different marker, so between them these cover every form and every
            // way of being wrong.
            int offset = random.Next(bytes.Length - DecimalEncoding.MaxEncodedLength);

            TryDecode(() => DecimalEncoding.EncodedLength(bytes.AsSpan(offset)));
            TryDecode(() => VarInt.ReadVLong(bytes.AsSpan(offset), out _));
        }

        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(5),
            $"twenty thousand decodes took {elapsed.Elapsed}, which is not a bounded read");
    }

    [Fact]
    public void EveryFirstByteIsEitherAFormOrRefused()
    {
        // Exhaustive over the marker, which is small enough to be exhaustive about. Whatever the rest
        // of the bytes are, the first one either begins a form or it does not.
        byte[] bytes = new byte[DecimalEncoding.MaxEncodedLength];

        for (int marker = 0; marker <= 0xFF; marker++)
        {
            bytes[0] = (byte)marker;

            TryDecode(() => Assert.InRange(
                DecimalEncoding.EncodedLength(bytes), 1, DecimalEncoding.MaxEncodedLength));
        }
    }

    /// <summary>
    /// Runs a decode that is expected either to succeed or to fail in one of the ways the read path
    /// knows how to report.
    /// </summary>
    /// <remarks>
    /// <see cref="InvalidDataException"/> is malformed data and <see cref="InvalidOperationException"/>
    /// is a null read as a value, which is the one other thing these decoders say. Anything else —
    /// an index out of range, an overflow — is the decoder having been walked off the end of something,
    /// and is what these tests exist to catch.
    /// </remarks>
    private static void TryDecode(Action decode)
    {
        try
        {
            decode();
        }
        catch (InvalidDataException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>A buffer in which every byte claims another follows it.</summary>
    private static byte[] AllContinuations(int length) => [.. Enumerable.Repeat((byte)0xFF, length)];
}
