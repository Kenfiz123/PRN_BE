using ActivityService.Contracts;
using ActivityService.Data;
using ActivityService.Models;
using ActivityService.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ClubReportHub.Tests;

public class MemberActivityStatisticsTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);
    private static MemberStatisticsInput Member(DateTimeOffset? joined = null) => new(7, joined ?? Now.AddMonths(-1));
    private static EligibleActivity Activity(int id, DateTimeOffset start, string status = ActivityStatuses.Completed, int clubId = 1) => new(id, clubId, $"Activity {id}", start, status);
    private static RecordedAttendance Attendance(int activityId, string status) => new(activityId, 7, status);

    [Fact]
    public void NoEligibleActivities_ReturnsZeroRate()
    {
        var result = MemberActivityStatisticsCalculator.Calculate(Member(), [], [], Now);
        result.Should().Be(new MemberStatisticsResponse(7, 0, 0, 0));
    }

    [Fact]
    public void PresentAttendance_CountsAsParticipation()
    {
        var result = MemberActivityStatisticsCalculator.Calculate(Member(), [Activity(1, Now.AddDays(-1))], [Attendance(1, AttendanceStatuses.Present)], Now);
        result.EligibleActivities.Should().Be(1);
        result.AttendedActivities.Should().Be(1);
        result.ParticipationRate.Should().Be(100);
    }

    [Theory]
    [InlineData(AttendanceStatuses.Absent)]
    [InlineData(AttendanceStatuses.Excused)]
    [InlineData(AttendanceStatuses.Late)]
    [InlineData(AttendanceStatuses.NotMarked)]
    public void NonPresentStatuses_DoNotCountAsParticipation(string status)
    {
        var result = MemberActivityStatisticsCalculator.Calculate(Member(), [Activity(1, Now.AddDays(-1))], [Attendance(1, status)], Now);
        result.Should().Be(new MemberStatisticsResponse(7, 1, 0, 0));
    }

    [Fact]
    public void CancelledActivity_IsExcluded()
    {
        var result = MemberActivityStatisticsCalculator.Calculate(Member(), [Activity(1, Now.AddDays(-1), ActivityStatuses.Cancelled)], [Attendance(1, AttendanceStatuses.Present)], Now);
        result.EligibleActivities.Should().Be(0);
    }

    [Fact]
    public void ActivityBeforeJoinDate_IsExcluded()
    {
        var result = MemberActivityStatisticsCalculator.Calculate(Member(Now.AddDays(-5)), [Activity(1, Now.AddDays(-6))], [Attendance(1, AttendanceStatuses.Present)], Now);
        result.EligibleActivities.Should().Be(0);
    }

    [Fact]
    public void FutureActivity_IsExcluded()
    {
        var result = MemberActivityStatisticsCalculator.Calculate(Member(), [Activity(1, Now.AddMinutes(1))], [], Now);
        result.EligibleActivities.Should().Be(0);
    }

    [Fact]
    public void DuplicatePresentRecords_CountActivityOnlyOnce()
    {
        var result = MemberActivityStatisticsCalculator.Calculate(Member(), [Activity(1, Now.AddDays(-1))], [Attendance(1, AttendanceStatuses.Present), Attendance(1, AttendanceStatuses.Present)], Now);
        result.AttendedActivities.Should().Be(1);
    }

    [Fact]
    public void ParticipationRate_IsRoundedToTwoDecimalPlaces()
    {
        var activities = new[] { Activity(1, Now.AddDays(-3)), Activity(2, Now.AddDays(-2)), Activity(3, Now.AddDays(-1)) };
        var result = MemberActivityStatisticsCalculator.Calculate(Member(), activities, [Attendance(1, AttendanceStatuses.Present), Attendance(2, AttendanceStatuses.Present)], Now);
        result.ParticipationRate.Should().Be(66.67m);
    }

    [Fact]
    public async Task StatisticsService_DoesNotIncludeActivitiesFromAnotherClub()
    {
        var options = new DbContextOptionsBuilder<ActivityDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ActivityDbContext(options);
        db.Activities.AddRange(
            new ClubActivity { ClubId = 1, ClubName = "One", Title = "Eligible", StartTimeUtc = Now.AddDays(-2), EndTimeUtc = Now.AddDays(-2).AddHours(1), Status = ActivityStatuses.Completed },
            new ClubActivity { ClubId = 2, ClubName = "Two", Title = "Wrong club", StartTimeUtc = Now.AddDays(-1), EndTimeUtc = Now.AddDays(-1).AddHours(1), Status = ActivityStatuses.Completed });
        await db.SaveChangesAsync();

        var result = await new MemberActivityStatisticsService(db).GetBatchAsync(1, [Member()], Now, CancellationToken.None);
        result[7].EligibleActivities.Should().Be(1);
    }
}
