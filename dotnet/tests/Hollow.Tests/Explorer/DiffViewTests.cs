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

using Hollow.Core;
using Hollow.Core.Index.Key;
using Hollow.Core.Read.Engine;
using Hollow.Core.Tools.Diff;
using Hollow.Core.Tools.Diff.Exact;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;
using Hollow.Explorer.Diff;
using Hollow.Explorer.Diff.Effigy;
using Hollow.Explorer.Diff.Effigy.Pairer;

namespace Hollow.Tests.Explorer;

/// <summary>
/// Laying two records out as rows, and deciding which of those rows a reader is shown.
/// </summary>
/// <remarks>
/// Opening the whole of a record would bury the change in everything that did not change, so the view
/// starts by showing only what differs and the branches leading down to it. These check that rule —
/// and that the tree beneath a row is built only when something asks for it, which is what makes the
/// page load on a record reaching thousands of others.
/// </remarks>
public class DiffViewTests
{
    private sealed record Studio(string Name, string Country);

    private sealed record Actor(int Id, string Name);

    [HollowPrimaryKey("Id")]
    private sealed record Film(int Id, string Title, Studio Studio, List<Actor> Cast);

    private static readonly Actor Neo = new(10, "Keanu Reeves");
    private static readonly Actor Trinity = new(11, "Carrie-Anne Moss");

    private static Film Matrix(string title = "The Matrix", string country = "US") =>
        new(1, title, new Studio("Warner Bros.", country), [Neo, Trinity]);

    private static HollowReadStateEngine Publish(Film film)
    {
        HollowWriteStateEngine writeEngine = new();
        HollowObjectMapper mapper = new(writeEngine);
        mapper.InitializeTypeState(typeof(Film));
        mapper.Add(film);

        return StateEngineRoundTripper.RoundTripSnapshot(writeEngine);
    }

    /// <summary>A diff UI answering from a real <see cref="HollowDiff"/>, as the diff pages do.</summary>
    private sealed class TestDiffUI(DiffEqualityMapping mapping, params (string Type, string Field)[] hints)
        : IHollowRecordDiffUI
    {
        public IReadOnlyDictionary<string, PrimaryKey> MatchHints { get; } =
            hints.ToDictionary(
                hint => hint.Type,
                hint => new PrimaryKey(hint.Type, hint.Field),
                StringComparer.Ordinal);

        public ICustomHollowEffigyFactory? GetCustomHollowEffigyFactory(string typeName) => null;

        public IExactRecordMatcher ExactRecordMatcher { get; } = new DiffExactRecordMatcher(mapping);
    }

    /// <summary>A diff UI with no equality mapping, as a caller outside a diff would have.</summary>
    private sealed class NoMatchDiffUI(params (string Type, string Field)[] hints) : IHollowRecordDiffUI
    {
        public IReadOnlyDictionary<string, PrimaryKey> MatchHints { get; } =
            hints.ToDictionary(
                hint => hint.Type,
                hint => new PrimaryKey(hint.Type, hint.Field),
                StringComparer.Ordinal);

        public ICustomHollowEffigyFactory? GetCustomHollowEffigyFactory(string typeName) => null;

        public IExactRecordMatcher ExactRecordMatcher => NoExactRecordMatcher.Instance;
    }

    private static HollowDiffView View(
        HollowReadStateEngine from, HollowReadStateEngine to, params (string, string)[] hints)
    {
        HollowDiff diff = new(from, to);
        diff.CalculateDiffs();

        TestDiffUI diffUI = new(diff.EqualityMapping, hints);

        HollowObjectDiffViewGenerator generator = new(from, to, diffUI, "Film", 0, 0);
        HollowDiffView view = new("Film", 0, 0, generator.GetHollowDiffViewRows(), diffUI.ExactRecordMatcher);

        view.ResetView();

        return view;
    }

    /// <summary>Every row beneath <paramref name="row"/> that is currently shown.</summary>
    private static List<HollowDiffViewRow> VisibleRows(HollowDiffViewRow row)
    {
        List<HollowDiffViewRow> visible = [];

        void Walk(HollowDiffViewRow current)
        {
            foreach (HollowDiffViewRow child in current.Children)
            {
                if (child.IsVisible)
                {
                    visible.Add(child);
                }

                if (child.AreChildrenPopulated)
                {
                    Walk(child);
                }
            }
        }

        Walk(row);

        return visible;
    }

