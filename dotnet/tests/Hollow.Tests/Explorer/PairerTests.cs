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

using Hollow.Core.Index.Key;
using Hollow.Core.Read.Engine;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;
using Hollow.Explorer.Diff.Effigy;
using Hollow.Explorer.Diff.Effigy.Pairer;

namespace Hollow.Tests.Explorer;

/// <summary>
/// Lining two records up against each other, which is what a side-by-side diff page is.
/// </summary>
/// <remarks>
/// An object pairs by field name and is uninteresting. A collection is the whole problem: the two
/// sides have no names and need not be the same length, so which element goes opposite which has to be
/// worked out — and getting it wrong turns a one-line change into a screen of red.
/// </remarks>
public class PairerTests
{
    private sealed record Actor(int Id, string Name, string Role);

    private sealed record Film(int Id, string Title, List<Actor> Cast);

    private static readonly IReadOnlyDictionary<string, PrimaryKey> NoHints =
        new Dictionary<string, PrimaryKey>(StringComparer.Ordinal);

    private static HollowReadStateEngine Publish(Film film)
    {
        HollowWriteStateEngine writeEngine = new();
        HollowObjectMapper mapper = new(writeEngine);
        mapper.Add(film);

        return StateEngineRoundTripper.RoundTripSnapshot(writeEngine);
    }

    private static HollowEffigy Effigise(Film film) =>
        new HollowEffigyFactory().Effigy(Publish(film), "Film", 0)!;

    /// <summary>The effigy of a film's cast, which is what the collection pairers are given.</summary>
    private static HollowEffigy Cast(HollowEffigy film) =>
        (HollowEffigy)film.Fields.Single(field => field.FieldName == "Cast").Value!;

    private static Film WithCast(params Actor[] cast) => new(1, "The Matrix", [.. cast]);

    private static readonly Actor Neo = new(10, "Keanu Reeves", "Neo");
    private static readonly Actor Trinity = new(11, "Carrie-Anne Moss", "Trinity");
    private static readonly Actor Morpheus = new(12, "Laurence Fishburne", "Morpheus");

    /// <summary>An object's fields pair by name, and position is not a difference.</summary>
    [Fact]
    public void AnObjectPairsByFieldName()
    {
        IReadOnlyList<EffigyFieldPair> pairs = HollowEffigyFieldPairer.Pair(
            Effigise(WithCast(Neo)), Effigise(WithCast(Neo)), NoHints);

        Assert.Equal(["Id", "Title", "Cast"], pairs.Select(pair => pair.From!.FieldName));
        Assert.All(pairs, pair => Assert.False(pair.IsDiff));

        // A field's place among its siblings does not identify it, so neither side is ever reordered.
        Assert.All(pairs, pair => Assert.False(pair.IsOrderingDiff));
    }

    /// <summary>A changed value shows as a difference on that row and on no other.</summary>
    [Fact]
    public void AChangedValueIsADifferenceOnItsOwnRow()
    {
        HollowEffigy from = Effigise(WithCast(Neo));
        HollowEffigy to = Effigise(WithCast(Neo) with { Title = "The Matrix Reloaded" });

        IReadOnlyList<EffigyFieldPair> pairs = HollowEffigyFieldPairer.Pair(from, to, NoHints);

        // Title is a reference to the shared String type, so the difference is one level down.
        EffigyFieldPair title = pairs.Single(pair => pair.From!.FieldName == "Title");

        Assert.False(title.IsDiff);
        Assert.False(title.IsLeafNode);

        EffigyFieldPair value = Assert.Single(HollowEffigyFieldPairer.Pair(
            (HollowEffigy)title.From!.Value!, (HollowEffigy)title.To!.Value!, NoHints));

        Assert.True(value.IsDiff);
        Assert.True(value.IsLeafNode);
    }

    /// <summary>
    /// A record with no counterpart at all has every one of its fields reported, so the page can show
    /// what arrived or left rather than an empty half.
    /// </summary>
    [Fact]
    public void ARecordWithNoCounterpartHasEveryFieldReported()
    {
        HollowEffigy film = Effigise(WithCast(Neo));

        IReadOnlyList<EffigyFieldPair> arrived = HollowEffigyFieldPairer.Pair(null, film, NoHints);

        Assert.Equal(film.Fields.Count, arrived.Count);
        Assert.All(arrived, pair => Assert.Null(pair.From));
        Assert.All(arrived, pair => Assert.True(pair.IsDiff));

        IReadOnlyList<EffigyFieldPair> left = HollowEffigyFieldPairer.Pair(film, null, NoHints);

        Assert.All(left, pair => Assert.Null(pair.To));
    }

