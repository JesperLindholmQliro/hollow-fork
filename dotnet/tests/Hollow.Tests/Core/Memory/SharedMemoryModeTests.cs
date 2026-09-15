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

using Hollow.Api.Objects;
using Hollow.Api.Objects.Generic;
using Hollow.Core.Memory;
using Hollow.Core.Memory.Encoding;
using Hollow.Core.Memory.Pool;
using Hollow.Core.Read;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Filter;
using Hollow.Core.Tools.Checksum;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Tests.Core.Memory;

/// <summary>
/// Reading a snapshot out of a mapped file rather than onto the heap.
/// </summary>
/// <remarks>
/// <para>
/// The claim the mode makes is that nothing about the data changes — only where it lives — so most of
/// these tests read the same blob both ways and insist on the same answer. That is a stronger check
/// than it looks: a bit string is stored as big-endian words while its own bit numbering runs the
/// other way, so any slip in the byte order shows up as records that read as nonsense.
/// </para>
/// <para>
/// Java tests this in <c>HollowBlobInputTest</c> and in shared-memory variants of a few existing read
/// tests. These are written afresh, around what the mode promises.
/// </para>
/// </remarks>
public class SharedMemoryModeTests : IDisposable
{
    private readonly string _blobPath =
        Path.Combine(Path.GetTempPath(), $"hollow-shared-memory-{Guid.NewGuid():N}.blob");

    /// <inheritdoc />
    public void Dispose()
    {
        GC.SuppressFinalize(this);

        try
        {
            File.Delete(_blobPath);
        }
        catch (IOException)
        {
            // Windows keeps a mapped file locked until every view is collected; a stray temp file is
            // not worth failing a test over.
        }
    }

    [Fact]
    public void AMappedStateHoldsTheSameRecordsAsAnOnHeapOne()
    {
        WriteBlob();

        HollowReadStateEngine onHeap = ReadOnHeap();
        HollowReadStateEngine mapped = ReadMapped();

        // Every record of every type, compared field by field in both directions.
        Assert.Equal(
            HollowChecksum.ForStateEngineWithCommonSchemas(onHeap, mapped),
            HollowChecksum.ForStateEngineWithCommonSchemas(mapped, onHeap));
    }

    [Fact]
    public void EveryFieldTypeReadsBackMapped()
    {
        WriteBlob();

        GenericHollowObject movie = FindMovie(ReadMapped(), 2);

        Assert.Equal(2, movie.GetInt("Id"));
        Assert.Equal(1962L, movie.GetLong("Year"));
        Assert.Equal("Movie 2", movie.GetObject("Title")!.GetString("value"));
        Assert.True(movie.GetBoolean("InColour"));
        Assert.Equal(2.5f, movie.GetFloat("Rating"));
        Assert.Equal(20.25d, movie.GetDouble("BoxOffice"));

        // The mapper maps a string and a byte array alike as a reference to a wrapper type whose one
        // field is the lowercase "value".
        Assert.Equal([2, 4, 6], movie.GetObject("Poster")!.GetBytes("value"));
    }

    [Fact]
    public void CollectionsReadBackMapped()
    {
        WriteBlob();

        GenericHollowObject movie = FindMovie(ReadMapped(), 1);

        Assert.Equal(
            ["Ada", "Grace"],
            movie.GetList("Cast")!.Select(Name).Order(StringComparer.Ordinal));

        Assert.Equal(
            ["drama", "silent"],
            movie.GetSet("Tags")!.Select(tag => ((GenericHollowObject)tag).GetString("value")!)
                .Order(StringComparer.Ordinal));

        GenericHollowMap awards = movie.GetMap("Awards")!;

        Assert.Single(awards);
        Assert.Equal("Oscar", ((GenericHollowObject)awards.Keys.Single()).GetString("value"));
        Assert.Equal(1, ((GenericHollowObject)awards.Values.Single()).GetInt("value"));
    }

