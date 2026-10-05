using GalactiLog.App.ViewModels.Settings;
using GalactiLog.App.ViewModels.Update;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

/// <summary>
/// Spec 17.1's "nothing is applied silently": the confirmation's two answers, and what each one
/// does.
/// </summary>
public class UpdatePromptViewModelTests
{
    private static UpdatePromptViewModel Page(Action? install = null, string? notes = "What changed")
        => new("2.0.0", "1.9.3.0", "stable", notes, install ?? (() => { }));

    [Fact]
    public void Summary_NamesBothVersionsAndTheChannel()
    {
        var page = Page();

        Assert.Contains("2.0.0", page.Summary, StringComparison.Ordinal);
        Assert.Contains("1.9.3.0", page.Summary, StringComparison.Ordinal);
        Assert.Contains("stable", page.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseNotes_FallBackToTheSharedMessage_WhenTheFeedCarriedNone()
    {
        Assert.Equal(AboutTabViewModel.NoReleaseNotesMessage, Page(notes: null).ReleaseNotes);
        Assert.Equal(AboutTabViewModel.NoReleaseNotesMessage, Page(notes: "").ReleaseNotes);
    }

    [Fact]
    public void InstallAndRestart_AppliesThenAsksToClose_WithTrue()
    {
        var order = new List<string>();
        var page = Page(install: () => order.Add("applied"));
        page.CloseRequested += (_, result) => order.Add($"closed:{result}");

        page.InstallAndRestartCommand.Execute(null);

        // The apply runs first: on the success path the process does not survive it, so a close
        // raised beforehand would be the last thing the user saw of the old build.
        Assert.Equal(["applied", "closed:True"], order);
    }

    [Fact]
    public void Later_ClosesWithFalse_AndAppliesNothing()
    {
        var applies = 0;
        var page = Page(install: () => applies++);
        bool? result = null;
        page.CloseRequested += (_, value) => result = value;

        page.LaterCommand.Execute(null);

        Assert.Equal(0, applies);
        Assert.False(result);
    }
}
