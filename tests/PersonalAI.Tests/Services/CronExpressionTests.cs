using FluentAssertions;
using PersonalAI.Core.Services.Scheduling;

namespace PersonalAI.Tests.Services;

public class CronExpressionTests
{
    [Fact]
    public void GetNextOccurrence_EveryMinute_ReturnsNextMinute()
    {
        var cron = CronExpression.Parse("* * * * *");
        var from = new DateTimeOffset(2026, 1, 1, 10, 30, 15, TimeSpan.Zero);

        var next = cron.GetNextOccurrence(from);

        next.Should().Be(new DateTimeOffset(2026, 1, 1, 10, 31, 0, TimeSpan.Zero));
    }

    [Fact]
    public void GetNextOccurrence_DailyAtSpecificTime_SkipsToNextDayWhenTimeHasPassed()
    {
        var cron = CronExpression.Parse("30 9 * * *"); // 09:30 every day
        var from = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

        var next = cron.GetNextOccurrence(from);

        next.Should().Be(new DateTimeOffset(2026, 1, 2, 9, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void GetNextOccurrence_DailyAtSpecificTime_SameDayWhenTimeHasNotPassed()
    {
        var cron = CronExpression.Parse("30 9 * * *");
        var from = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);

        var next = cron.GetNextOccurrence(from);

        next.Should().Be(new DateTimeOffset(2026, 1, 1, 9, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void GetNextOccurrence_WeeklyOnSpecificDay_FindsNextMatchingWeekday()
    {
        // Every Monday at 08:00. 2026-01-01 is a Thursday.
        var cron = CronExpression.Parse("0 8 * * 1");
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var next = cron.GetNextOccurrence(from);

        next.DayOfWeek.Should().Be(DayOfWeek.Monday);
        next.Should().Be(new DateTimeOffset(2026, 1, 5, 8, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void GetNextOccurrence_StepExpression_MatchesEveryFiveMinutes()
    {
        var cron = CronExpression.Parse("*/5 * * * *");
        var from = new DateTimeOffset(2026, 1, 1, 10, 32, 0, TimeSpan.Zero);

        var next = cron.GetNextOccurrence(from);

        next.Minute.Should().Be(35);
    }

    [Fact]
    public void GetNextOccurrence_LeapDayExpression_FindsFebruary29()
    {
        var cron = CronExpression.Parse("0 0 29 2 *");
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); // 2026 is not a leap year

        var next = cron.GetNextOccurrence(from);

        next.Year.Should().Be(2028); // next leap year after 2026
        next.Month.Should().Be(2);
        next.Day.Should().Be(29);
    }

    [Theory]
    [InlineData("* * * *")]
    [InlineData("70 * * * *")]
    [InlineData("* 25 * * *")]
    [InlineData("not a cron")]
    public void Parse_InvalidExpression_ThrowsFormatException(string expression)
    {
        var act = () => CronExpression.Parse(expression);
        act.Should().Throw<FormatException>();
    }
}
