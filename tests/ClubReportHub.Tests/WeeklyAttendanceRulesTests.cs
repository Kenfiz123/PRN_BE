using ActivityService.Services;
using FluentAssertions;

namespace ClubReportHub.Tests;

public class WeeklyAttendanceRulesTests
{
    [Fact]
    public void VietnamDate_UsesUtcPlusSevenAcrossMidnight()
    {
        var utcSundayEvening = new DateTimeOffset(2026, 7, 19, 18, 30, 0, TimeSpan.Zero);
        WeeklyAttendanceRules.GetVietnamDate(utcSundayEvening).Should().Be(new DateOnly(2026, 7, 20));
    }

    [Theory]
    [InlineData(2026, 7, 20, 1)]
    [InlineData(2026, 7, 25, 6)]
    [InlineData(2026, 7, 26, 7)]
    public void IsoDayOfWeek_MapsMondayThroughSunday(int year, int month, int day, int expected)
    {
        WeeklyAttendanceRules.GetIsoDayOfWeek(new DateOnly(year, month, day)).Should().Be(expected);
    }

    [Fact]
    public void ScheduledDay_AllowsFirstCheckIn()
    {
        var date = new DateOnly(2026, 7, 20);
        WeeklyAttendanceRules.ValidateCheckIn([1, 3, 5], date, []).Should().BeNull();
    }

    [Fact]
    public void UnscheduledDay_RejectsCheckIn()
    {
        var tuesday = new DateOnly(2026, 7, 21);
        WeeklyAttendanceRules.ValidateCheckIn([1, 3, 5], tuesday, []).Should().Contain("not open");
    }

    [Fact]
    public void SecondCheckInOnSameVietnamDate_IsRejected()
    {
        var monday = new DateOnly(2026, 7, 20);
        WeeklyAttendanceRules.ValidateCheckIn([1], monday, [monday]).Should().Contain("already");
    }

    [Fact]
    public void EmptySchedule_RejectsCheckIn()
    {
        WeeklyAttendanceRules.ValidateCheckIn([], new DateOnly(2026, 7, 20), []).Should().Contain("does not have");
    }

    [Fact]
    public void ScheduledDayCount_IncludesSelectedWeekdaysOnly()
    {
        var count = WeeklyAttendanceRules.CountScheduledDays([1, 3, 5], new DateOnly(2026, 7, 20), new DateOnly(2026, 7, 26));
        count.Should().Be(3);
    }

    [Fact]
    public void AttendanceRate_IsRoundedToTwoDecimals()
    {
        WeeklyAttendanceRules.CalculateRate(2, 3).Should().Be(66.67m);
        WeeklyAttendanceRules.CalculateRate(0, 0).Should().Be(0m);
    }
}
