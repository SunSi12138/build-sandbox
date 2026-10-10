using System.Globalization;
using Xunit;

namespace StreetEcoTool.Tests;

public sealed class ReminderRulesTests
{
    private static DateOnly Date(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("2026-10-05", "2026-10-05")]
    [InlineData("2026-10-10", "2026-10-05")]
    [InlineData("2026-10-11", "2026-10-05")]
    [InlineData("2026-10-12", "2026-10-12")]
    [InlineData("2026-01-01", "2025-12-29")]
    public void WeeklyPeriodStartsOnMondayIncludingAcrossYears(string today, string expected)
    {
        Assert.Equal(Date(expected), ReminderRules.PeriodStart("weekly", Date(today)));
    }

    [Theory]
    [InlineData("2024-02-29", "2024-02-01")]
    [InlineData("2024-03-01", "2024-03-01")]
    [InlineData("2026-10-31", "2026-10-01")]
    [InlineData("2027-01-01", "2027-01-01")]
    public void MonthlyPeriodUsesTheCalendarMonth(string today, string expected)
    {
        Assert.Equal(Date(expected), ReminderRules.PeriodStart("monthly", Date(today)));
    }

    [Theory]
    [InlineData("2026-01-01", "2026-01-01")]
    [InlineData("2026-03-31", "2026-01-01")]
    [InlineData("2026-04-01", "2026-04-01")]
    [InlineData("2026-06-30", "2026-04-01")]
    [InlineData("2026-07-01", "2026-07-01")]
    [InlineData("2026-09-30", "2026-07-01")]
    [InlineData("2026-10-01", "2026-10-01")]
    [InlineData("2026-12-31", "2026-10-01")]
    public void QuarterlyPeriodUsesCalendarQuarterBoundaries(string today, string expected)
    {
        Assert.Equal(Date(expected), ReminderRules.PeriodStart("quarterly", Date(today)));
    }

    [Fact]
    public void IrregularPointsHaveNoScheduledPeriod()
    {
        Assert.Null(ReminderRules.PeriodStart("irregular", Date("2026-10-10")));
        Assert.False(ReminderRules.NeedsInspection("irregular", false, (DateOnly?)null, Date("2026-10-10")));
        Assert.False(ReminderRules.NeedsInspection("irregular", false, Array.Empty<DateOnly>(), Date("2026-10-10")));
    }

    [Theory]
    [InlineData("pending", -1, true, false)]
    [InlineData("feedback", -1, true, false)]
    [InlineData("closed", -1, false, false)]
    [InlineData("pending", 0, false, true)]
    [InlineData("feedback", 0, false, true)]
    [InlineData("closed", 0, false, false)]
    [InlineData("pending", 1, false, true)]
    [InlineData("pending", 3, false, true)]
    [InlineData("feedback", 3, false, true)]
    [InlineData("closed", 3, false, false)]
    [InlineData("pending", 4, false, false)]
    public void DeadlineRemindersIncludeTodayAndDayThreeButNeverClosedIssues(string status, int offset, bool overdue, bool dueSoon)
    {
        var today = Date("2026-10-10");
        var deadline = today.AddDays(offset);
        Assert.Equal(overdue, ReminderRules.IsOverdue(deadline, status, today));
        Assert.Equal(dueSoon, ReminderRules.IsDueSoon(deadline, status, today));
    }

    [Fact]
    public void DueSoonWindowCrossesTheYearBoundary()
    {
        var today = Date("2026-12-30");
        Assert.True(ReminderRules.IsDueSoon(Date("2027-01-02"), "pending", today));
        Assert.False(ReminderRules.IsDueSoon(Date("2027-01-03"), "pending", today));
    }

    [Theory]
    [InlineData("pending", false)]
    [InlineData("feedback", true)]
    [InlineData("closed", false)]
    public void OnlyCommunityFeedbackAwaitsReview(string status, bool expected)
    {
        Assert.Equal(expected, ReminderRules.IsAwaitingReview(status));
    }

    [Theory]
    [InlineData("weekly")]
    [InlineData("monthly")]
    [InlineData("quarterly")]
    public void ScheduledPointsNeedARecordInTheCurrentPeriod(string frequency)
    {
        var today = Date("2026-10-10");
        var periodStart = ReminderRules.PeriodStart(frequency, today)!.Value;
        Assert.True(ReminderRules.NeedsInspection(frequency, false, (DateOnly?)null, today));
        Assert.True(ReminderRules.NeedsInspection(frequency, false, periodStart.AddDays(-1), today));
        Assert.False(ReminderRules.NeedsInspection(frequency, false, periodStart, today));
        Assert.False(ReminderRules.NeedsInspection(frequency, false, today, today));
        Assert.True(ReminderRules.NeedsInspection(frequency, false, today.AddDays(1), today));
    }

    [Theory]
    [InlineData("weekly")]
    [InlineData("monthly")]
    [InlineData("quarterly")]
    public void DisabledPointsAreExcludedEvenWhenTheyHaveNoRecords(string frequency)
    {
        var today = Date("2026-10-10");
        Assert.False(ReminderRules.NeedsInspection(frequency, true, (DateOnly?)null, today));
        Assert.False(ReminderRules.NeedsInspection(frequency, true, Array.Empty<DateOnly>(), today));
        Assert.False(ReminderRules.NeedsInspection(frequency, true, Date("2020-01-01"), today));
    }

    [Theory]
    [InlineData("weekly")]
    [InlineData("monthly")]
    [InlineData("quarterly")]
    public void FutureDatedRecordsNeverSatisfyOrHideTheCurrentPeriod(string frequency)
    {
        var today = Date("2026-10-10");
        var periodStart = ReminderRules.PeriodStart(frequency, today)!.Value;
        var future = today.AddDays(40);
        Assert.True(ReminderRules.NeedsInspection(frequency, false, new[] { periodStart.AddDays(-1), future }, today));
        Assert.False(ReminderRules.NeedsInspection(frequency, false, new[] { periodStart.AddDays(-1), periodStart, future }, today));
    }

    [Theory]
    [InlineData("weekly", "2026-10-12", "2026-10-11")]
    [InlineData("monthly", "2024-03-01", "2024-02-29")]
    [InlineData("quarterly", "2026-04-01", "2026-03-31")]
    public void CrossingAPeriodBoundaryRequiresANewInspection(string frequency, string today, string previous)
    {
        Assert.True(ReminderRules.NeedsInspection(frequency, false, new[] { Date(previous) }, Date(today)));
        Assert.False(ReminderRules.NeedsInspection(frequency, false, new[] { Date(previous), Date(today) }, Date(today)));
    }
}
