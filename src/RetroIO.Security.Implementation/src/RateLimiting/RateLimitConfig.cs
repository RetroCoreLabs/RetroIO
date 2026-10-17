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
        public bool IsAllowed { get; set; }
        public string? DenialReason { get; set; }
        public TimeSpan RetryAfter { get; set; }
        public int StatusCode { get; set; } = 200;

        public static RateLimitResult Allowed()
        {
            return new RateLimitResult { IsAllowed = true };
        }

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
