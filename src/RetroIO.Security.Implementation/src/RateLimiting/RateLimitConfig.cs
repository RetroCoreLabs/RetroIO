//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Security.Implementation.RateLimiting
{
    /// <summary>
    /// Rate limiting configuration
    /// Supports per-user, per-IP, and server-wide limits
    /// </summary>
    public sealed class RateLimitConfig
    {
        // ============================================================
        // PER-USER LIMITS (authenticated users identified by GUID)
        // ============================================================

        /// <summary>
        /// Max requests per second per user
        /// </summary>
        public int? PerUserRequestsPerSecond { get; set; } = 200;

        /// <summary>
        /// Max megabytes per hour per user
        /// </summary>
        public int? PerUserMegabytesPerHour { get; set; } = 100;

        /// <summary>
        /// Skip limits for admin users?
        /// </summary>
        public bool SkipLimitsForAdmins { get; set; } = true;

        // ============================================================
        // PER-IP LIMITS (guest/anonymous users identified by IP)
        // ============================================================

        /// <summary>
        /// Max requests per second per IP (for guests)
        /// </summary>
        public int? PerIpRequestsPerSecond { get; set; } = 200;

        /// <summary>
        /// Max megabytes per hour per IP (for guests)
        /// </summary>
        public int? PerIpMegabytesPerHour { get; set; } = 50;

        // ============================================================
        // SERVER-WIDE LIMITS (total, independent of user/IP)
        // ============================================================

        /// <summary>
        /// Max total requests per second (entire server)
        /// </summary>
        public int? ServerRequestsPerSecond { get; set; } = 1000;

        /// <summary>
        /// Max total megabytes per hour (entire server)
        /// </summary>
        public int? ServerMegabytesPerHour { get; set; } = 10000;

        // ============================================================
        // STATE PERSISTENCE
        // ============================================================

        /// <summary>
        /// Path to save rate limit state JSON
        /// </summary>
        public string StatePersistencePath { get; set; } = "ratelimit-state.json";

        /// <summary>
        /// How often to persist state to disk
        /// </summary>
        public TimeSpan PersistenceInterval { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Restore state from disk on startup?
        /// </summary>
        public bool RestoreStateOnStartup { get; set; } = true;
    }

    /// <summary>
    /// Rate limit result
    /// </summary>
    public sealed class RateLimitResult
    {
        /// <summary>
        /// Gets or sets whether the request is allowed.
        /// </summary>
        public bool IsAllowed { get; set; }
        /// <summary>
        /// Gets or sets the reason for a denial, or null when the request is allowed.
        /// </summary>
        public string? DenialReason { get; set; }
        /// <summary>
        /// Gets or sets how long the caller should wait before trying again.
        /// </summary>
        public TimeSpan RetryAfter { get; set; }
        /// <summary>
        /// Gets or sets the status code for the result. The default is 200.
        /// </summary>
        public int StatusCode { get; set; } = 200;

        /// <summary>
        /// Creates a result that allows the request.
        /// </summary>
        /// <returns>
        /// A result with IsAllowed true.
        /// </returns>
        public static RateLimitResult Allowed()
        {
            return new RateLimitResult { IsAllowed = true };
        }

        /// <summary>
        /// Creates a result that denies the request.
        /// </summary>
        /// <param name="reason">
        /// The reason for the denial.
        /// </param>
        /// <param name="retryAfter">
        /// How long the caller should wait before trying again.
        /// </param>
        /// <param name="statusCode">
        /// The status code for the denial.
        /// </param>
        /// <returns>
        /// A result with IsAllowed false and the given values.
        /// </returns>
        public static RateLimitResult Denied(string reason, TimeSpan retryAfter, int statusCode)
        {
            return new RateLimitResult
            {
                IsAllowed = false,
                DenialReason = reason,
                RetryAfter = retryAfter,
                StatusCode = statusCode
            };
        }
    }
}
