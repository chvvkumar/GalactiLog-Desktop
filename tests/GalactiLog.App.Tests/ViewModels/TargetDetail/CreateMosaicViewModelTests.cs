using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Phase 18 Task 6, spec 12.17's Create mosaic dialog. The dialog is built over plain data (the
// checked nights' frame groups and the existing mosaics), and its one write and its one route are
// delegates the tests record.
public sealed class CreateMosaicViewModelTests
{
    private static readonly string[] Keywords = ["Panel", "P"];

    private readonly List<(string? Name, Guid? Existing, IReadOnlyList<(DateOnly Date, string? FrameLabel, string Label)> Rows)> _writes = [];
    private readonly List<Guid> _opened = [];
    private readonly Guid _createdId = Guid.NewGuid();
    private Exception? _refusal;

    private static DateOnly Day(int month, int day) => new(2026, month, day);

    private CreateMosaicViewModel Build(
        IReadOnlyList<DateOnly> nights,
        IReadOnlyList<NightFrameGroup> frames,
        IReadOnlyList<MosaicLink>? existing = null,
        string targetName = "NGC 7000")
        => new(
            targetName,
            nights,
            frames,
            existing ?? [],
            Keywords,
            (name, existingId, rows) =>
            {
                if (_refusal is not null)
                {
                    throw _refusal;
                }

                _writes.Add((name, existingId, rows));
                return existingId ?? _createdId;
            },
            _opened.Add);

    [Fact]
    public void Subline_NamesTheTargetAndTheNightCount()
    {
        Assert.Equal("NGC 7000, 2 nights", Build([Day(3, 1), Day(3, 2)], []).Subline);
        Assert.Equal("NGC 7000, 1 night", Build([Day(3, 1)], []).Subline);
    }

    [Fact]
    public void Name_IsPrefilledWithTheBaseAndOneMonth_WhenEveryNightFallsInIt()
    {
        var page = Build(
            [Day(3, 20), Day(3, 1)],
            [new(Day(3, 1), "Panel 1", "NGC 7000 Panel 1", 3), new(Day(3, 20), "Panel 2", "NGC 7000 P2", 2)],
            targetName: "North America Nebula");

        Assert.True(page.IsNew);
        Assert.Equal("NGC 7000 (Mar 2026)", page.Name);
    }

    [Fact]
    public void Name_IsPrefilledWithTheMonthRange_AndTheBaseCarriedByTheMostFrames()
    {
        var page = Build(
            [Day(5, 2), Day(3, 1)],
            [
                new(Day(3, 1), "Panel 1", "NGC 7000 Panel 1", 3),
                new(Day(5, 2), "Panel 2", "North America P2", 2),
                new(Day(5, 2), "Panel 3", "North America P3", 2),
                new(Day(5, 2), null, "Unlabelled", 9),
            ]);

        Assert.Equal("North America (Mar 2026 - May 2026)", page.Name);
    }

    [Fact]
    public void Name_TiesGoToTheOrdinallyFirstBase_AndNoTokenFallsBackToTheTargetName()
    {
        var tied = Build([Day(3, 1)], [new(Day(3, 1), "Panel 1", "beta P1", 2), new(Day(3, 1), "Panel 2", "Alpha P2", 2)]);
        Assert.Equal("Alpha (Mar 2026)", tied.Name);

        var none = Build([Day(3, 1)], [new(Day(3, 1), null, "NGC 7000", 4)], targetName: "NGC 7000 primary");
        Assert.Equal("NGC 7000 primary (Mar 2026)", none.Name);
    }

    [Fact]
    public void Rows_AreOnePerNightAndFrameLabel_NewestFirst_LabelPrefilledFromTheFrameLabel()
    {
        var page = Build(
            [Day(3, 1), Day(3, 2)],
            [
                new(Day(3, 1), "Panel 1", "NGC 7000 Panel 1", 3),
                new(Day(3, 1), "Panel 1", "NGC 7000 panel 1", 1),
                new(Day(3, 1), null, "NGC 7000", 2),
                new(Day(3, 2), "Panel 2", "NGC 7000 P2", 5),
            ]);

        Assert.Equal(
            [("2026-03-02", "Panel 2", 5, "Panel 2"), ("2026-03-01", "No label", 2, ""), ("2026-03-01", "Panel 1", 4, "Panel 1")],
            page.Rows.Select(row => (row.NightText, row.FrameLabelText, row.Frames, row.Label)));
    }

