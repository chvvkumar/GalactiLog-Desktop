using Avalonia.Headless.XUnit;
using Avalonia.Media.Immutable;
using GalactiLog.App.Theme;
using GalactiLog.Core.Sessions;
using GalactiLog.App.ViewModels.Stats;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.StatisticsViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

// Spec 12.5's Imaging calendar, spec 13's five-step ramp, and the roadmap's named tooltip
// assertion. The two ramp cases are AvaloniaFacts because the ramp is read from the merged token
// dictionary; everything else is a plain fact.
public class ImagingCalendarViewModelTests
{
    private static ImagingCalendarViewModel Create(
        IReadOnlyList<CalendarEntry>? entries = null,
        bool coordinates = true,
        Action<DateOnly, DateOnly>? observeRange = null)
    {
        var calendar = CreateUnsettled(entries, coordinates, observeRange);
        Settle(calendar);
        return calendar;
    }

    /// <summary>The same view-model with its first load still in flight, for the tests that await
    /// it rather than block on it (review finding M11).</summary>
    private static ImagingCalendarViewModel CreateUnsettled(
        IReadOnlyList<CalendarEntry>? entries = null,
        bool coordinates = true,
        Action<DateOnly, DateOnly>? observeRange = null)
    {
        var calendar = new ImagingCalendarViewModel(
            (from, to) =>
            {
                observeRange?.Invoke(from, to);
                return entries ?? [];
            },
            coordinates ? Factory.WithCoordinates : Factory.WithoutCoordinates,
            () => Factory.Today,
            action => action());
        calendar.Reload();
        return calendar;
    }

    // In a harness helper rather than in a test method: xUnit1031 forbids a blocking wait in a
    // test, and the calendar load is started by the view-model rather than handed back as a task.
    private static void Settle(ImagingCalendarViewModel calendar)
    {
        calendar.PendingLoad?.GetAwaiter().GetResult();
    }

    [Fact]
    public void DefaultRange_IsTheLastTwelveMonths()
    {
        DateOnly from = default;
        DateOnly to = default;
        using var calendar = Create(observeRange: (start, end) => (from, to) = (start, end));

        Assert.Null(calendar.SelectedRange.Year);
        Assert.Equal("Last 12 months", calendar.SelectedRange.Label);
        Assert.Equal(Factory.Today, to);
        Assert.Equal(Factory.Today.AddYears(-1).AddDays(1), from);
    }

    [Fact]
    public void YearOptions_AreTheCurrentYearAndTheFiveBefore_PlusLastTwelveMonths()
    {
        using var calendar = Create();

        Assert.Equal(7, calendar.RangeOptions.Count);
        Assert.Null(calendar.RangeOptions[0].Year);
        Assert.Equal(
            new int?[] { 2025, 2024, 2023, 2022, 2021, 2020 },
            calendar.RangeOptions.Skip(1).Select(option => option.Year));
    }

    [Fact]
    public void SelectingAYear_ReadsThatWholeCalendarYear()
    {
        DateOnly from = default;
        DateOnly to = default;
        using var calendar = Create(observeRange: (start, end) => (from, to) = (start, end));

        calendar.SelectedRange = calendar.RangeOptions[2];
        Settle(calendar);

        Assert.Equal(new DateOnly(2024, 1, 1), from);
        Assert.Equal(new DateOnly(2024, 12, 31), to);
    }

