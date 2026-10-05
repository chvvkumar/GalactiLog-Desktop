using System.Collections.Concurrent;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GalactiLog.App.Tests.Services;
using GalactiLog.App.ViewModels.CustomColumns;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.CustomColumnTestFactory;

namespace GalactiLog.App.Tests.ViewModels.CustomColumns;

// Phase 20 Task 3, brief section 7 cases 1 to 14. Spec 12.15's one cell view-model: the kind comes
// from the column's type alone, the two at-once kinds commit on every change, the text kind writes
// through the one AutosaveField spine, and a refused write keeps the stored value and says so.
//
// No database and no dispatcher except where a case is about the dispatcher: the cell writes
// through a delegate and the debounce is FakeDelay, parked and released rather than slept through.
public class CustomValueViewModelTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static readonly CustomWriteResult Refused =
        new(CustomWriteStatus.NotAnOption, CustomColumnMessages.NotAnOption("Urgent"));

    [Fact]
    public void TheKind_ComesFromTheColumnTypeAlone()
    {
        // The scope and the stored value differ on every row and neither may reach the kind: a kind
        // chosen from either is what lets the four surfaces drift apart (spec 12.15).
        using var check = Factory.Cell(
            Factory.Boolean(scope: CustomColumnScope.Rig), stored: "some text");
        using var text = Factory.Cell(
            Factory.Text(scope: CustomColumnScope.Session), stored: CustomColumnSlug.True);
        using var choice = Factory.Cell(Factory.Dropdown("High", "Low"), stored: null);

        Assert.Equal<bool[]>([true, false, false], [check.IsCheckBox, check.IsTextBox, check.IsComboBox]);
        Assert.Equal<bool[]>([false, true, false], [text.IsCheckBox, text.IsTextBox, text.IsComboBox]);
        Assert.Equal<bool[]>([false, false, true], [choice.IsCheckBox, choice.IsTextBox, choice.IsComboBox]);

        // And the kind's own field is the only one built.
        Assert.Null(check.Text);
        Assert.NotNull(text.Text);
        Assert.Empty(text.Choices);
    }

    [Fact]
    public async Task AToggle_WritesTrueThenFalse_AtOnce()
    {
        // The delay is never released. A check box routed through the debounce would have asked for
        // a window and written nothing at all.
        var delay = new FakeDelay();
        var log = new Factory.WriteLog();
        using var cell = Factory.Cell(Factory.Boolean(), write: log.Write, delay: delay.Delay);

        cell.IsChecked = true;
        await Factory.SettleAsync(cell);
        cell.IsChecked = false;
        await Factory.SettleAsync(cell);

        Assert.Equal<string?[]>([CustomColumnSlug.True, CustomColumnSlug.False], [.. log.Values]);
        Assert.Empty(delay.Requested);
    }

    [Fact]
    public async Task ACheckBox_HasNoThirdState()
    {
        var log = new Factory.WriteLog();
        using var cell = Factory.Cell(Factory.Boolean(), stored: null, write: log.Write);

        // A slot that holds nothing renders cleared, which is the same as false.
        Assert.False(cell.IsChecked);

        cell.IsChecked = true;
        await Factory.SettleAsync(cell);
        cell.IsChecked = false;
        await Factory.SettleAsync(cell);

        // Clearing stores the literal false. A tri-state that deleted instead would make the
        // filter's No arm unanswerable, because a never-touched target and a cleared one would be
        // the same row absence with two meanings.
        Assert.Equal(CustomColumnSlug.False, log.Values[^1]);
        Assert.DoesNotContain(log.Values, value => value is null);
    }

    [Fact]
    public async Task Typing_WritesOnceAfterTheIdleWindow()
    {
        var delay = new FakeDelay();
        var log = new Factory.WriteLog();
        using var cell = Factory.Cell(Factory.Text(), write: log.Write, delay: delay.Delay);

        cell.Text!.Text = "M";
        cell.Text.Text = "M 3";
        cell.Text.Text = "M 31 notes";

        // Three windows opened and the first two were cancelled. A write per keystroke would have
        // put three calls in the log before the release below.
        Assert.Empty(log.Calls);
        Assert.Equal(3, delay.Requested.Count);

        delay.Release();
        await Factory.SettleAsync(cell);

        Assert.Equal("M 31 notes", Assert.Single(log.Calls).Value);
    }

    [Fact]
    public async Task TypingThenFlush_WritesTheNewestTextOnce()
    {
        var delay = new FakeDelay();
        var log = new Factory.WriteLog();
        using var cell = Factory.Cell(Factory.Text(), write: log.Write, delay: delay.Delay);

        cell.Text!.Text = "half";
        cell.Text.Text = "the whole note";
        await cell.FlushAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("the whole note", Assert.Single(log.Calls).Value);

        // Releasing the window the flush superseded must not queue a duplicate.
        delay.Release();
        await Factory.SettleAsync(cell);
        Assert.Single(log.Calls);
    }

    [Fact]
    public void ARefreshDuringTyping_DoesNotClobberTheText()
    {
        var delay = new FakeDelay();
        using var cell = Factory.Cell(Factory.Text(), stored: "the old note", delay: delay.Delay);

        cell.Text!.Text = "what the user is typing";
        cell.Reseed("the old note");

        // A reseed that assigned unconditionally would put the catalogue value back over live
        // typing, which is the bug AutosaveField's own reseed guard exists to prevent.
        Assert.Equal("what the user is typing", cell.Text.Text);
    }

    [Fact]
    public async Task ClearingTheTextBox_SendsNull()
    {
        var delay = new FakeDelay();
        var log = new Factory.WriteLog();
        using var cell = Factory.Cell(
            Factory.Text(), stored: "typed", write: log.Write, delay: delay.Delay);

        cell.Text!.Text = "";
        delay.Release();
        await Factory.SettleAsync(cell);

        // Null, not "", because that is what CustomColumnRepository.SetValue turns into a delete.
        Assert.Null(Assert.Single(log.Calls).Value);
    }

    [Fact]
    public async Task SelectingNotSet_SendsNull()
    {
        var log = new Factory.WriteLog();
        using var cell = Factory.Cell(
            Factory.Dropdown("High", "Low"), stored: "High", write: log.Write);

        cell.Selected = CustomValueViewModel.NotSet;
        await Factory.SettleAsync(cell);

        // Departure 3. The web guards on `if (val)` and never sends at all, so a dropdown value
        // once set can never be removed there.
        Assert.Null(Assert.Single(log.Calls).Value);
    }

    [Fact]
    public void TheFirstChoice_IsNotSet_AndTheRestAreTheOptionsInOrder()
    {
        using var cell = Factory.Cell(Factory.Dropdown("Urgent", "High", "Low"));

        Assert.Equal<string[]>(
            [CustomValueViewModel.NotSet, "Urgent", "High", "Low"], [.. cell.Choices]);

        // An empty slot renders as the first entry rather than as a blank box.
        Assert.Equal(CustomValueViewModel.NotSet, cell.Selected);
    }

    [Fact]
    public async Task ARefusedWrite_RevertsAndCarriesTheSentence_OnTheCheckBox()
    {
        var log = new Factory.WriteLog { Answer = _ => Refused };
        using var cell = Factory.Cell(Factory.Boolean(), stored: null, write: log.Write);

        cell.IsChecked = true;
        await Factory.SettleAsync(cell);

        Assert.False(cell.IsChecked);
        Assert.Equal(Refused.Message, cell.Refusal);
        Assert.True(cell.IsRefused);

        // The revert goes through the same suppression the constructor uses, so it queues no
        // second write. A revert that wrote would fight the refusal it came from.
        Assert.Single(log.Calls);
    }

    [Fact]
    public async Task ARefusedWrite_RevertsAndCarriesTheSentence_OnTheComboBox()
    {
        var log = new Factory.WriteLog { Answer = _ => Refused };
        using var cell = Factory.Cell(
            Factory.Dropdown("High", "Low"), stored: "High", write: log.Write);

        cell.Selected = "Low";
        await Factory.SettleAsync(cell);

        Assert.Equal("High", cell.Selected);
        Assert.Equal(Refused.Message, cell.Refusal);
        Assert.Single(log.Calls);
    }

    [Fact]
    public async Task ARefusedWrite_RevertsAndCarriesTheSentence_OnTheTextBox()
    {
        var delay = new FakeDelay();
        var log = new Factory.WriteLog { Answer = _ => Refused };
        using var cell = Factory.Cell(
            Factory.Text(), stored: "kept", write: log.Write, delay: delay.Delay);

        cell.Text!.Text = "refused text";
        delay.Release();
        await Factory.SettleAsync(cell);

        Assert.Equal("kept", cell.Text.Text);
        Assert.Equal(Refused.Message, cell.Refusal);

        // The revert reopened one idle window carrying the stored value; releasing it must still
        // leave one call, because a write of the value the catalogue already holds is dropped.
        delay.Release();
        await Factory.SettleAsync(cell);
        Assert.Single(log.Calls);
    }

    [Fact]
    public async Task TheNextSuccessfulWrite_ClearsTheRefusal()
    {
        var refuse = true;
        var log = new Factory.WriteLog { Answer = _ => refuse ? Refused : Factory.Written };
        using var cell = Factory.Cell(Factory.Boolean(), stored: null, write: log.Write);

        cell.IsChecked = true;
        await Factory.SettleAsync(cell);
        Assert.True(cell.IsRefused);

        refuse = false;
        cell.IsChecked = true;
        await Factory.SettleAsync(cell);

        Assert.Null(cell.Refusal);
        Assert.False(cell.IsRefused);
        Assert.True(cell.IsChecked);
    }

    [Fact]
    public void TheAutomationName_IsTheColumnNameThenTheSubject()
    {
        // Spec 12.15's three subject shapes. The surface composes the subject; this view-model only
        // puts the column's NAME in front of it, never its slug.
        using var targetRow = Factory.Cell(Factory.Boolean("Priority"), subject: "NGC 7000");
        using var nightRow = Factory.Cell(Factory.Boolean("Priority"), subject: "2026-03-14");
        using var rigRow = Factory.Cell(
            Factory.Boolean(), subject: "2026-03-14, Askar FMA180 / ASI2600MC");

        Assert.Equal("Priority, NGC 7000", targetRow.AutomationName);
        Assert.Equal("Priority, 2026-03-14", nightRow.AutomationName);
        Assert.Equal("Done, 2026-03-14, Askar FMA180 / ASI2600MC", rigRow.AutomationName);

        // The watermark and the first part of the name are the one string.
        Assert.Equal("Priority", targetRow.Label);
    }

    [AvaloniaFact]
    public async Task EveryContinuation_StaysOnTheCapturedContext()
    {
        // The shipped post seam, UiPost.Default, rather than the inline one every other case binds:
        // this is the case that catches a ConfigureAwait(false) on the way back from the write,
        // which is the shape the Phase 14B Clear log crash established (TRACKING section 5).
        var log = new Factory.WriteLog { Answer = _ => Refused };
        using var cell = new CustomValueViewModel(
            Factory.Boolean(),
            CustomValueKey.ForTarget(Guid.NewGuid()),
            stored: null,
            subject: "NGC 7000",
            log.Write,
            Factory.Instant);

        var offTheUiThread = false;
        var reached = false;
        cell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(CustomValueViewModel.Refusal))
            {
                return;
            }

            reached = true;
            offTheUiThread = !Dispatcher.UIThread.CheckAccess();
        };

        cell.IsChecked = true;
        await Factory.SettleAsync(cell);
        Dispatcher.UIThread.RunJobs();

        Assert.True(reached, "the refusal never reached the view-model");
        Assert.False(offTheUiThread, "an [ObservableProperty] write landed off the UI thread");
    }

    [Fact]
    public async Task EscapeThenARefresh_AdoptsTheNewerValue_AndNeverWritesThePreEscapeOne()
    {
        // Review P1-1, consequence 1. A cell holding A, the user types B and presses Escape, and a
        // refresh carrying C arrives inside the second the old idle window still had left.
        var delay = new FakeDelay();
        var log = new Factory.WriteLog();
        using var cell = Factory.Cell(
            Factory.Text(), stored: "A", write: log.Write, delay: delay.Delay);

        cell.Text!.Text = "B";
        cell.CancelEdit();
        Assert.Equal("A", cell.Text.Text);

        // Escape left the field clean, so the refresh is adopted. An Escape that only assigned the
        // text would have left it dirty: the adoption would be refused, the screen would keep A,
        // and the window would then write A over C.
        cell.Reseed("C");
        Assert.Equal("C", cell.Text.Text);

        delay.Release();
        await Factory.SettleAsync(cell);

        Assert.Empty(log.Calls);
        Assert.Equal("C", cell.Text.Text);
    }

    [Fact]
    public async Task AValueReturningToTheStoredOne_DuringAnInFlightWrite_IsNotDropped()
    {
        // Review P1-1, consequence 2. The publish back to the UI thread is queued and not yet run,
        // so the last known stored value has not caught up with the write that is finishing. The
        // deleted equality guard compared against it and dropped this write, leaving the catalogue
        // holding B while the screen showed A, with no dirty flag left to recover it.
        var delay = new FakeDelay();
        var log = new Factory.WriteLog();
        var posted = new ConcurrentQueue<Action>();
        using var cell = Factory.Cell(
            Factory.Text(), stored: "A", write: log.Write, delay: delay.Delay, post: posted.Enqueue);

        cell.Text!.Text = "B";
        delay.Release();
        await Factory.SettleAsync(cell);

        cell.Text.Text = "A";
        delay.Release();
        await Factory.SettleAsync(cell);

        while (posted.TryDequeue(out var publish))
        {
            publish();
        }

        Assert.Equal(new string?[] { "B", "A" }, log.Values);
    }

    [Fact]
    public async Task TypeEscapeThenTypeTheSameTextAgain_WritesOnce()
    {
        var delay = new FakeDelay();
        var log = new Factory.WriteLog();
        using var cell = Factory.Cell(
            Factory.Text(), stored: "A", write: log.Write, delay: delay.Delay);

        cell.Text!.Text = "B";
        cell.CancelEdit();

        // Escape cancelled the open window, so releasing every parked wait writes nothing. An
        // Abandon that left the window running would write B here, which is the value the user
        // abandoned.
        delay.Release();
        await Factory.SettleAsync(cell);
        Assert.Empty(log.Calls);

        // And the same text typed again is still a real edit: nothing dedupes it away.
        cell.Text.Text = "B";
        delay.Release();
        await Factory.SettleAsync(cell);

        Assert.Equal("B", Assert.Single(log.Calls).Value);
    }

    [Fact]
    public async Task TwoChangesInQuickSuccession_ReachTheStoreInOrder_AndTheLastOneWins()
    {
        // Review P2-1. The two at-once kinds are chained the way the text kind's writes are, so two
        // changes inside one write's duration cannot open two transactions over the same slot and
        // land in an undefined order.
        var order = new ConcurrentQueue<string?>();
        using var firstEntered = new ManualResetEventSlim(false);
        using var secondEntered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var outstanding = 1;

        using var cell = Factory.Cell(Factory.Dropdown("High", "Low"), stored: null, write: Blocking);

        cell.Selected = "High";
        Assert.True(firstEntered.Wait(Budget), "the first write never started");

        cell.Selected = "Low";

        // A bounded negative probe, not a settle: while the first write is still inside the
        // delegate the second must not be able to start. An independent task per change reaches
        // the delegate here and this returns true; a chained one cannot, and the probe costs a
        // quarter of a second once.
        Assert.False(
            secondEntered.Wait(TimeSpan.FromMilliseconds(250)),
            "the second write started while the first was still running");

        release.Set();
        await Factory.SettleAsync(cell);

        Assert.Equal(new string?[] { "High", "Low" }, order.ToArray());
        Assert.Equal("Low", cell.Selected);

        CustomWriteResult Blocking(Guid columnId, CustomValueKey key, string? value)
        {
            if (Interlocked.Exchange(ref outstanding, 0) == 1)
            {
                firstEntered.Set();
                Assert.True(release.Wait(Budget), "the first write was never released");
            }
            else
            {
                secondEntered.Set();
            }

            order.Enqueue(value);
            return Factory.Written;
        }
    }

    [Fact]
    public async Task AThrownWrite_ShowsTheApprovedSentence_NotTheExceptionMessage()
    {
        // Refusal is bound to a tooltip and to AutomationProperties.HelpText, so an exception
        // message would be drawn in a cell and read aloud by a screen reader. The sentence is the
        // shared one, so a cell that composed its own would fail here.
        var log = new Factory.WriteLog
        {
            Answer = _ => throw new InvalidOperationException("SQLite Error 5: 'database is locked'"),
        };
        using var cell = Factory.Cell(Factory.Boolean(), stored: null, write: log.Write);

        cell.IsChecked = true;
        await Factory.SettleAsync(cell);

        Assert.Equal(CustomColumnMessages.CouldNotSave, cell.Refusal);
        Assert.DoesNotContain("SQLite", cell.Refusal!, StringComparison.Ordinal);

        // And a thrown write leaves the cell exactly where a refusal does.
        Assert.False(cell.IsChecked);
    }

    [Fact]
    public async Task EscapeAfterTheWriteHasGone_ShowsTheTypedTextOnceItLands()
    {
        // Ruling C28. The idle window has already elapsed, so the typed value is on the write chain
        // and cannot be recalled. Escape drops the edit and puts the stored value on screen, and
        // nothing corrected the screen afterwards, so the reader saw the pre-edit text while the
        // catalogue held the typed one, until the next page load. Under a database held by a scan
        // that window is seconds.
        //
        // Red against the tree before this fix: the assertion below reads "A", the pre-edit text.
        var delay = new FakeDelay();
        var log = new Factory.WriteLog();
        var started = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        using var cell = Factory.Cell(
            Factory.Text(), stored: "A", write: Write, delay: delay.Delay);

        cell.Text!.Text = "B";
        delay.Release();
        Assert.True(started.Wait(Budget), "The write never reached the delegate.");

        // Escape lands with the write inside the delegate: the value is gone and the screen goes
        // back to A.
        cell.CancelEdit();
        Assert.Equal("A", cell.Text.Text);

        release.Set();
        await Factory.SettleAsync(cell);

        Assert.Equal(new string?[] { "B" }, log.Values);
        Assert.Equal("B", cell.Text.Text);

        CustomWriteResult Write(Guid columnId, CustomValueKey key, string? value)
        {
            started.Set();
            release.Wait(Budget);
            return log.Write(columnId, key, value);
        }
    }

    [Fact]
    public async Task AColumnThatNoLongerExists_ShowsOneSentenceRatherThanRevertingInSilence()
    {
        // Journeys P2-3. CustomColumnRepository answers ColumnNotFound with a null message, because
        // it was a programming error when it was written. It is a user path now: Reset database
        // clears both tables and does not reload an open page, so every toggle in a dashboard cell
        // built before the reset refuses. With a null message the cell snapped back with no error
        // ink, no tooltip and nothing announced, which reads as a control that lost the input.
        //
        // Red against `refusal = result.Ok ? null : result.Message`, where Refusal is null here.
        var log = new Factory.WriteLog
        {
            Answer = _ => new CustomWriteResult(CustomWriteStatus.ColumnNotFound, Message: null),
        };
        using var cell = Factory.Cell(Factory.Boolean(), write: log.Write);

        cell.IsChecked = true;
        await Factory.SettleAsync(cell);

        Assert.Equal(CustomColumnMessages.ColumnGone, cell.Refusal);
        Assert.True(cell.IsRefused);
        Assert.False(cell.IsChecked);
    }

    [Fact]
    public async Task ARefusalWithNoSentenceOfItsOwn_StillSaysSomething()
    {
        // The fail-closed half: the guard is at the one site that sets Refusal, so a status added
        // later with no message cannot revert a cell in silence either. KeyDoesNotMatchScope is the
        // second status that carries a null message today, and "this column no longer exists" would
        // be the wrong sentence for it.
        var log = new Factory.WriteLog
        {
            Answer = _ => new CustomWriteResult(CustomWriteStatus.KeyDoesNotMatchScope, Message: null),
        };
        using var cell = Factory.Cell(Factory.Boolean(), write: log.Write);

        cell.IsChecked = true;
        await Factory.SettleAsync(cell);

        Assert.Equal(CustomColumnMessages.CouldNotSave, cell.Refusal);
    }

    [Fact]
    public async Task FlushAsync_OnACheckBox_AwaitsTheToggleThatIsStillInFlight()
    {
        // Phase review target P2-2. Ruling C20 declares the flush as every cell; it forwarded to the
        // text field alone, so a toggle in flight was not awaited by the page's close flush or the
        // window's. Red against `Text?.FlushAsync() ?? Task.CompletedTask`, where the flush below is
        // already completed with the write still parked.
        var log = new Factory.WriteLog();
        var started = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        using var cell = Factory.Cell(Factory.Dropdown("High", "Low"), write: Write);

        cell.Selected = "High";
        Assert.True(started.Wait(Budget), "The at-once write never reached the delegate.");

        var flush = cell.FlushAsync();
        Assert.False(flush.IsCompleted);

        release.Set();
        await flush.WaitAsync(Budget);
        Assert.Equal(new string?[] { "High" }, log.Values);

        CustomWriteResult Write(Guid columnId, CustomValueKey key, string? value)
        {
            started.Set();
            release.Wait(Budget);
            return log.Write(columnId, key, value);
        }
    }

    [Fact]
    public async Task AReseedDuringAToggleOfItsOwn_LeavesTheCellAlone()
    {
        // Ruling C27's "a reseed never touches a cell that is dirty or focused", for the two kinds
        // that have no dirty flag because they commit on the click. The value offered here was read
        // before this cell's own write, so adopting it would put the pre-click state back under the
        // reader's hand and the next click would write what the catalogue already holds. Red against
        // a Reseed with no in-flight check, where IsChecked goes back to false.
        var log = new Factory.WriteLog();
        var started = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        using var cell = Factory.Cell(Factory.Boolean(), write: Write);

        cell.IsChecked = true;
        Assert.True(started.Wait(Budget), "The at-once write never reached the delegate.");

        cell.Reseed(null);
        Assert.True(cell.IsChecked);

        release.Set();
        await Factory.SettleAsync(cell);

        // And once the write has landed the cell adopts again, so a genuinely newer value still
        // reaches it.
        cell.Reseed(null);
        Assert.False(cell.IsChecked);

        CustomWriteResult Write(Guid columnId, CustomValueKey key, string? value)
        {
            started.Set();
            release.Wait(Budget);
            return log.Write(columnId, key, value);
        }
    }

    [Fact]
    public async Task Dispose_CancelsAnOpenWindow()
    {
        var delay = new FakeDelay();
        var log = new Factory.WriteLog();
        var cell = Factory.Cell(Factory.Text(), write: log.Write, delay: delay.Delay);

        cell.Text!.Text = "typed into a row that is about to be recycled";
        var pending = cell.PendingWrite;
        cell.Dispose();

        delay.Release();
        await pending.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Empty(log.Calls);
    }
}