    [Fact]
    public void Existing_IsDisabledWithTheReason_WhenNoMosaicIncludesTheTarget()
    {
        var none = Build([Day(3, 1)], []);
        Assert.False(none.CanUseExisting);
        Assert.Equal("No existing mosaic includes this target.", none.ExistingHint);

        var some = Build([Day(3, 1)], [], [new MosaicLink(Guid.NewGuid(), "Alpha")]);
        Assert.True(some.CanUseExisting);
        Assert.Null(some.ExistingHint);
        Assert.Equal("Alpha", Assert.Single(some.Existing).Name);
    }

    [Fact]
    public void Create_IsEnabledOnlyWithEveryLabelAndANameOrAChosenMosaic()
    {
        var alpha = new MosaicLink(Guid.NewGuid(), "Alpha");
        var page = Build([Day(3, 1)], [new(Day(3, 1), null, "NGC 7000", 2)], [alpha]);

        Assert.False(page.CreateCommand.CanExecute(null));
        page.Rows[0].Label = "  Panel 1 ";
        Assert.True(page.CreateCommand.CanExecute(null));
        page.Name = "   ";
        Assert.False(page.CreateCommand.CanExecute(null));

        page.IsNew = false;
        Assert.False(page.CreateCommand.CanExecute(null));
        page.SelectedExisting = alpha;
        Assert.True(page.CreateCommand.CanExecute(null));
    }

    [Fact]
    public void Create_WritesEveryRowWithItsFramesOwnLabel_SoRowsSharingALabelShareOnePanel_ThenClosesAndOpensThePage()
    {
        var page = Build(
            [Day(3, 1), Day(3, 2)],
            [new(Day(3, 1), "Panel 1", "NGC 7000 Panel 1", 3), new(Day(3, 2), null, "NGC 7000", 2)]);
        page.Rows[0].Label = "panel 1";
        var closed = new List<bool>();
        page.CloseRequested += (_, result) => closed.Add(result);

        page.CreateCommand.Execute(null);

        var write = Assert.Single(_writes);
        Assert.Equal("NGC 7000 (Mar 2026)", write.Name);
        Assert.Null(write.Existing);
        Assert.Equal([(Day(3, 2), (string?)null, "panel 1"), (Day(3, 1), "Panel 1", "Panel 1")], write.Rows);
        Assert.Equal([true], closed);
        Assert.Equal([_createdId], _opened);
    }

    [Fact]
    public void Create_IntoAnExistingMosaic_PassesItsIdAndNoName()
    {
        var alpha = new MosaicLink(Guid.NewGuid(), "Alpha");
        var page = Build([Day(3, 1)], [new(Day(3, 1), "Panel 1", "NGC 7000 P1", 2)], [alpha]);
        page.IsNew = false;
        page.SelectedExisting = alpha;

        page.CreateCommand.Execute(null);

        var write = Assert.Single(_writes);
        Assert.Null(write.Name);
        Assert.Equal(alpha.MosaicId, write.Existing);
        Assert.Equal([alpha.MosaicId], _opened);
    }

    [Fact]
    public void Create_ATakenName_IsRefusedInlineUnderTheName_AndNothingCloses()
    {
        var page = Build([Day(3, 1)], [new(Day(3, 1), "Panel 1", "NGC 7000 P1", 2)]);
        var closed = false;
        page.CloseRequested += (_, _) => closed = true;
        _refusal = new DuplicateMosaicNameException("NGC 7000 (Mar 2026)");

        page.CreateCommand.Execute(null);

        Assert.Equal("A mosaic named \"NGC 7000 (Mar 2026)\" already exists.", page.NameError);
        Assert.Null(page.Error);
        Assert.False(closed);
        Assert.Empty(_opened);

        page.Name = "Something else";
        Assert.Null(page.NameError);
    }

    [Fact]
    public void Create_AnotherRefusalOrAFailure_IsShownBesideCreate()
    {
        var page = Build([Day(3, 1)], [new(Day(3, 1), "Panel 1", "NGC 7000 P1", 2)]);
        _refusal = new NightAlreadyInMosaicException(Day(3, 1), "NGC 7000", "Panel 1", "Panel 4");

        page.CreateCommand.Execute(null);
        Assert.Equal("2026-03-01 of NGC 7000 (Panel 1) is already in panel Panel 4.", page.Error);

        _refusal = new InvalidOperationException("disk");
        page.CreateCommand.Execute(null);
        Assert.Equal(MosaicMessages.CouldNotSave, page.Error);
        Assert.Empty(_opened);
    }

    [Fact]
    public void Cancel_ClosesAndWritesNothing()
    {
        var page = Build([Day(3, 1)], [new(Day(3, 1), "Panel 1", "NGC 7000 P1", 2)]);
        var closed = new List<bool>();
        page.CloseRequested += (_, result) => closed.Add(result);

        page.CancelCommand.Execute(null);

        Assert.Equal([false], closed);
        Assert.Empty(_writes);
        Assert.Empty(_opened);
    }
}
