using AuthService.Data;
using AuthService.Models;
using AuthService.Services;
using ClubReportHub.Shared.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClubReportHub.Tests;

public class AuthServiceTests : IDisposable
{
    private readonly AuthDbContext _db;
    private readonly RefreshTokenService _refreshTokenService;
    private readonly IPasswordHasher<User> _passwordHasher;

    public AuthServiceTests()
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new AuthDbContext(options);

        var jwtOptions = new JwtOptions
        {
            Issuer = "TestIssuer",
            Audience = "TestAudience",
            SigningKey = "test-signing-key-with-at-least-32-characters",
            ExpirationMinutes = 30,
            RefreshTokenExpirationDays = 7
        };

        var tokenFactory = new JwtTokenFactory(Options.Create(jwtOptions));
        _refreshTokenService = new RefreshTokenService(_db, tokenFactory, Options.Create(jwtOptions));
        _passwordHasher = new PasswordHasher<User>();
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task CreateRefreshToken_ShouldGenerateValidToken()
    {
        // Arrange
        var user = new User
        {
            Username = "testuser",
            FullName = "Test User",
            Email = "test@example.com",
            PasswordHash = "hash"
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        // Act
        var refreshToken = await _refreshTokenService.CreateRefreshTokenAsync(user.Id);

        // Assert
        refreshToken.Should().NotBeNull();
        refreshToken.Token.Should().NotBeNullOrEmpty();
        refreshToken.Token.Length.Should().BeGreaterThan(50);
        refreshToken.UserId.Should().Be(user.Id);
        refreshToken.IsActive.Should().BeTrue();
        refreshToken.IsExpired.Should().BeFalse();
    }

    [Fact]
    public async Task RotateRefreshToken_ShouldRevokeOldAndCreateNew()
    {
        // Arrange
        var user = new User
        {
            Username = "testuser",
            FullName = "Test User",
            Email = "test@example.com",
            PasswordHash = "hash"
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var oldToken = await _refreshTokenService.CreateRefreshTokenAsync(user.Id);
        var oldTokenValue = oldToken.Token;

        // Act
        var newToken = await _refreshTokenService.RotateRefreshTokenAsync(oldToken, "127.0.0.1");

        // Assert
        newToken.Should().NotBeNull();
        newToken!.Token.Should().NotBe(oldTokenValue);
        newToken.FamilyId.Should().Be(oldToken.FamilyId);

        // Verify old token is revoked
        var revokedToken = await _db.RefreshTokens.FindAsync(oldToken.Id);
        revokedToken!.IsRevoked.Should().BeTrue();
        revokedToken.ReplacedByToken.Should().Be(newToken.Token);
    }

    [Fact]
    public async Task RotateRefreshToken_WithInactiveToken_ShouldReturnNull()
    {
        // Arrange
        var user = new User
        {
            Username = "testuser",
            FullName = "Test User",
            Email = "test@example.com",
            PasswordHash = "hash"
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var token = await _refreshTokenService.CreateRefreshTokenAsync(user.Id);
        token.RevokedAtUtc = DateTimeOffset.UtcNow; // Manually revoke

        // Act
        var result = await _refreshTokenService.RotateRefreshTokenAsync(token, "127.0.0.1");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task RevokeFamily_ShouldRevokeAllTokensInFamily()
    {
        // Arrange
        var user = new User
        {
            Username = "testuser",
            FullName = "Test User",
            Email = "test@example.com",
            PasswordHash = "hash"
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var token1 = await _refreshTokenService.CreateRefreshTokenAsync(user.Id);
        var token2 = await _refreshTokenService.CreateRefreshTokenAsync(user.Id);

        // Act
        await _refreshTokenService.RevokeFamilyAsync(token1.FamilyId, "127.0.0.1");

        // Assert - Both tokens in family should be revoked
        var tokens = await _db.RefreshTokens
            .Where(t => t.FamilyId == token1.FamilyId)
            .ToListAsync();

        tokens.Should().AllSatisfy(t => t.IsRevoked.Should().BeTrue());
    }

    [Fact]
    public async Task CreateAuthResponse_ShouldIncludeRefreshToken()
    {
        // Arrange
        var user = new User
        {
            Username = "testuser",
            FullName = "Test User",
            Email = "test@example.com",
            PasswordHash = "hash",
            IsActive = true
        };
        user.UserRoles.Add(new UserRole { User = user, Role = new Role { Name = AuthRoles.ClubMember } });
        _db.Users.Add(user);
        _db.Roles.Add(new Role { Name = AuthRoles.ClubMember });
        await _db.SaveChangesAsync();

        var refreshToken = await _refreshTokenService.CreateRefreshTokenAsync(user.Id);

        // Act
        var authResponse = _refreshTokenService.CreateAuthResponse(user, refreshToken);

        // Assert
        authResponse.AccessToken.Should().NotBeNullOrEmpty();
        authResponse.RefreshToken.Should().NotBeNullOrEmpty();
        authResponse.RefreshTokenExpiresAtUtc.Should().NotBeNull();
        authResponse.RefreshTokenExpiresAtUtc.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task User_CanHaveMultipleRefreshTokenFamilies()
    {
        // Arrange
        var user = new User
        {
            Username = "testuser",
            FullName = "Test User",
            Email = "test@example.com",
            PasswordHash = "hash"
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        // Act - Create multiple tokens (each call creates a new family)
        var token1 = await _refreshTokenService.CreateRefreshTokenAsync(user.Id);
        var token2 = await _refreshTokenService.CreateRefreshTokenAsync(user.Id);

        // Assert - Different families
        token1.FamilyId.Should().NotBe(token2.FamilyId);
    }
}
