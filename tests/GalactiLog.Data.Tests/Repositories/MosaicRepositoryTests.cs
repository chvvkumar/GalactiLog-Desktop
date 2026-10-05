using GalactiLog.Core.Mosaics;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GalactiLog.Data.Tests.Repositories;

// Phase 18 Task 3. Spec 5.22 to 5.25, 7.7 (accept and dismiss) and 12.17's write rules against a
// migrated temp database, so the NOCASE uniques, the coalesced session index and the cascades are
// the real ones.
public class MosaicRepositoryTests : IDisposable
{
    private readonly TestDatabaseHandle _db = TestDatabaseFactory.CreateMigratedDatabase();
    private readonly MosaicRepository _repository;

    public MosaicRepositoryTests()
        => _repository = new MosaicRepository(new DatabaseConnectionString(_db.ConnectionString));

    public void Dispose() => _db.Dispose();

    private static DateOnly Night(int day) => new(2026, 3, day);

    private Guid NewTarget(string name) => LibrarySeeder.AddTarget(_db.ConnectionString, name).Id;

    private void Frames(Guid target, int day, string? label, int count = 1, string filter = "Ha", string imageType = "LIGHT")
    {
        for (var i = 0; i < count; i++)
        {
            LibrarySeeder.AddFrame(_db.ConnectionString, target, Night(day), image =>
            {
                image.PanelLabel = label;
                image.FilterUsed = filter;
                image.ImageType = imageType;
            });
        }
    }

    private GalactiLogContext Open() => new(GalactiLogContextOptions.Create(_db.ConnectionString));

    private List<MosaicPanelSession> Rows(Guid panelId)
    {
        using var context = Open();
        return [.. context.MosaicPanelSessions.Where(row => row.PanelId == panelId)
            .OrderBy(row => row.SessionDate).ThenBy(row => row.FrameLabel)];
    }

    private List<MosaicPanel> Panels(Guid mosaicId)
    {
        using var context = Open();
        return [.. context.MosaicPanels.Where(panel => panel.MosaicId == mosaicId).OrderBy(panel => panel.SortOrder)];
    }

    // ---- mosaics -----------------------------------------------------------------------

    [Fact]
    public void Create_TrimsTheName_AndStampsBothTimes()
    {
        var id = _repository.Create("  NGC 7000  ");

        using var context = Open();
        var row = context.Mosaics.Single();
        Assert.Equal(id, row.Id);
        Assert.Equal("NGC 7000", row.Name);
        Assert.Equal(0d, row.RotationAngle);
        Assert.NotEqual(default, row.CreatedAt);
        Assert.Equal(row.CreatedAt, row.UpdatedAt);
    }

    [Fact]
    public void Create_ATakenNameInAnyCase_IsRefusedWithTheSentence()
    {
        _repository.Create("NGC 7000");

        var refused = Assert.Throws<DuplicateMosaicNameException>(() => _repository.Create(" ngc 7000 "));

        Assert.Equal("A mosaic named \"ngc 7000\" already exists.", refused.Message);
        Assert.Single(_repository.ExistingNames());
    }

    [Fact]
    public void Create_ABlankName_IsRefused()
    {
        var refused = Assert.Throws<MosaicWriteException>(() => _repository.Create("   "));
        Assert.Equal("Enter a name for the mosaic.", refused.Message);
    }

    [Fact]
    public void Rename_ToAnotherMosaicsName_IsRefused_AndToItsOwnInAnotherCase_IsAllowed()
    {
        var first = _repository.Create("Alpha");
        _repository.Create("Beta");

        Assert.Throws<DuplicateMosaicNameException>(() => _repository.Rename(first, "BETA"));
        _repository.Rename(first, "ALPHA");

        Assert.Contains("ALPHA", _repository.ExistingNames());
        Assert.Contains("alpha", _repository.ExistingNames());
    }

    [Fact]
    public void SetNotes_StoresText_AndAnEmptiedBoxStoresNull()
    {
        var id = _repository.Create("Alpha");

        _repository.SetNotes(id, "framing at 0 degrees");
        using (var context = Open()) Assert.Equal("framing at 0 degrees", context.Mosaics.Single().Notes);

        _repository.SetNotes(id, "   ");
        using (var context = Open()) Assert.Null(context.Mosaics.Single().Notes);
    }