    [Fact]
    public void ALongStringSpansMoreThanOneWordAndStillReadsBackMapped()
    {
        WriteBlob();

        // Variable-length data is a plain byte stream rather than a run of words, so it is the one
        // part of the blob that must not be byte-swapped. A long value makes that visible.
        Assert.Equal(
            new string('x', 5000), FindMovie(ReadMapped(), 3).GetObject("Title")!.GetString("value"));
    }

    [Fact]
    public void AShardedTypeReadsBackMapped()
    {
        HollowWriteStateEngine writeEngine = new()
        {
            // Small enough that the records land across several shards rather than one.
            TargetMaxTypeShardSize = 256,
        };

        HollowObjectMapper mapper = new(writeEngine);

        for (int i = 0; i < 200; i++)
        {
            mapper.Add(new Sharded(i, $"Record {i}"));
        }

        WriteBlob(writeEngine);

        HollowReadStateEngine mapped = ReadMapped();

        Assert.True(mapped.GetTypeState("Sharded")!.NumShards > 1);

        // Each shard is its own bit string at its own offset in the file, so sharding is where an
        // offset that was only ever right for the first shard would show up.
        Assert.Equal(
            HollowChecksum.ForStateEngineWithCommonSchemas(ReadOnHeap(), mapped),
            HollowChecksum.ForStateEngineWithCommonSchemas(mapped, ReadOnHeap()));
    }

    [Fact]
    public void ADeltaCannotBeAppliedToAMappedState()
    {
        WriteBlob();

        HollowReadStateEngine mapped = ReadMapped();

        // The records are in the file, and the file is read-only. A consumer in this mode follows the
        // chain by mapping the next snapshot instead.
        Assert.Throws<NotSupportedException>(
            () => new HollowBlobReader(mapped).ApplyDelta(HollowBlobInput.Serial([])));
    }

    [Fact]
    public void AFilterCannotBeAppliedToAMappedState()
    {
        WriteBlob();

        HollowReadStateEngine mapped = new(MemoryMode.SharedMemoryLazy);
        using HollowBlobInput input = HollowBlobInput.Mapped(_blobPath);

        ITypeFilter filter = TypeFilter.Include(["Movie"]);

        // Filtering rewrites each record's layout as it is read, and a mapped record is never rewritten.
        Assert.Throws<NotSupportedException>(
            () => new HollowBlobReader(mapped).ReadSnapshot(input, filter));
    }

    [Fact]
    public void TheInputAndTheStateEngineHaveToAgreeOnTheMode()
    {
        WriteBlob();

        using FileStream stream = File.OpenRead(_blobPath);

        HollowReadStateEngine mapped = new(MemoryMode.SharedMemoryLazy);

        // A state engine in shared-memory mode builds data elements that read through a mapping, and
        // there is no mapping behind a serial input.
        Assert.Throws<ArgumentException>(
            () => new HollowBlobReader(mapped).ReadSnapshot(
                HollowBlobInput.Serial(stream, leaveOpen: true)));
    }

    [Fact]
    public void AnEmptyFileCannotBeMapped()
    {
        File.WriteAllBytes(_blobPath, []);

        Assert.Throws<InvalidOperationException>(() => MemoryMappedBlob.Map(_blobPath));
    }

    [Fact]
    public void AWordIsReadBackInTheBitStringsOwnOrder()
    {
        // What a bit string of one word looks like in a blob: the word written big-endian, as
        // java.io.DataOutput writes it.
        File.WriteAllBytes(_blobPath, [0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF]);

        using MemoryMappedBlob blob = MemoryMappedBlob.Map(_blobPath);

        Assert.Equal(0x0123456789ABCDEFL, blob.GetWord(0, 0));
    }

    [Fact]
    public void AWordThatRunsPastTheEndIsPaddedWithZeroes()
    {
        File.WriteAllBytes(_blobPath, [0x01, 0x23, 0x45]);

        using MemoryMappedBlob blob = MemoryMappedBlob.Map(_blobPath);

        // The last element of a bit string is reached through a 64-bit window that may run off the end.
        // The bits beyond it are shifted away by the caller, so reading them as zero is enough.
        Assert.Equal(0x0123450000000000L, blob.GetWord(0, 0));
    }

