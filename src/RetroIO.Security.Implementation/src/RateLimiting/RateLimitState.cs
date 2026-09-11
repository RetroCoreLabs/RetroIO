//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Generic;

namespace RetroIO.Security.Implementation.RateLimiting
{
    /// <summary>
    /// In-memory rate limit state (persisted to JSON every minute)
    /// </summary>
    public sealed class RateLimitState
    {
        /// <summary>When state was last saved</summary>
        public DateTime LastSavedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Per-user request counters</summary>
        public Dictionary<Guid, UserRateLimitState> UserStates { get; set; } = new();

        /// <summary>Per-IP request counters</summary>
        public Dictionary<string, IpRateLimitState> IpStates { get; set; } = new();

        /// <summary>Server-wide counters</summary>
        public ServerRateLimitState ServerState { get; set; } = new();
    }

    /// <summary>Rate limit state for a single user</summary>
    public sealed class UserRateLimitState
    {
        public Guid UserId { get; set; }
        public int RequestsThisSecond { get; set; }
        public DateTime CurrentSecondStart { get; set; } = DateTime.UtcNow;
        public long BytesThisHour { get; set; }
        public DateTime CurrentHourStart { get; set; } = DateTime.UtcNow;
    }

    /// <summary>Rate limit state for a single IP address</summary>
    public sealed class IpRateLimitState
    {
        public string IpAddress { get; set; } = string.Empty;
        public int RequestsThisSecond { get; set; }
        public DateTime CurrentSecondStart { get; set; } = DateTime.UtcNow;
        public long BytesThisHour { get; set; }
        public DateTime CurrentHourStart { get; set; } = DateTime.UtcNow;
    }

    /// <summary>Server-wide rate limit state</summary>
    public sealed class ServerRateLimitState
    {
        public int RequestsThisSecond { get; set; }
        public DateTime CurrentSecondStart { get; set; } = DateTime.UtcNow;
        public long BytesThisHour { get; set; }
        public DateTime CurrentHourStart { get; set; } = DateTime.UtcNow;
    }
}
