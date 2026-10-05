using GalactiLog.Core.Help;
using Xunit;

namespace GalactiLog.Core.Tests.Help;

// Spec 12.12's help table, asserted where it is cheapest: in Core, with no toolkit and no
// shell. The placement side of the contract is GalactiLog.App.Tests' HelpPlacementCensusTest;
// this file is the table itself.
public class HelpTopicsTests
{
    // The figure appears here and in no other assertion in the repository. Spec 12.12's
    // table is the contract; 89 is its consequence (Phase 17 Task 7 added `page.analysis` and
    // the six `analysis.*` ids: `analysis.filters`, `analysis.correlation`,
    // `analysis.distributions`, `analysis.timeseries`, `analysis.matrix` and
    // `analysis.compare`), and the census compares the placed set against HelpTopics.Ids rather
    // than against a second transcription of the ids. Phase 20 Task 7 added four more:
    // `dashboard.custom`, `settings.display.ledger-columns`, `settings.custom-columns.add` and
    // `settings.custom-columns.table`, taking the count to 93. Phase 21 Task 6 added the four
    // `settings.external-tools.*` ids (`astrobin-filters`, `bortle`, `nina` and `stellarium`),
    // taking the count to 97. Phase 22 unit B added `settings.general.survey-downloads`, taking
    // the count to 98. Unit D added `page.sky-view`, taking the count to 99.
    // The export wizard added `export.destination`, `export.method`, `export.review` and `export.result`, taking it to 103.
    [Fact]
    public void All_Holds103Topics()
    {
        Assert.Equal(103, HelpTopics.All.Count);
        Assert.Equal(103, HelpTopics.Ids.Count);
    }

    // A typo in the id or a paragraph that names no host would still leave the count at 103 (the
    // record exists), and the census reports it only as "placed nowhere" once the glyph lands.
    [Fact]
    public void ThePageSurveyViewTopic_ExistsAndNamesTheOneHost()
    {
        var topic = HelpTopics.Get("page.sky-view");

        Assert.Equal("Sky view", topic.Title);
        Assert.Contains("alasky.cds.unistra.fr", topic.Paragraph, StringComparison.Ordinal);
    }

    // Phase 20 Task 7. A typo in one of the four new ids would still leave the count at 93 (the
    // record exists, just under the wrong id), and the census would only report it as "placed
    // nowhere" once a placing unit lands, which is a Wave 2 report and not this one. Pinning the
    // exact ids and titles here catches the typo where it is introduced.
    [Fact]
    public void TheFourPhase20Topics_ExistAndCarryTheirTitles()
    {
        Assert.Equal("Custom", HelpTopics.Get("dashboard.custom").Title);
        Assert.Equal("Nights ledger columns", HelpTopics.Get("settings.display.ledger-columns").Title);
        Assert.Equal("Add a column", HelpTopics.Get("settings.custom-columns.add").Title);
        Assert.Equal("Columns", HelpTopics.Get("settings.custom-columns.table").Title);
    }

    // Phase 20 Task 7, brief section 2.2. None of the four drafted paragraphs may enter the five
    // longest: a paragraph that does becomes spec text under
    // TheLongestParagraphs_MatchTheSpecTextExactly, and Task 8 has not folded the spec into
    // design-spec.md yet, so that case failing here is the instruction to escalate rather than to
    // add a transcription.
    [Fact]
    public void NoneOfTheFour_EntersTheFiveLongestParagraphs()
    {
        var phase20Ids = new HashSet<string>(StringComparer.Ordinal)
        {
            "dashboard.custom",
            "settings.display.ledger-columns",
            "settings.custom-columns.add",
            "settings.custom-columns.table",
        };

        var longestIds = FiveLongest().Select(topic => topic.Id).ToList();

        foreach (var id in phase20Ids)
        {
            Assert.DoesNotContain(id, longestIds);
        }
    }

    // Phase 21 Task 6, task6-help.md section 5 case 2. A typo in one of the four new ids would
    // still leave the count at 97 (the record exists, just under the wrong id), and the census
    // reports it only as "placed nowhere" once Task 4 lands, which is a later unit's report.
    // Pinning the exact ids and titles here catches the typo where it is introduced.
    [Fact]
    public void TheFourPhase21Topics_ExistAndCarryTheirTitles()
    {
        Assert.Equal("AstroBin filter ids", HelpTopics.Get("settings.external-tools.astrobin-filters").Title);
        Assert.Equal("Bortle class", HelpTopics.Get("settings.external-tools.bortle").Title);
        Assert.Equal("NINA instances", HelpTopics.Get("settings.external-tools.nina").Title);
        Assert.Equal("Stellarium instances", HelpTopics.Get("settings.external-tools.stellarium").Title);
    }

    // Phase 21 Task 6, task6-help.md section 5 case 3. None of the four drafted paragraphs may
    // enter the five longest: a paragraph that does becomes spec text under
    // TheLongestParagraphs_MatchTheSpecTextExactly, and Task 7 has not folded the spec into
    // design-spec.md yet, so that case failing here is the instruction to escalate rather than to
    // add a transcription.
    [Fact]
    public void NoneOfTheFourPhase21Topics_EntersTheFiveLongestParagraphs()
    {
        var phase21Ids = new HashSet<string>(StringComparer.Ordinal)
        {
            "settings.external-tools.astrobin-filters",
            "settings.external-tools.bortle",
            "settings.external-tools.nina",
            "settings.external-tools.stellarium",
        };

        var longestIds = FiveLongest().Select(topic => topic.Id).ToList();

        foreach (var id in phase21Ids)
        {
            Assert.DoesNotContain(id, longestIds);
        }
    }