    [Fact]
    public void AMappedBitStringCannotBeWritten()
    {
        File.WriteAllBytes(_blobPath, [0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF]);

        using HollowBlobInput input = HollowBlobInput.Mapped(_blobPath);

        IFixedLengthData mapped = FixedLengthDataFactory.Get(
            input, MemoryMode.SharedMemoryLazy, WastefulRecycler.DefaultInstance, numLongs: 1);

        // The mutating half of the interface is there for the write path and for delta application,
        // neither of which can touch a file this process only ever reads.
        Assert.IsType<EncodedLongBuffer>(mapped);
        Assert.Throws<NotSupportedException>(() => mapped.SetElementValue(0, 8, 1));
    }

    [Fact]
    public void MappedRecordDataCannotBeWritten()
    {
        IVariableLengthData mapped =
            VariableLengthDataFactory.Get(MemoryMode.SharedMemoryLazy, WastefulRecycler.DefaultInstance);

        Assert.IsType<EncodedByteBuffer>(mapped);
        Assert.Throws<NotSupportedException>(() => mapped.Copy(mapped, 0, 0, 1));
    }

    /// <summary>The movie with <paramref name="id"/>.</summary>
    private static GenericHollowObject FindMovie(HollowReadStateEngine stateEngine, int id)
    {
        foreach (int ordinal in stateEngine.GetTypeState("Movie")!.PopulatedOrdinals.EnumerateSetBits())
        {
            GenericHollowObject movie = new(stateEngine, "Movie", ordinal);

            if (movie.GetInt("Id") == id)
            {
                return movie;
            }
        }

        throw new InvalidOperationException($"no movie with id {id}");
    }

    private static string Name(IHollowRecord actor) =>
        ((GenericHollowObject)actor).GetObject("Name")!.GetString("value")!;

    private HollowReadStateEngine ReadOnHeap()
    {
        HollowReadStateEngine readEngine = new();

        using FileStream stream = File.OpenRead(_blobPath);
        new HollowBlobReader(readEngine).ReadSnapshot(stream);

        return readEngine;
    }

    private HollowReadStateEngine ReadMapped()
    {
        HollowReadStateEngine readEngine = new(MemoryMode.SharedMemoryLazy);

        using HollowBlobInput input = HollowBlobInput.Mapped(_blobPath);
        new HollowBlobReader(readEngine).ReadSnapshot(input);

        return readEngine;
    }

    /// <summary>Writes a snapshot of the sample dataset to <see cref="_blobPath"/>.</summary>
    private void WriteBlob()
    {
        HollowWriteStateEngine writeEngine = new();
        HollowObjectMapper mapper = new(writeEngine);

        Actor ada = new("Ada");
        Actor grace = new("Grace");

        mapper.Add(new Movie(
            1, 1961, "Movie 1", false, 1.5f, 10.25d, [1, 2, 3], [ada, grace], ["silent", "drama"],
            new Dictionary<string, int> { ["Oscar"] = 1 }));

        mapper.Add(new Movie(
            2, 1962, "Movie 2", true, 2.5f, 20.25d, [2, 4, 6], [ada], ["colour"],
            new Dictionary<string, int> { ["Palme"] = 2, ["Bear"] = 3 }));

        mapper.Add(new Movie(
            3, 1963, new string('x', 5000), true, 3.5f, 30.25d, [], [grace], [],
            new Dictionary<string, int>()));

        WriteBlob(writeEngine);
    }

    private void WriteBlob(HollowWriteStateEngine writeEngine)
    {
        using FileStream stream = File.Create(_blobPath);
        new HollowBlobWriter(writeEngine).WriteSnapshot(stream);
    }

    private sealed record Movie(
        int Id,
        long Year,
        string Title,
        bool InColour,
        float Rating,
        double BoxOffice,
        byte[] Poster,
        List<Actor> Cast,
        HashSet<string> Tags,
        Dictionary<string, int> Awards);

    private sealed record Actor(string Name);

    private sealed record Sharded(int Id, string Name);
}
