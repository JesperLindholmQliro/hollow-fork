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

using Hollow.Core.Read.Engine;
using Hollow.Core;
using Hollow.Core.Tools.Diff;
using Hollow.Core.Tools.Diff.Exact;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Tests.Core.Tools;

/// <summary>
/// What changed between two states, and where.
/// </summary>
/// <remarks>
/// The diff's answer is a score per field rather than a list of edits, so what these check is that the
/// score lands on the field that actually moved, and that the records which did not move contribute
/// nothing — which is the whole basis of it finishing on a dataset of any size.
/// </remarks>
public class DiffTests
{
    private sealed record Studio(string Name, string Country);

    private sealed record Actor(int Id, string Name);

    [HollowPrimaryKey("Id")]
    private sealed record Film(
        int Id,
        string Title,
        int Year,
        Studio Studio,
        List<Actor> Cast,
        HashSet<string> Tags);

    private static readonly Studio Warner = new("Warner Bros.", "US");
    private static readonly Studio Toho = new("Toho", "JP");

    private static readonly Actor Keanu = new(10, "Keanu Reeves");
    private static readonly Actor Carrie = new(11, "Carrie-Anne Moss");
    private static readonly Actor Takashi = new(12, "Takashi Shimura");

    private static HollowReadStateEngine Publish(params Film[] films)
    {
        HollowWriteStateEngine writeEngine = new();
        HollowObjectMapper mapper = new(writeEngine);
        mapper.InitializeTypeState(typeof(Film));

        foreach (Film film in films)
        {
            mapper.Add(film);
        }

        return StateEngineRoundTripper.RoundTripSnapshot(writeEngine);
    }

    private static Film TheMatrix(string title = "The Matrix", int year = 1999) =>
        new(1, title, year, Warner, [Keanu, Carrie], ["science fiction", "action"]);

    private static Film SevenSamurai() =>
        new(2, "Seven Samurai", 1954, Toho, [Takashi], ["drama"]);

    private static HollowDiff Diff(HollowReadStateEngine from, HollowReadStateEngine to)
    {
        HollowDiff diff = new(from, to);
        diff.CalculateDiffs();

        return diff;
    }

    /// <summary>The field diff for <paramref name="route"/>, by the route a reader would name it.</summary>
    private static HollowFieldDiff? FieldDiff(HollowTypeDiff typeDiff, string route) =>
        typeDiff.FieldDiffs.SingleOrDefault(
            diff => diff.FieldIdentifier.ToString().StartsWith(route, StringComparison.Ordinal));

    /// <summary>
    /// Two states holding the same records differ in nothing, which is the case that has to be cheap
    /// because it is nearly all of a real dataset.
    /// </summary>
    [Fact]
    public void AStateComparedWithItselfDiffersInNothing()
    {
        HollowReadStateEngine state = Publish(TheMatrix(), SevenSamurai());

        HollowTypeDiff typeDiff = Diff(state, state).GetTypeDiff("Film")!;

        Assert.Equal(2, typeDiff.TotalNumberOfMatches);
        Assert.Equal(0, typeDiff.UnmatchedOrdinalsInFrom.Count);
        Assert.Equal(0, typeDiff.UnmatchedOrdinalsInTo.Count);
        Assert.Equal(0, typeDiff.TotalDiffScore);
        Assert.Empty(typeDiff.FieldDiffs);
    }

    /// <summary>
    /// A changed value is attributed to the field that holds it and to nothing else — which is the
    /// point of the whole traversal.
    /// </summary>
    [Fact]
    public void AChangedFieldIsTheOnlyOneThatScores()
    {
        HollowTypeDiff typeDiff = Diff(
            Publish(TheMatrix(), SevenSamurai()),
            Publish(TheMatrix(title: "The Matrix (remastered)"), SevenSamurai())).GetTypeDiff("Film")!;

        Assert.Equal(2, typeDiff.TotalNumberOfMatches);

        HollowFieldDiff titleDiff = Assert.Single(typeDiff.FieldDiffs);

        Assert.Equal("Film.Title.value (String)", titleDiff.FieldIdentifier.ToString());

        // One value left and one arrived, so the pair scores two.
        Assert.Equal(2, titleDiff.TotalDiffScore);
        Assert.Equal(1, titleDiff.NumDiffs);
    }

