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
        /// <summary>
        /// Unique audit entry ID
        /// </summary>
        public Guid AuditId { get; set; } = Guid.NewGuid();

        /// <summary>
        /// When event occurred (UTC)
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Type of security event
        /// </summary>
        public SecurityEventType EventType { get; set; }

        /// <summary>
        /// Severity level
        /// </summary>
        public AuditSeverity Severity { get; set; } = AuditSeverity.Info;

        /// <summary>
        /// User who performed action (GUID, or Guid.Empty for UNKNOWN)
        /// </summary>
        public Guid PrincipalId { get; set; }

        /// <summary>
        /// Username (for readability)
        /// </summary>
        public string? PrincipalName { get; set; }

        /// <summary>
        /// Client IP address
        /// </summary>
        public string? IpAddress { get; set; }

        /// <summary>
        /// Session ID (for correlation)
        /// </summary>
        public string? SessionId { get; set; }

        /// <summary>
        /// Trace ID (for distributed tracing)
        /// </summary>
        public Guid? TraceId { get; set; }

        /// <summary>
        /// File system operation
        /// </summary>
        public string? Operation { get; set; }

        /// <summary>
        /// Path accessed
        /// </summary>
        public string? Path { get; set; }

        /// <summary>
        /// Resource identifier (volume ID, mount point)
        /// </summary>
        public string? Resource { get; set; }

        /// <summary>
        /// Was access granted?
        /// </summary>
        public bool? AccessGranted { get; set; }

        /// <summary>
        /// Reason for denial (if AccessGranted = false)
        /// </summary>
        public string? DenialReason { get; set; }

        /// <summary>
        /// Human-readable message
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// Additional metadata (JSON)
        /// </summary>
        public Dictionary<string, object>? Metadata { get; set; }

        /// <summary>
        /// How long operation took (milliseconds)
        /// </summary>
        public long? DurationMs { get; set; }
    }

    /// <summary>
    /// Security event types
    /// </summary>
    public enum SecurityEventType
    {
        // Authentication Events
        /// <summary>
        /// A login succeeded.
        /// </summary>
        AuthenticationSuccess,
        /// <summary>
        /// A login failed.
        /// </summary>
        AuthenticationFailure,
        /// <summary>
        /// An authentication token has expired.
        /// </summary>
        AuthenticationTokenExpired,
        /// <summary>
        /// An authentication token was revoked.
        /// </summary>
        AuthenticationTokenRevoked,

        // Authorization Events
        /// <summary>
        /// An access request was granted.
        /// </summary>
        AuthorizationGranted,
        /// <summary>
        /// An access request was denied.
        /// </summary>
        AuthorizationDenied,

        // Connection Events
        /// <summary>
        /// A connection was accepted.
        /// </summary>
        ConnectionAccepted,
        /// <summary>
        /// A connection was rejected.
        /// </summary>
        ConnectionRejected,
        /// <summary>
        /// The firewall blocked an address.
        /// </summary>
        FirewallBlocked,
        /// <summary>
        /// The firewall allowed an address.
        /// </summary>
        FirewallAllowed,

        // File Access Events
        /// <summary>
        /// A file was read.
        /// </summary>
        FileRead,
        /// <summary>
        /// A file was written.
        /// </summary>
        FileWrite,
        /// <summary>
        /// A file was deleted.
        /// </summary>
        FileDelete,
        /// <summary>
        /// A file was opened.
        /// </summary>
        FileOpen,
        /// <summary>
        /// A file was closed.
        /// </summary>
        FileClose,
        /// <summary>
        /// A seek was done in a file.
        /// </summary>
        FileSeek,
        /// <summary>
        /// A file was renamed.
        /// </summary>
        FileRename,
        /// <summary>
        /// A directory was listed.
        /// </summary>
        DirectoryList,
        /// <summary>
        /// A directory was created.
        /// </summary>
        DirectoryCreate,
        /// <summary>
        /// A directory was deleted.
        /// </summary>
        DirectoryDelete,
        /// <summary>
        /// A mount operation was done.
        /// </summary>
        MountOperation,

        // Security Violations
        /// <summary>
        /// A path traversal attempt was detected.
        /// </summary>
        PathTraversalAttempt,
        /// <summary>
        /// A rate limit was exceeded.
        /// </summary>
        RateLimitExceeded,
        /// <summary>
        /// An invalid token was presented.
        /// </summary>
        InvalidToken,
        /// <summary>
        /// Suspicious activity was detected.
        /// </summary>
        SuspiciousActivity,

        // System Events
        /// <summary>
        /// A session was created.
        /// </summary>
        SessionCreated,
        /// <summary>
        /// A session was terminated.
        /// </summary>
        SessionTerminated,
        /// <summary>
        /// A configuration was changed.
        /// </summary>
        ConfigurationChanged
    }

    /// <summary>
    /// Audit severity levels
    /// </summary>
    public enum AuditSeverity
    {
        /// <summary>
        /// Normal operation, for example a file read or a successful login.
        /// </summary>
        Info,        // Normal operation (file read, login success)
        /// <summary>
        /// Suspicious but not critical, for example an exceeded rate limit.
        /// </summary>
        Warning,     // Suspicious but not critical (rate limit exceeded)
        /// <summary>
        /// A security violation, for example a path traversal or a failed login.
        /// </summary>
        Error,       // Security violation (path traversal, auth failure)
        /// <summary>
        /// A severe incident, for example repeated failed logins.
        /// </summary>
        Critical     // Severe security incident (multiple auth failures, brute force)
    }
}
