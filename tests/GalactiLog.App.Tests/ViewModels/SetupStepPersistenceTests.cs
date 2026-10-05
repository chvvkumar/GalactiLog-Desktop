using System.Text.Json;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Setup;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// The roadmap's Phase 9 row 8 Verify line, verbatim: "GalactiLog.App.Tests asserts Next is
// disabled until a folder is chosen, that a failed save keeps the user on the step, that each
// step's settings are persisted before advancing, and that finishing sets setup_complete."
//
// The first clause is SetupWizardViewModelTests.Step1_NextIsDisabledUntilAFolderIsChosen. The
// other three are here, in one file, so they cannot be lost among the rest.
public class SetupStepPersistenceTests
{
    // The roadmap's third named assertion: each step's settings are persisted before advancing.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Next_PersistsTheCurrentStepBeforeAdvancing(int step)
    {
        using var harness = SetupWizardViewModelTestFactory.Create(
            timezones: () => ["Etc/UTC", "Europe/London"],
            localTimezoneId: () => "Europe/London");

        await AdvanceTo(harness, step);
        Fill(harness, step);

        var savesBefore = harness.Saves.Count;
        await harness.ViewModel.NextCommand.ExecuteAsync(null);

        Assert.Equal(step + 1, harness.ViewModel.StepIndex);
        Assert.Equal(savesBefore + 1, harness.Saves.Count);

        // The save ran while the wizard was still on this step: StepIndex advanced only after the
        // store returned.
        Assert.Equal(step, harness.StepIndexAtSave[^1]);

        AssertStored(harness, step);
    }

    // The roadmap's second named assertion: a failed save keeps the user on the step.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task AFailedSave_KeepsTheUserOnTheStep(int step)
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        await AdvanceTo(harness, step);
        Fill(harness, step);

        harness.SaveThrows = new InvalidOperationException("the disk went away");
        await harness.ViewModel.NextCommand.ExecuteAsync(null);