    /// <summary>
    /// A difference behind a reference is named by the whole route taken to reach it, because that is
    /// what tells a reader which field of which nested record moved.
    /// </summary>
    [Fact]
    public void ADifferenceBehindAReferenceIsNamedByItsRoute()
    {
        HollowTypeDiff typeDiff = Diff(
            Publish(TheMatrix()),
            Publish(TheMatrix() with { Studio = new Studio("Warner Bros.", "GB") })).GetTypeDiff("Film")!;

        HollowFieldDiff countryDiff = Assert.Single(typeDiff.FieldDiffs);

        Assert.Equal("Film.Studio.Country.value (String)", countryDiff.FieldIdentifier.ToString());
        Assert.Equal(2, countryDiff.TotalDiffScore);
    }

    /// <summary>
    /// An element leaving a collection is a difference under that collection, and the elements that
    /// stayed contribute nothing.
    /// </summary>
    [Fact]
    public void AnElementLeavingACollectionScoresOnce()
    {
        HollowTypeDiff typeDiff = Diff(
            Publish(TheMatrix()),
            Publish(TheMatrix() with { Cast = [Keanu] })).GetTypeDiff("Film")!;

        // Carrie-Anne Moss left, so both of her fields are gone; Keanu stayed and costs nothing.
        HollowFieldDiff nameDiff = FieldDiff(typeDiff, "Film.Cast.element.Name.value")!;
        HollowFieldDiff idDiff = FieldDiff(typeDiff, "Film.Cast.element.Id")!;

        Assert.Equal(1, nameDiff.TotalDiffScore);
        Assert.Equal(1, idDiff.TotalDiffScore);
    }

    /// <summary>
    /// A set is compared by what it holds rather than by the order it holds it in.
    /// </summary>
    [Fact]
    public void ReorderingASetIsNotADifference()
    {
        HollowTypeDiff typeDiff = Diff(
            Publish(TheMatrix()),
            Publish(TheMatrix() with { Tags = ["action", "science fiction"] })).GetTypeDiff("Film")!;

        Assert.Equal(0, typeDiff.TotalDiffScore);
    }

    /// <summary>
    /// Reordering a list is not reported as a difference in any field, even though the list records
    /// themselves are not equal.
    /// </summary>
    /// <remarks>
    /// Two things disagree here, on purpose. The equality mapping treats a list's order as part of its
    /// value, so the two films are <em>not</em> skipped as identical and the walk happens. The counting
    /// tree then flattens both collections and pairs their elements off by identity, and every element
    /// has a partner — so nothing scores. What the diff reports is which fields moved, and by that
    /// measure none did.
    /// </remarks>
    [Fact]
    public void ReorderingAListScoresNothingEvenThoughTheListsDiffer()
    {
        HollowReadStateEngine from = Publish(TheMatrix());
        HollowReadStateEngine to = Publish(TheMatrix() with { Cast = [Carrie, Keanu] });

        HollowTypeDiff typeDiff = Diff(from, to).GetTypeDiff("Film")!;

        Assert.Equal(1, typeDiff.TotalNumberOfMatches);
        Assert.Equal(0, typeDiff.TotalDiffScore);

        // The lists really are unequal — which is what made the pair worth walking at all.
        DiffEqualityMapping mapping = new(from, to);

        Assert.Equal(
            HollowConstants.OrdinalNone,
            mapping.GetEqualOrdinalMap("ListOfActor").GetIdentityFromOrdinal(0));
    }

    /// <summary>
    /// Records that pair with nothing are reported as having arrived or left, rather than being
    /// compared against something arbitrary.
    /// </summary>
    [Fact]
    public void RecordsThatPairWithNothingAreReportedAsSuch()
    {
        HollowTypeDiff typeDiff = Diff(
            Publish(TheMatrix(), SevenSamurai()),
            Publish(TheMatrix(), new Film(3, "Rashomon", 1950, Toho, [Takashi], ["drama"])))
            .GetTypeDiff("Film")!;

        Assert.Equal(1, typeDiff.TotalNumberOfMatches);
        Assert.Equal(1, typeDiff.UnmatchedOrdinalsInFrom.Count);
        Assert.Equal(1, typeDiff.UnmatchedOrdinalsInTo.Count);

        // The two unmatched films are not compared with each other, so no field scores.
        Assert.Equal(0, typeDiff.TotalDiffScore);

        Assert.Equal(2, typeDiff.TotalItemsInFromState);
        Assert.Equal(2, typeDiff.TotalItemsInToState);
    }

