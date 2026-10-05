using GalactiLog.Core.Settings;
using Xunit;

namespace GalactiLog.Core.Tests.Settings;

// Spec 12.15's slug rule (core-shapes.md section 3), one case per numbered step. Each case is
// written to fail if the step it names is skipped: reverting any one step of CustomColumnSlug.Base
// to a no-op turns that step's own case red without touching the others.
public class CustomColumnSlugTests
{
    [Fact]
    public void Base_Step1_LowersTheName()
    {
        // Reverting step 1 (no lower-casing) leaves "Processed" uppercase and this fails.
        Assert.Equal("custom_processed", CustomColumnSlug.Base("Processed"));
    }

    [Fact]
    public void Base_Step2_ReplacesEachRunOfDisallowedCharactersWithOneUnderscore()
    {
        // Reverting step 2 (no replacement) would leave the space in place, or a naive
        // per-character replacement would leave "notes__tag" (two underscores) instead of one.
        Assert.Equal("custom_notes_tag", CustomColumnSlug.Base("Notes tag"));
    }

    [Fact]
    public void Base_Step3_TrimsLeadingAndTrailingUnderscores()
    {
        // Reverting step 3 (no trim) leaves "custom__priority_" with the run from the leading and
        // trailing punctuation still attached.
        Assert.Equal("custom_priority", CustomColumnSlug.Base(" -Priority- "));
    }

    [Fact]
    public void Base_Step4_FallsBackToColumn_WhenNothingIsLeft()
    {
        // Reverting step 4 (no fallback) leaves "custom_", an empty slug body.
        Assert.Equal("custom_column", CustomColumnSlug.Base("***"));
    }

    [Fact]
    public void Base_Step5_AlwaysPrefixesCustom()
    {
        // Reverting step 5 (no prefix) leaves the bare "priority", which could collide with a
        // built-in display.columns key.
        Assert.StartsWith("custom_", CustomColumnSlug.Base("Priority"));
    }

    [Fact]
    public void Unique_Step6_AppendsAnIncrementingSuffixUntilFree()
    {
        // Reverting step 6 (no disambiguation) returns the bare base slug every time and this
        // fails, because the second and third calls would collide with the first.
        var taken = new HashSet<string>(StringComparer.Ordinal) { "custom_priority", "custom_priority_2" };

        Assert.Equal("custom_priority_3", CustomColumnSlug.Unique("Priority", taken.Contains));
    }

    [Fact]
    public void Unique_NameNotTaken_ReturnsTheBaseSlugUnchanged()
        => Assert.Equal("custom_priority", CustomColumnSlug.Unique("Priority", _ => false));

    [Fact]
    public void IsCustom_BuiltInKey_IsFalse()
        => Assert.False(CustomColumnSlug.IsCustom("name"));

    [Fact]
    public void IsCustom_CustomSlug_IsTrue()
        => Assert.True(CustomColumnSlug.IsCustom("custom_priority"));

    [Fact]
    public void IsCustom_Null_IsFalse()
        => Assert.False(CustomColumnSlug.IsCustom(null));

    [Fact]
    public void ColumnsFor_Ledger_DefaultsToEmpty()
    {
        // User choice 3: custom columns ship off on the Nights ledger. Red before
        // DisplaySettings.LedgerTableId and its DefaultColumns entry exist.
        Assert.Empty(new DisplaySettings().ColumnsFor(DisplaySettings.LedgerTableId));
    }
}
