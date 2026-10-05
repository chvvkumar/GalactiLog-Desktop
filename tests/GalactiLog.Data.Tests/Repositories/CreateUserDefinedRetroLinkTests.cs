using GalactiLog.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GalactiLog.Data.Tests.Repositories;

/// <summary>
/// Spec 9.7 step 5: the frames a new target's names already own move to it, and the pending
/// merge candidates that named them close as accepted. One code path, whether the form was opened
/// from the tab or from an unresolved name: the unresolved entry point differs only in what it
/// puts in the form.
/// </summary>
/// <remarks>
/// The link is frame-driven, not candidate-driven (Phase 14B Task 3 review, escalation 1). The
/// unresolved list the form is opened from is itself frame-driven, so a name whose candidate was
/// dismissed, or for which no candidate was ever written, must still move its frames. A pending
/// candidate closes when one exists and is never required.
/// </remarks>
public sealed class CreateUserDefinedRetroLinkTests : IDisposable
{
    private static readonly DateOnly SessionDate = new(2026, 1, 4);

    private readonly CreateTargetHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private void AddFrames(string objectName, int count, string imageType = "LIGHT", Guid? targetId = null)
    {
        for (var i = 0; i < count; i++)
        {
            LibrarySeeder.AddFrame(_harness.Cs, targetId, SessionDate, image =>
            {
                image.RawHeaders = LibrarySeeder.RawHeadersWithObject(objectName);
                image.ImageType = imageType;
            });
        }
    }

    private int FramesOn(Guid targetId)
    {
        using var context = _harness.Open();
        return context.Images.Count(row => row.ResolvedTargetId == targetId);
    }

    [Fact]
    public void APendingCandidateMatchingTheName_HasItsFramesLinked()
    {
        LibrarySeeder.AddMergeCandidate(_harness.Cs, "Comet C/2026 X1");
        AddFrames("Comet C/2026 X1", 4);

        var result = _harness.Repository.CreateUserDefined(CreateTargetHarness.Request("Comet C/2026 X1"));

        Assert.Equal(CreateTargetOutcome.Created, result.Outcome);
        Assert.Equal(4, result.LinkedFrames);
        Assert.Equal(4, FramesOn(result.TargetId!.Value));
    }

    [Fact]
    public void APendingCandidateMatchingAnAlias_HasItsFramesLinked()
    {
        LibrarySeeder.AddMergeCandidate(_harness.Cs, "2026 X1");
        AddFrames("2026 X1", 2);

        var result = _harness.Repository.CreateUserDefined(
            CreateTargetHarness.Request("Comet C/2026 X1", aliases: ["2026 X1"]));

        Assert.Equal(2, result.LinkedFrames);
        Assert.Equal(1, result.ClosedCandidates);
    }

    [Fact]
    public void AMatchedCandidate_IsClosedAsAcceptedWithAResolvedAt()
    {
        var candidate = LibrarySeeder.AddMergeCandidate(_harness.Cs, "Comet C/2026 X1");
        AddFrames("Comet C/2026 X1", 1);

        var result = _harness.Repository.CreateUserDefined(CreateTargetHarness.Request("Comet C/2026 X1"));

        using var context = _harness.Open();
        var closed = context.MergeCandidates.Single(row => row.Id == candidate.Id);
        Assert.Equal("accepted", closed.Status);
        Assert.Equal(result.TargetId, closed.SuggestedTargetId);
        Assert.NotNull(closed.ResolvedAt);
    }

    [Fact]
    public void OnlyLightFramesMove()
    {
        LibrarySeeder.AddMergeCandidate(_harness.Cs, "Comet C/2026 X1");
        AddFrames("Comet C/2026 X1", 2);
        AddFrames("Comet C/2026 X1", 5, imageType: "DARK");

        var result = _harness.Repository.CreateUserDefined(CreateTargetHarness.Request("Comet C/2026 X1"));

        Assert.Equal(2, result.LinkedFrames);
        Assert.Equal(2, FramesOn(result.TargetId!.Value));
    }

    [Fact]
    public void AFrameAlreadyOwnedByAnotherTarget_IsNotMoved()
    {
        var owner = LibrarySeeder.AddTarget(_harness.Cs, "Some other target");
        LibrarySeeder.AddMergeCandidate(_harness.Cs, "Comet C/2026 X1");
        AddFrames("Comet C/2026 X1", 3);
        AddFrames("Comet C/2026 X1", 2, targetId: owner.Id);

        var result = _harness.Repository.CreateUserDefined(CreateTargetHarness.Request("Comet C/2026 X1"));

        Assert.Equal(3, result.LinkedFrames);
        Assert.Equal(2, FramesOn(owner.Id));
    }

    [Fact]
    public void ANonPendingCandidate_IsNotTouched()
    {
        var dismissed = LibrarySeeder.AddMergeCandidate(_harness.Cs, "Comet C/2026 X1", candidate =>
            candidate.Status = "dismissed");
        AddFrames("Comet C/2026 X1", 3);

        _harness.Repository.CreateUserDefined(CreateTargetHarness.Request("Comet C/2026 X1"));

        using var context = _harness.Open();
        var untouched = context.MergeCandidates.Single(row => row.Id == dismissed.Id);
        Assert.Equal("dismissed", untouched.Status);
        Assert.Null(untouched.SuggestedTargetId);
        Assert.Null(untouched.ResolvedAt);
    }