    private static string Route(HollowDiffViewRow row)
    {
        List<string?> names = [];

        for (HollowDiffViewRow? current = row; current?.Parent is not null; current = current.Parent)
        {
            names.Add(current.FieldPair.From?.FieldName ?? current.FieldPair.To?.FieldName);
        }

        names.Reverse();

        return string.Join('.', names);
    }

    /// <summary>
    /// The row tree mirrors the record: the root's children are the record's own fields.
    /// </summary>
    [Fact]
    public void TheRootsChildrenAreTheRecordsFields()
    {
        HollowDiffView view = View(Publish(Matrix()), Publish(Matrix()));

        Assert.Equal(
            ["Id", "Title", "Studio", "Cast"],
            view.RootRow.Children.Select(row => row.FieldPair.From!.FieldName));

        Assert.Equal(0, view.RootRow.Indentation);
        Assert.All(view.RootRow.Children, row => Assert.Equal(1, row.Indentation));
    }

    /// <summary>
    /// A branch is built when something asks for it, not before — so a page over a record reaching the
    /// whole dataset does not pair the whole dataset to draw its first screen.
    /// </summary>
    [Fact]
    public void ABranchIsBuiltOnlyWhenAskedFor()
    {
        HollowDiff diff = new(Publish(Matrix()), Publish(Matrix()));
        diff.CalculateDiffs();

        TestDiffUI diffUI = new(diff.EqualityMapping);

        HollowObjectDiffViewGenerator generator = new(
            diff.FromStateEngine, diff.ToStateEngine, diffUI, "Film", 0, 0);

        HollowDiffViewRow root = generator.GetHollowDiffViewRows();
        HollowDiffViewRow cast = root.Children.Single(row => row.FieldPair.From!.FieldName == "Cast");

        // The first level exists so the page can draw; the level below it does not yet.
        Assert.True(root.AreChildrenPopulated);
        Assert.False(cast.AreChildrenPopulated);

        _ = cast.Children;

        Assert.True(cast.AreChildrenPopulated);
    }

    /// <summary>
    /// Two identical records show their top-level fields and nothing more, because there is nothing to
    /// lead the reader down to.
    /// </summary>
    [Fact]
    public void IdenticalRecordsOpenNothing()
    {
        HollowDiffView view = View(Publish(Matrix()), Publish(Matrix()));

        // The record's own fields are always shown, so the view is never blank.
        Assert.All(view.RootRow.Children, row => Assert.True(row.IsVisible));

        Assert.DoesNotContain(VisibleRows(view.RootRow), row => row.Indentation > 1);
    }

    /// <summary>
    /// A difference is shown along with every row on the way down to it, and nothing else is opened.
    /// </summary>
    [Fact]
    public void OnlyTheRouteToADifferenceIsOpened()
    {
        HollowDiffView view = View(Publish(Matrix()), Publish(Matrix(country: "GB")));

        List<HollowDiffViewRow> deep =
            [.. VisibleRows(view.RootRow).Where(row => row.Indentation > 1)];

        // Everything opened lies under Studio, which is where the change is.
        Assert.All(deep, row => Assert.StartsWith("Studio", Route(row), StringComparison.Ordinal));

        // The differing leaf itself is shown and marked.
        HollowDiffViewRow differing = Assert.Single(deep, row => row.FieldPair.IsDiff);

        Assert.Equal("Studio.Country.value", Route(differing));
        Assert.True(differing.FieldPair.IsLeafNode);
    }

    /// <summary>
    /// A subtree the diff already knows is identical is never walked, which is what keeps the view
    /// affordable on a record reaching many others.
    /// </summary>
    [Fact]
    public void AnIdenticalSubtreeIsNotWalked()
    {
        HollowDiffView view = View(Publish(Matrix()), Publish(Matrix(country: "GB")));

        HollowDiffViewRow cast =
            view.RootRow.Children.Single(row => row.FieldPair.From!.FieldName == "Cast");

        // The cast did not change, so resetting the view never looked inside it.
        Assert.False(cast.AreChildrenPopulated);
    }

