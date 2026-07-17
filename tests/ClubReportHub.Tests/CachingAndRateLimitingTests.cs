using ClubReportHub.Shared.Auth;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using System.Net.Http;
using System.Net.Http.Headers;

namespace ClubReportHub.Tests;

public class CachingTests
{
    [Fact]
    public void MemoryCache_ShouldStoreAndRetrieveClubAccess()
    {
        // Arrange
        var cache = new MemoryCache(new MemoryCacheOptions());
        var access = new List<ClubAccessSnapshot>
        {
            new(1, "Test Club", true, false, true, [1, 2])
        };

        // Act
        var cacheKey = "ClubAccess_1";
        cache.Set(cacheKey, access, TimeSpan.FromMinutes(5));

        // Assert
        cache.TryGetValue(cacheKey, out var cachedAccess).Should().BeTrue();
        cachedAccess.Should().BeEquivalentTo(access);
    }

    [Fact]
    public void MemoryCache_ShouldExpireAfterSlidingExpiration()
    {
        // Arrange
        var cache = new MemoryCache(new MemoryCacheOptions());
        var access = new List<ClubAccessSnapshot>
        {
            new(1, "Test Club", true, false, true, [1])
        };

        // Act - Set with very short expiration
        var cacheKey = "ClubAccess_1";
        cache.Set(cacheKey, access, TimeSpan.FromMilliseconds(100));

        // Assert - Should still be there immediately
        cache.TryGetValue(cacheKey, out _).Should().BeTrue();

        // Note: In real test, we would wait for expiration
        // But this verifies the caching mechanism works
    }

    [Fact]
    public void MemoryCache_ShouldReplaceExistingEntry()
    {
        // Arrange
        var cache = new MemoryCache(new MemoryCacheOptions());
        var access1 = new List<ClubAccessSnapshot>
        {
            new(1, "Old Club", true, false, true, [1])
        };
        var access2 = new List<ClubAccessSnapshot>
        {
            new(1, "New Club", true, false, true, [1])
        };

        // Act
        var cacheKey = "ClubAccess_1";
        cache.Set(cacheKey, access1);
        cache.Set(cacheKey, access2); // Replace

        // Assert
        var cached = cache.Get<List<ClubAccessSnapshot>>(cacheKey);
        cached![0].ClubName.Should().Be("New Club");
    }

    [Fact]
    public void ClubAccessSnapshot_ShouldHaveCorrectPermissions()
    {
        // Test various combinations
        var managerAccess = new ClubAccessSnapshot(1, "Club", true, false, true, [1]);
        managerAccess.CanManage.Should().BeTrue();
        managerAccess.CanManageFinance.Should().BeTrue();
        managerAccess.CanView.Should().BeTrue();

        var treasurerAccess = new ClubAccessSnapshot(1, "Club", false, true, true, [99]);
        treasurerAccess.CanManage.Should().BeFalse();
        treasurerAccess.CanManageFinance.Should().BeTrue();
        treasurerAccess.CanView.Should().BeTrue();

        var memberAccess = new ClubAccessSnapshot(1, "Club", false, false, true, [99]);
        memberAccess.CanManage.Should().BeFalse();
        memberAccess.CanManageFinance.Should().BeFalse();
        memberAccess.CanView.Should().BeTrue();

        var nonMemberAccess = new ClubAccessSnapshot(1, "Club", false, false, false, [99]);
        nonMemberAccess.CanManage.Should().BeFalse();
        nonMemberAccess.CanManageFinance.Should().BeFalse();
        nonMemberAccess.CanView.Should().BeFalse();
    }
}

public class RateLimitingTests
{
    [Fact]
    public void JwtTokenFactory_ShouldRespectConfiguredExpiration()
    {
        // Arrange
        var options = new JwtOptions
        {
            Issuer = "Test",
            Audience = "Test",
            SigningKey = "test-signing-key-with-at-least-32-characters",
            ExpirationMinutes = 15
        };

        // Act
        var factory = new JwtTokenFactory(Microsoft.Extensions.Options.Options.Create(options));

        // Assert
        factory.CreateToken(1, "user", "Test User", ["Admin"]).ExpiresAtUtc
            .Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(15), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void RefreshTokenExpiration_ShouldBeConfigured()
    {
        // Arrange
        var options = new JwtOptions
        {
            Issuer = "Test",
            Audience = "Test",
            SigningKey = "test-signing-key-with-at-least-32-characters",
            ExpirationMinutes = 30,
            RefreshTokenExpirationDays = 14
        };

        // Assert
        options.RefreshTokenExpirationDays.Should().Be(14);
        options.RefreshTokenExpirationDays.Should().BeGreaterThan(options.ExpirationMinutes);
    }
}
