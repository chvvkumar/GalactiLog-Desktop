using System.Globalization;
using System.Reflection;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Core.Targets;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

/// <summary>
/// Spec 12.7's Create target form (PAR-001): the object type list, the coordinate bounds, the four
/// refusals and the sentence a create reports. The write itself is the repository's and is pinned
/// in <c>GalactiLog.Data.Tests</c>; nothing here touches a database.
/// </summary>
public class CreateTargetViewModelTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private sealed class Harness
    {
        public List<CreateTargetRequest> Requests { get; } = [];

        public CreateTargetResult Result { get; set; } =
            new(CreateTargetOutcome.Created, Guid.NewGuid(), null, 0, 0);

        public Exception? Throws { get; set; }

        public int Reloads;

        public CreateTargetViewModel ViewModel { get; }

        public Harness()
            => ViewModel = new CreateTargetViewModel(
                request =>
                {
                    lock (Requests)
                    {
                        Requests.Add(request);
                    }

                    return Throws is null ? Result : throw Throws;
                },
                afterCreate: () => Interlocked.Increment(ref Reloads),
                post: action => action());

        /// <summary>Presses submit and joins the write, so a case asserts against a settled form.
        /// </summary>
        public Harness Submit()
        {
            ViewModel.SubmitCommand.Execute(null);

            // The command's own task, not PendingSubmit: a write that threw faults PendingSubmit,
            // and the message is set by the handler that runs after it.
            ViewModel.SubmitCommand.ExecutionTask?.Wait(Budget);
            return this;
        }
    }

    private static Harness Filled(Action<CreateTargetViewModel>? configure = null)
    {
        var harness = new Harness();
        harness.ViewModel.PrimaryName = "Comet C/2026 X1";
        configure?.Invoke(harness.ViewModel);
        return harness;
    }

    [Fact]
    public void TheForm_ShipsWithUserDefinedChecked()
    {
        // task1-report.md departure 5: the web defaults it off; here it ships on and stays the
        // user's to clear.
        Assert.True(new Harness().ViewModel.UserDefined);
    }

    [Fact]
    public void TheObjectTypeChoices_AreNineThenFiveThenOther()
    {
        var choices = new Harness().ViewModel.ObjectTypeChoices;

        Assert.Equal(16, choices.Count);
        Assert.Equal("", choices[0]);
        Assert.Equal(ObjectTypeCategories.DisplayCategories, choices.Skip(1).Take(9));
        Assert.Equal(ObjectTypeCategories.SolarSystemCategories, choices.Skip(10).Take(5));
        Assert.Equal(TargetWriteRepository.OtherCategory, choices[15]);
    }

    [Fact]
    public void TheObjectTypeChoices_AreNotASecondCopyOfTheCategoryArrays()
    {
        // The source scan NightStripTests.NightStripSource_ContainsNoColourLiteral uses, over the
        // one file: the list is built from ObjectTypeCategories, so no category literal is typed
        // in it. Comments are stripped first, so the explanation beside the list is not what makes
        // this pass.
        var source = SourceScan.StripComments(File.ReadAllText(Path.Combine(
            SourceScan.SrcRoot(), "GalactiLog.App", "ViewModels", "Settings", "CreateTargetViewModel.cs")));

        foreach (var category in ObjectTypeCategories.DisplayCategories
                     .Concat(ObjectTypeCategories.SolarSystemCategories))
        {
            Assert.DoesNotContain($"\"{category}\"", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ChoosingASolarSystemCategory_ClearsRaAndDec()
    {
        var harness = Filled(form =>
        {
            form.Ra = "10.5";
            form.Dec = "41.2";
            form.SelectedObjectType = "Comet";
        });

        Assert.Equal("", harness.ViewModel.Ra);
        Assert.Equal("", harness.ViewModel.Dec);

        // "Clearing means the boxes go empty and stay empty while that category is selected"
        // (spec 12.7): they are disabled as well, which is what keeps them empty.
        Assert.False(harness.ViewModel.CoordinatesEnabled);
    }

    [Fact]
    public void ChoosingASolarSystemCategory_SetsUserDefined()
    {
        var harness = Filled(form =>
        {
            form.UserDefined = false;
            form.SelectedObjectType = "Planet";
        });

        Assert.True(harness.ViewModel.UserDefined);
    }

    [Fact]
    public void ChoosingOther_RevealsTheFreeTextBox()
    {
        var harness = Filled();

        Assert.False(harness.ViewModel.ShowOtherBox);

        harness.ViewModel.SelectedObjectType = TargetWriteRepository.OtherCategory;
        Assert.True(harness.ViewModel.ShowOtherBox);

        harness.ViewModel.SelectedObjectType = "Galaxy";
        Assert.False(harness.ViewModel.ShowOtherBox);

        // A non-solar category leaves the coordinate boxes usable.
        Assert.True(harness.ViewModel.CoordinatesEnabled);
    }

    [Fact]
    public void TheEmptyChoice_LeavesObjectTypeNull()
    {
        var harness = Filled(form => form.SelectedObjectType = "").Submit();

        Assert.Null(Assert.Single(harness.Requests).ObjectType);
    }

    [Fact]
    public void TheChosenCategory_IsSentVerbatim_AndOtherSendsItsFreeText()
    {
        var category = Filled(form => form.SelectedObjectType = "Globular Cluster").Submit();
        Assert.Equal("Globular Cluster", Assert.Single(category.Requests).ObjectType);

        var other = Filled(form =>
        {
            form.SelectedObjectType = TargetWriteRepository.OtherCategory;
            form.OtherObjectType = " Sunspot group ";
        }).Submit();
        Assert.Equal("Sunspot group", Assert.Single(other.Requests).ObjectType);
    }

    [Fact]
    public void AnEmptyName_IsRefusedWithPrimaryNameIsRequired()
    {
        var harness = new Harness();
        harness.ViewModel.PrimaryName = "   ";

        harness.Submit();

        Assert.Equal("Primary name is required", harness.ViewModel.Message);
        Assert.Empty(harness.Requests);
    }

    [Theory]
    [InlineData("", true, null)]
    [InlineData("0", true, 0d)]
    [InlineData("360", true, 360d)]
    [InlineData("360.1", false, null)]
    [InlineData("-0.1", false, null)]
    [InlineData("-90", false, null)]
    [InlineData("90", true, 90d)]
    [InlineData("abc", false, null)]
    public void RaAndDec_ParseWithinTheirBounds(string typed, bool accepted, double? expected)
    {
        // RA is 0 to 360 inclusive, so -90 and -0.1 are refused and 0, 90 and 360 are not.
        var harness = Filled(form => form.Ra = typed).Submit();

        if (!accepted)
        {
            Assert.Equal("RA must be a number between 0 and 360.", harness.ViewModel.Message);
            Assert.Empty(harness.Requests);
            return;
        }

        Assert.Equal(expected, Assert.Single(harness.Requests).Ra);
    }

    [Theory]
    [InlineData("", true, null)]
    [InlineData("0", true, 0d)]
    [InlineData("-90", true, -90d)]
    [InlineData("90", true, 90d)]
    [InlineData("90.1", false, null)]
    [InlineData("-90.1", false, null)]
    [InlineData("360", false, null)]
    [InlineData("abc", false, null)]
    public void Dec_ParsesWithinItsBounds(string typed, bool accepted, double? expected)
    {
        var harness = Filled(form => form.Dec = typed).Submit();

        if (!accepted)
        {
            Assert.Equal("Dec must be a number between -90 and 90.", harness.ViewModel.Message);
            Assert.Empty(harness.Requests);
            return;
        }

        Assert.Equal(expected, Assert.Single(harness.Requests).Dec);
    }

    [Fact]
    public void AnOutOfRangeCoordinate_RefusesWithTheRangeInTheMessage()
    {
        var ra = Filled(form => form.Ra = "400").Submit();
        Assert.Contains("between 0 and 360", ra.ViewModel.Message!, StringComparison.Ordinal);
        Assert.Empty(ra.Requests);

        var dec = Filled(form => form.Dec = "-91").Submit();
        Assert.Contains("between -90 and 90", dec.ViewModel.Message!, StringComparison.Ordinal);
        Assert.Empty(dec.Requests);
    }

    [Fact]
    public void ACoordinate_ParsesInInvariantCulture()
    {
        // A German profile writes 10,5 for ten and a half. Parsed under the current culture that
        // would become 105, which is a different position in the sky; parsed invariantly it is
        // not a number at all and the submit is refused.
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var comma = Filled(form => form.Ra = "10,5").Submit();
            Assert.Equal("RA must be a number between 0 and 360.", comma.ViewModel.Message);
            Assert.Empty(comma.Requests);

            var point = Filled(form => form.Ra = "10.5").Submit();
            Assert.Equal(10.5d, Assert.Single(point.Requests).Ra);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ARefusal_LeavesTheFormHoldingWhatWasTyped()
    {
        var harness = Filled(form =>
        {
            form.CatalogId = "Kumar 1";
            form.AliasText = "x1, 2026 X1";
            form.Ra = "400";
            form.UserDefined = false;
        }).Submit();

        Assert.Equal("Comet C/2026 X1", harness.ViewModel.PrimaryName);
        Assert.Equal("Kumar 1", harness.ViewModel.CatalogId);
        Assert.Equal("x1, 2026 X1", harness.ViewModel.AliasText);
        Assert.Equal("400", harness.ViewModel.Ra);
        Assert.False(harness.ViewModel.UserDefined);
    }

    [Fact]
    public void AConflictRefusal_LeavesTheFormHoldingWhatWasTyped()
    {
        var harness = Filled(form =>
        {
            form.CatalogId = "M 31";
            form.AliasText = "andromeda";
        });
        harness.Result = new CreateTargetResult(CreateTargetOutcome.NameInUse, null, "M 31", 0, 0);

        harness.Submit();

        Assert.Equal(
            "Name already in use by \"M 31\". Use the merge action to attach these frames to it.",
            harness.ViewModel.Message);
        Assert.Equal("Comet C/2026 X1", harness.ViewModel.PrimaryName);
        Assert.Equal("M 31", harness.ViewModel.CatalogId);
        Assert.Equal("andromeda", harness.ViewModel.AliasText);
        Assert.Equal(0, harness.Reloads);
    }

    // Fix-wave review: nothing reached CreateTargetOutcome.CreatedWithoutBookkeeping, the outcome
    // fixer list item 22 added. The target row and its frame links are committed by the time the
    // repository's bookkeeping save can throw, so this is a CREATE that happened: the form closes
    // and reloads the list exactly as an ordinary create does, and the only difference is the
    // clause naming what is missing from the activity log.
    [Fact]
    public void APartialWrite_ReportsTheCreateAndNamesWhatWasNotWritten()
    {
        var harness = Filled(form => form.CatalogId = "C/2026 X1");
        harness.Result = new CreateTargetResult(
            CreateTargetOutcome.CreatedWithoutBookkeeping, Guid.NewGuid(), null, 6, 1);

        harness.Submit();

        Assert.Equal(
            "Created Comet C/2026 X1, linked 6 frames from 1 unresolved name. "
            + "The activity entry could not be written. See the log for details.",
            harness.ViewModel.Message);

        // The create happened, so the form behaves as it does after one: closed, cleared, and the
        // caller's reload run.
        Assert.False(harness.ViewModel.IsOpen);
        Assert.Equal("", harness.ViewModel.PrimaryName);
        Assert.Equal("", harness.ViewModel.CatalogId);
        Assert.Equal(1, harness.Reloads);
    }

    [Fact]
    public void ACatalogIdConflict_ReportsSpec97sSecondSentence()
    {
        var harness = Filled();
        harness.Result = new CreateTargetResult(CreateTargetOutcome.CatalogIdInUse, null, "Andromeda", 0, 0);

        harness.Submit();

        Assert.Equal("Catalog ID already belongs to \"Andromeda\".", harness.ViewModel.Message);
        Assert.Equal("Comet C/2026 X1", harness.ViewModel.PrimaryName);
    }

    [Fact]
    public void ASuccess_ReportsCreatedNameLinkedFramesFromUnresolvedNames()
    {
        var harness = Filled();
        harness.Result = new CreateTargetResult(CreateTargetOutcome.Created, Guid.NewGuid(), null, 12, 3);

        harness.Submit();

        Assert.Equal(
            "Created Comet C/2026 X1, linked 12 frames from 3 unresolved names",
            harness.ViewModel.Message);
    }

    [Fact]
    public void ASuccessWithNoLinks_StillReportsCorrectly()
    {
        var none = Filled();
        none.Result = new CreateTargetResult(CreateTargetOutcome.Created, Guid.NewGuid(), null, 0, 0);
        none.Submit();
        Assert.Equal(
            "Created Comet C/2026 X1, linked 0 frames from 0 unresolved names",
            none.ViewModel.Message);

        var one = Filled();
        one.Result = new CreateTargetResult(CreateTargetOutcome.Created, Guid.NewGuid(), null, 1, 1);
        one.Submit();
        Assert.Equal(
            "Created Comet C/2026 X1, linked 1 frame from 1 unresolved name",
            one.ViewModel.Message);
    }

    [Fact]
    public void ASuccess_RunsTheAfterCreateReload()
    {
        var harness = Filled().Submit();

        Assert.Equal(1, harness.Reloads);

        // The form clears and closes; the sentence it reported stays on screen, which is why the
        // message element is outside the form panel.
        Assert.False(harness.ViewModel.IsOpen);
        Assert.Equal("", harness.ViewModel.PrimaryName);
        Assert.NotNull(harness.ViewModel.Message);
    }

    [Fact]
    public void OpenForAName_PreFillsTheNameAndTheAliasList()
    {
        var harness = new Harness();

        harness.ViewModel.OpenForCommand.Execute("Zzyzx Blob 42");

        Assert.True(harness.ViewModel.IsOpen);
        Assert.Equal("Zzyzx Blob 42", harness.ViewModel.PrimaryName);
        Assert.Equal("Zzyzx Blob 42", harness.ViewModel.AliasText);
    }

    [Fact]
    public void OpenAndCancel_OpenAndClearTheForm()
    {
        var harness = new Harness();
        harness.ViewModel.OpenCommand.Execute(null);
        Assert.True(harness.ViewModel.IsOpen);

        harness.ViewModel.PrimaryName = "Half typed";
        harness.ViewModel.CancelCommand.Execute(null);

        Assert.False(harness.ViewModel.IsOpen);
        Assert.Equal("", harness.ViewModel.PrimaryName);
    }

    // Phase 14B fixer, fixer list item 11 (phase review P3-4). One shared form instance reached
    // from two places (ruling D4), so a stray "New target" press while a draft is open used to
    // clear seven typed fields with no warning. Open leaves an open form alone; OpenFor still
    // re-seeds, because it carries the name of the row that was pressed.
    [Fact]
    public void Open_OnAnAlreadyOpenForm_KeepsWhatWasTyped()
    {
        var harness = new Harness();
        harness.ViewModel.OpenCommand.Execute(null);
        harness.ViewModel.PrimaryName = "Half typed";
        harness.ViewModel.CatalogId = "NGC 7000";

        harness.ViewModel.OpenCommand.Execute(null);

        Assert.True(harness.ViewModel.IsOpen);
        Assert.Equal("Half typed", harness.ViewModel.PrimaryName);
        Assert.Equal("NGC 7000", harness.ViewModel.CatalogId);
    }

    [Fact]
    public void OpenFor_OnAnAlreadyOpenForm_ReSeedsItWithTheRowsName()
    {
        var harness = new Harness();
        harness.ViewModel.OpenCommand.Execute(null);
        harness.ViewModel.PrimaryName = "Half typed";

        harness.ViewModel.OpenForCommand.Execute("Comet C/2026 X1");

        Assert.True(harness.ViewModel.IsOpen);
        Assert.Equal("Comet C/2026 X1", harness.ViewModel.PrimaryName);
        Assert.Equal("Comet C/2026 X1", harness.ViewModel.AliasText);
    }

    [Fact]
    public void TheAliasList_IsSplitOnCommasAndTrimmed()
    {
        var harness = Filled(form => form.AliasText = " x1 , , 2026 X1 ,").Submit();

        Assert.Equal(["x1", "2026 X1"], Assert.Single(harness.Requests).Aliases);
    }

    [Fact]
    public void SubmitCommand_ExecutedPastCanExecute_StillRefusesAnEmptyName()
    {
        // TRACKING section 6 item 13: RelayCommand.Execute ignores CanExecute, so the rule lives
        // in the command body as well.
        var harness = new Harness();
        harness.ViewModel.PrimaryName = "";

        harness.ViewModel.SubmitCommand.Execute(null);
        harness.ViewModel.SubmitCommand.ExecutionTask?.Wait(Budget);

        Assert.Empty(harness.Requests);
        Assert.Equal("Primary name is required", harness.ViewModel.Message);
    }

    [Fact]
    public void SubmitCommand_TakesNoCancellationToken()
    {
        // FIXER F24 and deviation D10: a command built from a Func<CancellationToken, Task>
        // cancels the in-flight token on a second Execute, which would abort a write already
        // running rather than let the body's guard refuse it.
        var method = typeof(CreateTargetViewModel)
            .GetMethod("SubmitAsync", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        Assert.Empty(method!.GetParameters());
    }

    [Fact]
    public void AWriteThatThrew_IsReportedAndNotRethrown()
    {
        var harness = Filled();
        harness.Throws = new InvalidOperationException("the database went away");

        harness.Submit();

        Assert.Equal("The target could not be created. See the log for details.", harness.ViewModel.Message);
        Assert.False(harness.ViewModel.IsSubmitting);
        Assert.Equal(0, harness.Reloads);
    }
}
