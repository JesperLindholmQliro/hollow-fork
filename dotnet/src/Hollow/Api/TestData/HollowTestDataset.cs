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

using Hollow.Api.Consumer;
using Hollow.Core.Read.Engine;
using Hollow.Core.Util;

using Hollow.Core.Write;

namespace Hollow.Api.TestData;

/// <summary>
/// A dataset described as records rather than built through a producer.
/// </summary>
/// <remarks>
/// <para>
/// Add the records a test needs and ask for a state engine, or for a consumer that will refresh to
/// them. Cycles work too: add more records and ask for a delta, and the consumer follows it.
/// </para>
/// <para>
/// Java's is abstract with nothing abstract on it, so a generated subclass exists only to hold the
/// typed builders. This one is usable as it stands.
/// </para>
/// </remarks>
public class HollowTestDataset
{
    private readonly HollowWriteStateEngine _writeEngine = new();
    private readonly List<IHollowTestRecord> _records = [];
    private readonly HollowTestBlobRetriever _blobs = new();

    private long _currentVersion;

    /// <summary>The blobs this dataset has produced, for a consumer to read.</summary>
    public IBlobRetriever BlobRetriever => _blobs;

    /// <summary>The version the last cycle produced.</summary>
    public long CurrentVersion => _currentVersion;

    /// <summary>Adds a record to the next cycle.</summary>
    public void Add(IHollowTestRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        _records.Add(record);
    }

    /// <summary>
    /// Builds a snapshot of everything added so far, as a read state engine.
    /// </summary>
    /// <remarks>Does not clear what was added, so a following delta can build on it.</remarks>
    public HollowReadStateEngine BuildSnapshot()
    {
        AddRecords();

        return StateEngineRoundTripper.RoundTripSnapshot(_writeEngine);
    }

    /// <summary>
    /// Builds a delta from what has been added since the last cycle, and applies it to
    /// <paramref name="readEngine"/>.
    /// </summary>
    public void BuildDelta(HollowReadStateEngine readEngine)
    {
        ArgumentNullException.ThrowIfNull(readEngine);

        AddRecords();

        StateEngineRoundTripper.RoundTripDelta(_writeEngine, readEngine);
    }

    /// <summary>
    /// Builds a snapshot and refreshes <paramref name="consumer"/> to it.
    /// </summary>
    public void BuildSnapshot(HollowConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        AddRecords();

        _blobs.AddSnapshot(new InMemoryBlob(_currentVersion, WriteBlob(writer => writer.WriteSnapshot)));

        _writeEngine.PrepareForNextCycle();

        consumer.TriggerRefreshTo(_currentVersion);
    }

    /// <summary>
    /// Builds a delta from what has been added since the last cycle and refreshes
    /// <paramref name="consumer"/> across it.
    /// </summary>
    public void BuildDelta(HollowConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        AddRecords();

        long nextVersion = _currentVersion + 1;

        _blobs.AddDelta(
            new InMemoryBlob(_currentVersion, nextVersion, WriteBlob(writer => writer.WriteDelta)));

        _writeEngine.PrepareForNextCycle();

        consumer.TriggerRefreshTo(nextVersion);

        _currentVersion = nextVersion;
    }

    /// <summary>
    /// Adds the records described since the last cycle, and forgets them.
    /// </summary>
    /// <remarks>
    /// Java keeps the list and re-adds every record on every cycle, so a dataset built over three
    /// cycles writes the first cycle's records three times. Re-adding a record is harmless — it takes
    /// the ordinal it already has — but it means a record cannot be <em>removed</em> by leaving it
    /// out of the next cycle, which is most of what a delta is for. Clearing the list makes each
    /// cycle say what it holds.
    /// </remarks>
    private void AddRecords()
    {
        foreach (IHollowTestRecord record in _records)
        {
            record.AddTo(_writeEngine);
        }

        _records.Clear();
    }

