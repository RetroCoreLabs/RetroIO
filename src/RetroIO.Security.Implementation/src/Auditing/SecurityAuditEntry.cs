//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Security.Implementation.Auditing
{
    /// <summary>
    /// Security audit entry
    /// </summary>
    public sealed class SecurityAuditEntry
    {
        /// <summary>Unique audit entry ID</summary>
        public Guid AuditId { get; set; } = Guid.NewGuid();

        /// <summary>When event occurred (UTC)</summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>Type of security event</summary>
        public SecurityEventType EventType { get; set; }

        /// <summary>Severity level</summary>
        public AuditSeverity Severity { get; set; } = AuditSeverity.Info;

        /// <summary>User who performed action (GUID, or Guid.Empty for UNKNOWN)</summary>
        public Guid PrincipalId { get; set; }

        /// <summary>Username (for readability)</summary>
        public string? PrincipalName { get; set; }

        /// <summary>Client IP address</summary>
        public string? IpAddress { get; set; }

        /// <summary>Session ID (for correlation)</summary>
        public string? SessionId { get; set; }

        /// <summary>Trace ID (for distributed tracing)</summary>
        public Guid? TraceId { get; set; }

        /// <summary>File system operation</summary>
        public string? Operation { get; set; }

        /// <summary>Path accessed</summary>
        public string? Path { get; set; }

        /// <summary>Resource identifier (volume ID, mount point)</summary>
        public string? Resource { get; set; }

        /// <summary>Was access granted?</summary>
        public bool? AccessGranted { get; set; }

        /// <summary>Reason for denial (if AccessGranted = false)</summary>
        public string? DenialReason { get; set; }

        /// <summary>Human-readable message</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>Additional metadata (JSON)</summary>
        public Dictionary<string, object>? Metadata { get; set; }

        /// <summary>How long operation took (milliseconds)</summary>
        public long? DurationMs { get; set; }
    }

    /// <summary>
    /// Security event types
    /// </summary>
    public enum SecurityEventType
    {
        // Authentication Events
        AuthenticationSuccess,
        AuthenticationFailure,
        AuthenticationTokenExpired,
        AuthenticationTokenRevoked,

        // Authorization Events
        AuthorizationGranted,
        AuthorizationDenied,

        // Connection Events
        ConnectionAccepted,
        ConnectionRejected,
        FirewallBlocked,
        FirewallAllowed,

        // File Access Events
        FileRead,
        FileWrite,
        FileDelete,
        FileOpen,
        FileClose,
        FileSeek,
        FileRename,
        DirectoryList,
        DirectoryCreate,
        DirectoryDelete,
        MountOperation,

        // Security Violations
        PathTraversalAttempt,
        RateLimitExceeded,
        InvalidToken,
        SuspiciousActivity,

        // System Events
        SessionCreated,
        SessionTerminated,
        ConfigurationChanged
    }

    /// <summary>
    /// Audit severity levels
    /// </summary>
    public enum AuditSeverity
    {
        Info,        // Normal operation (file read, login success)
        Warning,     // Suspicious but not critical (rate limit exceeded)
        Error,       // Security violation (path traversal, auth failure)
        Critical     // Severe security incident (multiple auth failures, brute force)
    }
}