    /// <summary>
    /// The key is what makes a pair a pair: without one, every record is an arrival or a departure.
    /// </summary>
    [Fact]
    public void WithoutAKeyNothingPairsUp()
    {
        HollowDiff diff = new(Publish(TheMatrix()), Publish(TheMatrix()), autoDiscoverTypeDiffs: false);
        HollowTypeDiff typeDiff = diff.AddTypeDiff("Film");
        diff.CalculateDiffs();

        Assert.False(typeDiff.HasMatchPaths);
        Assert.Equal(0, typeDiff.TotalNumberOfMatches);
        Assert.Equal(1, typeDiff.UnmatchedOrdinalsInFrom.Count);
        Assert.Equal(1, typeDiff.UnmatchedOrdinalsInTo.Count);
    }

    /// <summary>
    /// A type the caller stops at is counted rather than compared, so the difference is reported
    /// without saying which of its fields moved.
    /// </summary>
    [Fact]
    public void AShortcutTypeIsCountedRatherThanCompared()
    {
        HollowDiff diff = new(
            Publish(TheMatrix()),
            Publish(TheMatrix() with { Studio = new Studio("Warner Bros.", "GB") }),
            autoDiscoverTypeDiffs: false);

        HollowTypeDiff typeDiff = diff.AddTypeDiff("Film", "Id");
        typeDiff.AddShortcutType("Studio");
        diff.CalculateDiffs();

        HollowFieldDiff studioDiff = Assert.Single(typeDiff.FieldDiffs);

        // Named for the reference rather than for the field inside it that actually changed.
        Assert.Equal("Film.Studio (Studio)", studioDiff.FieldIdentifier.ToString());
        Assert.True(studioDiff.TotalDiffScore > 0);
    }

    /// <summary>
    /// Every changed record is offered, so a page can show the records behind a field's score rather
    /// than only the number.
    /// </summary>
    [Fact]
    public void TheRecordsBehindAScoreAreKept()
    {
        HollowReadStateEngine from = Publish(TheMatrix(), SevenSamurai());
        HollowReadStateEngine to = Publish(
            TheMatrix(year: 2000), SevenSamurai() with { Year = 1955 });

        HollowTypeDiff typeDiff = Diff(from, to).GetTypeDiff("Film")!;
        HollowFieldDiff yearDiff = Assert.Single(typeDiff.FieldDiffs);

        Assert.Equal("Film.Year (Int)", yearDiff.FieldIdentifier.ToString());
        Assert.Equal(2, yearDiff.NumDiffs);

        for (int i = 0; i < yearDiff.NumDiffs; i++)
        {
            Assert.True(yearDiff.GetPairScore(i) > 0);
            Assert.True(yearDiff.GetFromOrdinal(i) >= 0);
            Assert.True(yearDiff.GetToOrdinal(i) >= 0);
        }
    }

    /// <summary>
    /// Every object type declaring a key is diffed without being asked for, which is what makes a diff
    /// useful against a dataset whose model the caller does not know.
    /// </summary>
    [Fact]
    public void TypesWithAKeyAreFoundWithoutBeingNamed()
    {
        HollowDiff diff = new(Publish(TheMatrix()), Publish(TheMatrix()));

        Assert.Contains(diff.TypeDiffs, typeDiff => typeDiff.TypeName == "Film");

        // A type declaring no key is left out, even one holding a single value that could stand in for
        // one: the single-field fallback only applies once keyless types have been asked for.
        Assert.DoesNotContain(diff.TypeDiffs, typeDiff => typeDiff.TypeName == "String");

        HollowDiff withKeyless = new(
            Publish(TheMatrix()), Publish(TheMatrix()), includeNonPrimaryKeyTypes: true);

        Assert.Contains(withKeyless.TypeDiffs, typeDiff => typeDiff.TypeName == "String");
    }
}