    [Fact]
    public void Delete_CascadesToPanelsAndNights_AndKeepsSuggestions()
    {
        var target = NewTarget("NGC 7000");
        var id = _repository.Create("Alpha");
        var panel = _repository.AddPanel(id, "Panel 1");
        _repository.IncludeNight(panel, target, Night(1), "Panel 1");
        _repository.ReplacePending([Candidate("Beta", target, "Panel 1", Night(1))]);

        _repository.Delete(id);

        using var context = Open();
        Assert.Empty(context.Mosaics);
        Assert.Empty(context.MosaicPanels);
        Assert.Empty(context.MosaicPanelSessions);
        Assert.Single(context.MosaicSuggestions);
    }

    [Fact]
    public void DeleteMany_ReturnsTheNumberDeleted_AndSkipsUnknownIds()
    {
        var a = _repository.Create("A");
        var b = _repository.Create("B");
        _repository.Create("C");

        Assert.Equal(2, _repository.DeleteMany([a, b, Guid.NewGuid()]));
        Assert.Equal("C", Assert.Single(_repository.ExistingNames()));
    }

    // ---- panels ------------------------------------------------------------------------

    [Fact]
    public void AddPanel_AppendsSortOrder_AndRefusesARepeatedLabelInAnyCase()
    {
        var id = _repository.Create("Alpha");

        _repository.AddPanel(id, "Panel 1");
        _repository.AddPanel(id, " Panel 2 ");
        var refused = Assert.Throws<DuplicatePanelLabelException>(() => _repository.AddPanel(id, "panel 1"));

        Assert.Equal("A panel named \"panel 1\" already exists in this mosaic.", refused.Message);
        Assert.Equal(new[] { ("Panel 1", 0), ("Panel 2", 1) },
            Panels(id).Select(panel => (panel.PanelLabel, panel.SortOrder)));
        Assert.Equal("Enter a panel label.",
            Assert.Throws<MosaicWriteException>(() => _repository.AddPanel(id, " ")).Message);
    }

    [Fact]
    public void AddPanel_TheSameLabelInAnotherMosaic_IsAllowed()
    {
        _repository.AddPanel(_repository.Create("A"), "Panel 1");
        _repository.AddPanel(_repository.Create("B"), "Panel 1");

        using var context = Open();
        Assert.Equal(2, context.MosaicPanels.Count());
    }

    [Fact]
    public void RenamePanel_ChangesTheLabel_AndRefusesASiblingsLabel()
    {
        var id = _repository.Create("Alpha");
        var first = _repository.AddPanel(id, "Panel 1");
        _repository.AddPanel(id, "Panel 2");

        _repository.RenamePanel(first, "Panel 1 (b)");
        Assert.Throws<DuplicatePanelLabelException>(() => _repository.RenamePanel(first, "PANEL 2"));

        Assert.Equal("Panel 1 (b)", Panels(id)[0].PanelLabel);
    }

    [Fact]
    public void DeletePanel_WithAnIncludedNight_IsRefused_AndWithOnlyAvailableRows_DeletesThem()
    {
        var target = NewTarget("NGC 7000");
        var id = _repository.Create("Alpha");
        var panel = _repository.AddPanel(id, "Panel 1");
        _repository.IncludeNight(panel, target, Night(1), "Panel 1");

        Assert.Throws<PanelNotEmptyException>(() => _repository.DeletePanel(panel));

        _repository.RemoveNight(panel, target, Night(1), "Panel 1");
        _repository.DeletePanel(panel);

        using var context = Open();
        Assert.Empty(context.MosaicPanels);
        Assert.Empty(context.MosaicPanelSessions);
    }

    // ---- nights ------------------------------------------------------------------------

    [Fact]
    public void IncludeNight_Inserts_AndFlipsAnAvailableRowRatherThanAddingOne()
    {
        var target = NewTarget("NGC 7000");
        var panel = _repository.AddPanel(_repository.Create("Alpha"), "Panel 1");

        _repository.IncludeNight(panel, target, Night(1), "Panel 1");
        _repository.RemoveNight(panel, target, Night(1), "panel 1");
        _repository.IncludeNight(panel, target, Night(1), "PANEL 1");

        var row = Assert.Single(Rows(panel));
        Assert.Equal(MosaicPanelSession.Included, row.Status);
        Assert.Equal("Panel 1", row.FrameLabel);
    }