    /// <summary>
    /// Without a hint, elements pair with whichever is least unlike them — so an element that changed
    /// in one field still finds the element it came from.
    /// </summary>
    [Fact]
    public void WithoutAHintElementsPairWithTheClosestCandidate()
    {
        HollowEffigy from = Cast(Effigise(WithCast(Neo, Trinity)));
        HollowEffigy to = Cast(Effigise(WithCast(Neo with { Role = "Neo (The One)" }, Trinity)));

        IReadOnlyList<EffigyFieldPair> pairs = HollowEffigyFieldPairer.Pair(from, to, NoHints);

        // Both elements pair; nothing is reported as having arrived or left.
        Assert.Equal(2, pairs.Count);
        Assert.All(pairs, pair => Assert.NotNull(pair.From));
        Assert.All(pairs, pair => Assert.NotNull(pair.To));
    }

    /// <summary>
    /// An element with nothing whatever in common with any candidate is reported as an arrival rather
    /// than paired with the least-bad option.
    /// </summary>
    [Fact]
    public void AnElementSharingNothingIsAnArrivalRatherThanAPairing()
    {
        HollowEffigy from = Cast(Effigise(WithCast(Neo)));
        HollowEffigy to = Cast(Effigise(WithCast(Neo, Morpheus)));

        IReadOnlyList<EffigyFieldPair> pairs = HollowEffigyFieldPairer.Pair(from, to, NoHints);

        Assert.Equal(2, pairs.Count);

        EffigyFieldPair arrival = pairs.Single(pair => pair.From is null);

        Assert.NotNull(arrival.To);
        Assert.True(arrival.IsDiff);
    }

    /// <summary>
    /// Given a key, elements pair by it — which is both cheaper than guessing and right when the
    /// guess would not have been.
    /// </summary>
    [Fact]
    public void AHintPairsElementsByTheirKey()
    {
        Dictionary<string, PrimaryKey> hints = new(StringComparer.Ordinal)
        {
            ["Actor"] = new PrimaryKey("Actor", "Id"),
        };

        // Both name and role change, so nothing but the id says these are the same actor.
        HollowEffigy from = Cast(Effigise(WithCast(Neo, Trinity)));
        HollowEffigy to = Cast(Effigise(
            WithCast(Neo with { Name = "K. Reeves", Role = "The One" }, Trinity)));

        IReadOnlyList<EffigyFieldPair> pairs = HollowEffigyFieldPairer.Pair(from, to, hints);

        Assert.Equal(2, pairs.Count);
        Assert.All(pairs, pair => Assert.NotNull(pair.From));
        Assert.All(pairs, pair => Assert.NotNull(pair.To));
    }

    /// <summary>
    /// Reordering a collection pairs every element with itself and says only that they moved, rather
    /// than reporting a screen of differences.
    /// </summary>
    [Fact]
    public void ReorderingShowsAsMovementRatherThanDifference()
    {
        Dictionary<string, PrimaryKey> hints = new(StringComparer.Ordinal)
        {
            ["Actor"] = new PrimaryKey("Actor", "Id"),
        };

        HollowEffigy from = Cast(Effigise(WithCast(Neo, Trinity)));
        HollowEffigy to = Cast(Effigise(WithCast(Trinity, Neo)));

        IReadOnlyList<EffigyFieldPair> pairs = HollowEffigyFieldPairer.Pair(from, to, hints);

        Assert.Equal(2, pairs.Count);
        Assert.All(pairs, pair => Assert.True(pair.IsOrderingDiff));

        // Moved, not changed: each row still holds the same record on both sides.
        Assert.All(pairs, pair => Assert.False(pair.IsDiff));
    }

    /// <summary>
    /// A collection that lost every element reports each of them once — Java's missing early return
    /// reports them twice.
    /// </summary>
    [Fact]
    public void ACollectionThatLostEverythingReportsEachElementOnce()
    {
        Dictionary<string, PrimaryKey> hints = new(StringComparer.Ordinal)
        {
            ["Actor"] = new PrimaryKey("Actor", "Id"),
        };

        HollowEffigy from = Cast(Effigise(WithCast(Neo, Trinity)));
        HollowEffigy to = Cast(Effigise(WithCast()));

        IReadOnlyList<EffigyFieldPair> pairs = HollowEffigyFieldPairer.Pair(from, to, hints);

        Assert.Equal(2, pairs.Count);
        Assert.All(pairs, pair => Assert.Null(pair.To));
    }

    /// <summary>
    /// An element pairs with exactly one counterpart, so no record is shown on more than one row —
    /// which Java's probe, continuing past a match, allows.
    /// </summary>
    [Fact]
    public void AnElementPairsWithExactlyOneCounterpart()
    {
        Dictionary<string, PrimaryKey> hints = new(StringComparer.Ordinal)
        {
            ["Actor"] = new PrimaryKey("Actor", "Id"),
        };

        HollowEffigy from = Cast(Effigise(WithCast(Neo, Trinity, Morpheus)));
        HollowEffigy to = Cast(Effigise(WithCast(Neo, Trinity, Morpheus)));

        IReadOnlyList<EffigyFieldPair> pairs = HollowEffigyFieldPairer.Pair(from, to, hints);

        Assert.Equal(3, pairs.Count);
        Assert.Equal(3, pairs.Select(pair => pair.FromIndex).Distinct().Count());
        Assert.Equal(3, pairs.Select(pair => pair.ToIndex).Distinct().Count());
    }
}
