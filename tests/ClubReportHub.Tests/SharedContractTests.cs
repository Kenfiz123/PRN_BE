using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClubReportHub.Shared.Auth;
using ClubReportHub.Shared.Messaging;
using Microsoft.Extensions.Options;

namespace ClubReportHub.Tests;

public sealed class SharedContractTests
{
    [Fact]
    public void JwtFactoryCreatesTokenWithRoleClaims()
    {
        var factory = new JwtTokenFactory(Options.Create(new JwtOptions
        {
            Issuer = "ClubReportHub",
            Audience = "ClubReportHub.Client",
            SigningKey = "unit-test-signing-key-with-at-least-32-characters",
            ExpirationMinutes = 30
        }));

        var result = factory.CreateToken(7, "manager@club.local", "Demo Manager", [AuthRoles.ClubManager]);
        var token = new JwtSecurityTokenHandler().ReadJwtToken(result.AccessToken);

        Assert.Equal("7", token.Claims.Single(x => x.Type == JwtRegisteredClaimNames.Sub).Value);
        Assert.Contains(token.Claims, x => (x.Type == ClaimTypes.Role || x.Type == "role") && x.Value == AuthRoles.ClubManager);
        Assert.True(result.ExpiresAtUtc > DateTimeOffset.UtcNow);
    }

    [Fact]
    public void RabbitMqRoutingKeysAreUnique()
    {
        var keys = new[]
        {
            EventRoutingKeys.ClubCreated,
            EventRoutingKeys.UserRegistered,
            EventRoutingKeys.ActivityCreated,
            EventRoutingKeys.ReportSubmitted,
            EventRoutingKeys.ReportApproved,
            EventRoutingKeys.ReportRejected,
            EventRoutingKeys.KpiCalculated,
            EventRoutingKeys.BudgetProposalSubmitted,
            EventRoutingKeys.BudgetApproved,
            EventRoutingKeys.SettlementOverdue,
            EventRoutingKeys.ExportRequested,
            EventRoutingKeys.ExportCompleted,
            EventRoutingKeys.ReportDeadlineReminder
        };

        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(true, false, false, true, true, true)]
    [InlineData(false, true, true, false, true, true)]
    [InlineData(false, false, true, false, false, true)]
    [InlineData(false, false, false, false, false, false)]
    public void ClubAccessSnapshotMapsMembershipCapabilities(
        bool isManager,
        bool isTreasurer,
        bool isApprovedMember,
        bool canManage,
        bool canManageFinance,
        bool canView)
    {
        var access = new ClubAccessSnapshot(42, "Test Club", isManager, isTreasurer, isApprovedMember, []);

        Assert.Equal(canManage, access.CanManage);
        Assert.Equal(canManageFinance, access.CanManageFinance);
        Assert.Equal(canView, access.CanView);
    }
}
