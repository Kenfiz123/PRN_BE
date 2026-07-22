using ActivityService.Contracts;
using ActivityService.Models;
using ActivityService.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ActivityService.Data;

namespace ClubReportHub.Tests;

public class AttendanceManagementRulesTests
{
    [Theory]
    [InlineData("NotMarked", AttendanceStatuses.NotMarked)]
    [InlineData("present", AttendanceStatuses.Present)]
    [InlineData("ABSENT", AttendanceStatuses.Absent)]
    [InlineData(" Excused ", AttendanceStatuses.Excused)]
    [InlineData("late", AttendanceStatuses.Late)]
    public void Normalize_AcceptsFiveSupportedStatuses(string input, string expected)
    {
        AttendanceStatuses.Normalize(input).Should().Be(expected);
        AttendanceManagementRules.ValidateEntry(input, null).Should().BeNull();
    }

    [Fact]
    public void InvalidStatus_IsRejected()
    {
        AttendanceManagementRules.ValidateEntry("Attended", null).Should().Contain("must be");
    }

    [Fact]
    public void NoteLongerThanOneThousandCharacters_IsRejected()
    {
        AttendanceManagementRules.ValidateEntry(AttendanceStatuses.Present, new string('x', 1001)).Should().Contain("1,000");
    }

    [Fact]
    public void DuplicateMembersInBulkRequest_AreRejectedBeforeMutation()
    {
        var items = new[]
        {
            new BulkAttendanceItemRequest(10, AttendanceStatuses.Present, null),
            new BulkAttendanceItemRequest(10, AttendanceStatuses.Absent, null)
        };
        AttendanceManagementRules.ValidateBulk(items).Should().Contain("Duplicate");
    }

    [Fact]
    public async Task InvalidBulkRequest_LeavesAttendanceRowsUnchanged()
    {
        var options = new DbContextOptionsBuilder<ActivityDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ActivityDbContext(options);
        var invalid = new[]
        {
            new BulkAttendanceItemRequest(10, AttendanceStatuses.Present, null),
            new BulkAttendanceItemRequest(10, AttendanceStatuses.Absent, null)
        };

        var error = AttendanceManagementRules.ValidateBulk(invalid);
        if (error is null) throw new InvalidOperationException("The invalid request unexpectedly passed validation.");

        (await db.ActivityAttendances.CountAsync()).Should().Be(0);
    }

    [Fact]
    public void EmptyBulkRequest_IsRejected()
    {
        AttendanceManagementRules.ValidateBulk([]).Should().Contain("between 1 and 500");
    }

    [Fact]
    public void MemberJoiningAfterActivity_IsNotEligible()
    {
        var start = DateTimeOffset.UtcNow;
        AttendanceManagementRules.ValidateEligibility(1, 1, start.AddMinutes(1), start).Should().Contain("joined after");
    }

    [Fact]
    public void ActivityFromAnotherClub_IsRejected()
    {
        var start = DateTimeOffset.UtcNow;
        AttendanceManagementRules.ValidateEligibility(1, 2, start.AddDays(-1), start).Should().Contain("another club");
    }

    [Fact]
    public void CancelledActivity_IsRejected()
    {
        AttendanceManagementRules.ValidateActivity(ActivityStatuses.Cancelled).Should().Contain("cancelled");
    }

    [Fact]
    public void UserWithoutAdministratorOrManagerRole_CannotManageAttendance()
    {
        AttendanceManagementRules.CanManage(false, false).Should().BeFalse();
        AttendanceManagementRules.CanManage(true, false).Should().BeTrue();
        AttendanceManagementRules.CanManage(false, true).Should().BeTrue();
    }
}
