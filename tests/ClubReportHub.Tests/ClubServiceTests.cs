using ClubService.Data;
using ClubService.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ClubReportHub.Tests;

public class ClubServiceTests : IDisposable
{
    private readonly ClubDbContext _db;

    public ClubServiceTests()
    {
        var options = new DbContextOptionsBuilder<ClubDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new ClubDbContext(options);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task CreateClub_ShouldHaveDefaultActiveStatus()
    {
        // Arrange & Act
        var club = new Club
        {
            Code = "TEST",
            Name = "Test Club",
            Description = "A test club",
            ContactEmail = "test@club.com",
            ContactPhone = "1234567890"
        };
        _db.Clubs.Add(club);
        await _db.SaveChangesAsync();

        // Assert
        club.IsActive.Should().BeTrue();
        club.CreatedAtUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AssignManager_ShouldCreateActiveAssignment()
    {
        // Arrange
        var club = new Club
        {
            Code = "TEST",
            Name = "Test Club",
            Description = "Test",
            ContactEmail = "test@club.com",
            ContactPhone = "1234567890"
        };
        _db.Clubs.Add(club);
        await _db.SaveChangesAsync();

        // Act
        var assignment = new ClubManagerAssignment
        {
            ClubId = club.Id,
            ManagerUserId = 1,
            ManagerName = "Test Manager",
            IsActive = true
        };
        club.ManagerAssignments.Add(assignment);
        await _db.SaveChangesAsync();

        // Assert
        assignment.IsActive.Should().BeTrue();
        assignment.EndedAtUtc.Should().BeNull();
        assignment.AssignedAtUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task JoinClub_ShouldCreatePendingMembership()
    {
        // Arrange
        var club = new Club
        {
            Code = "TEST",
            Name = "Test Club",
            Description = "Test",
            ContactEmail = "test@club.com",
            ContactPhone = "1234567890"
        };
        _db.Clubs.Add(club);
        await _db.SaveChangesAsync();

        // Act
        var membership = new ClubMembership
        {
            ClubId = club.Id,
            UserId = 1,
            FullName = "New Member",
            Role = ClubMemberRoles.Member,
            Status = ClubMembershipStatuses.Pending,
            PersonalInfo = "I'm a student",
            Goals = "Learn and grow",
            Reason = "Interested in club activities"
        };
        _db.ClubMemberships.Add(membership);
        await _db.SaveChangesAsync();

        // Assert
        membership.Status.Should().Be(ClubMembershipStatuses.Pending);
        membership.ReviewedAtUtc.Should().BeNull();
        membership.ReviewNote.Should().BeNull();
    }

    [Fact]
    public async Task ApproveMembership_ShouldSetApprovedStatus()
    {
        // Arrange
        var club = new Club
        {
            Code = "TEST",
            Name = "Test Club",
            Description = "Test",
            ContactEmail = "test@club.com",
            ContactPhone = "1234567890"
        };
        _db.Clubs.Add(club);
        await _db.SaveChangesAsync();

        var membership = new ClubMembership
        {
            ClubId = club.Id,
            UserId = 1,
            FullName = "New Member",
            Role = ClubMemberRoles.Member,
            Status = ClubMembershipStatuses.Pending
        };
        _db.ClubMemberships.Add(membership);
        await _db.SaveChangesAsync();

        // Act
        membership.Status = ClubMembershipStatuses.Approved;
        membership.ReviewedAtUtc = DateTimeOffset.UtcNow;
        membership.ReviewedByUserId = 99;
        membership.ReviewNote = "Approved!";
        await _db.SaveChangesAsync();

        // Assert
        membership.Status.Should().Be(ClubMembershipStatuses.Approved);
        membership.ReviewedAtUtc.Should().NotBeNull();
        membership.ReviewNote.Should().Be("Approved!");
    }

    [Fact]
    public async Task AssignTreasurer_ShouldUpdateMemberRole()
    {
        // Arrange
        var club = new Club
        {
            Code = "TEST",
            Name = "Test Club",
            Description = "Test",
            ContactEmail = "test@club.com",
            ContactPhone = "1234567890"
        };
        _db.Clubs.Add(club);
        await _db.SaveChangesAsync();

        var membership = new ClubMembership
        {
            ClubId = club.Id,
            UserId = 1,
            FullName = "Treasurer Member",
            Role = ClubMemberRoles.Member,
            Status = ClubMembershipStatuses.Approved
        };
        _db.ClubMemberships.Add(membership);
        await _db.SaveChangesAsync();

        // Act
        membership.Role = ClubMemberRoles.Treasurer;
        await _db.SaveChangesAsync();

        // Assert
        membership.Role.Should().Be(ClubMemberRoles.Treasurer);
    }

    [Fact]
    public async Task ClubCreationApplication_ShouldTrackReviewStatus()
    {
        // Arrange & Act
        var application = new ClubCreationApplication
        {
            RequesterUserId = 1,
            RequesterName = "Club Founder",
            Code = "NEWCLUB",
            Name = "New Club",
            Description = "A brand new club",
            Purpose = "To bring students together",
            Reason = "No similar club exists",
            ContactEmail = "new@club.com",
            ContactPhone = "1234567890",
            Status = ClubApplicationStatuses.Submitted,
            SubmittedAtUtc = DateTimeOffset.UtcNow
        };
        _db.ClubCreationApplications.Add(application);
        await _db.SaveChangesAsync();

        // Assert
        application.Status.Should().Be(ClubApplicationStatuses.Submitted);
        application.CreatedClubId.Should().BeNull();
        application.ReviewedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task ApproveClubApplication_ShouldSetCreatedClubId()
    {
        // Arrange
        var application = new ClubCreationApplication
        {
            RequesterUserId = 1,
            RequesterName = "Club Founder",
            Code = "NEWCLUB",
            Name = "New Club",
            Description = "A brand new club",
            Purpose = "To bring students together",
            Reason = "No similar club exists",
            ContactEmail = "new@club.com",
            ContactPhone = "1234567890",
            Status = ClubApplicationStatuses.Submitted
        };
        _db.ClubCreationApplications.Add(application);
        await _db.SaveChangesAsync();

        // Create the club
        var club = new Club
        {
            Code = "NEWCLUB",
            Name = "New Club",
            Description = "A brand new club",
            ContactEmail = "new@club.com",
            ContactPhone = "1234567890"
        };
        _db.Clubs.Add(club);
        await _db.SaveChangesAsync();

        // Act - Approve the application
        application.Status = ClubApplicationStatuses.Approved;
        application.CreatedClubId = club.Id;
        application.ReviewedAtUtc = DateTimeOffset.UtcNow;
        application.ReviewedByUserId = 99;
        await _db.SaveChangesAsync();

        // Assert
        application.Status.Should().Be(ClubApplicationStatuses.Approved);
        application.CreatedClubId.Should().Be(club.Id);
        application.ReviewedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task RejectClubApplication_ShouldSetRejectedStatus()
    {
        // Arrange
        var application = new ClubCreationApplication
        {
            RequesterUserId = 1,
            RequesterName = "Club Founder",
            Code = "REJECTED",
            Name = "Rejected Club",
            Description = "This will be rejected",
            Purpose = "Test rejection",
            Reason = "Test",
            ContactEmail = "reject@club.com",
            ContactPhone = "1234567890",
            Status = ClubApplicationStatuses.Submitted
        };
        _db.ClubCreationApplications.Add(application);
        await _db.SaveChangesAsync();

        // Act
        application.Status = ClubApplicationStatuses.Rejected;
        application.ReviewedAtUtc = DateTimeOffset.UtcNow;
        application.ReviewedByUserId = 99;
        application.ReviewNote = "Does not meet requirements";
        await _db.SaveChangesAsync();

        // Assert
        application.Status.Should().Be(ClubApplicationStatuses.Rejected);
        application.CreatedClubId.Should().BeNull();
        application.ReviewNote.Should().Be("Does not meet requirements");
    }

    [Fact]
    public void ClubCode_ShouldBeUnique()
    {
        // The EF InMemory provider does not enforce relational unique constraints,
        // so verify the production model declares the expected unique index.
        var clubEntity = _db.Model.FindEntityType(typeof(Club));
        var codeIndex = clubEntity!.GetIndexes()
            .Single(index => index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(Club.Code)]));

        codeIndex.IsUnique.Should().BeTrue();
    }

    [Fact]
    public void ClubMembership_ShouldContainStructuredApplicationFields()
    {
        var membershipEntity = _db.Model.FindEntityType(typeof(ClubMembership));
        var requiredProperties = new[]
        {
            nameof(ClubMembership.DateOfBirth),
            nameof(ClubMembership.Gender),
            nameof(ClubMembership.Email),
            nameof(ClubMembership.PhoneNumber),
            nameof(ClubMembership.Address),
            nameof(ClubMembership.Hobbies),
            nameof(ClubMembership.Skills),
            nameof(ClubMembership.Expectations),
            nameof(ClubMembership.Contributions),
            nameof(ClubMembership.AdditionalInfoJson),
            nameof(ClubMembership.AcceptedClubRules),
            nameof(ClubMembership.CommittedToParticipate)
        };

        membershipEntity.Should().NotBeNull();
        requiredProperties
            .Select(name => membershipEntity!.FindProperty(name))
            .Should()
            .NotContainNulls();
    }

    [Fact]
    public void ClubCreationApplication_ShouldSupportFullPlanAndRevisionWorkflow()
    {
        var applicationEntity = _db.Model.FindEntityType(typeof(ClubCreationApplication));
        var requiredProperties = new[]
        {
            nameof(ClubCreationApplication.Category),
            nameof(ClubCreationApplication.FoundingMembersJson),
            nameof(ClubCreationApplication.MainActivities),
            nameof(ClubCreationApplication.VenueSupport),
            nameof(ClubCreationApplication.FundingSupport),
            nameof(ClubCreationApplication.ReviewConditions),
            nameof(ClubCreationApplication.ReviewerSignature)
        };

        applicationEntity.Should().NotBeNull();
        requiredProperties
            .Select(name => applicationEntity!.FindProperty(name))
            .Should()
            .NotContainNulls();
        ClubApplicationStatuses.NeedsRevision.Should().Be("NeedsRevision");
    }

    [Theory]
    [InlineData(ClubCategories.Sports)]
    [InlineData(ClubCategories.Arts)]
    [InlineData(ClubCategories.Academic)]
    [InlineData(ClubCategories.Volunteer)]
    [InlineData(ClubCategories.Technology)]
    [InlineData(ClubCategories.Other)]
    public void ClubCategory_ShouldAcceptAllSupportedValues(string category)
    {
        ClubCategories.All.Should().Contain(category);
    }

    [Fact]
    public void Club_ShouldKeepSoftDeleteAuditMetadata()
    {
        var clubEntity = _db.Model.FindEntityType(typeof(Club));

        clubEntity.Should().NotBeNull();
        clubEntity!.FindProperty(nameof(Club.DeletedAtUtc)).Should().NotBeNull();
        clubEntity.FindProperty(nameof(Club.DeletedByUserId)).Should().NotBeNull();
    }
}