    [Fact]
    public void RemoveNight_KeepsTheRowAsAvailable()
    {
        var target = NewTarget("NGC 7000");
        var panel = _repository.AddPanel(_repository.Create("Alpha"), "Panel 1");
        _repository.IncludeNight(panel, target, Night(1), null);

        _repository.RemoveNight(panel, target, Night(1), null);

        var row = Assert.Single(Rows(panel));
        Assert.Equal(MosaicPanelSession.Available, row.Status);
        Assert.Null(row.FrameLabel);
    }

    [Fact]
    public void IncludeNight_ATripleASiblingPanelIncludes_IsRefusedWithTheSentence()
    {
        var target = NewTarget("NGC 7000");
        var id = _repository.Create("Alpha");
        var first = _repository.AddPanel(id, "Panel 1");
        var second = _repository.AddPanel(id, "Panel 2");
        _repository.IncludeNight(first, target, Night(1), "Panel 1");

        var refused = Assert.Throws<NightAlreadyInMosaicException>(
            () => _repository.IncludeNight(second, target, Night(1), "panel 1"));

        Assert.Equal("2026-03-01 of NGC 7000 (panel 1) is already in panel Panel 1.", refused.Message);
        Assert.Empty(Rows(second));
    }

    [Fact]
    public void IncludeNight_TheNullLabelSentence_ReadsNoLabel()
    {
        var target = NewTarget("NGC 7000");
        var id = _repository.Create("Alpha");
        var first = _repository.AddPanel(id, "Panel 1");
        var second = _repository.AddPanel(id, "Panel 2");
        _repository.IncludeNight(first, target, Night(1), null);

        var refused = Assert.Throws<NightAlreadyInMosaicException>(
            () => _repository.IncludeNight(second, target, Night(1), " "));

        Assert.Equal("2026-03-01 of NGC 7000 (no label) is already in panel Panel 1.", refused.Message);
    }

    // Ruling R19a: two panels of one target may share a night when their frame labels differ, and
    // the rule is per mosaic.
    [Fact]
    public void IncludeNight_ADifferentLabelOrAnotherMosaic_IsAllowed()
    {
        var target = NewTarget("NGC 7000");
        var id = _repository.Create("Alpha");
        var first = _repository.AddPanel(id, "Panel 1");
        var second = _repository.AddPanel(id, "Panel 2");
        var other = _repository.AddPanel(_repository.Create("Beta"), "Panel 1");

        _repository.IncludeNight(first, target, Night(1), "Panel 1");
        _repository.IncludeNight(second, target, Night(1), "Panel 2");
        _repository.IncludeNight(other, target, Night(1), "Panel 1");

        using var context = Open();
        Assert.Equal(3, context.MosaicPanelSessions.Count(row => row.Status == MosaicPanelSession.Included));
    }

    // The available half of the rule: an available row is not bound by it.
    [Fact]
    public void AnAvailableRowInASibling_DoesNotBlockAnInclude()
    {
        var target = NewTarget("NGC 7000");
        var id = _repository.Create("Alpha");
        var first = _repository.AddPanel(id, "Panel 1");
        var second = _repository.AddPanel(id, "Panel 2");
        _repository.IncludeNight(first, target, Night(1), "Panel 1");
        _repository.RemoveNight(first, target, Night(1), "Panel 1");

        _repository.IncludeNight(second, target, Night(1), "Panel 1");

        Assert.Equal(MosaicPanelSession.Included, Assert.Single(Rows(second)).Status);
    }

