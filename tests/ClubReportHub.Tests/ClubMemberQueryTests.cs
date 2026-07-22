using ClubService.Data;
using ClubService.Models;
using ClubService.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ClubReportHub.Tests;

public class ClubMemberQueryTests
{
    private static ClubDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ClubDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new ClubDbContext(options);
        var club = new Club { Code = "QUERY", Name = "Query Club", Description = "Test", ContactEmail = "query@test.local", ContactPhone = "0123456789" };
        db.Clubs.Add(club);
        db.ClubMemberships.AddRange(
            new ClubMembership { Club = club, UserId = 1, FullName = "Zoe Member", Email = "zoe@test.local", PhoneNumber = "111", Role = ClubMemberRoles.Member, Status = ClubMembershipStatuses.Approved, ReviewedAtUtc = DateTimeOffset.UtcNow.AddDays(-3) },
            new ClubMembership { Club = club, UserId = 2, FullName = "Amy Treasurer", Email = "amy@test.local", PhoneNumber = "222", Role = ClubMemberRoles.Treasurer, Status = ClubMembershipStatuses.Approved, ReviewedAtUtc = DateTimeOffset.UtcNow.AddDays(-1) },
            new ClubMembership { Club = club, UserId = 3, FullName = "Pending User", Email = "pending@test.local", PhoneNumber = "333", Role = ClubMemberRoles.Member, Status = ClubMembershipStatuses.Pending });
        db.SaveChanges();
        return db;
    }

    [Fact]
    public async Task Search_MatchesNameEmailOrPhone()
    {
        await using var db = CreateDb();
        var result = await ClubMemberQuery.ApplyFilters(db.ClubMemberships.AsNoTracking(), "222", "All", "All").ToListAsync();
        result.Should().ContainSingle(x => x.FullName == "Amy Treasurer");
    }

    [Fact]
    public async Task Filters_StatusAndPositionOnServerQuery()
    {
        await using var db = CreateDb();
        var result = await ClubMemberQuery.ApplyFilters(db.ClubMemberships.AsNoTracking(), null, "Approved", ClubMemberRoles.Treasurer).ToListAsync();
        result.Should().ContainSingle(x => x.Role == ClubMemberRoles.Treasurer);
    }

    [Fact]
    public async Task SortAndPagination_AreDeterministic()
    {
        await using var db = CreateDb();
        var query = ClubMemberQuery.ApplyFilters(db.ClubMemberships.AsNoTracking(), null, "Approved", "All");
        var firstPage = await ClubMemberQuery.ApplySort(query, "name", "asc").Skip(0).Take(1).ToListAsync();
        var secondPage = await ClubMemberQuery.ApplySort(query, "name", "asc").Skip(1).Take(1).ToListAsync();
        firstPage.Single().FullName.Should().Be("Amy Treasurer");
        secondPage.Single().FullName.Should().Be("Zoe Member");
    }
}
