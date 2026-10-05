using System.Text.Json;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GalactiLog.Data.Tests.Repositories;

/// <summary>
/// Spec 9.7 step 3's two refusals. The check runs before the insert rather than catching the
/// unique violation, because the alias half has no index behind it and because both sentences
/// name the conflicting target.
/// </summary>
/// <remarks>
/// Phase 14B fixer, fixer list item 24 (task3-review P3). Three cases used to wrap both sides of
/// an assertion in the same local sentence helper, which asserts only that two strings are equal,
/// and one compared the literal spec sentence against that helper's own format string. What this
/// layer actually decides is <c>ConflictingTargetName</c>, so that is what these cases assert.
/// The sentences themselves are composed in <c>CreateTargetViewModel.Publish</c> and pinned
/// against the production text by <c>CreateTargetViewModelTests</c>, which is the assembly that
/// can reach them; there is no second copy of either sentence here to drift.
/// </remarks>
public sealed class CreateUserDefinedConflictTests : IDisposable
{
    private readonly CreateTargetHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private Target Seed(string primaryName, string[]? aliases = null, Action<Target>? configure = null)
        => LibrarySeeder.AddTarget(_harness.Cs, primaryName, target =>
        {
            target.Aliases = JsonSerializer.Serialize(aliases ?? Array.Empty<string>());
            configure?.Invoke(target);
        });

    [Fact]
    public void AName_MatchingAnActiveTargetsPrimaryName_IsRefusedAndNamesThatTarget()
    {
        Seed("M 31");

        var result = _harness.Repository.CreateUserDefined(CreateTargetHarness.Request("M 31"));

        Assert.Equal(CreateTargetOutcome.NameInUse, result.Outcome);
        Assert.Equal("M 31", result.ConflictingTargetName);
        Assert.Null(result.TargetId);
    }

    [Fact]
    public void AName_MatchingAnActiveTargetsAlias_IsRefusedAndNamesThatTarget()
    {
        Seed("Andromeda", ["M 31", "NGC 224"]);

        var result = _harness.Repository.CreateUserDefined(CreateTargetHarness.Request("M 31"));

        Assert.Equal(CreateTargetOutcome.NameInUse, result.Outcome);
        Assert.Equal("Andromeda", result.ConflictingTargetName);
    }

    [Fact]
    public void AnAlias_MatchingAnActiveTargetsPrimaryName_IsRefusedAndNamesThatTarget()
    {
        Seed("M 31");

        var result = _harness.Repository.CreateUserDefined(
            CreateTargetHarness.Request("Something else entirely", aliases: ["M 31"]));

        Assert.Equal(CreateTargetOutcome.NameInUse, result.Outcome);
        Assert.Equal("M 31", result.ConflictingTargetName);
    }

    [Fact]
    public void AnAlias_MatchingAnActiveTargetsAlias_IsRefusedAndNamesThatTarget()
    {
        Seed("Andromeda", ["NGC 224"]);

        var result = _harness.Repository.CreateUserDefined(
            CreateTargetHarness.Request("Something else entirely", aliases: ["ngc 224"]));

        Assert.Equal(CreateTargetOutcome.NameInUse, result.Outcome);
        Assert.Equal("Andromeda", result.ConflictingTargetName);
    }

    [Fact]
    public void ACatalogId_MatchingAnActiveTargetsNormalizedId_IsRefusedAndNamesThatTarget()
    {
        Seed("Andromeda", configure: target =>
        {
            target.CatalogId = "M 31";
            target.CatalogIdNormalized = "M 31";
        });

        var result = _harness.Repository.CreateUserDefined(
            CreateTargetHarness.Request("Something else entirely", catalogId: "m   31"));

        Assert.Equal(CreateTargetOutcome.CatalogIdInUse, result.Outcome);
        Assert.Equal("Andromeda", result.ConflictingTargetName);
        Assert.Null(result.TargetId);
    }

    [Fact]
    public void TheRefusal_NamesTheConflictingTargetsPrimaryName_NotWhatWasTyped()
    {
        Seed("Andromeda Galaxy", ["M 31"]);

        var result = _harness.Repository.CreateUserDefined(CreateTargetHarness.Request("m 31"));

        // The conflicting target's primary_name, not the name that was typed. That is the whole
        // decision this layer makes; the sentence built from it is CreateTargetViewModel's.
        Assert.Equal("Andromeda Galaxy", result.ConflictingTargetName);
        Assert.NotEqual("m 31", result.ConflictingTargetName);
    }

    [Fact]
    public void AConflictWithAMergedAwayTarget_IsNotARefusal()
    {
        var winner = Seed("Winner");
        Seed("M 31", ["NGC 224"], target =>
        {
            target.MergedIntoId = winner.Id;
            target.MergedAt = DateTime.UtcNow;
            target.CatalogId = "M 31";
            target.CatalogIdNormalized = "M 31";
        });

        var result = _harness.Repository.CreateUserDefined(
            CreateTargetHarness.Request("M 31", catalogId: "M 31", aliases: ["NGC 224"]));

        Assert.Equal(CreateTargetOutcome.Created, result.Outcome);
    }

    [Fact]
    public void AConflict_WritesNothing()
    {
        Seed("M 31");
        var candidate = LibrarySeeder.AddMergeCandidate(_harness.Cs, "M 31");
        LibrarySeeder.AddFrame(_harness.Cs, null, new DateOnly(2026, 1, 4), image =>
            image.RawHeaders = LibrarySeeder.RawHeadersWithObject("M 31"));

        var before = Counts();

        var result = _harness.Repository.CreateUserDefined(CreateTargetHarness.Request("M 31"));

        Assert.Equal(CreateTargetOutcome.NameInUse, result.Outcome);
        Assert.Equal(before, Counts());

        using var context = _harness.Open();
        var unchanged = context.MergeCandidates.Single(row => row.Id == candidate.Id);
        Assert.Equal("pending", unchanged.Status);
        Assert.Null(unchanged.SuggestedTargetId);
        Assert.Null(unchanged.ResolvedAt);
    }

    [Fact]
    public void TheComparison_IsCaseInsensitive()
    {
        Seed("m 31");

        var result = _harness.Repository.CreateUserDefined(CreateTargetHarness.Request("M 31"));

        Assert.Equal(CreateTargetOutcome.NameInUse, result.Outcome);
    }

    [Fact]
    public void ASecondCreateOfTheSameName_IsRefusedRatherThanCreatingADuplicate()
    {
        // Spec 12.7's closing clause on the Targets cell.
        var first = _harness.Repository.CreateUserDefined(CreateTargetHarness.Request("Comet C/2026 X1"));
        Assert.Equal(CreateTargetOutcome.Created, first.Outcome);

        var second = _harness.Repository.CreateUserDefined(CreateTargetHarness.Request("Comet C/2026 X1"));

        Assert.Equal(CreateTargetOutcome.NameInUse, second.Outcome);
        Assert.Equal("Comet C/2026 X1", second.ConflictingTargetName);

        using var context = _harness.Open();
        Assert.Single(context.Targets);
    }

    /// <summary>The four row counts <c>AConflict_WritesNothing</c> brackets the refusal with:
    /// targets, frames carrying a resolved target, merge candidates that are still pending, and
    /// activity events.</summary>
    private (int Targets, int AssignedFrames, int PendingCandidates, int Events) Counts()
    {
        using var context = _harness.Open();
        return (
            context.Targets.Count(),
            context.Images.Count(row => row.ResolvedTargetId != null),
            context.MergeCandidates.Count(row => row.Status == "pending"),
            context.ActivityEvents.Count());
    }
}
