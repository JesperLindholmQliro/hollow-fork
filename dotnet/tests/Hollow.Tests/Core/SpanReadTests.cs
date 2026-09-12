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
using Hollow.Api.Objects.Generic;
using Hollow.Core.Memory;
using Hollow.Core.Memory.Pool;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Schema;
using Hollow.Core.Write;

namespace Hollow.Tests.Core;

/// <summary>
/// Reading strings and byte arrays without allocating one.
/// </summary>
/// <remarks>
/// <para>
/// Two different things are going on. A byte field is stored raw, so it can often be handed back as a
/// span over the blob itself and copied nowhere — except where the value straddles a storage segment
/// boundary, which has no contiguous view. A string field cannot: Hollow stores a character as a
/// variable-length integer, so the stored bytes are not characters and always have to be decoded into
/// a buffer the caller owns.
/// </para>
/// <para>
/// What these tests pin is that the allocation-free reads agree with the allocating ones in every
/// case, and that null stays distinguishable from empty.
/// </para>
/// </remarks>
public class SpanReadTests
{
    private static HollowObjectSchema ValueSchema()
    {
        HollowObjectSchema schema = new("Value", 3);
        schema.AddField("id", FieldType.Int);
        schema.AddField("text", FieldType.String);
        schema.AddField("blob", FieldType.Bytes);

        return schema;
    }

    private static HollowObjectTypeReadState Read(params (int Id, string? Text, byte[]? Blob)[] values)
    {
        HollowObjectSchema schema = ValueSchema();
        HollowWriteStateEngine engine = new();
        engine.AddTypeState(new HollowObjectTypeWriteState(schema));

        HollowObjectWriteRecord record = new(schema);
        foreach ((int id, string? text, byte[]? blob) in values)
        {
            record.Reset();
            record.SetInt("id", id);

            if (text is not null)
            {
                record.SetString("text", text);
            }

            if (blob is not null)
            {
                record.SetBytes("blob", blob);
            }

            engine.Add("Value", record);
        }

        return (HollowObjectTypeReadState)
            StateEngineRoundTripper.RoundTripSnapshot(engine).GetTypeState("Value")!;
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("The Matrix")]
    [InlineData("a string comfortably longer than one storage segment, so that it has to straddle one")]
    [InlineData("non-ASCII: 龍爭虎鬥 — em dash, ünlaut, é")]
    public void AStringReadIntoASpanMatchesTheAllocatingRead(string text)
    {
        HollowObjectTypeReadState values = Read((1, text, null));
        int textField = values.Schema.GetPosition("text");

        int byteLength = values.VarLengthFieldByteLength(0, textField);
        Assert.True(byteLength >= 0);

        // The byte length is an upper bound on the character count, so it always sizes the buffer.
        char[] buffer = new char[byteLength];
        int written = values.ReadStringInto(0, textField, buffer);

        Assert.Equal(text.Length, written);
        Assert.Equal(text, new string(buffer.AsSpan(0, written)));
        Assert.Equal(values.ReadString(0, textField), new string(buffer.AsSpan(0, written)));
    }

    [Fact]
    public void ANullStringIsDistinguishableFromAnEmptyOne()
    {
        HollowObjectTypeReadState values = Read((1, null, null), (2, string.Empty, null));
        int textField = values.Schema.GetPosition("text");

        char[] buffer = new char[16];

        // Null: nothing stored at all.
        Assert.Equal(-1, values.VarLengthFieldByteLength(0, textField));
        Assert.Equal(-1, values.ReadStringInto(0, textField, buffer));
        Assert.Null(values.ReadString(0, textField));

        // Empty: stored, with nothing in it.
        Assert.Equal(0, values.VarLengthFieldByteLength(1, textField));
        Assert.Equal(0, values.ReadStringInto(1, textField, buffer));
        Assert.Equal(string.Empty, values.ReadString(1, textField));
    }

