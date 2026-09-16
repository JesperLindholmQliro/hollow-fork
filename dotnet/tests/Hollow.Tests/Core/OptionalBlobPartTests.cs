/*
 *  Copyright 2021 Netflix, Inc.
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

using Hollow.Api.Producer;
using Hollow.Core.Read;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Read.Filter;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Tests.Core;

/// <summary>
/// Splitting a blob so a consumer can skip the parts it does not need.
/// </summary>
/// <remarks>
/// The point of a part is that a consumer can leave it on the shelf, so the tests that matter are the
/// ones that read the main blob <em>without</em> a part and find a working dataset that simply lacks
/// those types — and the one that refuses a part belonging to a different state, because reading that
/// would give records at ordinals meaning something else entirely.
/// </remarks>
public class OptionalBlobPartTests
{
    [Fact]
    public void ATypeInAPartIsNotInTheMainBlob()
    {
        Written written = Write();

        HollowReadStateEngine withoutParts = ReadSnapshot(written, parts: null);

        Assert.NotNull(withoutParts.GetTypeState("Movie"));
        Assert.Null(withoutParts.GetTypeState("Review"));

        // And the part is a fraction of the whole, which is the reason to split at all.
        Assert.True(written.Parts["reviews"].Length > 0);
    }

    [Fact]
    public void ReadingTheBlobWithItsPartGivesTheWholeDataset()
    {
        Written written = Write();

        HollowReadStateEngine engine = ReadSnapshot(written, ["reviews"]);

        Assert.NotNull(engine.GetTypeState("Movie"));
        Assert.Equal(2, engine.GetTypeState("Movie")!.PopulatedOrdinals.Cardinality());
        Assert.Equal(3, engine.GetTypeState("Review")!.PopulatedOrdinals.Cardinality());

        Assert.Equal(
            ["awful", "fine", "great"],
            Texts(engine, "Review").Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ADeltaCarriesItsPartsToo()
    {
        Catalogue catalogue = new();

        Written first = catalogue.WriteSnapshot();
        HollowReadStateEngine engine = ReadSnapshot(first, ["reviews"]);

        Written delta = catalogue.WriteDelta();

        using OptionalBlobPartInput parts = new();
        parts.AddInput("reviews", delta.Parts["reviews"]);

        new HollowBlobReader(engine).ApplyDelta(HollowBlobInput.Serial(delta.Main), parts);

        Assert.Equal(3, engine.GetTypeState("Movie")!.PopulatedOrdinals.Cardinality());
        Assert.Contains("astonishing", Texts(engine, "Review"));
    }

    [Fact]
    public void APartOfADifferentStateIsRefused()
    {
        Written first = Write();
        Written second = Write();

        using OptionalBlobPartInput parts = new();
        parts.AddInput("reviews", second.Parts["reviews"]);

        HollowBlobReader reader = new(new HollowReadStateEngine());

        // The randomized tags are the only thing tying a part to a blob, so this is the check that
        // stands between a mismatched pair and silently wrong records.
        ArgumentException refusal = Assert.Throws<ArgumentException>(
            () => reader.ReadSnapshot(HollowBlobInput.Serial(first.Main), parts));

        Assert.Contains("different state", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APartUnderTheWrongNameIsRefused()
    {
        Written written = Write();

        using OptionalBlobPartInput parts = new();
        parts.AddInput("ratings", written.Parts["reviews"]);

        HollowBlobReader reader = new(new HollowReadStateEngine());

        ArgumentException refusal = Assert.Throws<ArgumentException>(
            () => reader.ReadSnapshot(HollowBlobInput.Serial(written.Main), parts));

        Assert.Contains("says it is 'reviews'", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFilterSeesTheTypesThatLiveInAPart()
    {
        Written written = Write();

        using OptionalBlobPartInput parts = new();
        parts.AddInput("reviews", written.Parts["reviews"]);

        HollowReadStateEngine engine = new();

        // A filter is resolved against every schema the transition declares, wherever it declared it.
        new HollowBlobReader(engine).ReadSnapshot(
            HollowBlobInput.Serial(written.Main), parts, TypeFilter.Include(["Review", "String"]));

        Assert.NotNull(engine.GetTypeState("Review"));
        Assert.Null(engine.GetTypeState("Movie"));
    }

    [Fact]
    public void ATypeCannotBelongToTwoParts()
    {
        OptionalBlobPartConfig config = new();

        config.AddTypesToPart("reviews", "Review");

        // Java allows this and writes the type's records into both, which leaves a consumer holding
        // whichever part it happened to read last.
        ArgumentException refusal =
            Assert.Throws<ArgumentException>(() => config.AddTypesToPart("ratings", "Review"));

        Assert.Contains("already assigned", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryConfiguredPartNeedsAnOutput()
    {
        OptionalBlobPartConfig config = new();

        config.AddTypesToPart("reviews", "Review");

        Assert.Throws<ArgumentException>(() => config.NewOutputs(_ => null!));
    }

    private static IEnumerable<string?> Texts(HollowReadStateEngine engine, string typeName)
    {
        HollowObjectTypeReadState records = (HollowObjectTypeReadState)engine.GetTypeState(typeName)!;
        HollowObjectTypeReadState strings = (HollowObjectTypeReadState)engine.GetTypeState("String")!;

        int text = records.Schema.GetPosition("Text");

        return
        [
            .. records.PopulatedOrdinals.EnumerateSetBits()
                .Select(ordinal => strings.ReadString(records.ReadOrdinal(ordinal, text), 0)),
        ];
    }

    private static Written Write() => new Catalogue().WriteSnapshot();

    private static HollowReadStateEngine ReadSnapshot(Written written, string[]? parts)
    {
        HollowReadStateEngine engine = new();

        if (parts is null)
        {
            new HollowBlobReader(engine).ReadSnapshot(HollowBlobInput.Serial(written.Main));

            return engine;
        }

        using OptionalBlobPartInput inputs = new();

        foreach (string part in parts)
        {
            inputs.AddInput(part, written.Parts[part]);
        }

        new HollowBlobReader(engine).ReadSnapshot(HollowBlobInput.Serial(written.Main), inputs);

        return engine;
    }

    /// <summary>One cycle's blob, and the parts written alongside it.</summary>
    private sealed record Written(byte[] Main, IReadOnlyDictionary<string, byte[]> Parts);

    /// <summary>A catalogue whose reviews are bulky enough to be worth leaving behind.</summary>
    private sealed class Catalogue
    {
        private readonly HollowWriteStateEngine _engine = new();
        private readonly HollowObjectMapper _mapper;
        private readonly OptionalBlobPartConfig _config = new();

        internal Catalogue()
        {
            _mapper = new HollowObjectMapper(_engine);
            _mapper.InitializeTypeState(typeof(Movie));
            _mapper.InitializeTypeState(typeof(Review));

            _config.AddTypesToPart("reviews", "Review");
        }

        internal Written WriteSnapshot()
        {
            Add(new Movie(1, "Heat"), new Movie(2, "Ronin"));
            Add(new Review(1, "great"), new Review(2, "fine"), new Review(3, "awful"));

            return Write((writer, output, parts) => writer.WriteSnapshot(output, parts));
        }

        internal Written WriteDelta()
        {
            Add(new Movie(1, "Heat"), new Movie(2, "Ronin"), new Movie(3, "Collateral"));
            Add(new Review(1, "great"), new Review(2, "fine"), new Review(4, "astonishing"));

            return Write((writer, output, parts) => writer.WriteDelta(output, parts));
        }

        private void Add(params object[] records)
        {
            foreach (object record in records)
            {
                _mapper.Add(record);
            }
        }

        private Written Write(Action<HollowBlobWriter, HollowBlobOutput, OptionalBlobPartOutputs> write)
        {
            Dictionary<string, MemoryStream> partStreams = new(StringComparer.Ordinal);
            List<HollowBlobOutput> partOutputs = [];

            OptionalBlobPartOutputs parts = _config.NewOutputs(part =>
            {
                MemoryStream stream = new();
                partStreams[part] = stream;

                HollowBlobOutput output = HollowBlobOutput.Serial(stream, leaveOpen: true);
                partOutputs.Add(output);

                return output;
            });

            using MemoryStream main = new();

            using (HollowBlobOutput output = HollowBlobOutput.Serial(main, leaveOpen: true))
            {
                write(new HollowBlobWriter(_engine), output, parts);
            }

            foreach (HollowBlobOutput output in partOutputs)
            {
                output.Dispose();
            }

            _engine.PrepareForNextCycle();

            return new Written(
                main.ToArray(),
                partStreams.ToDictionary(
                    part => part.Key, part => part.Value.ToArray(), StringComparer.Ordinal));
        }
    }

    [HollowPrimaryKey("Id")]
    private sealed record Movie(int Id, string Title);

    [HollowPrimaryKey("Id")]
    private sealed record Review(int Id, string Text);
}
