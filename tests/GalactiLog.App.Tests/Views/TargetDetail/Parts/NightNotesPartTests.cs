using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
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

// Content cases of NightNotesPart.
public class NightNotesPartTests
{
    [AvaloniaFact]
    public void NightNotesPart_UnresolvedGroup_RendersNoNotesBox()
    {
        // An obj: group has no target id to key a session note on, so the box is absent rather
        // than present and silently discarding what is typed into it.
        using var harness = Cards.Create(withNotes: false);
        harness.Card.IsExpanded = true;
        harness.Settle();

        var part = new NightNotesPart { DataContext = harness.Card };
        Show(part);

        Assert.False(part.Named<StackPanel>("SessionNotesBox").IsVisible);
    }

    [AvaloniaFact]
    public void NightNotesPart_SingleCard_RendersTodaysOneBox()
    {
        using var harness = Cards.Create();

        var part = new NightNotesPart { DataContext = harness.Card };
        Show(part);

        Assert.True(part.Named<TextBox>("SessionNotesTextBox").IsEffectivelyVisible);
        Assert.False(part.Named<ItemsControl>("MemberNotesBoxes").IsEffectivelyVisible);
        Assert.DoesNotContain(harness.Card.SessionDateText, VisibleTexts(part));
    }

    [AvaloniaFact]
    public void NightNotesPart_MergedCard_RendersOneHeadedBoxPerMemberBoundToThatNightsField()
    {
        // G5: the merged card's section body is the member nights' own boxes, in NoteNights order,
        // each headed by its date. Typing into one edits that member's field and no other.
        using var first = Cards.Create(overview: Page.Session(Page.LastSession.AddDays(-1)));
        using var second = Cards.Create(overview: Page.Session(Page.LastSession));
        using var merged = Cards.Create(
            nights: [first.Card.SessionDate, second.Card.SessionDate],
            noteNights: [first.Card, second.Card]);
        first.Card.Notes!.Text = "first night";
        second.Card.Notes!.Text = "second night";

        var part = new NightNotesPart { DataContext = merged.Card };
        Show(part);

        var boxes = MemberBoxes(part);
        Assert.Equal(new[] { "first night", "second night" }, boxes.Select(box => box.Text));
        var texts = VisibleTexts(part);
        Assert.Contains(first.Card.SessionDateText, texts);
        Assert.Contains(second.Card.SessionDateText, texts);
        Assert.False(part.Named<StackPanel>("SessionNotesBox").IsEffectivelyVisible);

        boxes[1].Text = "second night, edited";
        Assert.Equal("second night, edited", second.Card.Notes.Text);
        Assert.Equal("first night", first.Card.Notes.Text);
    }

    [AvaloniaFact]
    public void NightNotesPart_MergedCard_SkipsAMemberWithoutANotesField()
    {
        using var first = Cards.Create(overview: Page.Session(Page.LastSession.AddDays(-1)));
        using var second = Cards.Create(overview: Page.Session(Page.LastSession), withNotes: false);
        using var merged = Cards.Create(
            nights: [first.Card.SessionDate, second.Card.SessionDate],
            noteNights: [first.Card, second.Card]);

        var part = new NightNotesPart { DataContext = merged.Card };
        Show(part);

        Assert.Single(MemberBoxes(part));
        var texts = VisibleTexts(part);
        Assert.Contains(first.Card.SessionDateText, texts);
        Assert.DoesNotContain(second.Card.SessionDateText, texts);
    }

    /// <summary>The member boxes a merged card shows, in visual order.</summary>
    private static IReadOnlyList<TextBox> MemberBoxes(Control part)
        => [.. part.Named<ItemsControl>("MemberNotesBoxes")
            .GetVisualDescendants()
            .OfType<TextBox>()
            .Where(box => box.IsEffectivelyVisible)];
}
