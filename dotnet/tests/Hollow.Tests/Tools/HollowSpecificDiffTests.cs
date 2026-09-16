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
using Hollow.Core.Schema;
using Hollow.Core.Write;
using Hollow.Tools.Diff.Specific;

namespace Hollow.Tests.Tools;

/// <summary>
/// Counts how a few named fields differ between two states.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>HollowSpecificDiffTest</c>. Each film carries a list of ratings, so the two paths
/// under test cross a collection and one record contributes as many values as it has ratings — which
/// is the case the counting is actually for. A record that contributes exactly one value would not
/// tell a correct implementation from one that counts records.
/// </para>
/// <para>
/// The distinction the element key paths draw is the point of the whole tool: without one, a changed
/// score is a value that went and a value that arrived; with one, it is a value that changed.
/// </para>
/// </remarks>
public class HollowSpecificDiffTests
{
    private const string SourcePath = "Ratings.element.Source.value";
    private const string ScorePath = "Ratings.element.Score";

    [Fact]
    public void AnUnchangedStateHasNothingToReport()
    {
        HollowSpecificDiff diff = Diff(Catalogue(), Catalogue());

        Assert.Equal(3, diff.TotalMatchedEqualElements);
        Assert.Equal(0, diff.TotalModifiedElements);
        Assert.Equal(0, diff.TotalUnmatchedFromElements);
        Assert.Equal(0, diff.TotalUnmatchedToElements);
    }

    [Fact]
    public void AValueAddedAndOneRemovedAreCountedSeparately()
    {
        HollowSpecificDiff diff = Diff(Catalogue(), Rescored());

        // With nothing to pair the ratings by, a changed score is two unrelated facts.
        Assert.Equal(0, diff.TotalModifiedElements);
        Assert.Equal(1, diff.TotalUnmatchedFromElements);
        Assert.Equal(1, diff.TotalUnmatchedToElements);
        Assert.Equal(2, diff.TotalMatchedEqualElements);
    }

    [Fact]
    public void AKeyTurnsTwoUnmatchedValuesIntoOneModification()
    {
        HollowSpecificDiff diff = Diff(Catalogue(), Rescored(), SourcePath);

        // Same two states, same change; naming the source as the key makes it one changed rating.
        Assert.Equal(1, diff.TotalModifiedElements);
        Assert.Equal(0, diff.TotalUnmatchedFromElements);
        Assert.Equal(0, diff.TotalUnmatchedToElements);
        Assert.Equal(2, diff.TotalMatchedEqualElements);
    }

    [Fact]
    public void ARecordOnOneSideContributesEveryValueItReaches()
    {
        HollowReadStateEngine to = State(
            Film(1, ("imdb", 7), ("rt", 8)),
            Film(2, ("imdb", 6)),
            Film(3, ("imdb", 5), ("rt", 4), ("mc", 3)));

        HollowSpecificDiff diff = Diff(Catalogue(), to);

        // Three ratings arrived, not one film.
        Assert.Equal(3, diff.TotalUnmatchedToElements);
        Assert.Equal(0, diff.TotalUnmatchedFromElements);
        Assert.Equal(3, diff.TotalMatchedEqualElements);
    }

    [Fact]
    public void ARecordWithNoValuesContributesNothing()
    {
        HollowReadStateEngine from = State(Film(1, ("imdb", 7), ("rt", 8)), Film(2));
        HollowReadStateEngine to = State(Film(1, ("imdb", 7), ("rt", 8)), Film(2));

        HollowSpecificDiff diff = Diff(from, to);

        // Two ratings between them, and the film with none is not a value of its own.
        Assert.Equal(2, diff.TotalMatchedEqualElements);
        Assert.Equal(0, diff.TotalModifiedElements);
        Assert.Equal(0, diff.TotalUnmatchedFromElements);
        Assert.Equal(0, diff.TotalUnmatchedToElements);
    }

