//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
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
        /// <summary>
        /// When state was last saved
        /// </summary>
        public DateTime LastSavedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Per-user request counters
        /// </summary>
        public Dictionary<Guid, UserRateLimitState> UserStates { get; set; } = new();

        /// <summary>
        /// Per-IP request counters
        /// </summary>
        public Dictionary<string, IpRateLimitState> IpStates { get; set; } = new();

        /// <summary>
        /// Server-wide counters
        /// </summary>
        public ServerRateLimitState ServerState { get; set; } = new();
    }

    /// <summary>
    /// Rate limit state for a single user
    /// </summary>
    public sealed class UserRateLimitState
    {
        /// <summary>
        /// Gets or sets the identifier of the user.
        /// </summary>
        public Guid UserId { get; set; }
        /// <summary>
        /// Gets or sets the number of requests counted in the current second.
        /// </summary>
        public int RequestsThisSecond { get; set; }
        /// <summary>
        /// Gets or sets the start of the current one-second window.
        /// </summary>
        public DateTime CurrentSecondStart { get; set; } = DateTime.UtcNow;
        /// <summary>
        /// Gets or sets the number of bytes counted in the current hour.
        /// </summary>
        public long BytesThisHour { get; set; }
        /// <summary>
        /// Gets or sets the start of the current one-hour window.
        /// </summary>
        public DateTime CurrentHourStart { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Rate limit state for a single IP address
    /// </summary>
    public sealed class IpRateLimitState
    {
        /// <summary>
        /// Gets or sets the IP address.
        /// </summary>
        public string IpAddress { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the number of requests counted in the current second.
        /// </summary>
        public int RequestsThisSecond { get; set; }
        /// <summary>
        /// Gets or sets the start of the current one-second window.
        /// </summary>
        public DateTime CurrentSecondStart { get; set; } = DateTime.UtcNow;
        /// <summary>
        /// Gets or sets the number of bytes counted in the current hour.
        /// </summary>
        public long BytesThisHour { get; set; }
        /// <summary>
        /// Gets or sets the start of the current one-hour window.
        /// </summary>
        public DateTime CurrentHourStart { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Server-wide rate limit state
    /// </summary>
    public sealed class ServerRateLimitState
    {
        /// <summary>
        /// Gets or sets the number of requests counted in the current second.
        /// </summary>
        public int RequestsThisSecond { get; set; }
        /// <summary>
        /// Gets or sets the start of the current one-second window.
        /// </summary>
        public DateTime CurrentSecondStart { get; set; } = DateTime.UtcNow;
        /// <summary>
        /// Gets or sets the number of bytes counted in the current hour.
        /// </summary>
        public long BytesThisHour { get; set; }
        /// <summary>
        /// Gets or sets the start of the current one-hour window.
        /// </summary>
        public DateTime CurrentHourStart { get; set; } = DateTime.UtcNow;
    }
}