    [Fact]
    public void IncludeAll_IncludesThePanelsAvailableTriples_ButNotOnesASiblingIncludes()
    {
        var target = NewTarget("NGC 7000");
        Frames(target, 1, "Panel 1");
        Frames(target, 2, "Panel 1");
        Frames(target, 3, null);
        var id = _repository.Create("Alpha");
        var first = _repository.AddPanel(id, "Panel 1");
        var second = _repository.AddPanel(id, "Panel 2");
        _repository.IncludeNight(second, target, Night(2), "Panel 1");
        _repository.IncludeNight(first, target, Night(1), "Panel 1");
        _repository.RemoveNight(first, target, Night(1), "Panel 1");

        var included = _repository.IncludeAll(first);

        // Night 1 (its own available row) and night 3 (no label, a triple of a contributing
        // target with no row yet). Night 2 is included in the sibling and stays hidden.
        Assert.Equal(2, included);
        Assert.Equal(
            new[] { (Night(1), (string?)"Panel 1"), (Night(3), null) },
            Rows(first).Where(row => row.Status == MosaicPanelSession.Included).Select(row => (row.SessionDate, row.FrameLabel)));
    }

    [Fact]
    public void IncludeAllAvailable_ATripleAvailableInTwoPanels_GoesToTheFirst()
    {
        var target = NewTarget("NGC 7000");
        Frames(target, 1, "Panel 1");
        var id = _repository.Create("Alpha");
        var first = _repository.AddPanel(id, "Panel 1");
        var second = _repository.AddPanel(id, "Panel 2");
        _repository.AddTargetNights(second, target);
        _repository.AddTargetNights(first, target);

        Assert.Equal(1, _repository.IncludeAllAvailable(id));

        Assert.Equal(MosaicPanelSession.Included, Assert.Single(Rows(first)).Status);
        Assert.Equal(MosaicPanelSession.Available, Assert.Single(Rows(second)).Status);
    }

    [Fact]
    public void IncludeAsNewPanel_MakesAPanelAtTheEnd_HoldingTheTripleWithItsOwnFrameLabel()
    {
        var target = NewTarget("NGC 7000");
        var id = _repository.Create("Alpha");
        var first = _repository.AddPanel(id, "Panel 1");
        _repository.AddPanel(id, "Panel 2");
        _repository.AddTargetNights(first, target);

        var created = _repository.IncludeAsNewPanel(id, first, target, Night(5), "Panel 1", "Panel 1 (b)");

        var panel = Panels(id)[^1];
        Assert.Equal((created, "Panel 1 (b)", 2), (panel.Id, panel.PanelLabel, panel.SortOrder));
        var row = Assert.Single(Rows(created));
        Assert.Equal((Night(5), (string?)"Panel 1", MosaicPanelSession.Included), (row.SessionDate, row.FrameLabel, row.Status));
    }

    [Fact]
    public void AddTargetNights_WritesAvailableRows_SkippingTriplesTheMosaicIncludesOrThePanelHolds()
    {
        var target = NewTarget("North America Nebula");
        Frames(target, 1, null, count: 3);
        Frames(target, 2, null);
        Frames(target, 2, "Panel 1");
        Frames(target, 3, null, imageType: "DARK");
        var id = _repository.Create("Alpha");
        var first = _repository.AddPanel(id, "Panel 1");
        var second = _repository.AddPanel(id, "Panel 2");
        _repository.IncludeNight(second, target, Night(1), null);
        _repository.RemoveNight(first, target, Night(2), "Panel 1");

        var added = _repository.AddTargetNights(first, target);

        // Night 1 no label is included in the sibling; night 2 Panel 1 already has a row here; the
        // dark frame of night 3 is not LIGHT. That leaves night 2 with no label.
        Assert.Equal(1, added);
        Assert.Equal(
            new[] { (Night(2), (string?)null), (Night(2), "Panel 1") },
            Rows(first).Select(row => (row.SessionDate, row.FrameLabel)));
        Assert.All(Rows(first), row => Assert.Equal(MosaicPanelSession.Available, row.Status));
    }

