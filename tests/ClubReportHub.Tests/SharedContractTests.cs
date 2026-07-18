using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClubReportHub.Shared.Auth;
using ClubReportHub.Shared.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
    [InlineData(true, false, false, true, false, true)]
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

    [Theory]
    [InlineData(AuthPolicies.SystemAdministration, AuthRoles.Admin, AuthRoles.SystemAdmin)]
    [InlineData(AuthPolicies.StudentAffairsAdministration, AuthRoles.Admin, AuthRoles.StudentAffairsAdmin)]
    [InlineData(
        AuthPolicies.BusinessAccess,
        AuthRoles.Admin,
        AuthRoles.StudentAffairsAdmin,
        AuthRoles.ClubManager,
        AuthRoles.Treasurer,
        AuthRoles.ClubMember)]
    public void AuthorizationPoliciesContainOnlyExpectedActors(string policyName, params string[] expectedRoles)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Issuer"] = "ClubReportHub",
                ["Jwt:Audience"] = "ClubReportHub.Client",
                ["Jwt:SigningKey"] = "unit-test-signing-key-with-at-least-32-characters"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddClubReportJwt(configuration);

        using var provider = services.BuildServiceProvider();
        var authorizationOptions = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;
        var policy = authorizationOptions.GetPolicy(policyName);
        var roleRequirement = Assert.Single(policy!.Requirements.OfType<RolesAuthorizationRequirement>());

        Assert.Equal(
            expectedRoles.OrderBy(x => x),
            roleRequirement.AllowedRoles.OrderBy(x => x));
    }

    [Theory]
    [InlineData(AuthRoles.Admin)]
    [InlineData(AuthRoles.SystemAdmin)]
    [InlineData(AuthRoles.StudentAffairsAdmin)]
    [InlineData(AuthRoles.ClubManager)]
    [InlineData(AuthRoles.Treasurer)]
    [InlineData(AuthRoles.ClubMember)]
    public void KnownActorRolesAreAccepted(string role)
    {
        Assert.True(AuthRoles.IsKnown(role));
    }

    [Theory]
    [InlineData("")]
    [InlineData("UNKNOWN")]
    [InlineData("REPORT_APPROVER")]
    public void ArbitraryRolesAreRejected(string role)
    {
        Assert.False(AuthRoles.IsKnown(role));
    }
}
