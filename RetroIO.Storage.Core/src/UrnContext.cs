//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// Context information extracted from a URN for filesystem operations.
    /// Represents the minimal information needed to route and authorize requests.
    /// </summary>
    /// <remarks>
    /// URN format: urn:retrofs:transport:transport-params:volume-id:path[#fragment]
    /// </remarks>
    public sealed class UrnContext
    {
        /// <summary>
        /// Volume identifier (unique within the protocol adapter).
        /// </summary>
        public string VolumeId { get; set; } = string.Empty;

        /// <summary>
        /// If true, only read operations are permitted.
        /// </summary>
        public bool ReadOnly { get; set; }

        /// <summary>
        /// Optional username for authentication.
        /// </summary>
        public string? Username { get; set; }

        /// <summary>
        /// Optional authentication token.
        /// </summary>
        public string? AuthToken { get; set; }

        /// <summary>
        /// Optional role for RBAC authorization.
        /// </summary>
        public string? Role { get; set; }

        /// <summary>
        /// Optional security context for advanced RBAC scenarios.
        /// </summary>
        public object? SecurityContext { get; set; }
    }
}
