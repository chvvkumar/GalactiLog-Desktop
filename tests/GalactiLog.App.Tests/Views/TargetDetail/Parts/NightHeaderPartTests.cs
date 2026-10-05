using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.CustomColumns;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.ViewModels;
using GalactiLog.App.Views.TargetDetail.Parts;
using GalactiLog.Core.Phd2;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;
using static GalactiLog.App.Tests.Views.TargetDetail.Parts.NightPartsTestKit;
using Cards = GalactiLog.App.Tests.TestSupport.SessionCardViewModelTestFactory;
using Page = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

namespace GalactiLog.App.Tests.Views.TargetDetail.Parts;

// Content cases of NightHeaderPart: the night title, the facts line, the failure callout.
public class NightHeaderPartTests
{
    [AvaloniaFact]
    public void NightHeaderPart_SingleRigNight_RendersNoRigCount()
    {
        // The comp's ledger has no rig marker and no notes marker, so IsMultiRig, RigCountText and
        // HasNotesIndicator went with the retired card (phase review P2-4). What is left to assert
        // is the rendering: the part names no rig count at all.
        using var harness = Cards.Create(
            overview: Page.Session(Page.LastSession) with { RigCount = 1, HasNotes = false });
        var part = new NightHeaderPart { DataContext = harness.Card };
        Show(part);

        Assert.DoesNotContain(VisibleTexts(part), text => text.EndsWith(" rigs", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void NightHeaderPart_WithNoOutlierPill_StillShowsTheNightDetailGlyph()
    {
        // The night-detail glyph is the header's own and does not follow the frames chrome.
        using var harness = Cards.Create(detail: Cards.PopulatedDetail(insights: []));
        harness.Card.IsExpanded = true;
        harness.Settle();
        Assert.Empty(harness.Card.OutlierPills);
        var part = new NightHeaderPart { DataContext = harness.Card };
        Show(part);

        var glyph = part.Named<GalactiLog.App.Controls.HelpButton>("NightDetailHelp");
        Assert.True(glyph.IsEffectivelyVisible);
        Assert.Equal("target.night-detail", glyph.Topic);
    }

    [AvaloniaFact]
    public void NightHeaderPart_MergedCardWhileLoading_NamesTheNightCount()
    {
        // P25 R8: the loading line counts the member nights on a merged card. The query is parked
        // inside the delegate so the card is genuinely loading while the part is read.
        var nights = new[] { Page.LastSession.AddDays(-1), Page.LastSession };
        using var harness = Cards.Create(nights: nights);
        harness.QueryGate.Reset();
        harness.Card.IsExpanded = true;
        Assert.True(harness.WaitForQuery(), "the session query never ran");

        var part = new NightHeaderPart { DataContext = harness.Card };
        Show(part);

        Assert.True(harness.Card.IsLoading);
        Assert.Contains("Loading 2 nights...", VisibleTexts(part));
        Assert.DoesNotContain("Loading this session...", VisibleTexts(part));

        harness.QueryGate.Set();
        harness.Settle();
    }

    [AvaloniaFact]
    public void NightHeaderPart_FailedExpansion_RendersTheWarningCallout()
    {
        using var harness = Cards.Create();
        harness.Throws = new InvalidOperationException("the database is locked");
        harness.Card.IsExpanded = true;
        harness.Settle();

        var part = new NightHeaderPart { DataContext = harness.Card };
        Show(part);

        // The failure is on the part, not swallowed and not a blank page: the night header still
        // renders because it never needed the query.
        Assert.True(part.Named<Border>("FailureCallout").IsVisible);
        Assert.Contains("Night of 2025-12-07", VisibleTexts(part));
    }

    [Fact]
    public void FactsLineText_DropsEveryAbsentClause()
    {
        using var harness = Cards.Create(detail: Cards.PopulatedDetail() with
        {
            Gain = null,
            ExposureTimes = [],
            MedianAirmass = null,
            MedianAmbientTemp = null,
            MedianHumidity = null,
        });

        harness.Card.IsExpanded = true;
        harness.Settle();

        // An unmeasured night reads as a shorter sentence, never as a row of empty labels.
        Assert.Equal("21:05 to 03:40", harness.Card.FactsLineText);
    }

    [Fact]
    public void FactsLineText_JoinsEveryPresentClauseInSpecOrder()
    {
        using var harness = Cards.Create();
        harness.Card.IsExpanded = true;
        harness.Settle();

        Assert.Equal(
            "21:05 to 03:40, gain 100, 180 s, 300 s, airmass 1.23, 4.5 C, 62 %",
            harness.Card.FactsLineText);
    }
}
