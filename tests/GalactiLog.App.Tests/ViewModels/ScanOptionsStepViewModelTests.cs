using System.Collections.Specialized;
using GalactiLog.App.ViewModels.Setup;
using GalactiLog.Core.Settings;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Design-spec 12.1's step 4, the auto-scan interval. The step's fields, its defaults and its
// persistence are covered by SetupWizardViewModelTests and SetupStepPersistenceTests; this file is
// the interval list alone.
//
// Phase 15B fixer pass, the coordinator's ruling on fix-b-report.md's first escalation. The step
// carried the same clear-and-refill of a bound interval list that left the Library tab's "Scan
// interval" select empty on a first visit (task5c-review.md P3-6), which is the third occurrence of
// "a bound list replaced under a two-way selection" in this phase. Both view-models now publish
// their options through one body, IntervalChoices.Publish, and these two cases are what hold it
// from this side: the wizard's own Load must leave the offered options and the selection exactly
// where they are, because the window binds a ComboBox to both.
public class ScanOptionsStepViewModelTests
{
    private static ScanOptionsStepViewModel Step()
        => new(() => new[] { @"C:\Astro\Captures" }, post: action => action());

    private static GeneralSettings Stored(int minutes)
        => new GeneralSettings() with { AutoScanIntervalMinutes = minutes };

    [Fact]
    public void OnFirstShow_TheSevenPresetsAreOffered_AndTheWizardsOwnLoadLeavesThemAlone()
    {
        using var step = Step();

        // First show. The wizard builds every step and then loads the document into it, both in
        // its own constructor, so this is the state the window's ComboBox binds to.
        Assert.Equal(
            [60, 120, 240, 360, 480, 720, 1440],
            step.IntervalOptions.Select(option => option.Minutes));
        Assert.Equal(240, step.SelectedInterval!.Minutes);

        // The selection is an option the list already holds, never one assigned beside it.
        Assert.Contains(step.IntervalOptions, option => ReferenceEquals(option, step.SelectedInterval));

        var offered = step.IntervalOptions.ToArray();
        var selected = step.SelectedInterval;
        var resets = 0;
        step.IntervalOptions.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset)
            {
                resets++;
            }
        };

        step.Load(Stored(240));

        Assert.Equal(0, resets);
        Assert.Equal(offered.Length, step.IntervalOptions.Count);
        for (var i = 0; i < offered.Length; i++)
        {
            Assert.Same(offered[i], step.IntervalOptions[i]);
        }

        Assert.Same(selected, step.SelectedInterval);
    }

    [Fact]
    public void AStoredNonPresetInterval_IsTheOneExtraEntry_AndLeavesNoStaleEntryBehind()
    {
        using var step = Step();
        var resets = 0;
        step.IntervalOptions.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset)
            {
                resets++;
            }
        };

        // Spec 5.8.1 allows any positive minute count, and a re-run of the wizard over a
        // hand-edited document is exactly where one shows up.
        step.Load(Stored(90));

        Assert.Equal(8, step.IntervalOptions.Count);
        Assert.Equal(90, step.IntervalOptions[^1].Minutes);
        Assert.Equal("90 minutes", step.IntervalOptions[^1].Label);
        Assert.Same(step.IntervalOptions[^1], step.SelectedInterval);

        // And a later load whose value is a preset again takes the extra entry back out rather
        // than leaving an option nobody can reach any more.
        step.Load(Stored(720));

        Assert.Equal(7, step.IntervalOptions.Count);
        Assert.Equal(720, step.SelectedInterval!.Minutes);
        Assert.Equal(0, resets);
    }
}
