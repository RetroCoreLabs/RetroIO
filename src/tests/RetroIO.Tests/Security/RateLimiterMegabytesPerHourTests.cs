//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Threading.Tasks;
using NUnit.Framework;
using RetroIO.Security.Implementation.RateLimiting;

namespace RetroIO.Tests.Security;

/// <summary>
/// Unit tests for MB/hour rate limiting functionality.
/// Tests per-user, per-IP, and server-wide bandwidth limits.
/// </summary>
[TestFixture]
[Category("Security")]
[Category("RateLimiting")]
public class RateLimiterMegabytesPerHourTests
{
    private const int ONE_MB = 1024 * 1024;

    [Test]
    public async Task Test_PerUser_MegabytesPerHour_WithinLimit_Allowed()
    {
        // Arrange
        var config = new RateLimitConfig
        {
            RestoreStateOnStartup = false,
            PerUserMegabytesPerHour = 100, // 100 MB/hour
            PerUserRequestsPerSecond = null, // Disable req/sec to isolate MB/hour testing
            PerIpMegabytesPerHour = null,
            ServerMegabytesPerHour = null
        };

        using var rateLimiter = new InMemoryRateLimiter(config);
        var userId = Guid.NewGuid();

        // Act - Transfer 50 MB (within 100 MB limit)
        var result = await rateLimiter.CheckRateLimitAsync(
            userId,
            "192.168.1.100",
            bytesToTransfer: 50 * ONE_MB,
            isAdmin: false);

        // Assert
        Assert.That(result.IsAllowed, Is.True);
        Assert.That(result.DenialReason, Is.Null);
    }

    [Test]
    public async Task Test_PerUser_MegabytesPerHour_ExceedLimit_Denied()
    {
        // Arrange
        var config = new RateLimitConfig
        {
            RestoreStateOnStartup = false,
            PerUserMegabytesPerHour = 10, // 10 MB/hour
            PerUserRequestsPerSecond = null,
            PerIpMegabytesPerHour = null,
            ServerMegabytesPerHour = null
        };

        using var rateLimiter = new InMemoryRateLimiter(config);
        var userId = Guid.NewGuid();

        // Act - First transfer 8 MB (allowed)
        var result1 = await rateLimiter.CheckRateLimitAsync(
            userId, "192.168.1.100", 8 * ONE_MB, false);

        // Act - Second transfer 5 MB (would exceed 10 MB limit)
        var result2 = await rateLimiter.CheckRateLimitAsync(
            userId, "192.168.1.100", 5 * ONE_MB, false);

        // Assert
        Assert.That(result1.IsAllowed, Is.True, "First 8 MB should be allowed");
        Assert.That(result2.IsAllowed, Is.False, "Second 5 MB should be denied (total 13 MB > 10 MB limit)");
        Assert.That(result2.StatusCode, Is.EqualTo(429), "Should return 429 Too Many Requests");
        Assert.That(result2.DenialReason, Does.Contain("bandwidth limit exceeded"));
    }

    [Test]
    public async Task Test_PerUser_MegabytesPerHour_MultipleSmallTransfers_Accumulate()
    {
        // Arrange
        var config = new RateLimitConfig
        {
            RestoreStateOnStartup = false,
            PerUserMegabytesPerHour = 5, // 5 MB/hour
            PerUserRequestsPerSecond = null,
            PerIpMegabytesPerHour = null,
            ServerMegabytesPerHour = null
        };

        using var rateLimiter = new InMemoryRateLimiter(config);
        var userId = Guid.NewGuid();

        // Act - Transfer 1 MB six times (total 6 MB, exceeds 5 MB limit)
        var results = new RateLimitResult[6];
        for (int i = 0; i < 6; i++)
        {
            results[i] = await rateLimiter.CheckRateLimitAsync(
                userId, "192.168.1.100", 1 * ONE_MB, false);
        }

        // Assert
        Assert.That(results[0].IsAllowed, Is.True, "Transfer 1 allowed");
        Assert.That(results[1].IsAllowed, Is.True, "Transfer 2 allowed");
        Assert.That(results[2].IsAllowed, Is.True, "Transfer 3 allowed");
        Assert.That(results[3].IsAllowed, Is.True, "Transfer 4 allowed");
        Assert.That(results[4].IsAllowed, Is.True, "Transfer 5 allowed (total 5 MB)");
        Assert.That(results[5].IsAllowed, Is.False, "Transfer 6 denied (would be 6 MB)");
    }