    [Fact]
    public void Weeks_StartOnMonday_AndAlwaysHoldSevenDays()
    {
        using var calendar = Create();

        Assert.NotEmpty(calendar.Cells);
        foreach (var week in calendar.Cells.GroupBy(cell => cell.Column))
        {
            Assert.Equal(7, week.Count());
            Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6 }, week.Select(cell => cell.Row).Order());
            Assert.Equal(DayOfWeek.Monday, week.Single(cell => cell.Row == 0).Date.DayOfWeek);
        }

        Assert.Equal(calendar.Cells.Count / 7, calendar.WeekCount);
    }

    [Fact]
    public void MonthLabels_AppearAtTheFirstWeekOfEachMonth()
    {
        using var calendar = Create();

        // One label per month change, never two for the same column, and each label names the
        // month its column's Monday falls in.
        Assert.Equal(
            calendar.MonthLabels.Select(label => label.Column).Distinct().Count(),
            calendar.MonthLabels.Count);
        foreach (var label in calendar.MonthLabels)
        {
            var monday = calendar.Cells.Single(cell => cell.Column == label.Column && cell.Row == 0).Date;
            Assert.Equal(
                System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat
                    .GetAbbreviatedMonthName(monday.Month),
                label.Text);
        }

        // A rolling twelve months crosses thirteen month boundaries at most.
        Assert.InRange(calendar.MonthLabels.Count, 12, 14);
    }

    [Fact]
    public void DayLabels_RenderOnMondayWednesdayFridayAndSunday()
    {
        Assert.Equal(new[] { 0, 2, 4, 6 }, ImagingCalendarViewModel.LabelledDayRows);
        Assert.Equal(
            new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" },
            ImagingCalendarViewModel.DayNames);
        Assert.Equal(
            new[] { "Mon", "Wed", "Fri", "Sun" },
            ImagingCalendarViewModel.LabelledDayRows.Select(row => ImagingCalendarViewModel.DayNames[row]));
    }

    [Theory]
    [InlineData(0d, 0)]
    [InlineData(0.5d, 1)]
    [InlineData(2d, 2)]
    [InlineData(5d, 3)]
    [InlineData(9d, 4)]
    public void Intensity_UsesTheFiveWebThresholds(double hours, int expected)
        => Assert.Equal(expected, ImagingCalendarViewModel.Intensity(hours));

    [AvaloniaFact]
    public async Task Intensity_StepZero_IsTheElevatedBackgroundToken()
    {
        // Review finding M11: an AvaloniaFact runs on the headless UI thread, so it awaits the
        // calendar load rather than blocking on it, whatever the post seam does.
        ChartTheme.Apply();
        using var calendar = CreateUnsettled();
        await calendar.PendingLoad!;

        var token = ChartTheme.Read("ColorBgElevated", ChartTheme.Fallback);
        var step0 = Assert.IsType<ImmutableSolidColorBrush>(calendar.Ramp[0]);

        Assert.Equal(token.Red, step0.Color.R);
        Assert.Equal(token.Green, step0.Color.G);
        Assert.Equal(token.Blue, step0.Color.B);
    }

    [AvaloniaFact]
    public async Task Intensity_StepFour_IsTheMetricIntegrationToken()
    {
        ChartTheme.Apply();
        using var calendar = CreateUnsettled();
        await calendar.PendingLoad!;

        var token = ChartTheme.Read("ColorMetricIntegration", ChartTheme.Fallback);
        var step4 = Assert.IsType<ImmutableSolidColorBrush>(calendar.Ramp[4]);

        Assert.Equal(token.Red, step4.Color.R);
        Assert.Equal(token.Green, step4.Color.G);
        Assert.Equal(token.Blue, step4.Color.B);
    }

    [Fact]
    public void Brushes_AreImmutableSolidColorBrushes()
    {
        using var calendar = Create();

        // Spec 14.5: SolidColorBrush's constructor calls VerifyAccess once a dispatcher exists, so
        // one built on the calendar's background load thread would throw.
        Assert.Equal(5, calendar.Ramp.Count);
        Assert.All(calendar.Ramp, brush => Assert.IsType<ImmutableSolidColorBrush>(brush));
        Assert.All(calendar.Legend, swatch => Assert.IsType<ImmutableSolidColorBrush>(swatch.Brush));
        Assert.All(calendar.Cells, cell => Assert.IsType<ImmutableSolidColorBrush>(cell.Brush));
    }

    [Fact]
    public void Tooltip_CarriesDateIntegrationFrameCountTargetCountAndDarkHours()
    {
        var date = new DateOnly(2025, 3, 5);
        var dark = AstroNight.DarkHoursForNight(date, Factory.Latitude, Factory.Longitude);

        var tooltip = ImagingCalendarViewModel.Tooltip(date, 9_000d, frames: 30, targets: 2, darkHours: dark);

        Assert.Contains("2025-03-05", tooltip, StringComparison.Ordinal);
        Assert.Contains("2.5h", tooltip, StringComparison.Ordinal);
        Assert.Contains("30 frames", tooltip, StringComparison.Ordinal);
        Assert.Contains("2 targets", tooltip, StringComparison.Ordinal);
        Assert.Contains("dark", tooltip, StringComparison.Ordinal);
        Assert.Contains(dark.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void DarkHours_AreComputedOnlyForImagedNights()
    {
        // Review finding M7: a blank cell has no tooltip, so it needs no dark-hours figure. The
        // only observable consequence is the tooltip itself, which is asserted below; this case
        // pins the imaged side so a future "optimisation" cannot drop it entirely.
        var imaged = Factory.Today.AddDays(-4);
        using var calendar = Create(entries: [new CalendarEntry(imaged, 9_000d, 1, 12)]);

        var cell = calendar.Cells.Single(entry => entry.Date == imaged);
        Assert.Contains("dark", cell.Tooltip, StringComparison.Ordinal);
        Assert.All(
            calendar.Cells.Where(entry => entry.Date != imaged),
            entry => Assert.Equal("", entry.Tooltip));
    }

    [Fact]
    public void Tooltip_OmitsDarkHours_WhenObserverCoordinatesAreUnset()
    {
        using var calendar = Create(
            entries: [new CalendarEntry(Factory.Today.AddDays(-3), 9_000d, 2, 30)],
            coordinates: false);

        var cell = calendar.Cells.Single(entry => entry.Date == Factory.Today.AddDays(-3));

        Assert.NotEqual("", cell.Tooltip);
        Assert.DoesNotContain("dark", cell.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void Tooltip_IsSuppressed_ForACellWithNoIntegration()
    {
        using var calendar = Create();

        // The web suppresses the tooltip entirely at or below zero hours: a blank cell explains
        // itself.
        Assert.All(calendar.Cells, cell => Assert.Equal("", cell.Tooltip));
        Assert.Equal("", ImagingCalendarViewModel.Tooltip(Factory.Today, 0d, 0, 0, 5d));
    }

    [Theory]
    [InlineData(1, 1, "1 target,", "1 frame")]
    [InlineData(2, 3, "2 targets,", "3 frames")]
    public void Tooltip_PluralisesTargetsAndFrames(int targets, int frames, string targetText, string frameText)
    {
        var tooltip = ImagingCalendarViewModel.Tooltip(Factory.Today, 3_600d, frames, targets, null);

        Assert.Contains(targetText, tooltip, StringComparison.Ordinal);
        Assert.Contains(frameText, tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadedEntries_LandOnTheirOwnCells()
    {
        var imaged = Factory.Today.AddDays(-10);
        using var calendar = Create(entries: [new CalendarEntry(imaged, 21_600d, 3, 40)]);

        var cell = calendar.Cells.Single(entry => entry.Date == imaged);

        Assert.Equal(4, cell.Intensity);
        Assert.Contains("40 frames", cell.Tooltip, StringComparison.Ordinal);
    }
}
