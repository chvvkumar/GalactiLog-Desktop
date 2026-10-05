using GalactiLog.App.ViewModels.Wizard;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels.Wizard;

// The wizard spine's navigation rules, on a two-step fake wizard with no setup behaviour in it.
public class WizardViewModelTests
{
    private sealed class FakeStep(string title) : WizardStepViewModel(action => action())
    {
        public int Entered { get; private set; }

        public string? Gate { get; set; }

        public override string Title => title;

        public override string HelpTopicId => "fake." + title;

        public override void OnEntered() => Entered++;

        public override string? BeforeAdvance() => Gate;

        public bool Disposed => IsDisposed;
    }

    private sealed class FakeWizard(int count = 2)
        : WizardViewModel<FakeStep>([.. new[] { "one", "two", "three" }.Take(count).Select(title => new FakeStep(title))])
    {
        public bool Advance { get; set; } = true;

        public TaskCompletionSource<bool>? Pending { get; set; }

        public void Jump(int index) => GoTo(index);

        public int Advancing { get; private set; }

        public void Commit() => IsCommitted = true;

        public void StartAgain()
        {
            ReleaseCommit();
            GoTo(0);
        }

        protected override Task<bool> OnAdvancingAsync(FakeStep step)
        {
            Advancing++;
            return Pending?.Task ?? Task.FromResult(Advance);
        }
    }

    // A failure here is a Next that does not move, or moves without telling the target step.
    [Fact]
    public async Task Next_Advances_AndRaisesOnEnteredOnTheTarget()
    {
        using var wizard = new FakeWizard();

        await wizard.NextCommand.ExecuteAsync(null);

        Assert.Equal(1, wizard.StepIndex);
        Assert.Same(wizard.Steps[1], wizard.CurrentStep);
        Assert.Equal(1, wizard.Steps[1].Entered);
        Assert.Equal("Step 2 of 2: two", wizard.StepHeader);
        Assert.Equal([false, true], wizard.StepRail);
    }

    // A failure here is a gate message that is lost or that lets the wizard move anyway.
    [Fact]
    public async Task BeforeAdvance_ReturningAMessage_StaysAndSetsStepError()
    {
        using var wizard = new FakeWizard();
        wizard.Steps[0].Gate = "Not yet";

        await wizard.NextCommand.ExecuteAsync(null);

        Assert.Equal(0, wizard.StepIndex);
        Assert.Equal("Not yet", wizard.StepError);
        Assert.Equal(0, wizard.Advancing);

        wizard.Steps[0].Gate = null;
        await wizard.NextCommand.ExecuteAsync(null);

        Assert.Equal(1, wizard.StepIndex);
        Assert.Null(wizard.StepError);
    }

    // A failure here is an advance hook whose refusal is ignored.
    [Fact]
    public async Task OnAdvancingAsync_ReturningFalse_Stays()
    {
        using var wizard = new FakeWizard { Advance = false };

        await wizard.NextCommand.ExecuteAsync(null);

        Assert.Equal(0, wizard.StepIndex);
        Assert.Equal(1, wizard.Advancing);
        Assert.Equal(0, wizard.Steps[1].Entered);

        wizard.Advance = true;
        await wizard.NextCommand.ExecuteAsync(null);

        Assert.Equal(1, wizard.StepIndex);
    }

    // A failure here is a committed wizard that still navigates, through the buttons or through a
    // direct Execute (TRACKING item 13).
    [Fact]
    public async Task IsCommitted_RefusesNextAndBack_InCanExecuteAndInTheBodies()
    {
        using var wizard = new FakeWizard();

        wizard.Commit();
        Assert.False(wizard.CanGoNext);
        Assert.False(wizard.NextCommand.CanExecute(null));
        await wizard.NextCommand.ExecuteAsync(null);
        Assert.Equal(0, wizard.StepIndex);
        Assert.Equal(0, wizard.Advancing);

        wizard.StartAgain();
        await wizard.NextCommand.ExecuteAsync(null);
        Assert.Equal(1, wizard.StepIndex);

        wizard.Commit();
        Assert.False(wizard.CanGoBack);
        Assert.False(wizard.BackCommand.CanExecute(null));
        wizard.BackCommand.Execute(null);
        Assert.Equal(1, wizard.StepIndex);
    }

    // A failure here is a restart that keeps the lock or skips the first step's refresh.
    [Fact]
    public async Task ReleaseCommit_ThenGoTo0_LandsOnTheFirstStep_WithOnEnteredRaised()
    {
        using var wizard = new FakeWizard();
        await wizard.NextCommand.ExecuteAsync(null);
        wizard.Commit();
        var enteredBefore = wizard.Steps[0].Entered;

        wizard.StartAgain();

        Assert.False(wizard.IsCommitted);
        Assert.Equal(0, wizard.StepIndex);
        Assert.Equal(enteredBefore + 1, wizard.Steps[0].Entered);
        Assert.True(wizard.CanGoNext);
    }

    // A failure here is a step left alive after its wizard is gone.
    [Fact]
    public void Dispose_DisposesEveryStep()
    {
        var wizard = new FakeWizard();

        wizard.Dispose();

        Assert.All(wizard.Steps, step => Assert.True(step.Disposed));
    }

    // A failure here is an override that awaits without setting IsBusy and leaves Back and Next
    // live while its work is pending.
    [Fact]
    public async Task APendingAdvance_DisablesBackAndNext_UntilItReturns()
    {
        using var wizard = new FakeWizard(3);
        await wizard.NextCommand.ExecuteAsync(null);
        var pending = new TaskCompletionSource<bool>();
        wizard.Pending = pending;

        var advancing = wizard.NextCommand.ExecuteAsync(null);

        Assert.True(wizard.IsBusy);
        Assert.False(wizard.CanGoBack);
        Assert.False(wizard.CanGoNext);

        pending.SetResult(true);
        await advancing;

        Assert.False(wizard.IsBusy);
        Assert.Equal(2, wizard.StepIndex);
        Assert.True(wizard.CanGoBack);
    }

    // A failure here is GoTo moving StepIndex outside the steps before anything refuses it.
    [Fact]
    public void GoTo_OutsideTheSteps_Throws_AndLeavesStepIndexUnchanged()
    {
        using var wizard = new FakeWizard();

        Assert.Throws<ArgumentOutOfRangeException>(() => wizard.Jump(-1));
        Assert.Equal(0, wizard.StepIndex);
        Assert.Throws<ArgumentOutOfRangeException>(() => wizard.Jump(wizard.Steps.Count));
        Assert.Equal(0, wizard.StepIndex);
    }
}