    private byte[] WriteBlob(Func<HollowBlobWriter, Action<Stream>> write)
    {
        _writeEngine.PrepareForWrite();

        using MemoryStream stream = new();
        write(new HollowBlobWriter(_writeEngine))(stream);

        return stream.ToArray();
    }
}

/// <summary>
/// A blob store that holds everything in memory, for a test.
/// </summary>
/// <remarks>
/// Java's is package-private, so a test outside <c>api.testdata</c> that wants an in-memory blob
/// store writes its own. There is nothing private about it.
/// </remarks>
public sealed class HollowTestBlobRetriever : IBlobRetriever
{
    private readonly Dictionary<long, Blob> _snapshots = [];
    private readonly Dictionary<long, Blob> _deltas = [];
    private readonly Dictionary<long, Blob> _reverseDeltas = [];
    private readonly Dictionary<long, HeaderBlob> _headers = [];

    /// <inheritdoc />
    /// <remarks>
    /// Java returns only an exact match, so a consumer asked to refresh to a version between two
    /// snapshots finds nothing. The contract is the greatest snapshot at or below the version asked
    /// for, and that is what this answers.
    /// </remarks>
    public Blob? RetrieveSnapshotBlob(long desiredVersion)
    {
        if (_snapshots.TryGetValue(desiredVersion, out Blob? exact))
        {
            return exact;
        }

        return _snapshots
            .Where(snapshot => snapshot.Key <= desiredVersion)
            .OrderByDescending(snapshot => snapshot.Key)
            .Select(snapshot => snapshot.Value)
            .FirstOrDefault();
    }

    /// <inheritdoc />
    public Blob? RetrieveDeltaBlob(long currentVersion) => _deltas.GetValueOrDefault(currentVersion);

    /// <inheritdoc />
    public Blob? RetrieveReverseDeltaBlob(long currentVersion) =>
        _reverseDeltas.GetValueOrDefault(currentVersion);

    /// <inheritdoc />
    public HeaderBlob? RetrieveHeaderBlob(long currentVersion) =>
        _headers.GetValueOrDefault(currentVersion);

    /// <summary>Adds a snapshot, reachable at its own version.</summary>
    public void AddSnapshot(Blob snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _snapshots[snapshot.ToVersion] = snapshot;
    }

    /// <summary>Adds a delta, reachable from the version it starts at.</summary>
    public void AddDelta(Blob delta)
    {
        ArgumentNullException.ThrowIfNull(delta);

        _deltas[delta.FromVersion] = delta;
    }

    /// <summary>Adds a reverse delta, reachable from the version it starts at.</summary>
    public void AddReverseDelta(Blob reverseDelta)
    {
        ArgumentNullException.ThrowIfNull(reverseDelta);

        _reverseDeltas[reverseDelta.FromVersion] = reverseDelta;
    }

    /// <summary>Adds a header blob.</summary>
    public void AddHeader(HeaderBlob header)
    {
        ArgumentNullException.ThrowIfNull(header);

        _headers[header.Version] = header;
    }
}

/// <summary>A blob whose bytes are held in memory.</summary>
public sealed class InMemoryBlob : Blob
{
    private readonly byte[] _data;

    /// <summary>A snapshot of <paramref name="version"/>.</summary>
    public InMemoryBlob(long version, byte[] data)
        : base(version) =>
        _data = data ?? throw new ArgumentNullException(nameof(data));

    /// <summary>A delta, or reverse delta, between two versions.</summary>
    public InMemoryBlob(long fromVersion, long toVersion, byte[] data)
        : base(fromVersion, toVersion) =>
        _data = data ?? throw new ArgumentNullException(nameof(data));

    /// <inheritdoc />
    public override Stream OpenStream() => new MemoryStream(_data, writable: false);
}

/// <summary>A header blob whose bytes are held in memory.</summary>
public sealed class InMemoryHeaderBlob(long version, byte[] data) : HeaderBlob(version)
{
    private readonly byte[] _data = data ?? throw new ArgumentNullException(nameof(data));

    /// <inheritdoc />
    public override Stream OpenStream() => new MemoryStream(_data, writable: false);
}