    [Fact]
    public void ABufferTooSmallForTheStoredStringIsRefused()
    {
        HollowObjectTypeReadState values = Read((1, "The Matrix", null));
        int textField = values.Schema.GetPosition("text");

        Assert.Throws<ArgumentException>(() =>
        {
            char[] tooSmall = new char[3];
            values.ReadStringInto(0, textField, tooSmall);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(4096)]
    public void BytesReadIntoASpanMatchTheAllocatingRead(int length)
    {
        byte[] blob = [.. Enumerable.Range(0, length).Select(i => (byte)(i * 31))];

        HollowObjectTypeReadState values = Read((1, null, blob));
        int blobField = values.Schema.GetPosition("blob");

        Assert.Equal(length, values.VarLengthFieldByteLength(0, blobField));

        byte[] buffer = new byte[length];
        Assert.Equal(length, values.ReadBytesInto(0, blobField, buffer));
        Assert.Equal(blob, buffer);
        Assert.Equal(values.ReadBytes(0, blobField), buffer);
    }

    [Fact]
    public void ANullByteArrayIsDistinguishableFromAnEmptyOne()
    {
        HollowObjectTypeReadState values = Read((1, null, null), (2, null, []));
        int blobField = values.Schema.GetPosition("blob");

        byte[] buffer = new byte[16];

        Assert.Equal(-1, values.VarLengthFieldByteLength(0, blobField));
        Assert.Equal(-1, values.ReadBytesInto(0, blobField, buffer));
        Assert.Null(values.ReadBytes(0, blobField));

        Assert.Equal(0, values.VarLengthFieldByteLength(1, blobField));
        Assert.Equal(0, values.ReadBytesInto(1, blobField, buffer));
        Assert.Empty(values.ReadBytes(1, blobField)!);
    }

    /// <summary>
    /// The fast path: a byte field that sits inside one storage segment is handed back as a view over
    /// the blob, copying nothing.
    /// </summary>
    [Fact]
    public void AByteFieldIsViewedInPlaceWhereTheStorageAllows()
    {
        byte[] blob = [1, 2, 3, 4, 5];

        HollowObjectTypeReadState values = Read((1, null, blob));
        int blobField = values.Schema.GetPosition("blob");

        Assert.True(values.TryGetBytesSpan(0, blobField, out ReadOnlySpan<byte> inPlace));
        Assert.True(inPlace.SequenceEqual(blob));
    }

    [Fact]
    public void ANullByteFieldHasNoInPlaceView()
    {
        HollowObjectTypeReadState values = Read((1, null, null));

        Assert.False(
            values.TryGetBytesSpan(0, values.Schema.GetPosition("blob"), out ReadOnlySpan<byte> _));
    }

    /// <summary>
    /// Whether a value has a contiguous view is an artefact of where it landed, not of the data, so a
    /// caller that must not allocate needs the copying path to work wherever the view does not.
    /// </summary>
    [Fact]
    public void AValueStraddlingASegmentBoundaryFallsBackToCopying()
    {
        // Segments of 32 bytes, so a value of 100 bytes has to cross at least two boundaries.
        SegmentedByteArray data = new(WastefulRecycler.SmallArrayRecycler);

        byte[] blob = [.. Enumerable.Range(0, 100).Select(i => (byte)i)];
        for (int i = 0; i < blob.Length; i++)
        {
            data.Set(i, blob[i]);
        }

        IByteData byteData = data;

        // No contiguous view across the boundary...
        Assert.False(byteData.TryGetSpan(0, blob.Length, out ReadOnlySpan<byte> _));

        // ...but a range wholly inside one segment does have one.
        Assert.True(byteData.TryGetSpan(0, 16, out ReadOnlySpan<byte> withinSegment));
        Assert.True(withinSegment.SequenceEqual(blob.AsSpan(0, 16)));

        // And copying works either way.
        byte[] copied = new byte[blob.Length];
        byteData.CopyTo(0, copied);
        Assert.Equal(blob, copied);
    }

    [Fact]
    public void AFlatArrayAlwaysHasAnInPlaceView()
    {
        byte[] backing = [9, 8, 7, 6, 5];
        IByteData data = new ArrayByteData(backing);

        Assert.True(data.TryGetSpan(1, 3, out ReadOnlySpan<byte> view));
        Assert.True(view.SequenceEqual(backing.AsSpan(1, 3)));

        // Past the end there is nothing to view.
        Assert.False(data.TryGetSpan(3, 99, out ReadOnlySpan<byte> _));

        byte[] copied = new byte[2];
        data.CopyTo(3, copied);
        Assert.Equal<byte[]>([6, 5], copied);
    }

    /// <summary>
    /// The same reads through the record wrapper, which is where a caller actually meets them.
    /// </summary>
    [Fact]
    public void ARecordWrapperReadsWithoutAllocating()
    {
        byte[] blob = [1, 2, 3];
        HollowReadStateEngine engine = ReadEngine((1, "The Matrix", blob));

        GenericHollowObject value = new(engine, "Value", 0);

        Span<char> textBuffer = stackalloc char[value.GetVarLengthByteLength("text")];
        ReadOnlySpan<char> text = value.GetString("text", textBuffer);
        Assert.True(text.SequenceEqual("The Matrix"));

        Assert.True(value.TryGetBytes("blob", out ReadOnlySpan<byte> bytes));
        Assert.True(bytes.SequenceEqual(blob));

        Span<byte> blobBuffer = stackalloc byte[value.GetVarLengthByteLength("blob")];
        Assert.True(value.GetBytes("blob", blobBuffer).SequenceEqual(blob));
    }

    /// <summary>
    /// A field the loaded dataset does not have reads as absent through the span path too, rather than
    /// throwing where the allocating path would have returned null.
    /// </summary>
    [Fact]
    public void AMissingFieldReadsAsAbsentThroughTheSpanPath()
    {
        HollowReadStateEngine engine = ReadEngine((1, "The Matrix", null));

        GenericHollowObject value = new(engine, "Value", 0);

        Assert.Equal(-1, value.GetVarLengthByteLength("noSuchField"));

        Span<char> buffer = stackalloc char[8];
        Assert.Equal(-1, value.ReadStringInto("noSuchField", buffer));
        Assert.True(value.GetString("noSuchField", buffer).IsEmpty);

        Span<byte> bytes = stackalloc byte[8];
        Assert.Equal(-1, value.ReadBytesInto("noSuchField", bytes));
        Assert.False(value.TryGetBytes("noSuchField", out ReadOnlySpan<byte> _));
    }

    /// <summary>
    /// The sequence path is the span path without the failure case: a value that straddles a segment
    /// boundary comes back in pieces rather than not at all, and still copies nothing.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(4096)]
    [InlineData(100_000)]
    public void ABytesFieldReadsAsASequenceWhateverItsLength(int length)
    {
        byte[] blob = [.. Enumerable.Range(0, length).Select(i => (byte)(i * 31))];

        HollowObjectTypeReadState values = Read((1, null, blob));
        int blobField = values.Schema.GetPosition("blob");

        ReadOnlySequence<byte> sequence = values.GetVarLengthSequence(0, blobField);

        Assert.Equal(length, sequence.Length);
        Assert.Equal(blob, sequence.ToArray());
    }

    [Fact]
    public void AnEmptyOrNullBytesFieldReadsAsAnEmptySequence()
    {
        HollowObjectTypeReadState values = Read((1, null, null), (2, null, []));
        int blobField = values.Schema.GetPosition("blob");

        Assert.True(values.GetVarLengthSequence(0, blobField).IsEmpty);
        Assert.True(values.GetVarLengthSequence(1, blobField).IsEmpty);
    }

    /// <summary>
    /// The case the sequence exists for: storage segments small enough that a value has to cross
    /// several, so there is no contiguous view but there is a perfectly good discontiguous one.
    /// </summary>
    [Fact]
    public void ASequenceSpansSegmentBoundariesWithoutCopying()
    {
        // Segments of 32 bytes, so 100 bytes covers four of them.
        SegmentedByteArray data = new(WastefulRecycler.SmallArrayRecycler);

        byte[] expected = [.. Enumerable.Range(0, 100).Select(i => (byte)i)];
        for (int i = 0; i < expected.Length; i++)
        {
            data.Set(i, expected[i]);
        }

        IByteData byteData = data;

        Assert.False(byteData.TryGetSpan(0, expected.Length, out ReadOnlySpan<byte> _));

        ReadOnlySequence<byte> sequence = byteData.GetSequence(0, expected.Length);

        Assert.Equal(expected.Length, sequence.Length);
        Assert.False(sequence.IsSingleSegment);
        Assert.Equal(expected, sequence.ToArray());

        // A SequenceReader walks it without the caller minding the boundaries.
        SequenceReader<byte> reader = new(sequence);
        Assert.True(reader.TryAdvanceTo(50, advancePastDelimiter: false));
        Assert.True(reader.TryPeek(out byte atFifty));
        Assert.Equal(50, atFifty);
    }

    [Fact]
    public void ASequenceWithinOneSegmentIsASingleSegment()
    {
        SegmentedByteArray data = new(WastefulRecycler.SmallArrayRecycler);

        for (int i = 0; i < 16; i++)
        {
            data.Set(i, (byte)i);
        }

        ReadOnlySequence<byte> sequence = ((IByteData)data).GetSequence(0, 16);

        Assert.True(sequence.IsSingleSegment);
        Assert.Equal(16, sequence.Length);
    }

    [Fact]
    public void AFlatArrayReadsAsOneSequenceSegment()
    {
        byte[] backing = [9, 8, 7, 6, 5];

        ReadOnlySequence<byte> sequence = ((IByteData)new ArrayByteData(backing)).GetSequence(1, 3);

        Assert.True(sequence.IsSingleSegment);
        Assert.Equal<byte[]>([8, 7, 6], sequence.ToArray());
    }

    /// <summary>
    /// A string field's stored bytes are available as a sequence too, but they are the encoded form —
    /// a character is a variable-length integer — so they are for hashing or copying, not for reading
    /// text.
    /// </summary>
    [Fact]
    public void AStringFieldExposesItsEncodedBytesAsASequence()
    {
        HollowReadStateEngine engine = ReadEngine((1, "The Matrix", null));
        GenericHollowObject value = new(engine, "Value", 0);

        ReadOnlySequence<byte> encoded = value.GetStringBytesSequence("text");

        // ASCII encodes one byte per character, so here the lengths happen to agree.
        Assert.Equal(value.GetVarLengthByteLength("text"), encoded.Length);
        Assert.Equal("The Matrix"u8.ToArray(), encoded.ToArray());
    }

    [Fact]
    public void ARecordWrapperReadsASequence()
    {
        byte[] blob = [.. Enumerable.Range(0, 5000).Select(i => (byte)i)];

        HollowReadStateEngine engine = ReadEngine((1, null, blob));
        GenericHollowObject value = new(engine, "Value", 0);

        ReadOnlySequence<byte> sequence = value.GetBytesSequence("blob");

        Assert.Equal(blob.Length, sequence.Length);
        Assert.Equal(blob, sequence.ToArray());

        // Walking it piece by piece never copies the value.
        long walked = 0;
        foreach (ReadOnlyMemory<byte> piece in sequence)
        {
            walked += piece.Length;
        }

        Assert.Equal(blob.Length, walked);
    }

    [Fact]
    public void AMissingFieldReadsAsAnEmptySequence()
    {
        HollowReadStateEngine engine = ReadEngine((1, "The Matrix", null));
        GenericHollowObject value = new(engine, "Value", 0);

        Assert.True(value.GetBytesSequence("noSuchField").IsEmpty);
    }

    private static HollowReadStateEngine ReadEngine(params (int Id, string? Text, byte[]? Blob)[] values)
    {
        HollowObjectSchema schema = ValueSchema();
        HollowWriteStateEngine engine = new();
        engine.AddTypeState(new HollowObjectTypeWriteState(schema));

        HollowObjectWriteRecord record = new(schema);
        foreach ((int id, string? text, byte[]? blob) in values)
        {
            record.Reset();
            record.SetInt("id", id);

            if (text is not null)
            {
                record.SetString("text", text);
            }

            if (blob is not null)
            {
                record.SetBytes("blob", blob);
            }

            engine.Add("Value", record);
        }

        return StateEngineRoundTripper.RoundTripSnapshot(engine);
    }
}
