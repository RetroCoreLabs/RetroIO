//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using RetroIO.Protocols.Tnfs.Protocol;

namespace RetroIO.Protocols.Tnfs.Server;

/// <summary>
/// Manages TNFS session lifecycle, creation, lookup, and cleanup.
/// Thread-safe for concurrent access.
/// </summary>
public sealed class SessionManager : IDisposable
{
    private readonly ConcurrentDictionary<ushort, TnfsSession> _sessions = new();
    private readonly ILogger<SessionManager> _logger;
    private readonly TimeSpan _sessionTimeout;
    private readonly int _maxSessionsPerIP;
    private readonly Timer _cleanupTimer;
    private bool _disposed;

    public SessionManager(ILogger<SessionManager> logger, TimeSpan? sessionTimeout = null, int maxSessionsPerIP = 8)
    {
        _logger = logger;
        _sessionTimeout = sessionTimeout ?? TimeSpan.FromSeconds(TnfsConstants.SESSION_TIMEOUT);
        _maxSessionsPerIP = maxSessionsPerIP;

        // Start cleanup timer (runs every minute)
        _cleanupTimer = new Timer(CleanupExpiredSessions, null,
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// Get current session count.
    /// </summary>
    public int SessionCount => _sessions.Count;

    /// <summary>
    /// Create new session for client with optional user binding.
    /// Enforces MAX_SESSIONS_PER_IP limit.
    /// </summary>
    public TnfsSession CreateSession(IPAddress clientIP, string rootPath, Guid userId = default, string username = "UNKNOWN", bool isAdmin = false)
    {
        // Check if we need to recycle a session from this IP
        var existingSessions = _sessions.Values
            .Where(s => s.ClientIP.Equals(clientIP))
            .ToList();

        if (existingSessions.Count >= _maxSessionsPerIP)
        {
            // Find oldest session for this IP and recycle it
            var oldest = existingSessions.OrderBy(s => s.LastContact).First();
            _logger.LogInformation("Recycling session 0x{SessionId:X4} for IP {IP} (max sessions per IP reached)",
                oldest.SessionId, clientIP);

            RemoveSession(oldest.SessionId);
        }

        // Generate unique session ID
        ushort sessionId = GenerateSessionId();

        var session = new TnfsSession
        {
            SessionId = sessionId,
            ClientIP = clientIP,
            RootPath = rootPath,
            UserId = userId,
            Username = username,
            IsAdmin = isAdmin,
            CreatedAt = DateTime.UtcNow,
            LastContact = DateTime.UtcNow
        };

        // Initialize directory handles
        for (int i = 0; i < session.DirectoryHandles.Length; i++)
        {
            session.DirectoryHandles[i] = new DirectoryHandle();
        }

        if (!_sessions.TryAdd(sessionId, session))
        {
            // Extremely unlikely - SessionId collision
            _logger.LogError("Failed to add session 0x{SessionId:X4} - collision?", sessionId);
            throw new InvalidOperationException($"Session ID collision: 0x{sessionId:X4}");
        }

        _logger.LogInformation("Created session 0x{SessionId:X4} for user={User} ({UserId}), IP={IP}, root={Root}",
            sessionId, username, userId, clientIP, rootPath);

        return session;
    }

    /// <summary>
    /// Find session by session ID.
    /// Returns null if not found.
    /// </summary>
    public TnfsSession? FindSession(ushort sessionId)
    {
        return _sessions.TryGetValue(sessionId, out var session) ? session : null;
    }

    /// <summary>
    /// Find session and validate it belongs to the specified IP address.
    /// Returns null if not found or IP doesn't match.
    /// </summary>
    public TnfsSession? FindSessionAndValidateIP(ushort sessionId, IPAddress clientIP)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return null;

        if (!session.ClientIP.Equals(clientIP))
        {
            _logger.LogWarning("Session 0x{SessionId:X4} IP mismatch: expected {Expected}, got {Actual}",
                sessionId, session.ClientIP, clientIP);
            return null;
        }

        return session;
    }

    /// <summary>
    /// Update session last contact timestamp.
    /// </summary>
    public void TouchSession(ushort sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.LastContact = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Remove session and clean up resources.
    /// </summary>
    public bool RemoveSession(ushort sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            // Clean up session resources
            session.CloseAllFiles();
            session.CloseAllDirectories();

            _logger.LogInformation("Removed session 0x{SessionId:X4}", sessionId);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Remove all sessions for a TCP connection that has closed.
    /// </summary>
    public void RemoveSessionsForTcpConnection(int tcpConnectionId)
    {
        var sessionsToRemove = _sessions.Values
            .Where(s => s.TcpConnectionId == tcpConnectionId)
            .Select(s => s.SessionId)
            .ToList();

        foreach (var sessionId in sessionsToRemove)
        {
            _logger.LogInformation("Removing session 0x{SessionId:X4} due to TCP disconnect", sessionId);
            RemoveSession(sessionId);
        }
    }

    /// <summary>
    /// Periodic cleanup of expired sessions.
    /// </summary>
    private void CleanupExpiredSessions(object? state)
    {
        try
        {
            var expiredSessions = _sessions.Values
                .Where(s => s.IsExpired(_sessionTimeout) && s.TcpConnectionId == 0)  // Only UDP sessions timeout
                .Select(s => s.SessionId)
                .ToList();

            foreach (var sessionId in expiredSessions)
            {
                _logger.LogInformation("Session 0x{SessionId:X4} expired (timeout={Timeout}s)",
                    sessionId, _sessionTimeout.TotalSeconds);
                RemoveSession(sessionId);
            }

            if (expiredSessions.Count > 0)
            {
                _logger.LogInformation("Cleaned up {Count} expired sessions", expiredSessions.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during session cleanup");
        }
    }

    /// <summary>
    /// Generate unique session ID.
    /// Uses cryptographic random for security.
    /// </summary>
    private ushort GenerateSessionId()
    {
        const int maxTries = 256;

        for (int i = 0; i < maxTries; i++)
        {
            ushort sessionId = (ushort)RandomNumberGenerator.GetInt32(1, ushort.MaxValue);

            if (!_sessions.ContainsKey(sessionId))
                return sessionId;
        }

        throw new InvalidOperationException($"Failed to generate unique session ID after {maxTries} attempts");
    }

    /// <summary>
    /// Snapshot of all currently active sessions, for Management API reporting. A snapshot, not a
    /// live view — callers must not assume sessions in the returned list are still active by the
    /// time they read it.
    /// </summary>
    public IReadOnlyList<TnfsSession> GetAllSessions()
    {
        TnfsSession[] sessions = _sessions.Values.ToArray();
        var result = new List<TnfsSession>(sessions.Length);
        for (int i = 0; i < sessions.Length; i++)
        {
            result.Add(sessions[i]);
        }
        return result;
    }

    /// <summary>
    /// Get statistics about active sessions.
    /// </summary>
    public SessionStatistics GetStatistics()
    {
        var sessions = _sessions.Values.ToList();

        return new SessionStatistics
        {
            TotalSessions = sessions.Count,
            UdpSessions = sessions.Count(s => s.TcpConnectionId == 0),
            TcpSessions = sessions.Count(s => s.TcpConnectionId != 0),
            UniqueIPs = sessions.Select(s => s.ClientIP).Distinct().Count(),
            OldestSessionAge = sessions.Any()
                ? DateTime.UtcNow - sessions.Min(s => s.LastContact)
                : TimeSpan.Zero
        };
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _cleanupTimer?.Dispose();

        // Clean up all sessions
        foreach (var sessionId in _sessions.Keys.ToList())
        {
            RemoveSession(sessionId);
        }

        _disposed = true;
    }
}

/// <summary>
/// Session statistics for monitoring.
/// </summary>
public record SessionStatistics
{
    public int TotalSessions { get; init; }
    public int UdpSessions { get; init; }
    public int TcpSessions { get; init; }
    public int UniqueIPs { get; init; }
    public TimeSpan OldestSessionAge { get; init; }
}
