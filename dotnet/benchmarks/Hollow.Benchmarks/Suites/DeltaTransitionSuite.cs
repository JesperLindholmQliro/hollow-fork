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

using System.Globalization;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.Engine;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Benchmarks.Suites;

/// <summary>
/// The port of <c>HollowObjectTypeReadStateDeltaTransitionBenchmark</c>: what a read costs while
/// delta transitions are being applied underneath it, with and without the shard count changing.
/// </summary>
/// <remarks>
/// <para>
/// The question is what a read costs when the storage under it keeps being replaced: every
/// transition builds new data elements, and with resharding on, the shard a record lives in changes
/// too, so nothing stays in cache.
/// </para>
/// <para>
/// <strong>A lock is taken here that Java's version does not take.</strong> Applying a delta
/// releases the storage it replaced the moment the new storage is published —
/// <c>HollowObjectTypeReadState.ApplyDelta</c> calls <c>Destroy()</c> on the old data elements, and
/// Java's does the same — so a read that has already reached into the old elements and is part way
/// through them will fault. That is not a difference between the two ports and it is not what this
/// benchmark is trying to price; it is the reason Hollow has object longevity. Java's version
/// happens to survive it by reading only the first few hundred ordinals over and over. Rather than
/// rely on that, the read and the transition are fenced against each other with a
/// <see cref="ReaderWriterLockSlim"/>, which costs an uncontended read lock per operation — small
/// against a string read, but there.
/// </para>
/// </remarks>
internal sealed class DeltaTransitionSuite : BenchmarkSuite
{
    internal override string Name => "ObjectTypeReadStateDeltaTransition";

    internal override IEnumerable<BenchmarkCase> Cases(double scale)
    {
        int stored = Scaled(100_000, scale);
        int reads = Scaled(500, scale);
        int changesPerDelta = Scaled(2_000, scale);

        foreach (bool resharding in (bool[])[false, true])
        {
            foreach (int maxStringLength in (int[])[5, 100])
            {
                const int shardSizeKilobytes = 500;

                DeltaTransitionState state = new(
                    stored, reads, changesPerDelta, maxStringLength, shardSizeKilobytes, resharding);

                yield return new BenchmarkCase(
                    "ReadStringDuringDelta",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"resharding={resharding}, maxStringLength={maxStringLength}, stored={stored}"),
                    state.ReadOne,
                    Setup: state.Start,
                    TearDown: state.Stop);
            }
        }
    }

    /// <summary>
    /// The dataset, the refresh task that keeps changing it, and the read the benchmark times.
    /// </summary>
    private sealed class DeltaTransitionState(
        int stored,
        int reads,
        int changesPerDelta,
        int maxStringLength,
        int shardSizeKilobytes,
        bool resharding)
    {
        private readonly Random _readRandom = new(42);
        private readonly List<string> _pinned = [];
        private readonly HashSet<int> _pinnedKeys = [];
        private readonly ReaderWriterLockSlim _transition = new();

        private HollowWriteStateEngine _writeEngine = new();
        private HollowObjectMapper _mapper = null!;
        private HollowReadStateEngine _readEngine = new();
        private IHollowObjectTypeDataAccess _dataAccess = null!;
        private int[] _readOrder = [];

        private CancellationTokenSource? _stopping;
        private Task? _refreshing;

        internal void Start()
        {
            _writeEngine = new HollowWriteStateEngine
            {
                TargetMaxTypeShardSize = shardSizeKilobytes * 1000L,
            };

            _mapper = new HollowObjectMapper(_writeEngine);
            _mapper.InitializeTypeState(typeof(string));

            Random random = new(7);

            _readOrder = new int[reads];

            for (int i = 0; i < reads; i++)
            {
                _readOrder[i] = random.Next(stored);
            }

            _pinnedKeys.Clear();
            _pinnedKeys.UnionWith(_readOrder);
            _pinned.Clear();

            for (int i = 0; i < stored; i++)
            {
                string value = ObjectTypeReadStringSuite.RandomString(
                    random, i, maxStringLength, probabilityUnicode: 0);

                _mapper.Add(value);

                // The records the benchmark reads are never changed by the refresh task, so a read
                // that comes back with something unexpected is a bug rather than a race the benchmark
                // set up for itself.
                if (_pinnedKeys.Contains(i))
                {
                    _pinned.Add(value);
                }
            }

            _readEngine = new HollowReadStateEngine();
            RoundTripper.RoundTripSnapshot(_writeEngine, _readEngine);
            _dataAccess = (IHollowObjectTypeDataAccess)_readEngine.GetTypeDataAccess("String")!;

            _stopping = new CancellationTokenSource();
            _refreshing = Task.Run(() => Refresh(_stopping.Token), _stopping.Token);
        }

        internal void ReadOne()
        {
            int ordinal = _readOrder[_readRandom.Next(_readOrder.Length)];

            _transition.EnterReadLock();

            try
            {
                Blackhole.Consume(_dataAccess.ReadString(ordinal, 0));
            }
            finally
            {
                _transition.ExitReadLock();
            }
        }

        internal void Stop()
        {
            _stopping?.Cancel();

            try
            {
                _refreshing?.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // Asked for.
            }
            finally
            {
                _stopping?.Dispose();
                _stopping = null;
                _refreshing = null;
            }
        }

        /// <summary>
        /// Publishes a delta over and over until the benchmark is done, halving and restoring the
        /// shard size each time if resharding is on.
        /// </summary>
        private void Refresh(CancellationToken cancellationToken)
        {
            Random random = new(11);
            long originalShardSize = shardSizeKilobytes * 1000L;
            long shardSize = originalShardSize;

            while (!cancellationToken.IsCancellationRequested)
            {
                foreach (string value in _pinned)
                {
                    _mapper.Add(value);
                }

                for (int i = 0; i < changesPerDelta; i++)
                {
                    int key = random.Next(stored);

                    if (_pinnedKeys.Contains(key))
                    {
                        continue;
                    }

                    _mapper.Add(ObjectTypeReadStringSuite.RandomString(
                        random, key, maxStringLength, probabilityUnicode: 0));
                }

                if (resharding)
                {
                    shardSize = shardSize == originalShardSize ? originalShardSize / 10 : originalShardSize;
                    _writeEngine.TargetMaxTypeShardSize = shardSize;
                }

                _transition.EnterWriteLock();

                try
                {
                    RoundTripper.RoundTripDelta(_writeEngine, _readEngine);
                }
                finally
                {
                    _transition.ExitWriteLock();
                }
            }
        }
    }
}
