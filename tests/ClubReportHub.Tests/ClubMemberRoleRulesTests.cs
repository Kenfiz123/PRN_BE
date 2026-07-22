using ClubService.Models;
using ClubService.Services;
using FluentAssertions;

namespace ClubReportHub.Tests;

public sealed class ClubMemberRoleRulesTests
{
    [Fact]
    public void ResolveDisplayRole_ActiveManager_IsClubOwner()
    {
        ClubMemberRoleRules.ResolveDisplayRole(ClubMemberRoles.Member, true)
            .Should().Be(ClubMemberRoleRules.ClubOwner);
    }

    [Fact]
    public void ResolveDisplayRole_RegularMember_PreservesMembershipRole()
    {
        ClubMemberRoleRules.ResolveDisplayRole(ClubMemberRoles.Treasurer, false)
            .Should().Be(ClubMemberRoles.Treasurer);
    }

    [Fact]
    public void ValidateTreasurerAssignment_ClubOwner_IsRejected()
    {
        ClubMemberRoleRules.ValidateTreasurerAssignment(true, false, 0)
            .Should().Be("A club owner cannot be assigned as treasurer.");
    }

    [Fact]
    public void ValidateTreasurerAssignment_ThirdTreasurer_IsRejected()
    {
        ClubMemberRoleRules.ValidateTreasurerAssignment(false, false, 2)
            .Should().Be("A club can have at most two treasurers.");
    }

    [Fact]
    public void ValidateRemoval_ClubOwner_IsRejected()
    {
        ClubMemberRoleRules.ValidateRemoval(true)
            .Should().Be("A club owner cannot be removed from the member roster.");
    }

    [Fact]
    public void ApplyTreasurerRole_PreservesOriginalApprovalAudit()
    {
        var reviewedAt = new DateTimeOffset(2026, 7, 22, 6, 20, 0, TimeSpan.Zero);
        var membership = new ClubMembership
        {
            FullName = "Original Name",
            Role = ClubMemberRoles.Member,
            ReviewedAtUtc = reviewedAt,
            ReviewedByUserId = 42
        };

        ClubMemberRoleRules.ApplyTreasurerRole(membership, "Updated Name");

        membership.Role.Should().Be(ClubMemberRoles.Treasurer);
        membership.FullName.Should().Be("Updated Name");
        membership.ReviewedAtUtc.Should().Be(reviewedAt);
        membership.ReviewedByUserId.Should().Be(42);
    }

    [Fact]
    public void ApplyMemberRole_PreservesOriginalApprovalAudit()
    {
        var reviewedAt = new DateTimeOffset(2026, 7, 22, 6, 20, 0, TimeSpan.Zero);
        var membership = new ClubMembership
        {
            Role = ClubMemberRoles.Treasurer,
            ReviewedAtUtc = reviewedAt,
            ReviewedByUserId = 42
        };

        ClubMemberRoleRules.ApplyMemberRole(membership);

        membership.Role.Should().Be(ClubMemberRoles.Member);
        membership.ReviewedAtUtc.Should().Be(reviewedAt);
        membership.ReviewedByUserId.Should().Be(42);
    }
}