    [Test]
    public async Task Test_PerIp_MegabytesPerHour_WithinLimit_Allowed()
    {
        // Arrange
        var config = new RateLimitConfig
        {
            RestoreStateOnStartup = false,
            PerUserMegabytesPerHour = 1000, // High value
            PerUserRequestsPerSecond = 1000,
            PerIpMegabytesPerHour = 50, // 50 MB/hour for guests
            PerIpRequestsPerSecond = 1000,
            ServerMegabytesPerHour = 10000, // High value
            ServerRequestsPerSecond = 10000
        };

        using var rateLimiter = new InMemoryRateLimiter(config);
        var guestUserId = Guid.Empty; // Unauthenticated

        // Act - Transfer small amounts that accumulate to test MB/hour limit
        var result1 = await rateLimiter.CheckRateLimitAsync(guestUserId, "192.168.1.200", 10 * ONE_MB, false);
        var result2 = await rateLimiter.CheckRateLimitAsync(guestUserId, "192.168.1.200", 10 * ONE_MB, false);
        var result3 = await rateLimiter.CheckRateLimitAsync(guestUserId, "192.168.1.200", 10 * ONE_MB, false);

        // Assert - 30 MB total is within 50 MB limit
        Assert.That(result1.IsAllowed, Is.True, "First 10 MB allowed");
        Assert.That(result2.IsAllowed, Is.True, "Second 10 MB allowed");
        Assert.That(result3.IsAllowed, Is.True, "Third 10 MB allowed (30 MB < 50 MB)");
    }

    [Test]
    public async Task Test_PerIp_MegabytesPerHour_ExceedLimit_Denied()
    {
        // Arrange
        var config = new RateLimitConfig
        {
            RestoreStateOnStartup = false,
            PerUserMegabytesPerHour = 10000,
            PerUserRequestsPerSecond = 10000,
            PerIpMegabytesPerHour = 20, // 20 MB/hour
            PerIpRequestsPerSecond = 10000,
            ServerMegabytesPerHour = 10000,
            ServerRequestsPerSecond = 10000
        };

        using var rateLimiter = new InMemoryRateLimiter(config);
        var guestUserId = Guid.Empty;
        string ipAddress = "192.168.1.200";

        // Act - Transfer 10 MB three times (total 30 MB > 20 MB limit)
        var result1 = await rateLimiter.CheckRateLimitAsync(guestUserId, ipAddress, 10 * ONE_MB, false);
        var result2 = await rateLimiter.CheckRateLimitAsync(guestUserId, ipAddress, 10 * ONE_MB, false);
        var result3 = await rateLimiter.CheckRateLimitAsync(guestUserId, ipAddress, 10 * ONE_MB, false);

        // Assert
        Assert.That(result1.IsAllowed, Is.True, "First 10 MB allowed");
        Assert.That(result2.IsAllowed, Is.True, "Second 10 MB allowed (20 MB = limit)");
        Assert.That(result3.IsAllowed, Is.False, "Third 10 MB denied (would be 30 MB > 20 MB)");
        Assert.That(result3.DenialReason, Does.Contain("IP bandwidth limit exceeded"));
    }

    [Test]
    public async Task Test_ServerWide_MegabytesPerHour_WithinLimit_Allowed()
    {
        // Arrange
        var config = new RateLimitConfig
        {
            RestoreStateOnStartup = false,
            PerUserMegabytesPerHour = 10000,
            PerUserRequestsPerSecond = 10000,
            PerIpMegabytesPerHour = 10000,
            PerIpRequestsPerSecond = 10000,
            ServerMegabytesPerHour = 100, // 100 MB/hour server-wide
            ServerRequestsPerSecond = 10000
        };

        using var rateLimiter = new InMemoryRateLimiter(config);

        // Act - Multiple users transfer total within limit
        var user1 = await rateLimiter.CheckRateLimitAsync(Guid.NewGuid(), "192.168.1.1", 30 * ONE_MB, false);
        var user2 = await rateLimiter.CheckRateLimitAsync(Guid.NewGuid(), "192.168.1.2", 30 * ONE_MB, false);
        var user3 = await rateLimiter.CheckRateLimitAsync(Guid.NewGuid(), "192.168.1.3", 30 * ONE_MB, false);

        // Assert - 90 MB total is within 100 MB limit
        Assert.That(user1.IsAllowed, Is.True, "First 30 MB allowed");
        Assert.That(user2.IsAllowed, Is.True, "Second 30 MB allowed");
        Assert.That(user3.IsAllowed, Is.True, "Third 30 MB allowed (90 MB < 100 MB)");
    }

