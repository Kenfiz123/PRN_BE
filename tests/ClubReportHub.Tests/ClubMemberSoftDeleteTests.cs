using ActivityService.Data;
using ActivityService.Models;
using ClubService.Data;
using ClubService.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ClubReportHub.Tests;

public class ClubMemberSoftDeleteTests
{
    [Fact]
    public async Task SoftDeletedMembership_IsHiddenByDefaultQueryFilter()
    {
        var options = new DbContextOptionsBuilder<ClubDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ClubDbContext(options);
        var club = new Club { Code = "C1", Name = "Club", Description = "Test", ContactEmail = "club@test.local", ContactPhone = "0123456789" };
        db.Clubs.Add(club);
        db.ClubMemberships.Add(new ClubMembership { Club = club, UserId = 5, FullName = "Member", Email = "member@test.local", Status = ClubMembershipStatuses.Inactive, IsDeleted = true, DeletedAtUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        (await db.ClubMemberships.CountAsync()).Should().Be(0);
        (await db.ClubMemberships.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task DeletingMembership_DoesNotDeleteActivityAttendanceHistory()
    {
        var clubOptions = new DbContextOptionsBuilder<ClubDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var activityOptions = new DbContextOptionsBuilder<ActivityDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var clubDb = new ClubDbContext(clubOptions);
        await using var activityDb = new ActivityDbContext(activityOptions);
        var club = new Club { Code = "C2", Name = "Club", Description = "Test", ContactEmail = "club@test.local", ContactPhone = "0123456789" };
        var member = new ClubMembership { Club = club, UserId = 9, FullName = "Member", Email = "m@test.local", Status = ClubMembershipStatuses.Approved };
        clubDb.AddRange(club, member);
        var activity = new ClubActivity { ClubId = 1, ClubName = "Club", Title = "Past event", StartTimeUtc = DateTimeOffset.UtcNow.AddDays(-2), EndTimeUtc = DateTimeOffset.UtcNow.AddDays(-2).AddHours(1) };
        activityDb.Activities.Add(activity);
        activityDb.ActivityAttendances.Add(new ActivityAttendance { Activity = activity, UserId = 9, FullName = "Member", AttendanceDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2)), Status = AttendanceStatuses.Present });
        await clubDb.SaveChangesAsync();
        await activityDb.SaveChangesAsync();

        member.IsDeleted = true;
        member.Status = ClubMembershipStatuses.Inactive;
        await clubDb.SaveChangesAsync();

        (await activityDb.ActivityAttendances.CountAsync()).Should().Be(1);
        (await clubDb.ClubMemberships.CountAsync()).Should().Be(0);
    }
}