    // Phase 21 Task 6, task6-help.md section 5 case 4. Spec 19.2's standing promise is that
    // GalactiLog opens no listening socket; the NINA topic is where a reader asking "does this
    // open a port" lands, so a brevity pass deleting the one sentence that answers it is the
    // failure this case pins against.
    [Fact]
    public void ExternalToolsNina_SaysNothingListensOnThisMachine()
    {
        var paragraph = HelpTopics.Get("settings.external-tools.nina").Paragraph;

        Assert.Contains(
            "Nothing listens on this machine: GalactiLog only ever makes the call.",
            paragraph,
            StringComparison.Ordinal);
    }

    // Phase 21 Task 6, task6-help.md section 5 case 5. A blank filter id still gets its own row
    // in the AstroBin CSV rather than being dropped, which is easy to misstate as the opposite.
    [Fact]
    public void ExternalToolsAstrobinFilters_SaysABlankIdStillGetsARow()
    {
        var paragraph = HelpTopics.Get("settings.external-tools.astrobin-filters").Paragraph;

        Assert.Contains(
            "A filter you leave blank still gets its own row in the AstroBin CSV",
            paragraph,
            StringComparison.Ordinal);
    }

    // Fix pass 1, task7-review.md P1-1. The landed sentence was a blanket "a new column appears
    // nowhere until you switch it on", which spec-draft.md 12.15's "Where the cells appear" table
    // contradicts for the session and rig scopes: neither has a picker at all (questions.md
    // question 4), so a Night or Rig column is visible the moment it is created. Pinned on the
    // corrected per-scope sentence, with the false blanket reading pinned absent so a later
    // brevity pass cannot restore it.
    [Fact]
    public void CustomColumnsAdd_StatesThePerScopeVisibilityGates()
    {
        var paragraph = HelpTopics.Get("settings.custom-columns.add").Paragraph;

        Assert.Contains(
            "stays hidden on the dashboard until you switch it on in the column picker",
            paragraph,
            StringComparison.Ordinal);
        // The place is named rather than pointed at. "There" read as the Nights list, which has no
        // picker of its own: the control is on the Display tab, under "Nights ledger columns", which
        // is what settings.display.ledger-columns describes. The old phrasing is pinned absent
        // below, so a later brevity pass cannot put the misdirection back.
        Assert.Contains(
            "stays hidden on the Nights list until you switch it on in Settings, under Display",
            paragraph,
            StringComparison.Ordinal);
        Assert.Contains(
            "Night columns appear in the dashboard's night expander and Rig columns appear on a "
            + "night's rig lines straight away",
            paragraph,
            StringComparison.Ordinal);
        Assert.DoesNotContain("appears nowhere until you switch it on", paragraph, StringComparison.Ordinal);
        Assert.DoesNotContain("until you switch it on there", paragraph, StringComparison.Ordinal);
    }

    // Ruling C36, which replaces the count ruling C34 asked for. No count is defensible: the Nights
    // list draws nine headings, only seven of them figures, and a tenth grid column is the
    // heading-less expander. The paragraph names no number, and this case pins the absence of every
    // figure the wording has carried, so a future sweep cannot quietly put one back.
    //
    // Red against the landed wording: it read "the ten figures already on that list" and "The ten
    // built-in columns", so both Contains failed and DoesNotContain("ten ") failed with them.
    [Fact]
    public void LedgerColumns_NamesNoColumnCount()
    {
        var paragraph = HelpTopics.Get("settings.display.ledger-columns").Paragraph;

        Assert.Contains(
            "so the columns already on that list never lose room", paragraph, StringComparison.Ordinal);
        Assert.Contains("The built-in columns are always shown", paragraph, StringComparison.Ordinal);
        Assert.DoesNotContain("eight", paragraph, StringComparison.Ordinal);
        Assert.DoesNotContain("ten ", paragraph, StringComparison.Ordinal);
        Assert.DoesNotContain(" nine", paragraph, StringComparison.Ordinal);
    }

    // Fix pass 1, task7-review.md P3-1. A second column with the same name is refused
    // (questions.md question 14), stated where the create form's other creation rules already
    // are.
    [Fact]
    public void CustomColumnsAdd_StatesTheDuplicateNameRefusal()
    {
        var paragraph = HelpTopics.Get("settings.custom-columns.add").Paragraph;

        Assert.Contains("A second column with the same name is refused", paragraph, StringComparison.Ordinal);
    }