        Assert.Equal(step, harness.ViewModel.StepIndex);
        Assert.Equal(SetupWizardViewModel.CouldNotSaveMessage, harness.ViewModel.StepError);
    }

    [Fact]
    public async Task AFailedSave_ShowsTheValidationMessage()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        harness.Folders.AddFolder(SetupWizardViewModelTestFactory.Folder);
        harness.SaveThrows = new SettingsValidationException("general.scan_roots entry is not absolute.");

        await harness.ViewModel.NextCommand.ExecuteAsync(null);

        Assert.Equal(0, harness.ViewModel.StepIndex);
        Assert.Equal("general.scan_roots entry is not absolute.", harness.ViewModel.StepError);
    }

    [Fact]
    public async Task AFailedSave_ShowsAGenericMessageForANonValidationException()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        harness.Folders.AddFolder(SetupWizardViewModelTestFactory.Folder);
        harness.SaveThrows = new TimeoutException("database is locked");

        await harness.ViewModel.NextCommand.ExecuteAsync(null);

        Assert.Equal(SetupWizardViewModel.CouldNotSaveMessage, harness.ViewModel.StepError);
        Assert.DoesNotContain("database is locked", harness.ViewModel.StepError!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedSave_LeavesTheStoredDocumentUnchanged()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        var before = harness.Stored;

        harness.Folders.AddFolder(SetupWizardViewModelTestFactory.Folder);
        harness.SaveThrows = new InvalidOperationException("no");
        await harness.ViewModel.NextCommand.ExecuteAsync(null);

        var after = harness.Stored;
        Assert.Empty(after.ScanRoots);
        Assert.Equal(before.ScanRoots, after.ScanRoots);
        Assert.False(after.SetupComplete);
    }

    // The roadmap's fourth named assertion.
    [Fact]
    public async Task Finish_SetsSetupComplete()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        await AdvanceTo(harness, 4);

        Assert.False(harness.Stored.SetupComplete);
        await harness.ViewModel.FinishCommand.ExecuteAsync(null);

        Assert.True(harness.Stored.SetupComplete);
        Assert.True(harness.ViewModel.IsFinished);
    }

    [Fact]
    public async Task Finish_NavigatesToTheDashboard()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        await AdvanceTo(harness, 4);

        await harness.ViewModel.FinishCommand.ExecuteAsync(null);

        Assert.Equal(1, harness.Navigations);
    }

    // Questions.md Q32: the five substring folder-exclude rules, first run only.
    [Fact]
    public async Task Finish_OnAFirstRun_SeedsTheFiveDefaultExcludeRules()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        await AdvanceTo(harness, 4);

        await harness.ViewModel.FinishCommand.ExecuteAsync(null);

        var rules = harness.Stored.ScanFilters.NameRules;
        Assert.Equal(5, rules.Count);
        Assert.Equal(
            ScanOptionsStepViewModel.ExcludeDefaults.Select(name => $"setup-exclude-{name}"),
            rules.Select(rule => rule.Id));
        Assert.All(rules, rule =>
        {
            Assert.Equal("exclude", rule.Action);
            Assert.Equal("substring", rule.Type);
            Assert.Equal("folder", rule.Target);
            Assert.True(rule.Enabled);
        });
        Assert.Equal(
            ScanOptionsStepViewModel.ExcludeDefaults,
            rules.Select(rule => rule.Pattern));
    }

    // Phase 14B fixer, fixer list item 3 (Task 5 review escalation 1, ruled to the fixer). The
    // census the ruling names: what the wizard writes and what Core's notice predicate recognises
    // are one projection, not two that agree today. The wizard now delegates to
    // ScanFilterConfig.SeededRules(), so this fails the moment anyone gives it its own copy back
    // and that copy drifts, and the second assertion is the consequence that matters: spec 12.2's
    // notice comes up on a first run and can never come up against the wizard's own output.
    [Fact]
    public async Task Finish_OnAFirstRun_WritesExactlyTheRulesTheNoticePredicateRecognises()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        await AdvanceTo(harness, 4);

        await harness.ViewModel.FinishCommand.ExecuteAsync(null);

        var seeded = ScanFilterConfig.SeededRules();
        var written = SetupWizardViewModel.DefaultExcludeRules();

        Assert.Equal(
            seeded.Select(r => (r.Id, r.Action, r.Type, r.Pattern, r.Target, r.Enabled)),
            written.Select(r => (r.Id, r.Action, r.Type, r.Pattern, r.Target, r.Enabled)));
        Assert.True(ScanFilterConfig.IsOnlyTheSeededRules(harness.Stored.ScanFilters));
    }

    // Review cannot-verify 4: the seeding is a record `with` on the stored ScanFilterConfig, so
    // the fields a newer version of the application writes and this one does not know about ride
    // through it in [JsonExtensionData]. Planted here and read back out of SQLite, rather than
    // trusted to the copy semantics.
    [Fact]
    public async Task Finish_OnAFirstRun_KeepsTheFilterDocumentsUnknownFields()
    {
        using var harness = SetupWizardViewModelTestFactory.Create(
            seed: general => general with
            {
                ScanFilters = general.ScanFilters with
                {
                    ExtensionData = new Dictionary<string, JsonElement>
                    {
                        ["future_rule_kind"] = JsonSerializer.SerializeToElement("kept"),
                    },
                },
            });
        await AdvanceTo(harness, 4);

        await harness.ViewModel.FinishCommand.ExecuteAsync(null);

        Assert.Equal(5, harness.Stored.ScanFilters.NameRules.Count);
        var extra = harness.Stored.ScanFilters.ExtensionData;
        Assert.NotNull(extra);
        Assert.True(extra!.ContainsKey("future_rule_kind"));
        Assert.Equal("kept", extra["future_rule_kind"].GetString());
    }

    [Fact]
    public async Task Finish_OnAReRun_DoesNotOverwriteTheFilterDocument()
    {
        var tuned = new NameRule
        {
            Id = "mine",
            Action = "exclude",
            Type = "substring",
            Pattern = "scratch",
            Target = "folder",
            Enabled = true,
        };

        using var harness = SetupWizardViewModelTestFactory.Create(
            seed: general => general with
            {
                SetupComplete = true,
                ScanRoots = [SetupWizardViewModelTestFactory.Folder],
                ScanFilters = general.ScanFilters with
                {
                    IncludePaths = [SetupWizardViewModelTestFactory.Folder + @"\2025"],
                    NameRules = [tuned],
                },
            });

        Assert.True(harness.ViewModel.SetupWasComplete);
        await AdvanceTo(harness, 4);
        await harness.ViewModel.FinishCommand.ExecuteAsync(null);

        var filters = harness.Stored.ScanFilters;
        Assert.Equal(["mine"], filters.NameRules.Select(rule => rule.Id));
        Assert.Equal([SetupWizardViewModelTestFactory.Folder + @"\2025"], filters.IncludePaths);
        Assert.True(harness.Stored.SetupComplete);
    }

    // The divergence from the web, which closes anyway and marks setup complete locally.
    [Fact]
    public async Task Finish_WithAFailedWrite_KeepsTheUserOnTheStep_AndDoesNotSetSetupComplete()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        await AdvanceTo(harness, 4);

        var closed = 0;
        harness.ViewModel.CloseRequested += (_, _) => closed++;
        harness.SaveThrows = new InvalidOperationException("the disk went away");

        await harness.ViewModel.FinishCommand.ExecuteAsync(null);

        Assert.Equal(4, harness.ViewModel.StepIndex);
        Assert.False(harness.Stored.SetupComplete);
        Assert.False(harness.ViewModel.IsFinished);
        Assert.Equal(0, closed);
        Assert.Equal(0, harness.Navigations);
        Assert.Equal(SetupWizardViewModel.CouldNotFinishMessage, harness.ViewModel.StepError);
    }

    [Fact]
    public async Task Finish_WritesTheFlagAndTheRulesInOneMutation()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        await AdvanceTo(harness, 4);

        var savesBefore = harness.Saves.Count;
        await harness.ViewModel.FinishCommand.ExecuteAsync(null);

        // One write, not the web's two: a refused write leaves the document exactly as it was,
        // rather than seeding rules into an install whose setup_complete is still false.
        Assert.Equal(savesBefore + 1, harness.Saves.Count);
        Assert.True(harness.Saves[^1].SetupComplete);
        Assert.Equal(5, harness.Saves[^1].ScanFilters.NameRules.Count);
    }

    // Walks to the requested step, choosing a folder so step 1's Next is enabled and consuming
    // the blank-longitude nudge on the way past step 3.
    private static async Task AdvanceTo(SetupWizardViewModelTestFactory.Harness harness, int target)
    {
        if (harness.Folders.Folders.Count == 0)
        {
            harness.Folders.AddFolder(SetupWizardViewModelTestFactory.Folder);
        }

        var guard = 0;
        while (harness.ViewModel.StepIndex < target && guard++ < 12)
        {
            await harness.ViewModel.NextCommand.ExecuteAsync(null);
        }

        Assert.Equal(target, harness.ViewModel.StepIndex);
    }

    // Gives each step a value that is distinguishable from its default, so the stored document
    // proves the step's own fields were written.
    private static void Fill(SetupWizardViewModelTestFactory.Harness harness, int step)
    {
        switch (step)
        {
            case 0:
                harness.Folders.AddFolder(SetupWizardViewModelTestFactory.SecondFolder);
                break;
            case 1:
                harness.Cache.CachePath = @"E:\cache\thumbnails";
                break;
            case 2:
                harness.Observer.LatitudeText = "42.3601";
                harness.Observer.LongitudeText = "-71.0589";
                harness.Observer.SelectedTimezone =
                    harness.Observer.Timezones.First(option => option.Id == "Etc/UTC");
                harness.Observer.UseImagingNight = false;
                break;
            default:
                harness.Options.IncludeCalibration = true;
                harness.Options.WatcherEnabled = false;
                harness.Options.AutoScanEnabled = true;
                harness.Options.SelectedInterval =
                    harness.Options.IntervalOptions.First(option => option.Minutes == 720);
                break;
        }
    }

    private static void AssertStored(SetupWizardViewModelTestFactory.Harness harness, int step)
    {
        var stored = harness.Stored;
        switch (step)
        {
            case 0:
                Assert.Equal(
                    [SetupWizardViewModelTestFactory.Folder, SetupWizardViewModelTestFactory.SecondFolder],
                    stored.ScanRoots);
                break;
            case 1:
                Assert.Equal(@"E:\cache\thumbnails", stored.ThumbnailCacheDir);
                break;
            case 2:
                Assert.Equal(42.3601d, stored.ObserverLatitude);
                Assert.Equal(-71.0589d, stored.ObserverLongitude);
                Assert.Equal("Etc/UTC", stored.ObserverTimezone);
                Assert.False(stored.UseImagingNight);
                break;
            default:
                Assert.True(stored.IncludeCalibration);
                Assert.False(stored.WatcherEnabled);
                Assert.True(stored.AutoScanEnabled);
                Assert.Equal(720, stored.AutoScanIntervalMinutes);
                break;
        }

        // No step but Finish writes the flag.
        Assert.False(stored.SetupComplete);
    }
}