    [Fact]
    public void ADismissedCandidate_DoesNotStopTheFramesLinking()
    {
        // The review's escalation 1: the unresolved list is frame-driven, so this row is on screen
        // with a frame count even though its candidate is closed. Creating from it must move those
        // frames rather than report "linked 0 frames from 0 unresolved names".
        LibrarySeeder.AddMergeCandidate(_harness.Cs, "Comet C/2026 X1", candidate =>
            candidate.Status = "dismissed");
        AddFrames("Comet C/2026 X1", 3);

        var result = _harness.Repository.CreateUserDefined(CreateTargetHarness.Request("Comet C/2026 X1"));

        Assert.Equal(3, result.LinkedFrames);
        Assert.Equal(1, result.ClosedCandidates);
        Assert.Equal(3, FramesOn(result.TargetId!.Value));
    }

    [Fact]
    public void WithNoCandidateAtAll_TheFramesStillLink()
    {
        // The other half of the same rule: a name the dedup pass never wrote a candidate for is
        // still an unresolved name with frames, and a create still owns them.
        AddFrames("Comet C/2026 X1", 4);

        var result = _harness.Repository.CreateUserDefined(CreateTargetHarness.Request("Comet C/2026 X1"));

        Assert.Equal(4, result.LinkedFrames);
        Assert.Equal(1, result.ClosedCandidates);
        Assert.Equal(4, FramesOn(result.TargetId!.Value));

        using var context = _harness.Open();
        Assert.Empty(context.MergeCandidates);
    }

    [Fact]
    public void APendingCandidateForTheName_IsClosedAsAccepted()
    {
        var pending = LibrarySeeder.AddMergeCandidate(_harness.Cs, "Comet C/2026 X1");
        AddFrames("Comet C/2026 X1", 2);

        var result = _harness.Repository.CreateUserDefined(CreateTargetHarness.Request("Comet C/2026 X1"));

        using var context = _harness.Open();
        var closed = context.MergeCandidates.Single(row => row.Id == pending.Id);
        Assert.Equal("accepted", closed.Status);
        Assert.Equal(result.TargetId, closed.SuggestedTargetId);
        Assert.NotNull(closed.ResolvedAt);
    }

    [Fact]
    public void APendingCandidateWithNoFramesLeftToMove_IsStillClosed()
    {
        // Every frame of this name is already owned, so nothing moves, but the suggestion is
        // answered by the create either way. The name count stays 0, because no frame moved.
        var owner = LibrarySeeder.AddTarget(_harness.Cs, "Some other target");
        var pending = LibrarySeeder.AddMergeCandidate(_harness.Cs, "Comet C/2026 X1");
        AddFrames("Comet C/2026 X1", 2, targetId: owner.Id);

        var result = _harness.Repository.CreateUserDefined(CreateTargetHarness.Request("Comet C/2026 X1"));

        Assert.Equal(0, result.LinkedFrames);
        Assert.Equal(0, result.ClosedCandidates);

        using var context = _harness.Open();
        Assert.Equal("accepted", context.MergeCandidates.Single(row => row.Id == pending.Id).Status);
    }

    [Fact]
    public void ACreateWithNoMatchingCandidate_LinksNothingAndClosesNothing()
    {
        // Neither the candidate nor the frames carry one of the new target's names, so nothing
        // matches on either side.
        LibrarySeeder.AddMergeCandidate(_harness.Cs, "A different unresolved name");
        AddFrames("A different unresolved name", 4);

        var result = _harness.Repository.CreateUserDefined(CreateTargetHarness.Request("Comet C/2026 X1"));

        Assert.Equal(CreateTargetOutcome.Created, result.Outcome);
        Assert.Equal(0, result.LinkedFrames);
        Assert.Equal(0, result.ClosedCandidates);
        Assert.Equal(0, FramesOn(result.TargetId!.Value));
    }

    [Fact]
    public void TheResult_CarriesTheFrameAndCandidateCounts()
    {
        LibrarySeeder.AddMergeCandidate(_harness.Cs, "Comet C/2026 X1");
        LibrarySeeder.AddMergeCandidate(_harness.Cs, "2026 X1");
        AddFrames("Comet C/2026 X1", 3);
        AddFrames("2026 X1", 2);

        var result = _harness.Repository.CreateUserDefined(
            CreateTargetHarness.Request("Comet C/2026 X1", aliases: ["2026 X1"]));

        // Five frames from two distinct unresolved names, which is spec 12.7's "<n> frames from
        // <m> unresolved names".
        Assert.Equal(5, result.LinkedFrames);
        Assert.Equal(2, result.ClosedCandidates);
    }

    [Fact]
    public void TheMatch_IsCaseInsensitive()
    {
        LibrarySeeder.AddMergeCandidate(_harness.Cs, "comet c/2026 x1");
        AddFrames("comet c/2026 x1", 2);

        var result = _harness.Repository.CreateUserDefined(CreateTargetHarness.Request("Comet C/2026 X1"));

        Assert.Equal(2, result.LinkedFrames);
        Assert.Equal(1, result.ClosedCandidates);
    }
}