    /// <summary>
    /// A pure reorder of identical elements shows nothing, because each element pair is an exact match
    /// and an exact match is never walked into.
    /// </summary>
    /// <remarks>
    /// Surprising, and faithful: the exact-match check comes first in both passes, so it fires before
    /// the row's own ordering flag is ever consulted. The rows do carry the flag — the view simply does
    /// not open them. What this means in practice is that a collection whose elements merely swapped
    /// places reads as "nothing changed", which for a diff is arguably the right answer.
    /// </remarks>
    [Fact]
    public void APureReorderOfIdenticalElementsIsNotOpened()
    {
        HollowReadStateEngine from = Publish(Matrix());
        HollowReadStateEngine to = Publish(Matrix() with { Cast = [Trinity, Neo] });

        HollowDiffView view = View(from, to, ("Actor", "Id"));

        Assert.DoesNotContain(VisibleRows(view.RootRow), row => row.Indentation > 1);

        // The pairing did happen, and did notice the movement.
        HollowDiffViewRow cast =
            view.RootRow.Children.Single(row => row.FieldPair.From!.FieldName == "Cast");

        Assert.All(cast.Children, row => Assert.True(row.FieldPair.IsOrderingDiff));
        Assert.All(cast.Children, row => Assert.False(row.FieldPair.IsDiff));
    }

    /// <summary>
    /// Where nothing is known to be identical, a reorder is what gets shown — which is the case for a
    /// caller with no equality mapping to answer from.
    /// </summary>
    [Fact]
    public void WithoutAnEqualityMappingAReorderIsShown()
    {
        HollowReadStateEngine from = Publish(Matrix());
        HollowReadStateEngine to = Publish(Matrix() with { Cast = [Trinity, Neo] });

        NoMatchDiffUI diffUI = new(("Actor", "Id"));

        HollowObjectDiffViewGenerator generator = new(from, to, diffUI, "Film", 0, 0);
        HollowDiffView view = new("Film", 0, 0, generator.GetHollowDiffViewRows(), diffUI.ExactRecordMatcher);

        view.ResetView();

        List<HollowDiffViewRow> visible = VisibleRows(view.RootRow);

        Assert.DoesNotContain(visible, row => row.FieldPair.IsDiff);
        Assert.Contains(visible, row => row.FieldPair.IsOrderingDiff);
    }

    /// <summary>
    /// A row offers to open its branch, close it, or finish opening it — and a value offers nothing.
    /// </summary>
    [Fact]
    public void ARowOffersWhateverItsBranchAllows()
    {
        HollowDiffView view = View(Publish(Matrix()), Publish(Matrix(country: "GB")));

        HollowDiffViewRow id = view.RootRow.Children.Single(row => row.FieldPair.From!.FieldName == "Id");

        Assert.Equal(DiffViewRowAction.None, id.AvailableAction);

        HollowDiffViewRow studio =
            view.RootRow.Children.Single(row => row.FieldPair.From!.FieldName == "Studio");

        // Studio holds Name and Country; only Country differs, so only Country was opened — which is
        // exactly the "partly open" case.
        Assert.Equal(DiffViewRowAction.PartialUncollapse, studio.AvailableAction);

        foreach (HollowDiffViewRow child in studio.Children)
        {
            child.IsVisible = false;
        }

        Assert.Equal(DiffViewRowAction.Uncollapse, studio.AvailableAction);

        foreach (HollowDiffViewRow child in studio.Children)
        {
            child.IsVisible = true;
        }

        Assert.Equal(DiffViewRowAction.Collapse, studio.AvailableAction);
    }

    /// <summary>
    /// A record with no counterpart lays out as every field having arrived, rather than as an empty
    /// half a page.
    /// </summary>
    [Fact]
    public void ARecordWithNoCounterpartShowsAsEntirelyArrived()
    {
        HollowReadStateEngine from = Publish(Matrix());
        HollowReadStateEngine to = Publish(Matrix());

        HollowDiff diff = new(from, to);
        diff.CalculateDiffs();

        TestDiffUI diffUI = new(diff.EqualityMapping);

        HollowObjectDiffViewGenerator generator = new(
            from, to, diffUI, "Film", HollowConstants.OrdinalNone, 0);

        HollowDiffView view = new(
            "Film", HollowConstants.OrdinalNone, 0, generator.GetHollowDiffViewRows(), diffUI.ExactRecordMatcher);

        view.ResetView();

        Assert.All(view.RootRow.Children, row => Assert.Null(row.FieldPair.From));
        Assert.All(view.RootRow.Children, row => Assert.True(row.FieldPair.IsDiff));
    }
}