    [Fact]
    public void RecalculatingDoesNotDoubleTheCounts()
    {
        HollowSpecificDiff diff = Diff(Catalogue(), Rescored(), SourcePath);

        diff.Calculate();

        Assert.Equal(1, diff.TotalModifiedElements);
        Assert.Equal(2, diff.TotalMatchedEqualElements);
        Assert.Equal(0, diff.TotalUnmatchedFromElements);
        Assert.Equal(0, diff.TotalUnmatchedToElements);
    }

    [Fact]
    public void AKeyPathThatIsNotAnElementPathIsRefused()
    {
        HollowSpecificDiff diff = new(Catalogue(), Catalogue(), "Movie");

        diff.SetElementMatchPaths(SourcePath, ScorePath);

        Assert.Throws<ArgumentException>(() => diff.SetElementKeyPaths("Ratings.element.Reviewer"));
    }

    /// <summary>Two films and three ratings between them.</summary>
    private static HollowReadStateEngine Catalogue() =>
        State(Film(1, ("imdb", 7), ("rt", 8)), Film(2, ("imdb", 6)));

    /// <summary>The same catalogue, with one rating's score changed.</summary>
    private static HollowReadStateEngine Rescored() =>
        State(Film(1, ("imdb", 9), ("rt", 8)), Film(2, ("imdb", 6)));

    private static (int Id, (string Source, int Score)[] Ratings) Film(
        int id, params (string Source, int Score)[] ratings) => (id, ratings);

    private static HollowSpecificDiff Diff(
        HollowReadStateEngine from, HollowReadStateEngine to, params string[] elementKeyPaths)
    {
        HollowSpecificDiff diff = new(from, to, "Movie");

        diff.SetRecordMatchPaths("Id");
        diff.SetElementMatchPaths(SourcePath, ScorePath);
        diff.PrepareMatches();

        if (elementKeyPaths.Length > 0)
        {
            diff.SetElementKeyPaths(elementKeyPaths);
        }

        diff.Calculate();

        return diff;
    }

    private static HollowReadStateEngine State(
        params (int Id, (string Source, int Score)[] Ratings)[] films)
    {
        HollowWriteStateEngine engine = new();

        HollowObjectSchema stringSchema = new("String", 1, "value");
        stringSchema.AddField("value", FieldType.String);

        HollowObjectSchema ratingSchema = new("Rating", 2);
        ratingSchema.AddField("Source", FieldType.Reference, "String");
        ratingSchema.AddField("Score", FieldType.Int);

        HollowObjectSchema movieSchema = new("Movie", 2, "Id");
        movieSchema.AddField("Id", FieldType.Int);
        movieSchema.AddField("Ratings", FieldType.Reference, "ListOfRating");

        engine.AddTypeState(new HollowObjectTypeWriteState(stringSchema));
        engine.AddTypeState(new HollowObjectTypeWriteState(ratingSchema));
        engine.AddTypeState(new HollowListTypeWriteState(new HollowListSchema("ListOfRating", "Rating")));
        engine.AddTypeState(new HollowObjectTypeWriteState(movieSchema));

        foreach ((int id, (string Source, int Score)[] ratings) in films)
        {
            HollowListWriteRecord list = new();

            foreach ((string source, int score) in ratings)
            {
                HollowObjectWriteRecord sourceRecord = new(stringSchema);
                sourceRecord.SetString("value", source);

                HollowObjectWriteRecord rating = new(ratingSchema);
                rating.SetReference("Source", engine.Add("String", sourceRecord));
                rating.SetInt("Score", score);

                list.AddElement(engine.Add("Rating", rating));
            }

            HollowObjectWriteRecord movie = new(movieSchema);
            movie.SetInt("Id", id);
            movie.SetReference("Ratings", engine.Add("ListOfRating", list));

            engine.Add("Movie", movie);
        }

        engine.PrepareForWrite();

        return StateEngineRoundTripper.RoundTripSnapshot(engine);
    }
}