    // Spec 12.17's add panel form: the target's frames carrying the label when any does.
    [Fact]
    public void AddPanelWithTarget_IncludesTheLabelledPairs_WhenTheTargetCarriesTheLabel()
    {
        var target = NewTarget("NGC 7000");
        Frames(target, 1, "Panel 2");
        Frames(target, 1, "Panel 1");
        Frames(target, 2, "Panel 2");
        var id = _repository.Create("Alpha");

        var result = _repository.AddPanelWithTarget(id, target, "panel 2");

        Assert.Equal((2, 0), (result.Included, result.Skipped));
        Assert.Equal("panel 2", Assert.Single(Panels(id)).PanelLabel);
        Assert.Equal(new[] { Night(1), Night(2) }, Rows(result.PanelId).Select(row => row.SessionDate));
        Assert.All(Rows(result.PanelId), row => Assert.Equal("Panel 2", row.FrameLabel));
    }

    [Fact]
    public void AddPanelWithTarget_WithoutTheLabel_TakesEveryPair_SkipsSiblingIncludes_AndReusesThePanel()
    {
        var target = NewTarget("North America Nebula");
        Frames(target, 1, null);
        Frames(target, 2, null);
        Frames(target, 2, "Panel 7");
        var id = _repository.Create("Alpha");
        var existing = _repository.AddPanel(id, "Panel 1");
        var sibling = _repository.AddPanel(id, "Panel 2");
        _repository.IncludeNight(sibling, target, Night(1), null);

        var result = _repository.AddPanelWithTarget(id, target, "Panel 1");

        Assert.Equal(existing, result.PanelId);
        Assert.Equal((2, 1), (result.Included, result.Skipped));
        Assert.Equal(2, Panels(id).Count);
    }

    // ---- the Create mosaic dialog ------------------------------------------------------

    [Fact]
    public void CreateFromNights_RowsSharingALabel_BecomeOnePanel_WithTheFramesOwnLabels()
    {
        var target = NewTarget("NGC 7000");

        var id = _repository.CreateFromNights("NGC 7000 (Mar 2026)", null, target,
        [
            (Night(2), "Panel 1", "Panel 1"),
            (Night(1), null, " panel 1 "),
            (Night(1), "Panel 2", "Panel 2"),
        ]);

        var panels = Panels(id);
        Assert.Equal(new[] { "Panel 1", "Panel 2" }, panels.Select(panel => panel.PanelLabel));
        Assert.Equal(
            new[] { (Night(1), (string?)null), (Night(2), "Panel 1") },
            Rows(panels[0].Id).Select(row => (row.SessionDate, row.FrameLabel)));
        Assert.All(Rows(panels[0].Id), row => Assert.Equal(MosaicPanelSession.Included, row.Status));
    }

    [Fact]
    public void CreateFromNights_IntoAnExistingMosaic_UsesItsPanelOfThatLabel_AndAppendsNewOnes()
    {
        var target = NewTarget("NGC 7000");
        var id = _repository.Create("Alpha");
        var existing = _repository.AddPanel(id, "Panel 1");

        Assert.Equal(id, _repository.CreateFromNights(null, id, target,
            [(Night(1), "Panel 1", "PANEL 1"), (Night(1), "Panel 3", "Panel 3")]));

        Assert.Single(Rows(existing));
        Assert.Equal(new[] { "Panel 1", "Panel 3" }, Panels(id).Select(panel => panel.PanelLabel));
    }

    [Fact]
    public void CreateFromNights_ARefusal_WritesNothing()
    {
        var target = NewTarget("NGC 7000");
        var id = _repository.Create("Alpha");
        var panel = _repository.AddPanel(id, "Panel 1");
        _repository.IncludeNight(panel, target, Night(1), "Panel 1");

        Assert.Throws<NightAlreadyInMosaicException>(() => _repository.CreateFromNights(null, id, target,
            [(Night(2), "Panel 1", "Panel 2"), (Night(1), "Panel 1", "Panel 3")]));
        Assert.Throws<DuplicateMosaicNameException>(() => _repository.CreateFromNights("ALPHA", null, target,
            [(Night(3), null, "Panel 1")]));
        Assert.Throws<MosaicWriteException>(() => _repository.CreateFromNights("Beta", null, target,
            [(Night(3), null, "  ")]));

        Assert.Single(Panels(id));
        Assert.Single(Rows(panel));
        Assert.Single(_repository.ExistingNames());
    }

    // ---- suggestions -------------------------------------------------------------------

    private static SuggestionCandidate Candidate(string name, Guid target, string label, params DateOnly[] dates)
        => Candidate(name, [(target, label, dates)]);