    [Test]
    public async Task Test_ServerWide_MegabytesPerHour_ExceedLimit_Denied()
    {
        // Arrange
        var config = new RateLimitConfig
        {
            RestoreStateOnStartup = false,
            PerUserMegabytesPerHour = 10000,
            PerUserRequestsPerSecond = 10000,
            PerIpMegabytesPerHour = 10000,
            PerIpRequestsPerSecond = 10000,
            ServerMegabytesPerHour = 50, // 50 MB/hour server-wide
            ServerRequestsPerSecond = 10000
        };

        using var rateLimiter = new InMemoryRateLimiter(config);

        // Act - Multiple users transfer total > 50 MB
        var user1 = await rateLimiter.CheckRateLimitAsync(Guid.NewGuid(), "192.168.1.1", 20 * ONE_MB, false);
        var user2 = await rateLimiter.CheckRateLimitAsync(Guid.NewGuid(), "192.168.1.2", 20 * ONE_MB, false);
        var user3 = await rateLimiter.CheckRateLimitAsync(Guid.NewGuid(), "192.168.1.3", 20 * ONE_MB, false);

        // Assert
        Assert.That(user1.IsAllowed, Is.True, "First user 20 MB allowed");
        Assert.That(user2.IsAllowed, Is.True, "Second user 20 MB allowed (40 MB)");
        Assert.That(user3.IsAllowed, Is.False, "Third user 20 MB denied (would be 60 MB > 50 MB)");
        Assert.That(user3.StatusCode, Is.EqualTo(503), "Should return 503 Service Unavailable");
        Assert.That(user3.DenialReason, Does.Contain("Server bandwidth limit exceeded"));
    }

    [Test]
    public async Task Test_Admin_SkipsUserLimits_ButNotServerLimits()
    {
        // Arrange
        var config = new RateLimitConfig
        {
            RestoreStateOnStartup = false,
            PerUserMegabytesPerHour = 10, // 10 MB/hour per user
            PerUserRequestsPerSecond = 10000,
            PerIpMegabytesPerHour = 10000,
            PerIpRequestsPerSecond = 10000,
            ServerMegabytesPerHour = 50, // 50 MB/hour server-wide
            ServerRequestsPerSecond = 10000,
            SkipLimitsForAdmins = true
        };

        using var rateLimiter = new InMemoryRateLimiter(config);
        var adminUserId = Guid.NewGuid();

        // Act - Admin transfers exceed user limit but stay within server limit
        var result1 = await rateLimiter.CheckRateLimitAsync(adminUserId, "192.168.1.100", 20 * ONE_MB, isAdmin: true);
        var result2 = await rateLimiter.CheckRateLimitAsync(adminUserId, "192.168.1.100", 20 * ONE_MB, isAdmin: true);
        var result3 = await rateLimiter.CheckRateLimitAsync(adminUserId, "192.168.1.100", 20 * ONE_MB, isAdmin: true);

        // Assert
        Assert.That(result1.IsAllowed, Is.True, "Admin bypasses user limit (20 MB > 10 MB user limit)");
        Assert.That(result2.IsAllowed, Is.True, "Admin still bypasses user limit (40 MB total)");
        Assert.That(result3.IsAllowed, Is.False, "Admin still subject to server limit (60 MB > 50 MB)");
        Assert.That(result3.StatusCode, Is.EqualTo(503));
    }

    [Test]
    public async Task Test_DifferentUsers_IndependentLimits()
    {
        // Arrange
        var config = new RateLimitConfig
        {
            RestoreStateOnStartup = false,
            PerUserMegabytesPerHour = 10, // 10 MB/hour per user
            PerUserRequestsPerSecond = null,
            PerIpMegabytesPerHour = null,
            ServerMegabytesPerHour = null
        };

        using var rateLimiter = new InMemoryRateLimiter(config);
        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();

        // Act - User1 transfers 9 MB, User2 transfers 9 MB
        var result1 = await rateLimiter.CheckRateLimitAsync(
            user1, "192.168.1.1", 9 * ONE_MB, false);
        var result2 = await rateLimiter.CheckRateLimitAsync(
            user2, "192.168.1.2", 9 * ONE_MB, false);

        // Assert - Both allowed (independent limits)
        Assert.That(result1.IsAllowed, Is.True, "User1 within their 10 MB limit");
        Assert.That(result2.IsAllowed, Is.True, "User2 within their 10 MB limit");
    }

