//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Threading;
using System.Threading.Tasks;
using RetroIO.Security.Authentication;
using RetroIO.Security.Authorization;

namespace RetroIO.Security.Audit;

/// <summary>
/// Security event type enumeration
/// </summary>
public enum SecurityEventType : byte
{
    /// <summary>Successful authentication</summary>
    AuthenticationSuccess = 0,

    /// <summary>Failed authentication attempt</summary>
    AuthenticationFailure = 1,

    /// <summary>User logged out</summary>
    Logout = 2,

    /// <summary>Session token refreshed</summary>
    TokenRefresh = 3,

    /// <summary>Successful authorization</summary>
    AuthorizationSuccess = 4,

    /// <summary>Authorization denied</summary>
    AuthorizationDenied = 5,

    /// <summary>File accessed (read)</summary>
    FileAccessed = 6,

    /// <summary>File modified (write)</summary>
    FileModified = 7,

    /// <summary>File created</summary>
    FileCreated = 8,

    /// <summary>File deleted</summary>
    FileDeleted = 9,

    /// <summary>Directory created</summary>
    DirectoryCreated = 10,

    /// <summary>Directory deleted</summary>
    DirectoryDeleted = 11,

    /// <summary>Permissions changed</summary>
    PermissionsChanged = 12,

    /// <summary>Rate limit exceeded</summary>
    RateLimitExceeded = 13,

    /// <summary>Path traversal attempt detected</summary>
    PathTraversalAttempt = 14,

    /// <summary>Invalid input detected</summary>
    InvalidInput = 15,

    /// <summary>Configuration changed</summary>
    ConfigurationChanged = 16,

    /// <summary>Security policy violation</summary>
    PolicyViolation = 17
}

/// <summary>
/// Security event severity enumeration
/// </summary>
public enum SecurityEventSeverity : byte
{
    /// <summary>Informational event</summary>
    Info = 0,

    /// <summary>Warning event</summary>
    Warning = 1,

    /// <summary>Error event</summary>
    Error = 2,

    /// <summary>Critical security event</summary>
    Critical = 3
}

/// <summary>
/// Base security audit entry
/// </summary>
public class SecurityAuditEntry
{
    /// <summary>Unique entry identifier</summary>
    public Guid EntryId { get; set; } = Guid.NewGuid();

    /// <summary>Timestamp of event (UTC)</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>Event type</summary>
    public SecurityEventType EventType { get; set; }

    /// <summary>Event severity</summary>
    public SecurityEventSeverity Severity { get; set; }

    /// <summary>Request trace ID for correlation</summary>
    public Guid? TraceId { get; set; }

    /// <summary>Client IP address</summary>
    public string? ClientIpAddress { get; set; }

    /// <summary>User agent or client identifier</summary>
    public string? UserAgent { get; set; }

    /// <summary>Additional event data (JSON serializable)</summary>
    public string? AdditionalData { get; set; }
}

/// <summary>
/// Authentication audit entry
/// </summary>
public sealed class AuthenticationAuditEntry : SecurityAuditEntry
{
    /// <summary>Authentication method used</summary>
    public AuthenticationMethod Method { get; set; }

    /// <summary>Username or principal identifier</summary>
    public string? PrincipalName { get; set; }

    /// <summary>Whether authentication succeeded</summary>
    public bool Success { get; set; }

    /// <summary>Failure reason (if applicable)</summary>
    public AuthenticationFailureReason? FailureReason { get; set; }

    /// <summary>Session token issued (do not log actual token value)</summary>
    public bool TokenIssued { get; set; }
}

/// <summary>
/// Authorization audit entry
/// </summary>
public sealed class AuthorizationAuditEntry : SecurityAuditEntry
{
    /// <summary>Principal identifier</summary>
    public string PrincipalName { get; set; } = string.Empty;

    /// <summary>Operation being authorized</summary>
    public FileSystemOperation Operation { get; set; }

    /// <summary>Target path</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Destination path (for rename)</summary>
    public string? DestinationPath { get; set; }

    /// <summary>Whether authorization was granted</summary>
    public bool Allowed { get; set; }

    /// <summary>Denial reason (if not allowed)</summary>
    public string? DenialReason { get; set; }

    /// <summary>Effective permissions</summary>
    public PathPermissions EffectivePermissions { get; set; }
}

/// <summary>
/// File access audit entry
/// </summary>
public sealed class FileAccessAuditEntry : SecurityAuditEntry
{
    /// <summary>Principal identifier</summary>
    public string PrincipalName { get; set; } = string.Empty;

    /// <summary>File path accessed</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Operation performed</summary>
    public FileSystemOperation Operation { get; set; }

    /// <summary>Number of bytes read/written</summary>
    public long BytesTransferred { get; set; }

    /// <summary>Whether operation succeeded</summary>
    public bool Success { get; set; }

    /// <summary>Error message (if failed)</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Operation duration in milliseconds</summary>
    public long DurationMs { get; set; }
}

/// <summary>
/// Security audit logger interface - implement to provide custom audit logging
/// </summary>
public interface ISecurityAuditLogger
{
    /// <summary>
    /// Log authentication event
    /// </summary>
    /// <param name="entry">Authentication audit entry</param>
    /// <param name="ct">Cancellation token</param>
    ValueTask LogAuthenticationAsync(
        AuthenticationAuditEntry entry,
        CancellationToken ct = default);

    /// <summary>
    /// Log authorization event
    /// </summary>
    /// <param name="entry">Authorization audit entry</param>
    /// <param name="ct">Cancellation token</param>
    ValueTask LogAuthorizationAsync(
        AuthorizationAuditEntry entry,
        CancellationToken ct = default);

    /// <summary>
    /// Log file access event
    /// </summary>
    /// <param name="entry">File access audit entry</param>
    /// <param name="ct">Cancellation token</param>
    ValueTask LogFileAccessAsync(
        FileAccessAuditEntry entry,
        CancellationToken ct = default);

    /// <summary>
    /// Log generic security event
    /// </summary>
    /// <param name="entry">Security audit entry</param>
    /// <param name="ct">Cancellation token</param>
    ValueTask LogSecurityEventAsync(
        SecurityAuditEntry entry,
        CancellationToken ct = default);

    /// <summary>
    /// Query audit log entries
    /// </summary>
    /// <param name="startTime">Start time (UTC)</param>
    /// <param name="endTime">End time (UTC)</param>
    /// <param name="eventTypes">Event types to filter (null for all)</param>
    /// <param name="principalName">Principal name to filter (null for all)</param>
    /// <param name="maxResults">Maximum number of results</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Array of audit entries</returns>
    ValueTask<SecurityAuditEntry[]> QueryAuditLogAsync(
        DateTime startTime,
        DateTime endTime,
        SecurityEventType[]? eventTypes = null,
        string? principalName = null,
        int maxResults = 1000,
        CancellationToken ct = default);
}
