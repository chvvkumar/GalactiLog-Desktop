using GalactiLog.App.Services;
using Xunit;

namespace GalactiLog.App.Tests.Services;

// Task 3 (feedback): the in-window notice stack. Plain xunit: a synchronous post, and a schedule
// seam that records each timer so a case fires it by hand rather than sleeping.
public class NotifierTests
{
    private sealed class Harness
    {
        public List<(TimeSpan Delay, Action Action)> Timers { get; } = [];

        public Notifier Notifier { get; }

        public Harness() => Notifier = new Notifier(action => action(), (delay, action) => Timers.Add((delay, action)));

        public void FireAll()
        {
            // Firing a dismiss timer schedules the fade's removal, so drain until quiet.
            while (Timers.Count > 0)
            {
                var (_, action) = Timers[0];
                Timers.RemoveAt(0);
                action();
            }
        }
    }

    [Theory]
    [InlineData(NoticeKind.Success, true)]
    [InlineData(NoticeKind.Info, true)]
    [InlineData(NoticeKind.Warning, false)]
    [InlineData(NoticeKind.Error, false)]
    public void SuccessAndInfo_DismissThemselvesAfterThreeSeconds_WarningAndErrorStay(NoticeKind kind, bool autoDismisses)
    {
        var harness = new Harness();

        harness.Notifier.Show("Something happened", kind);

        var notice = Assert.Single(harness.Notifier.Notices);
        Assert.Equal(kind, notice.Kind);
        if (autoDismisses)
        {
            Assert.Equal(Notifier.DefaultAutoDismiss, Assert.Single(harness.Timers).Delay);
            Assert.Equal(TimeSpan.FromSeconds(3), Notifier.DefaultAutoDismiss);
            harness.FireAll();
            Assert.Empty(harness.Notifier.Notices);
        }
        else
        {
            Assert.Empty(harness.Timers);
        }
    }

    [Fact]
    public void AnExplicitSpan_OverridesTheKindsDefault()
    {
        var harness = new Harness();

        harness.Notifier.Show("Stays", NoticeKind.Success, Timeout.InfiniteTimeSpan);
        harness.Notifier.Show("Goes", NoticeKind.Error, TimeSpan.FromSeconds(5));

        Assert.Equal(TimeSpan.FromSeconds(5), Assert.Single(harness.Timers).Delay);
    }

    [Fact]
    public void TheStack_HoldsThree_AndAFourthPushesTheOldestOut()
    {
        var harness = new Harness();

        for (var i = 1; i <= 4; i++)
        {
            harness.Notifier.Show($"Notice {i}", NoticeKind.Error);
        }

        Assert.Equal(Notifier.Cap, harness.Notifier.Notices.Count);
        Assert.Equal(["Notice 2", "Notice 3", "Notice 4"], harness.Notifier.Notices.Select(n => n.Text));
    }

    [Fact]
    public void Dismiss_FadesFirst_ThenRemoves_AndASecondDismissDoesNothing()
    {
        var harness = new Harness();
        harness.Notifier.Show("Failed", NoticeKind.Error);
        var notice = harness.Notifier.Notices[0];

        notice.DismissCommand.Execute(null);

        Assert.True(notice.IsClosing);
        Assert.Contains(notice, harness.Notifier.Notices);
        var fade = Assert.Single(harness.Timers);
        Assert.Equal(TimeSpan.FromMilliseconds(150), fade.Delay);

        notice.DismissCommand.Execute(null);
        Assert.Single(harness.Timers);

        harness.FireAll();
        Assert.Empty(harness.Notifier.Notices);
    }

    [Fact]
    public async Task SaveAll_ThatLeavesEveryTabClean_SaysSettingsSaved()
    {
        var notices = new Harness();
        var registry = new GalactiLog.App.ViewModels.PendingEditsRegistry(notifier: notices.Notifier, post: action => action());
        var source = new PendingSource { ClearsOnSave = true };
        registry.Register(source);
        source.Dirty = true;

        await registry.SaveAllCommand.ExecuteAsync(null);

        var notice = Assert.Single(notices.Notifier.Notices);
        Assert.Equal("Settings saved", notice.Text);
        Assert.Equal(NoticeKind.Success, notice.Kind);
    }

    // Review fix round 1, I2: a tab reports a refused or invalid save by returning with its edits
    // still pending, not by throwing, so "no source threw" is not success.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveAll_ThatLeavesATabUnsaved_SaysWhichTab_AsAnError(bool throws)
    {
        var notices = new Harness();
        var registry = new GalactiLog.App.ViewModels.PendingEditsRegistry(notifier: notices.Notifier, post: action => action());
        var source = new PendingSource { Throws = throws };
        registry.Register(source);
        source.Dirty = true;

        await registry.SaveAllCommand.ExecuteAsync(null);

        var notice = Assert.Single(notices.Notifier.Notices);
        Assert.Equal("Settings not saved: Library still has unsaved changes", notice.Text);
        Assert.Equal(NoticeKind.Error, notice.Kind);
    }

    private sealed class PendingSource : GalactiLog.App.ViewModels.IPendingEdits
    {
        private bool _dirty;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public string Label => "Library";

        public string NavigationKey => "library";

        public string? SaveRefusal => null;

        public bool Throws { get; set; }

        public bool ClearsOnSave { get; set; }

        public bool Dirty
        {
            set
            {
                _dirty = value;
                PropertyChanged?.Invoke(this, new(nameof(HasPendingEdits)));
            }
        }

        public bool HasPendingEdits => _dirty;

        public Task SaveAsync()
        {
            if (Throws)
            {
                throw new InvalidOperationException("locked");
            }

            if (ClearsOnSave)
            {
                Dirty = false;
            }

            return Task.CompletedTask;
        }

        public void Discard()
        {
        }
    }
}