    [Test]
    public async Task Test_DifferentIPs_IndependentLimits()
    {
        // Arrange
        var config = new RateLimitConfig
        {
            RestoreStateOnStartup = false,
            PerUserMegabytesPerHour = 10000,
            PerUserRequestsPerSecond = 10000,
            PerIpMegabytesPerHour = 15, // 15 MB/hour per IP
            PerIpRequestsPerSecond = 10000,
            ServerMegabytesPerHour = 10000,
            ServerRequestsPerSecond = 10000
        };

        using var rateLimiter = new InMemoryRateLimiter(config);
        var guestUserId = Guid.Empty;

        // Act - IP1 transfers 14 MB, IP2 transfers 14 MB
        var result1 = await rateLimiter.CheckRateLimitAsync(guestUserId, "192.168.1.1", 14 * ONE_MB, false);
        var result2 = await rateLimiter.CheckRateLimitAsync(guestUserId, "192.168.1.2", 14 * ONE_MB, false);

        // Assert - Both allowed (independent limits)
        Assert.That(result1.IsAllowed, Is.True, "IP1 within their 15 MB limit");
        Assert.That(result2.IsAllowed, Is.True, "IP2 within their 15 MB limit");
    }

    [Test]
    public async Task Test_SmallTransfers_RoundingDoesNotCauseIssues()
    {
        // Arrange - Test that small transfers (< 1 MB) accumulate correctly
        var config = new RateLimitConfig
        {
            RestoreStateOnStartup = false,
            PerUserMegabytesPerHour = 2, // 2 MB/hour to account for rounding
            PerUserRequestsPerSecond = 10000,
            PerIpMegabytesPerHour = 10000,
            PerIpRequestsPerSecond = 10000,
            ServerMegabytesPerHour = 10000,
            ServerRequestsPerSecond = 10000
        };

        using var rateLimiter = new InMemoryRateLimiter(config);
        var userId = Guid.NewGuid();

        // Act - Transfer 512 KB chunks (0.5 MB each) - 5 times = 2.5 MB total
        int halfMB = ONE_MB / 2;
        var result1 = await rateLimiter.CheckRateLimitAsync(userId, "192.168.1.1", halfMB, false);
        var result2 = await rateLimiter.CheckRateLimitAsync(userId, "192.168.1.1", halfMB, false);
        var result3 = await rateLimiter.CheckRateLimitAsync(userId, "192.168.1.1", halfMB, false);
        var result4 = await rateLimiter.CheckRateLimitAsync(userId, "192.168.1.1", halfMB, false);
        var result5 = await rateLimiter.CheckRateLimitAsync(userId, "192.168.1.1", halfMB, false);

        // Assert - With integer division truncation, actual byte accumulation determines limit
        Assert.That(result1.IsAllowed, Is.True, "First 0.5 MB allowed");
        Assert.That(result2.IsAllowed, Is.True, "Second 0.5 MB allowed");
        Assert.That(result3.IsAllowed, Is.True, "Third 0.5 MB allowed");
        Assert.That(result4.IsAllowed, Is.True, "Fourth 0.5 MB allowed (total 2.0 MB)");
        Assert.That(result5.IsAllowed, Is.False, "Fifth 0.5 MB denied (would be 2.5 MB > 2 MB limit)");
    }

    [Test]
    public async Task Test_RetryAfter_IndicatesWaitTime()
    {
        // Arrange
        var config = new RateLimitConfig
        {
            RestoreStateOnStartup = false,
            PerUserMegabytesPerHour = 5,
            PerUserRequestsPerSecond = null,
            PerIpMegabytesPerHour = null,
            ServerMegabytesPerHour = null
        };

        using var rateLimiter = new InMemoryRateLimiter(config);
        var userId = Guid.NewGuid();

        // Act - Transfer 5 MB (at limit), then try to exceed
        var firstResult = await rateLimiter.CheckRateLimitAsync(userId, "192.168.1.1", 5 * ONE_MB, false);
        var deniedResult = await rateLimiter.CheckRateLimitAsync(userId, "192.168.1.1", 1 * ONE_MB, false);

        // Assert - RetryAfter should be close to 1 hour
        Assert.That(firstResult.IsAllowed, Is.True, "First 5 MB should be allowed");
        Assert.That(deniedResult.IsAllowed, Is.False, "Additional 1 MB should be denied");
        Assert.That(deniedResult.RetryAfter, Is.GreaterThan(TimeSpan.FromMinutes(59)));
        Assert.That(deniedResult.RetryAfter, Is.LessThan(TimeSpan.FromMinutes(61)));
    }
}