    // Fix pass 1, task7-review.md P2-1. The Delete sentence said only "asks once", never that the
    // first press states the count of values about to go, unlike the neighbouring
    // removed-choice sentence in the same paragraph which already says how many. Pinned on the
    // corrected two-press wording with the old single-dialog phrasing pinned absent.
    [Fact]
    public void CustomColumnsTable_DeleteStatesTheCount()
    {
        var paragraph = HelpTopics.Get("settings.custom-columns.table").Paragraph;

        Assert.Contains(
            "The first press of Delete arms it and states how many stored values will go with "
            + "the column",
            paragraph,
            StringComparison.Ordinal);
        Assert.Contains(
            "the second press removes the column and every one of those values",
            paragraph,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Delete asks once", paragraph, StringComparison.Ordinal);
    }

    // Fix pass 1, task7-review.md P3-1. Clearing a text value and choosing "Not set" on a list
    // both remove the value entirely rather than storing a blank (questions.md questions 11 and
    // 12), stated where this topic already discusses "the values you have filled in yourself".
    [Fact]
    public void DashboardCustom_CoversClearingAValue()
    {
        var paragraph = HelpTopics.Get("dashboard.custom").Paragraph;

        Assert.Contains("Clearing a text value removes it entirely", paragraph, StringComparison.Ordinal);
        Assert.Contains(
            "choosing \"Not set\" on a list column clears its value the same way",
            paragraph,
            StringComparison.Ordinal);
    }

    // The page has one layout; a paragraph that names a layout box, a selector or another layout
    // describes controls that no longer exist.
    [Fact]
    public void PageTarget_DescribesOneLayoutAndNoSelector()
    {
        var paragraph = HelpTopics.Get("page.target").Paragraph;

        Assert.Contains(
            "The page is one layout, Question Modes, with a mode switch under the header: Night review, "
            + "Compare nights, and Integration",
            paragraph,
            StringComparison.Ordinal);
        Assert.Contains("The ledger is a sidebar: drag the handle on its right edge", paragraph, StringComparison.Ordinal);
        Assert.Contains("the chevron in its header collapses it", paragraph, StringComparison.Ordinal);
        Assert.DoesNotContain("lit ledger row", paragraph, StringComparison.Ordinal);
        foreach (var gone in new[] { "layout box", "four layouts", "Night Bench", "Cascading Rows", "Aligned Tracks", "brush" })
        {
            Assert.DoesNotContain(gone, paragraph, StringComparison.Ordinal);
        }
    }

    // The paragraph is under the fifth longest; growing into the five would make
    // TheLongestParagraphs_MatchTheSpecTextExactly demand a transcription of it.
    [Fact]
    public void PageTarget_StaysOutOfTheFiveLongestParagraphs()
    {
        Assert.DoesNotContain("page.target", FiveLongest().Select(topic => topic.Id));
    }

    // The night's parts sit on the page; a paragraph that sends the reader to a drawer or a
    // button describes a control that no longer exists.
    [Fact]
    public void TargetNight_SaysWhereTheNightPartsAre()
    {
        var paragraph = HelpTopics.Get("target.night").Paragraph;

        Assert.Contains("the Night metrics section holds the per-filter table, the ranges, the comparison line and the sharpest frame", paragraph, StringComparison.Ordinal);
        Assert.Contains("Session notes as a section at the bottom of the lanes", paragraph, StringComparison.Ordinal);
        Assert.DoesNotContain("Night notes", paragraph, StringComparison.Ordinal);
        Assert.DoesNotContain("left column", paragraph, StringComparison.Ordinal);
        Assert.DoesNotContain("drawer", paragraph, StringComparison.Ordinal);
        Assert.DoesNotContain("Night detail button", paragraph, StringComparison.Ordinal);
        Assert.DoesNotContain("Night Bench", paragraph, StringComparison.Ordinal);
    }

    // Phase 25 R8: the ledger paragraph names the three selection gestures and the night
    // paragraph says what several checked nights look like. Neither may grow into the five
    // longest, which would make it spec text under TheLongestParagraphs_MatchTheSpecTextExactly.
    [Fact]
    public void TargetNightsAndTargetNight_SayHowSeveralNightsAreReviewed()
    {
        var nights = HelpTopics.Get("target.nights").Paragraph;
        var night = HelpTopics.Get("target.night").Paragraph;

        Assert.Contains("A plain click lights one night and clears the checks", nights, StringComparison.Ordinal);
        Assert.Contains("Ctrl and click adds or removes that night", nights, StringComparison.Ordinal);
        Assert.Contains("Shift and click adds the range", nights, StringComparison.Ordinal);
        Assert.Contains("several nights are checked they show here as one night", night, StringComparison.Ordinal);
        Assert.Contains("a dashed line at the start of each night", night, StringComparison.Ordinal);

        var longestIds = FiveLongest().Select(topic => topic.Id).ToList();
        Assert.DoesNotContain("target.nights", longestIds);
        Assert.DoesNotContain("target.night", longestIds);
    }

    [Fact]
    public void TargetSessionMetrics_DescribesTheLanesHandle()
    {
        var paragraph = HelpTopics.Get("target.session-metrics").Paragraph;

        Assert.Contains(
            "Drag the handle between the night charts and the frames table to trade chart height "
            + "for frame rows",
            paragraph,
            StringComparison.Ordinal);
        Assert.Contains("Up and Down move it from the keyboard", paragraph, StringComparison.Ordinal);
        Assert.Contains("a double click returns it to automatic", paragraph, StringComparison.Ordinal);
        Assert.Contains("The height is kept per profile", paragraph, StringComparison.Ordinal);
        Assert.DoesNotContain("per layout", paragraph, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetSessionMetrics_DescribesTheOneHandleSizingTheChart()
    {
        // Red if the retired second handle (Phase 24 R31) is back in the topic, or the chart's
        // floor and default under the one handle are missing (night pane round).
        var paragraph = HelpTopics.Get("target.session-metrics").Paragraph;

        Assert.DoesNotContain("bottom edge", paragraph, StringComparison.Ordinal);
        Assert.Contains("The chart takes what the handle leaves under the timeline", paragraph, StringComparison.Ordinal);
        Assert.Contains("never under 120", paragraph, StringComparison.Ordinal);
        Assert.Contains("automatic gives it 180", paragraph, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryId_IsUnique()
    {
        var ids = HelpTopics.All.Select(topic => topic.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryTopic_HasATitleAndAParagraph()
    {
        foreach (var topic in HelpTopics.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(topic.Id));
            Assert.False(string.IsNullOrWhiteSpace(topic.Title));
            Assert.False(string.IsNullOrWhiteSpace(topic.Paragraph));

            // A paragraph is one to five sentences of plain text with no markup, so no
            // wrapped fragment may have lost or gained a space at a join.
            Assert.DoesNotContain("  ", topic.Paragraph, StringComparison.Ordinal);
            Assert.Equal(topic.Paragraph.Trim(), topic.Paragraph);
            Assert.EndsWith(".", topic.Paragraph, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Get_ReturnsTheTopic()
    {
        var topic = HelpTopics.Get("page.dashboard");

        Assert.Equal("page.dashboard", topic.Id);
        Assert.Equal("Dashboard", topic.Title);
        Assert.Same(topic, HelpTopics.All[0]);
    }

    [Fact]
    public void Get_OnAnUnknownId_Throws()
    {
        Assert.Throws<KeyNotFoundException>(() => HelpTopics.Get("page.nowhere"));

        // Ordinal, so a case difference is a different id rather than the same one.
        Assert.Throws<KeyNotFoundException>(() => HelpTopics.Get("Page.Dashboard"));
    }

    [Fact]
    public void TryGet_OnAnUnknownId_IsFalse()
    {
        Assert.False(HelpTopics.TryGet("page.nowhere", out var missing));
        Assert.Null(missing);

        Assert.True(HelpTopics.TryGet("target.frames", out var found));
        Assert.Equal("Per-frame grading", found.Title);
    }

    // The prose rule of the documentation style, enforced where it is cheapest. The port
    // writes no em dash and no en dash anywhere, and a help paragraph is the one place a
    // pasted web original would carry one in.
    [Fact]
    public void NoParagraph_ContainsAnEmOrEnDash()
    {
        var emDash = ((char)0x2014).ToString();
        var enDash = ((char)0x2013).ToString();

        foreach (var topic in HelpTopics.All)
        {
            Assert.DoesNotContain(emDash, topic.Paragraph, StringComparison.Ordinal);
            Assert.DoesNotContain(enDash, topic.Paragraph, StringComparison.Ordinal);
            Assert.DoesNotContain(emDash, topic.Title, StringComparison.Ordinal);
            Assert.DoesNotContain(enDash, topic.Title, StringComparison.Ordinal);
        }
    }

    // Ruling C2 made executable. This port grades in raw MAD units and its help text says so;
    // the web said sigma. The shipped form of the rule is the second of the two the brief
    // offered: no paragraph carries the Greek letter, and no paragraph claims a figure is
    // sigma. The one legal occurrence of the word is target.frames' "not sigma" clause and
    // its "about 0.67 sigma" conversion, which are what the correction says rather than a
    // relapse into it, so the word is allowed only in that topic and only in those two
    // sentences.
    [Fact]
    public void NoParagraph_SaysSigma()
    {
        var greekSigma = ((char)0x03C3).ToString();

        foreach (var topic in HelpTopics.All)
        {
            Assert.DoesNotContain(greekSigma, topic.Paragraph, StringComparison.Ordinal);

            if (topic.Id == "target.frames")
            {
                Assert.Contains("raw MAD units and not sigma", topic.Paragraph, StringComparison.Ordinal);
                Assert.Contains("one MAD unit is about 0.67 sigma", topic.Paragraph, StringComparison.Ordinal);
                continue;
            }

            Assert.DoesNotContain("sigma", topic.Paragraph, StringComparison.OrdinalIgnoreCase);
        }

        // And the correction itself is present rather than merely the absence of the word.
        Assert.Contains(
            HelpTopics.All,
            topic => topic.Paragraph.Contains("MAD units", StringComparison.Ordinal));
    }

    // Phase 15B Task 6. The two sentences the user asked for by name are the ones a later
    // brevity pass would drop first, so each is pinned on its own distinctive fragment rather
    // than trusted to a whole-paragraph literal (section 5.5 forbids that for these two ids).
    [Fact]
    public void TargetGuiding_CoversTheGate()
    {
        var paragraph = HelpTopics.Get("target.guiding").Paragraph;

        Assert.Contains("too short to grade", paragraph, StringComparison.Ordinal);
    }

    // Phase 24 R3. The Guiding section and its calibration chips are gone; the guide trace is the
    // night chart's Guiding pill. Both topics say so, and neither grows into the five longest,
    // which would make it spec text under TheLongestParagraphs_MatchTheSpecTextExactly.
    [Fact]
    public void TheTwoNightChartTopics_DescribeTheGuidingPill_AndNoChip()
    {
        var metrics = HelpTopics.Get("target.session-metrics").Paragraph;
        var guiding = HelpTopics.Get("target.guiding").Paragraph;

        Assert.Contains("Guiding pill", metrics, StringComparison.Ordinal);
        Assert.Contains("Guiding pill", guiding, StringComparison.Ordinal);
        Assert.Contains("whole night", guiding, StringComparison.Ordinal);
        Assert.DoesNotContain("calibration chips", guiding, StringComparison.Ordinal);
        Assert.DoesNotContain("Night detail", guiding, StringComparison.Ordinal);
    }

    [Fact]
    public void NeitherNightChartTopic_EntersTheFiveLongestParagraphs()
    {
        var longestIds = FiveLongest().Select(topic => topic.Id).ToList();

        Assert.DoesNotContain("target.session-metrics", longestIds);
        Assert.DoesNotContain("target.guiding", longestIds);
    }

    [Fact]
    public void StatsGuiding_CoversTheEightRigFloorAndTheAltitudeCard()
    {
        var paragraph = HelpTopics.Get("stats.guiding").Paragraph;

        // Section 4.3's sentence: the floor is eight rigs, not eight sessions or samples.
        Assert.Contains("means eight telescopes", paragraph, StringComparison.Ordinal);
        Assert.Contains("with one rig every cell is neutral", paragraph, StringComparison.Ordinal);

        // Section 4.4's sentence: the altitude card needs no observer coordinates.
        Assert.Contains("needs no observer coordinates", paragraph, StringComparison.Ordinal);
    }

    // Phase 15B fixer pass, phase review findings F5 and F11: two phrases these topics had wrong.
    // The scorecard's baseline is the median across every rig, the graded one included, and the
    // graph's bands are the windows where guiding was settling, which a dither does not open and
    // which a slew or a calibration also produces. Each correction is pinned on its own fragment,
    // with the wrong reading pinned absent, so a later brevity pass cannot quietly restore it.
    [Fact]
    public void StatsGuiding_TakesTheBaselineAcrossEveryRig()
    {
        var paragraph = HelpTopics.Get("stats.guiding").Paragraph;

        Assert.Contains("against the middle of your rigs", paragraph, StringComparison.Ordinal);
        Assert.DoesNotContain("your other rigs", paragraph, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetGuiding_NamesTheGraphsBandsAsTheSettleWindows()
    {
        var paragraph = HelpTopics.Get("target.guiding").Paragraph;

        Assert.Contains("the bands where guiding was settling", paragraph, StringComparison.Ordinal);
        Assert.DoesNotContain("after each dither", paragraph, StringComparison.Ordinal);
    }

    // Fixer-list item 86. Save as defaults writes the exclusion patterns alone since the phase
    // review's P2-1, and the staging folder is remembered by its own commit, so the closing
    // sentence no longer credits that button with the folder. This paragraph is outside the five
    // longest, so the correction is pinned on its own fragment with the wrong reading pinned
    // absent, the same shape the two guiding corrections above take.
    [Fact]
    public void ExportSettings_CreditsSaveAsDefaultsWithThePatternsAlone()
    {
        var paragraph = HelpTopics.Get("export.settings").Paragraph;

        Assert.Contains(
            "Save as defaults keeps the patterns for next time.",
            paragraph,
            StringComparison.Ordinal);
        Assert.DoesNotContain("keeps the folder", paragraph, StringComparison.Ordinal);

        // And the sentence that carries the true rule is still there, so a later brevity pass
        // cannot leave the paragraph silent about how the folder is remembered.
        Assert.Contains(
            "It is remembered the moment you choose it",
            paragraph,
            StringComparison.Ordinal);
    }

    // Phase 17 Task 7. The seven Analysis topics carry three corrections the port makes rather
    // than copies from the web's own popovers (spec 12.14's Help topics subsection, task7.md
    // section 2.1), plus three figures a reader is most likely to misread (task7.md section
    // 2.2). Each case pins its correction on its own distinctive fragment and pins the wrong,
    // web-shaped reading absent, so a later brevity pass cannot restore it.

    // Correction 1: the web's Matrix popover describes a different feature entirely (a heatmap
    // over two categorical axes). Every DoesNotContain fragment below is taken from the pinned
    // `AnalysisPage.tsx:317` paragraph itself (task7.md 5.1), so red against that paragraph's own
    // words fires all four (plus the added "n/a", an extension of the pinned wording).
    [Fact]
    public void AnalysisMatrix_DescribesThisTabAndNotTheWebsHeatmap()
    {
        var paragraph = HelpTopics.Get("analysis.matrix").Paragraph;

        Assert.Contains("Pearson correlation coefficients for every pair", paragraph, StringComparison.Ordinal);
        Assert.DoesNotContain("Heatmap grid", paragraph, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("categorical", paragraph, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("integration time per channel", paragraph, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("telescope by filter", paragraph, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("n/a", paragraph, StringComparison.OrdinalIgnoreCase);
    }

    // Correction 2: the pinned Time Series popover (`AnalysisPage.tsx:296`) says the metric can be
    // "either per frame or aggregated by session or night". It is always per night; the
    // granularity segment never reaches this tab. The forbidden fragment is "aggregated by
    // session", the pinned wording's own fragment (task7.md 5.2); "per frame" stays unasserted
    // because the corrected paragraph legitimately says "switching it between per frame and per
    // session changes nothing here".
    [Fact]
    public void AnalysisTimeseries_SaysAlwaysPerNight()
    {
        var paragraph = HelpTopics.Get("analysis.timeseries").Paragraph;

        Assert.Contains("One point per imaging night", paragraph, StringComparison.Ordinal);
        Assert.Contains("the granularity segment does not reach this tab", paragraph, StringComparison.Ordinal);
        Assert.DoesNotContain("aggregated by session", paragraph, StringComparison.OrdinalIgnoreCase);
    }

    // Correction 3: every topic that mentions the date range calls it the imaging night, never
    // the capture date, because that is the column the range filters. analysis.filters is NOT
    // exempt (ruling P2-17): the pinned web says "restrict by capture date" exactly there.
    // analysis.distributions is the one legal exception, because it also has to name the
    // exposure's own calendar month for By Month without letting the two collide, so there
    // "capture date" is confined to the sentence that also names the calendar month rather than
    // dropped from the check entirely.
    [Fact]
    public void EveryAnalysisTopic_CallsTheRangeTheImagingNight()
    {
        string[] ids =
        [
            "page.analysis", "analysis.filters", "analysis.correlation",
            "analysis.distributions", "analysis.timeseries", "analysis.matrix", "analysis.compare",
        ];

        foreach (var id in ids)
        {
            var paragraph = HelpTopics.Get(id).Paragraph;
            if (!paragraph.Contains("date range", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Assert.Contains("imaging night", paragraph, StringComparison.OrdinalIgnoreCase);

            if (id == "analysis.distributions")
            {
                // The legal exception: By Month reads the exposure's own calendar month while the
                // shared range reads the imaging night. "capture date" may appear only inside the
                // sentence that also names the calendar month; anywhere else, or nowhere, is fine.
                Assert.Contains("calendar month", paragraph, StringComparison.OrdinalIgnoreCase);

                foreach (var sentence in paragraph.Split(". ", StringSplitOptions.None))
                {
                    if (sentence.Contains("capture date", StringComparison.OrdinalIgnoreCase))
                    {
                        Assert.Contains("calendar month", sentence, StringComparison.OrdinalIgnoreCase);
                    }
                }

                continue;
            }

            Assert.DoesNotContain("capture date", paragraph, StringComparison.OrdinalIgnoreCase);
        }
    }

    // Figure 1 of 2.2: a per-session point is a night and a target, not a night and a rig. The
    // fragment naming the point's identity is pinned on its own sentence, and that sentence
    // (not the whole paragraph, which also legitimately says "rig" two sentences later while
    // explaining the two-rig example) is checked for the word, so the assertion is on the
    // fragment rather than on a blunt whole-paragraph absence.
    [Fact]
    public void AnalysisFilters_SaysAPerSessionPointIsANightAndATarget()
    {
        var paragraph = HelpTopics.Get("analysis.filters").Paragraph;
        const string fragment = "A per-session point is one night and one target.";

        Assert.Contains(fragment, paragraph, StringComparison.Ordinal);

        var sentence = paragraph
            .Split(". ", StringSplitOptions.None)
            .FirstOrDefault(s => s.Contains("one night and one target", StringComparison.Ordinal));
        Assert.NotNull(sentence);
        Assert.DoesNotContain("rig", sentence, StringComparison.OrdinalIgnoreCase);
    }

    // Figure 2 of 2.2: the matrix refuses to answer below 10 paired frames, and the cell that
    // gets no answer is named as blank rather than a placeholder value.
    [Fact]
    public void AnalysisMatrix_StatesTheTenFrameMinimum()
    {
        var paragraph = HelpTopics.Get("analysis.matrix").Paragraph;

        Assert.Contains("10", paragraph, StringComparison.Ordinal);
        Assert.Contains("blank", paragraph, StringComparison.Ordinal);
    }

    // Figure 3 of 2.2: the confidence band is indicative, not a true 95 percent interval on a
    // small point set. Red against the web's own "95% CI" framing taken literally, which would
    // name the multiplier or the distribution behind it.
    [Fact]
    public void AnalysisCorrelation_CallsTheBandIndicative()
    {
        var paragraph = HelpTopics.Get("analysis.correlation").Paragraph;

        Assert.Contains(
            "a rough guide rather than a true 95 percent interval",
            paragraph,
            StringComparison.Ordinal);
        Assert.DoesNotContain("1.96", paragraph, StringComparison.Ordinal);
        Assert.DoesNotContain("t distribution", paragraph, StringComparison.OrdinalIgnoreCase);
    }

    // Spec 12.14: applying the bar's equipment and filter selections on top of Compare's own two
    // groups would let a reader compare two groups the bar had already emptied, which is why
    // Compare ignores both.
    [Fact]
    public void AnalysisCompare_SaysWhyTheBarsSelectionsAreIgnored()
    {
        var paragraph = HelpTopics.Get("analysis.compare").Paragraph;

        Assert.Contains(
            "compare two groups the bar had already emptied",
            paragraph,
            StringComparison.Ordinal);
    }

    // A box plot group with fewer than four frames is dropped silently, so the topic states the
    // minimum a group needs before it is drawn at all.
    [Fact]
    public void AnalysisDistributions_SaysAGroupNeedsFour()
    {
        var paragraph = HelpTopics.Get("analysis.distributions").Paragraph;

        Assert.Contains("group with fewer than four", paragraph, StringComparison.OrdinalIgnoreCase);
    }

    // The moving average window counts points, meaning nights that have a frame, not calendar
    // days, so a gap between imaged nights does not widen it. Red against a paragraph that
    // describes the window in fixed calendar days, such as "7 days".
    [Fact]
    public void AnalysisTimeseries_SaysTheWindowCountsNights()
    {
        var paragraph = HelpTopics.Get("analysis.timeseries").Paragraph;

        Assert.Contains("nights that have a frame, not calendar days", paragraph, StringComparison.Ordinal);
        Assert.DoesNotContain("7 days", paragraph, StringComparison.OrdinalIgnoreCase);
    }

    // The five longest paragraphs, each against the spec's text as one literal. The table is
    // wrapped across source lines and joined with single spaces, and a fragment that lost or
    // gained a space at a join is exactly the defect the eye does not catch.
    //
    // Fixer-list item 85. The five were named here as five InlineData ids, which were the five
    // longest when this was written and had stopped being so by Phase 16: the assertion still
    // passed while three of the longest paragraphs in the application were pinned by no byte-exact
    // case, and nothing could go red to say so. The set is now computed from HelpTopics.All
    // itself, so a topic that grows into it joins by being long rather than by being remembered,
    // and one that leaves it keeps its transcription here at no cost. A newcomer with no spec
    // transcription fails with its own name and its length, which is the instruction to transcribe
    // spec 12.12's row for it rather than to narrow the set.
    [Fact]
    public void TheLongestParagraphs_MatchTheSpecTextExactly()
    {
        foreach (var topic in FiveLongest())
        {
            Assert.True(
                Expected.ContainsKey(topic.Id),
                $"{topic.Id} is one of the five longest help paragraphs at "
                + $"{topic.Paragraph.Length} characters and this file holds no transcription of "
                + "spec 12.12's row for it. Transcribe the row into Expected below.");

            Assert.Equal(Expected[topic.Id], topic.Paragraph);
        }
    }

    // Pinned apart from the five longest, so a stale transcription cannot hide while the topic is shorter than they are.
    [Fact]
    public void TargetNightDetail_MatchesTheSpecTextExactly()
    {
        Assert.Equal(Expected["target.night-detail"], HelpTopics.Get("target.night-detail").Paragraph);
    }

    /// <summary>The five longest paragraphs, longest first, ties broken by id so one run's set is
    /// every run's set.</summary>
    private static IEnumerable<HelpTopic> FiveLongest()
        => HelpTopics.All
            .OrderByDescending(topic => topic.Paragraph.Length)
            .ThenBy(topic => topic.Id, StringComparer.Ordinal)
            .Take(5);

    /// <summary>Spec 12.12's paragraph table for the longest topics, transcribed here a
    /// second time on purpose: a literal that has to agree with the shipped table is the only
    /// check a wrapped source string cannot pass by accident. It holds at least the five longest
    /// and may hold more, because a paragraph that has fallen out of the five keeps its pin.
    /// </summary>
    private static readonly Dictionary<string, string> Expected = new(StringComparer.Ordinal)
    {
        // Phase 17 Task 7. analysis.filters entered the five longest the day it was written, at
        // 993 characters, ahead of export.quality. This is the instruction the case's own failure
        // message names: transcribe rather than narrow. The fix pass (review-p17-t7core majors 1
        // and 2) rewrote its opening clause, which moved this transcription to 1041 characters
        // without displacing it from the five longest.
        ["analysis.filters"] =
            "Five controls set the scope for the tabs below, though not every tab takes every one. "
            + "Equipment picks one telescope and camera combination or all of them, Filter "
            + "restricts to a single optical filter, Granularity "
            + "switches between per frame and per session, and the date range bounds the imaging "
            + "night. Granularity reaches only the Correlation tab and the Distributions histogram; "
            + "every other tab, including the Distributions box plot, ignores it. A per-session "
            + "point is one night and one target. A night on which two rigs imaged the same target "
            + "still gives one point, not two, because the grouping key is the night and the target, "
            + "never the rig. Compare ignores the equipment and filter controls entirely and reads "
            + "only the date range, because its own two group pickers are the equipment or filter "
            + "selection. Grouping two cameras together on the Equipment settings tab can fold two "
            + "equipment combinations into one, merging two rigs whose plate scales differ, so this "
            + "equipment list is only as separate as the alias groups you have configured.",
        // Phase 17 Task 7 fix pass. analysis.correlation grew past export.script into the five
        // longest (873 characters) once the band and Hide Outliers corrections (review-p17-t7core
        // mediums 4 and 5, minor 13) landed.
        ["analysis.correlation"] =
            "A scatter of one X metric against one Y metric across the filtered frames, drawn with a "
            + "trend line and a shaded confidence band. The trend, the band, the two stats cards and "
            + "the point counts are all computed over every point that matches the filters. Hide "
            + "Outliers removes only the points sitting outside 1.5 times the interquartile range on "
            + "either axis from what is drawn, changing none of those figures. The band is a rough "
            + "guide rather than a true 95 percent interval, and it is narrower than that interval "
            + "on a small point set. When a PHD2 guiding metric is chosen on the X axis, its values "
            + "are night-level figures joined by rig and imaging night, and a night with no mapped "
            + "PHD2 profile is omitted rather than shown as a gap. Past 5,000 points the chart draws "
            + "an even sample for speed, while the trend, the band and both stats cards still use "
            + "every point that matched.",
        ["stats.guiding"] =
            "Each rig here is the telescope your PHD2 profile is mapped to, through the same "
            + "equipment alias map the rest of the application uses, so two spellings of one scope "
            + "become one row and two cameras sharing one telescope share that row too. A session "
            + "whose profile is mapped to no telescope belongs to no rig: it is counted in the "
            + "unmapped tally and appears on neither card. The coloured cells compare each rig's "
            + "figure against the middle of your rigs, and that comparison refuses to judge "
            + "anything until it has at least eight values to compare. Here a value is one rig, so "
            + "it means eight telescopes: a library with one, two or seven rigs sees plain "
            + "uncoloured figures, and with one rig every cell is neutral. The figures themselves "
            + "are correct and comparable either way; only the colour is withheld. The frame "
            + "table's own grading counts differently: there a group is a rig and a filter, a "
            + "sample is a frame, and eight samples per group is enough. This table is not that "
            + "table. The altitude card reads each session's altitude from the pointing line PHD2 "
            + "wrote in its own log, so it needs no observer coordinates and works on a library "
            + "that has configured none. The table beneath the arcs lists the same rows the arcs "
            + "draw, one row per rig and altitude band, for a reader who wants the figures rather "
            + "than the shapes.",
        ["target.guiding"] =
            "This night's guiding, read from the PHD2 guide log a scan catalogued for it and not "
            + "from any frame's FITS headers. Nothing here is read from the log until Night detail "
            + "is opened. A session under 100 frames is too short to grade, so it still counts in "
            + "the totals while leaving no RMS figure behind it. The calibration chips print text "
            + "PHD2 itself wrote about its own calibration run, so a chip on screen reports a "
            + "fault PHD2 found rather than one this application decided. The graph beneath it "
            + "draws five layers, back to front: the bands where guiding was settling, the axis "
            + "grid, the RA and Dec error traces, the points marking a lost star, and the dither "
            + "lines. It answers four gestures: Ctrl with the wheel zooms time, Ctrl and Shift with "
            + "the wheel zoom the arcsecond scale, a drag pans, and a double click resets both; a "
            + "plain wheel scrolls the pane.",
        ["export.folders"] =
            "The rows under a night are that night's own frames' folders, from the top of your "
            + "library down to the folder the frames sit in, and one of them is copied whole. The "
            + "default pick is the deepest folder that holds every one of that night's frames and "
            + "drags nothing else along. A badge reading \"+2 other nights\" means that folder "
            + "also holds frames from two other nights, which are copied too; it is advisory, and "
            + "choosing such a folder is how you deliberately copy a date folder two targets "
            + "share. The size figure counts every catalogued file in that folder, whatever target "
            + "it belongs to and whatever kind of frame it is, because it answers how much the "
            + "copy will move: it reads unknown rather than a partial total when any one file's "
            + "size was never recorded, and sidecars and files GalactiLog never reads are copied "
            + "too and are not counted. A night that says it has no folder to copy contributes "
            + "nothing to the export.",
        ["export.quality"] =
            "The chips are absolute limits you type, judged per frame on the metrics that frame "
            + "actually carries. Type a limit with a decimal point, such as 3.5, whatever your "
            + "regional number format is. A limit on a metric a frame does not carry is skipped, "
            + "never failed, so a guiding limit does not silently drop every unguided frame. A "
            + "frame carrying none of the limited metrics reads Unmeasured, and Unmeasured is not "
            + "copied; the Copy box on its row is how you copy one anyway. The cell colours grade "
            + "each frame against the baseline the segment chooses and decide nothing: the limits "
            + "alone decide a verdict, and the colours are advisory. The limits are remembered per "
            + "rig, so two rigs keep two sets, and the tally counts every light frame of the "
            + "checked nights, a wider set than the footer's figures below it.",
        // The closing sentence is the launched-app look's D3, added at this close: the page's own
        // byte figures are 1000-based and the script's console is 1024-based, so one copy reads as
        // two totals. Spec 12.12's row moves with it in the coordinator's own edit.
        ["export.script"] =
            "The script is a text file GalactiLog wrote once and never runs; you run it. It only "
            + "copies: it contains no delete, no move and no rename, and it writes only under the "
            + "staging folder. The run command beneath it is the line to paste; the PowerShell one "
            + "unblocks the file first, which is harmless when the file was never marked. Copy "
            + "script, Show script and the file all carry the same text. Starting another export "
            + "withdraws this section, because it would otherwise describe a plan that is no "
            + "longer on screen; the file already written is not touched. If a file of the same "
            + "name is already in the staging folder, the script overwrites it; nothing in your "
            + "library is ever overwritten, moved or deleted. GalactiLog counts a kilobyte as 1000 "
            + "bytes and the script's own console counts it as 1024, so the two state slightly "
            + "different totals for the same files.",
        ["target.frames"] =
            "Each frame is graded by how far its metrics sit from a typical baseline, not against a "
            + "fixed threshold. Compare to picks it: This session is the other frames of the same "
            + "night, and This rig is every frame captured with the same telescope, camera and filter "
            + "across your whole library, whatever the target. Deviation is how many median absolute "
            + "deviations a frame sits from its group's median, in raw MAD units and not sigma; one MAD"
            + " unit is about 0.67 sigma, so they run smaller than a standard deviation score. Both "
            + "ignore outliers, so a few bad frames cannot drag the baseline. A group under 8 frames, "
            + "or one whose values are all identical, is left ungraded: there is no trustworthy spread "
            + "to measure against. The colour states the verdict in both directions: better than "
            + "typical, normal, watch, and likely reject. Signal metrics (star count, background ADU "
            + "and guiding) are always compared within the same night whichever baseline is selected, "
            + "because they drift with the sky. A Filter pill per measured metric shows only the frames"
            + " the night's own outlier rule flagged on it, with their count; a pill at zero is "
            + "disabled, and Clear filters restores every row. Grading is advisory. No frame is deleted"
            + " or hidden.",
        ["page.preview"] =
            "A rendered view of one frame, at the configured preview resolution. The wheel zooms, "
            + "a drag pans, and a double-click or the 0 key fits. The left and right arrows step "
            + "through the frames of the list this preview was opened from. The metadata strip "
            + "under the frame name carries that frame's filter, exposure and graded metrics, and "
            + "it updates on every step. Render full preview on navigation is on by default, so "
            + "every step renders a fresh full-resolution preview; turned off, stepping shows each "
            + "frame's cached thumbnail, which is faster across a few hundred frames.",
        ["page.frame-list"] =
            "Copies the file list for the nights checked in the ledger. Good copies the frames "
            + "the grading did not reject and Bad copies the frames it did. A frame with no "
            + "recorded quality data counts as unmeasured: it is never bad, and it joins the good "
            + "list only while Include unmeasured is checked. Absolute paths gives one full path "
            + "per line, Names gives one bare file name per line, and Explorer search gives a "
            + "string to paste into the Windows Explorer search box, which works best up to a few "
            + "dozen files. This dialog writes the clipboard and nothing else; it moves, renames "
            + "and deletes no file.",
        ["target.nights"] =
            "Every night this target was imaged, newest first, with that night's medians beside "
            + "the target's own means in the top row. A night's figure is marked only when it is "
            + "worse than the target mean by more than one unit of the last displayed decimal; "
            + "better stays silent. A night median against a target mean is not a like-for-like "
            + "comparison, which is why only the clearly worse figures are marked. The check box on "
            + "a row selects that night for an action; the lit row is the night the pane beside it "
            + "is showing, and the two are different marks.",
        // The thumbnail sentences arrived here from target.night with the strip itself, when the
        // user's B1 ruling of 2026-09-18 moved the strip into this section. Task 8 makes the same
        // move in spec 12.12's table, which is what this literal is a transcription of.
        ["target.night-detail"] =
            "It opens with this night's sharpest light frame, the one with the lowest "
            + "recorded HFR, shown whole at the frame's own aspect ratio and never cropped, and on "
            + "a night imaged by more than one rig there is one per rig; clicking one opens that "
            + "frame in the preview. Then the per-filter figures for this night and the ranges "
            + "across it. HFR and eccentricity in the per-filter table are "
            + "graded against this rig overall, meaning every frame captured with the same "
            + "telescope, camera and filter across your whole library, whatever the target. "
            + "Integration, frame counts and exposure are not graded. On a night imaged by more "
            + "than one rig the table splits per rig under a rig label row. This night's guiding "
            + "is the Guiding pill on the night chart.",
    };
}
