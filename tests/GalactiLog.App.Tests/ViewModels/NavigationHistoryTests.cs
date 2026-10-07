using GalactiLog.App.ViewModels;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// The list-and-cursor behind the shell's Back and Forward (.planning/mouse-navigation.md): the
// browser rules on their own, with no shell.
public class NavigationHistoryTests
{
    private static NavigationEntry Rail(string key) => new(key, DetailKind.None, null, null);

    [Fact]
    public void Empty_HasNoCurrent_AndNowhereToGo()
    {
        var history = new NavigationHistory();

        Assert.Null(history.Current);
        Assert.False(history.CanGoBack);
        Assert.False(history.CanGoForward);
        Assert.Null(history.Back());
        Assert.Null(history.Forward());
    }

    [Fact]
    public void Push_MakesTheEntryCurrent_AndBackWalksToTheOneBefore()
    {
        var history = new NavigationHistory();
        Assert.True(history.Push(Rail("dashboard")));
        Assert.True(history.Push(Rail("statistics")));

        Assert.Equal(Rail("statistics"), history.Current);
        Assert.True(history.CanGoBack);
        Assert.Equal(Rail("dashboard"), history.Back());
        Assert.False(history.CanGoBack);
        Assert.True(history.CanGoForward);
        Assert.Equal(Rail("statistics"), history.Forward());
        Assert.False(history.CanGoForward);
    }

    [Fact]
    public void PushingTheCurrentEntry_IsANoOp()
    {
        var history = new NavigationHistory();
        history.Push(Rail("dashboard"));

        // A record, so an equal value is the same place even as a different instance.
        Assert.False(history.Push(new NavigationEntry("dashboard", DetailKind.None, null, null)));
        Assert.Equal(1, history.Count);
        Assert.False(history.CanGoBack);
    }

    [Fact]
    public void PushingAfterBack_DiscardsTheForwardSide()
    {
        var history = new NavigationHistory();
        history.Push(Rail("dashboard"));
        history.Push(Rail("statistics"));
        history.Push(Rail("activity"));
        history.Back();
        history.Back();

        history.Push(Rail("settings"));

        Assert.False(history.CanGoForward);
        Assert.Equal(2, history.Count);
        Assert.Equal(Rail("dashboard"), history.Back());
    }

    [Fact]
    public void TheCap_DropsTheOldestEntry()
    {
        var history = new NavigationHistory();
        for (var i = 0; i <= NavigationHistory.HistoryCap; i++)
        {
            history.Push(Rail(i.ToString()));
        }

        Assert.Equal(NavigationHistory.HistoryCap, history.Count);
        Assert.Equal(Rail(NavigationHistory.HistoryCap.ToString()), history.Current);
        while (history.CanGoBack)
        {
            history.Back();
        }

        Assert.Equal(Rail("1"), history.Current);
    }
}
