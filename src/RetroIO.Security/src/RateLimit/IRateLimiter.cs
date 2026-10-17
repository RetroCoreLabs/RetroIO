//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Threading;
using System.Threading.Tasks;

namespace RetroIO.Security.RateLimit;

/// <summary>
/// Rate limit key identifying what is being rate limited
/// </summary>
public sealed class RateLimitKey
{
    /// <summary>
    /// Client IP address
    /// </summary>
    public string? IpAddress { get; set; }

    /// <summary>
    /// User identifier
    /// </summary>
    public Guid? UserId { get; set; }

    /// <summary>
    /// API key identifier
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Additional key components
    /// </summary>
    public string? CustomKey { get; set; }

    /// <summary>
    /// Get combined key string for storage
    /// </summary>
    public string GetCombinedKey()
    {
        if (!string.IsNullOrEmpty(CustomKey))
            return CustomKey;
        if (UserId.HasValue)
            return $"user:{UserId.Value}";
        if (!string.IsNullOrEmpty(ApiKey))
            return $"apikey:{ApiKey}";
        if (!string.IsNullOrEmpty(IpAddress))
            return $"ip:{IpAddress}";
        return "anonymous";
    }
}

/// <summary>
/// Rate limit result
/// </summary>
public sealed class RateLimitResult
{
    /// <summary>
    /// Whether request is allowed (within rate limit)
    /// </summary>
    public bool Allowed { get; set; }

    /// <summary>
    /// Current request count in window
    /// </summary>
    public int CurrentCount { get; set; }

    /// <summary>
    /// Maximum allowed requests in window
    /// </summary>
    public int Limit { get; set; }

    /// <summary>
    /// Time until rate limit resets
    /// </summary>
    public TimeSpan RetryAfter { get; set; }

    /// <summary>
    /// Remaining requests allowed in current window
    /// </summary>
    public int Remaining => Math.Max(0, Limit - CurrentCount);

    /// <summary>
    /// Create allowed result
    /// </summary>
    public static RateLimitResult Allow(int currentCount, int limit, TimeSpan retryAfter)
    {
        return new RateLimitResult
        {
            Allowed = true,
            CurrentCount = currentCount,
            Limit = limit,
            RetryAfter = retryAfter
        };
    }

    /// <summary>
    /// Create denied result
    /// </summary>
    public static RateLimitResult Deny(int currentCount, int limit, TimeSpan retryAfter)
    {
        return new RateLimitResult
        {
            Allowed = false,
            CurrentCount = currentCount,
            Limit = limit,
            RetryAfter = retryAfter
        };
    }
}

/// <summary>
/// Rate limit configuration using token bucket algorithm
/// </summary>
public sealed class RateLimitConfig
{
    /// <summary>
    /// Maximum requests allowed in time window
    /// </summary>
    public int MaxRequests { get; set; } = 100;

    /// <summary>
    /// Time window duration
    /// </summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Burst size (additional requests allowed above rate)
    /// </summary>
    public int BurstSize { get; set; } = 10;

    /// <summary>
    /// Token refill rate (requests per second)
    /// </summary>
    public double RefillRate => MaxRequests / Window.TotalSeconds;

    /// <summary>
    /// Create configuration for requests per minute
    /// </summary>
    public static RateLimitConfig PerMinute(int maxRequests, int burstSize = 10)
    {
        return new RateLimitConfig
        {
            MaxRequests = maxRequests,
            Window = TimeSpan.FromMinutes(1),
            BurstSize = burstSize
        };
    }

    /// <summary>
    /// Create configuration for requests per hour
    /// </summary>
    public static RateLimitConfig PerHour(int maxRequests, int burstSize = 10)
    {
        return new RateLimitConfig
        {
            MaxRequests = maxRequests,
            Window = TimeSpan.FromHours(1),
            BurstSize = burstSize
        };
    }

    /// <summary>
    /// Create configuration for requests per day
    /// </summary>
    public static RateLimitConfig PerDay(int maxRequests, int burstSize = 10)
    {
        return new RateLimitConfig
        {
            MaxRequests = maxRequests,
            Window = TimeSpan.FromDays(1),
            BurstSize = burstSize
        };
    }
}

/// <summary>
/// Rate limiter interface - implement to provide custom rate limiting
/// </summary>
public interface IRateLimiter
{
    /// <summary>
    /// Check if request is allowed under rate limit
    /// </summary>
    /// <param name="key">
    /// Rate limit key identifying the client/user
    /// </param>
    /// <param name="ct">
    /// Cancellation token
    /// </param>
    /// <returns>
    /// Rate limit result indicating whether request is allowed
    /// </returns>
    ValueTask<RateLimitResult> CheckRateLimitAsync(
        RateLimitKey key,
        CancellationToken ct = default);

    /// <summary>
    /// Reset rate limit for a specific key
    /// </summary>
    /// <param name="key">
    /// Rate limit key to reset
    /// </param>
    /// <param name="ct">
    /// Cancellation token
    /// </param>
    /// <returns>
    /// True if reset was successful
    /// </returns>
    ValueTask<bool> ResetRateLimitAsync(
        RateLimitKey key,
        CancellationToken ct = default);

    /// <summary>
    /// Get current rate limit status for a key
    /// </summary>
    /// <param name="key">
    /// Rate limit key
    /// </param>
    /// <param name="ct">
    /// Cancellation token
    /// </param>
    /// <returns>
    /// Current rate limit result
    /// </returns>
    ValueTask<RateLimitResult> GetRateLimitStatusAsync(
        RateLimitKey key,
        CancellationToken ct = default);
}