    private static SuggestionCandidate Candidate(string name, IReadOnlyList<(Guid Target, string Label, DateOnly[] Dates)> entries)
    {
        var panels = entries
            .Select(entry => new SuggestionPanel(entry.Target, entry.Label, $"%{name}%Panel%{PanelTokens.NumberFromLabel(entry.Label)}%", entry.Dates))
            .ToList();
        return new SuggestionCandidate(
            name, name, panels, "high", "both",
            new SuggestionGeometry([.. panels.Select(panel => new GeometryPanel(panel.TargetId, panel.Label, 10.5, 41.25))], [60.123], 85.5),
            ["Only one panel found."],
            MosaicDetection.DedupSignature(name, panels.Select(panel => panel.TargetId), panels.Select(panel => panel.Label)));
    }

    [Fact]
    public void ReplacePending_RoundTripsEveryField_ThroughListPending()
    {
        var target = NewTarget("NGC 7000");
        var candidate = Candidate("NGC 7000", target, "Panel 1", Night(1), Night(2));

        _repository.ReplacePending([candidate]);

        var row = Assert.Single(_repository.ListPending());
        Assert.Equal(("NGC 7000", "NGC 7000", "high", "both"), (row.SuggestedName, row.BaseName, row.Confidence, row.DiscoverySource));
        var panel = Assert.Single(row.Panels);
        Assert.Equal((target, "Panel 1", "%NGC 7000%Panel%1%"), (panel.TargetId, panel.Label, panel.Pattern));
        Assert.Equal(new[] { Night(1), Night(2) }, panel.Dates);
        Assert.Equal(candidate.Geometry.Pitches, row.Geometry!.Pitches);
        Assert.Equal(85.5, row.Geometry.FovArcmin);
        Assert.Equal(41.25, Assert.Single(row.Geometry.Panels).Dec);
        Assert.Equal(["Only one panel found."], row.Flags);
        Assert.Equal(candidate.DedupSignature, row.DedupSignature);

        using var context = Open();
        var stored = context.MosaicSuggestions.Single();
        Assert.Equal($"[\"{target}\"]".ToUpperInvariant(), stored.TargetIds.ToUpperInvariant());
        Assert.Equal("[\"Panel 1\"]", stored.PanelLabels);
        Assert.Contains("\"dates\":[\"2026-03-01\",\"2026-03-02\"]", stored.SessionDates, StringComparison.Ordinal);
        Assert.Contains("\"fov_arcmin\":85.5", stored.Geometry, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplacePending_DeletesPendingRowsOnly_LeavingAcceptedAndRejectedOnes()
    {
        var target = NewTarget("NGC 7000");
        _repository.ReplacePending(
        [
            Candidate("A", target, "Panel 1", Night(1)),
            Candidate("B", target, "Panel 2", Night(1)),
            Candidate("C", target, "Panel 3", Night(1)),
        ]);
        var pending = _repository.ListPending();
        _repository.Accept(pending.Single(row => row.SuggestedName == "A").Id, ["Panel 1"]);
        _repository.Dismiss(pending.Single(row => row.SuggestedName == "B").Id);

        _repository.ReplacePending([Candidate("D", target, "Panel 4", Night(1))]);

        using var context = Open();
        Assert.Equal(
            new[] { ("A", "accepted"), ("B", "rejected"), ("D", "pending") },
            context.MosaicSuggestions.OrderBy(row => row.SuggestedName).AsEnumerable().Select(row => (row.SuggestedName, row.Status)));
    }

    [Fact]
    public void ListPending_HidesASuggestionWhoseNameAMosaicCarries_AndOrdersByName()
    {
        var target = NewTarget("NGC 7000");
        _repository.ReplacePending(
        [
            Candidate("beta", target, "Panel 1", Night(1)),
            Candidate("Alpha", target, "Panel 1", Night(1)),
            Candidate("Gamma", target, "Panel 1", Night(1)),
        ]);
        _repository.Create("GAMMA");

        Assert.Equal(new[] { "Alpha", "beta" }, _repository.ListPending().Select(row => row.SuggestedName));
    }

    [Fact]
    public void Accept_WritesIncludedRowsForTheDates_AndAvailableRowsForTheLabelsOtherNights()
    {
        var target = NewTarget("NGC 7000");
        Frames(target, 1, "Panel 1");
        Frames(target, 1, "Panel 2");
        Frames(target, 9, "Panel 1");
        Frames(target, 9, "Panel 3");
        Frames(target, 9, null);
        _repository.ReplacePending([Candidate("NGC 7000",
        [
            (target, "Panel 1", [Night(1)]),
            (target, "Panel 2", [Night(1)]),
        ])]);
        var suggestion = Assert.Single(_repository.ListPending());

        var id = _repository.Accept(suggestion.Id, ["Panel 1", "Panel 2"]);

        var panels = Panels(id);
        Assert.Equal(new[] { "Panel 1", "Panel 2" }, panels.Select(panel => panel.PanelLabel));
        Assert.Equal(
            new[] { (Night(1), (string?)"Panel 1", "included"), (Night(9), "Panel 1", "available") },
            Rows(panels[0].Id).Select(row => (row.SessionDate, row.FrameLabel, row.Status)));
        Assert.Equal(
            new[] { (Night(1), (string?)"Panel 2", "included") },
            Rows(panels[1].Id).Select(row => (row.SessionDate, row.FrameLabel, row.Status)));
        using var context = Open();
        Assert.Equal(MosaicSuggestion.Accepted, context.MosaicSuggestions.Single().Status);
        Assert.Equal("NGC 7000", context.Mosaics.Single().Name);
    }

    [Fact]
    public void Accept_AnUncheckedLabel_MakesNoPanel_AndEntriesSharingALabelShareOne()
    {
        var a = NewTarget("Alpha Nebula");
        var b = NewTarget("Beta Nebula");
        _repository.ReplacePending([Candidate("Field",
        [
            (a, "Panel 1", [Night(1)]),
            (b, "Panel 1", [Night(2)]),
            (b, "Panel 2", [Night(2)]),
        ])]);

        var id = _repository.Accept(_repository.ListPending().Single().Id, ["panel 1"]);

        var panel = Assert.Single(Panels(id));
        Assert.Equal("Panel 1", panel.PanelLabel);
        Assert.Equal(new[] { a, b }.Order(), Rows(panel.Id).Select(row => row.TargetId).Order());
    }

    [Fact]
    public void Accept_WithNoLabelChecked_OrATakenName_IsRefusedAndWritesNothing()
    {
        var target = NewTarget("NGC 7000");
        _repository.ReplacePending([Candidate("NGC 7000", target, "Panel 1", Night(1))]);
        var id = _repository.ListPending().Single().Id;

        Assert.Equal("Select at least one panel to accept.",
            Assert.Throws<MosaicWriteException>(() => _repository.Accept(id, [])).Message);
        _repository.Create("ngc 7000");
        Assert.Throws<DuplicateMosaicNameException>(() => _repository.Accept(id, ["Panel 1"]));

        using var context = Open();
        Assert.Equal(MosaicSuggestion.Pending, context.MosaicSuggestions.Single().Status);
        Assert.Empty(context.MosaicPanels);
    }

    // Review fix 4: only a pending row can be accepted.
    [Fact]
    public void Accept_ARowThatIsNoLongerPending_IsRefused()
    {
        var target = NewTarget("NGC 7000");
        _repository.ReplacePending([Candidate("NGC 7000", target, "Panel 1", Night(1))]);
        var id = _repository.ListPending().Single().Id;
        _repository.Dismiss(id);

        Assert.Throws<KeyNotFoundException>(() => _repository.Accept(id, ["Panel 1"]));
        Assert.Empty(_repository.ExistingNames());
    }

    // Review fix 4: an entry whose target was merged away since detection writes no row.
    [Fact]
    public void Accept_SkipsATargetMergedAwaySinceDetection()
    {
        var kept = NewTarget("NGC 7000");
        var gone = NewTarget("North America Nebula");
        _repository.ReplacePending([Candidate("Field", [(kept, "Panel 1", [Night(1)]), (gone, "Panel 2", [Night(1)])])]);
        new MergeRepository(new DatabaseConnectionString(_db.ConnectionString)).Merge(kept, gone);

        var id = _repository.Accept(_repository.ListPending().Single().Id, ["Panel 1", "Panel 2"]);

        var panel = Assert.Single(Panels(id));
        Assert.Equal("Panel 1", panel.PanelLabel);
        Assert.Equal(kept, Assert.Single(Rows(panel.Id)).TargetId);
    }

    [Fact]
    public void Dismiss_SetsRejected_AndDismissedSignaturesPoolTheNightsOfOneSignature()
    {
        var target = NewTarget("NGC 7000");
        _repository.ReplacePending([Candidate("NGC 7000", target, "Panel 1", Night(1), Night(2))]);
        _repository.Dismiss(_repository.ListPending().Single().Id);
        _repository.ReplacePending([Candidate("NGC 7000", target, "Panel 1", Night(5)) with { SuggestedName = "NGC 7000 (#2)" }]);
        var second = _repository.ListPending().Single();
        _repository.Dismiss(second.Id);

        var dismissed = Assert.Single(_repository.DismissedSignatures());

        Assert.Equal(second.DedupSignature, dismissed.Signature);
        Assert.Equal(new[] { Night(1), Night(2), Night(5) }, dismissed.Dates.Order());
        Assert.Empty(_repository.ListPending());
    }

    [Fact]
    public void CoveredTriples_AreTheIncludedRowsWithALabel_ComparedCaseInsensitively()
    {
        var target = NewTarget("NGC 7000");
        var panel = _repository.AddPanel(_repository.Create("Alpha"), "Panel 1");
        _repository.IncludeNight(panel, target, Night(1), "Panel 1");
        _repository.IncludeNight(panel, target, Night(2), null);
        _repository.AddTargetNights(panel, target);

        var covered = _repository.CoveredTriples();

        Assert.Single(covered);
        Assert.Contains((target, Night(1), "PANEL 1"), covered);
    }

    // ---- layout (Phase 19A ships the caller) --------------------------------------------

    [Fact]
    public void UpdateLayout_WritesTheRotationAndEveryPanel_InOneCall()
    {
        var id = _repository.Create("Alpha");
        var first = _repository.AddPanel(id, "Panel 1");
        var second = _repository.AddPanel(id, "Panel 2");

        _repository.UpdateLayout(id, -12.5, [(first, 10, 20, 90, true), (second, null, null, 0, false)]);

        using var context = Open();
        Assert.Equal(-12.5, context.Mosaics.Single().RotationAngle);
        var panels = context.MosaicPanels.OrderBy(panel => panel.SortOrder).ToList();
        Assert.Equal((10d, 20d, 90, true), (panels[0].CanvasX!.Value, panels[0].CanvasY!.Value, panels[0].Rotation, panels[0].FlipH));
        Assert.Equal(((double?)null, (double?)null, 0, false), (panels[1].CanvasX, panels[1].CanvasY, panels[1].Rotation, panels[1].FlipH));
    }

    [Fact]
    public void UpdateLayout_ARotationOutsideTheFourAngles_WritesNothing()
    {
        var id = _repository.Create("Alpha");
        var first = _repository.AddPanel(id, "Panel 1");
        var second = _repository.AddPanel(id, "Panel 2");

        Assert.Throws<ArgumentOutOfRangeException>(
            () => _repository.UpdateLayout(id, 5, [(first, 1, 1, 90, false), (second, 1, 1, 45, false)]));

        using var context = Open();
        Assert.Equal(0d, context.Mosaics.Single().RotationAngle);
        Assert.All(context.MosaicPanels, panel => Assert.Null(panel.CanvasX));
    }

    [Fact]
    public void AWriteToANight_TouchesTheMosaicsUpdatedAt()
    {
        var target = NewTarget("NGC 7000");
        var id = _repository.Create("Alpha");
        var panel = _repository.AddPanel(id, "Panel 1");
        DateTime before;
        using (var context = Open()) before = context.Mosaics.Single().UpdatedAt;
        Thread.Sleep(15);

        _repository.IncludeNight(panel, target, Night(1), null);

        using (var context = Open()) Assert.True(context.Mosaics.Single().UpdatedAt > before);
    }
}
