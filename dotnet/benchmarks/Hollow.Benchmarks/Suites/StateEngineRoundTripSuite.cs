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
using Hollow.Core.Read.Engine;
using Hollow.Core.Schema;
using Hollow.Core.Write;

namespace Hollow.Benchmarks.Suites;

/// <summary>
/// The port of <c>ReadWriteStateEngineTest</c>: writing a snapshot and reading it back, through each
/// of the things a blob can travel over.
/// </summary>
/// <remarks>
/// The question is whether a producer should stage a blob in memory, on disk, or stream it straight
/// into a reader. Java answers it with four cases and this port has the same four, over
/// <see cref="InProcessPipe"/> where Java uses <c>PipedInputStream</c> — which .NET has nothing
/// equivalent to.
/// </remarks>
internal sealed class StateEngineRoundTripSuite : BenchmarkSuite
{
    internal override string Name => "ReadWriteStateEngine";

    internal override IEnumerable<BenchmarkCase> Cases(double scale)
    {
        int records = Scaled(1_000_000, scale);

        HollowWriteStateEngine writeEngine = new();

        HollowObjectSchema schema = new("TestObject", 2);
        schema.AddField("f1", FieldType.Int);
        schema.AddField("f2", FieldType.String);

        writeEngine.AddTypeState(new HollowObjectTypeWriteState(schema));

        for (int i = 0; i < records; i++)
        {
            HollowObjectWriteRecord record = new(schema);
            record.SetInt("f1", 1);
            record.SetString("f2", i.ToString(CultureInfo.InvariantCulture));
            writeEngine.Add("TestObject", record);
        }

        string parameters = string.Create(CultureInfo.InvariantCulture, $"n={records}");

        yield return new BenchmarkCase(
            "RoundTripMemory", parameters, () => Blackhole.Consume(RoundTripMemory(writeEngine)));

        yield return new BenchmarkCase(
            "RoundTripFile", parameters, () => Blackhole.Consume(RoundTripFile(writeEngine)));

        yield return new BenchmarkCase(
            "RoundTripPipe",
            parameters,
            () => Blackhole.Consume(RoundTripPipe(writeEngine, bufferSize: 1 << 15)));

        yield return new BenchmarkCase(
            "RoundTripPipeBuffered",
            parameters,
            () => Blackhole.Consume(RoundTripPipe(writeEngine, bufferSize: 1 << 20)));
    }

    private static HollowReadStateEngine RoundTripMemory(HollowWriteStateEngine writeEngine)
    {
        using MemoryStream blob = new();
        new HollowBlobWriter(writeEngine).WriteSnapshot(blob);

        blob.Position = 0;

        HollowReadStateEngine readEngine = new();
        new HollowBlobReader(readEngine).ReadSnapshot(blob);

        return readEngine;
    }

    private static HollowReadStateEngine RoundTripFile(HollowWriteStateEngine writeEngine)
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        try
        {
            using (FileStream blob = File.Create(path))
            {
                new HollowBlobWriter(writeEngine).WriteSnapshot(blob);
            }

            using FileStream reading = File.OpenRead(path);

            HollowReadStateEngine readEngine = new();
            new HollowBlobReader(readEngine).ReadSnapshot(reading);

            return readEngine;
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Writes and reads at the same time, so neither the whole blob nor a temporary file is ever
    /// held.
    /// </summary>
    /// <remarks>
    /// The writer runs on a task and says when it has finished, which is what tells the reader the
    /// blob has ended. The buffer size is what the two cases differ in: a small one makes the two
    /// threads hand off constantly, a large one lets the writer get ahead.
    /// </remarks>
    private static HollowReadStateEngine RoundTripPipe(HollowWriteStateEngine writeEngine, int bufferSize)
    {
        InProcessPipe pipe = new(bufferSize);

        Task writer = Task.Run(() =>
        {
            try
            {
                new HollowBlobWriter(writeEngine).WriteSnapshot(pipe.Writing);
            }
            finally
            {
                pipe.CompleteWriting();
            }
        });

        HollowReadStateEngine readEngine = new();

        try
        {
            new HollowBlobReader(readEngine).ReadSnapshot(pipe.Reading);
        }
        finally
        {
            // Waited for rather than abandoned: an exception on the writing side is the explanation
            // for whatever the reading side just complained about.
            writer.GetAwaiter().GetResult();
        }

        return readEngine;
    }
}
