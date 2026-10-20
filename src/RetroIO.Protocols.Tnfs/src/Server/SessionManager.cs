//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
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
    private readonly int _maxSessions;
    private readonly Timer _cleanupTimer;
    private bool _disposed;

    /// <summary>
    /// Creates the manager and starts a timer that runs the session cleanup every minute.
    /// </summary>
    /// <param name="logger">
    /// The logger to write to.
    /// </param>
    /// <param name="sessionTimeout">
    /// How long a session may be idle before it expires. Null uses the SESSION_TIMEOUT constant (600 seconds).
    /// </param>
    /// <param name="maxSessionsPerIP">
    /// Limit on the number of sessions from one IP address.
    /// </param>
    /// <param name="maxSessions">
    /// Limit on the number of sessions open at one time.
    /// </param>
    public SessionManager(ILogger<SessionManager> logger, TimeSpan? sessionTimeout = null, int maxSessionsPerIP = TnfsConstants.MAX_SESSIONS_PER_IP, int maxSessions = TnfsConstants.MAX_SESSIONS)
    {
        _logger = logger;
        _sessionTimeout = sessionTimeout ?? TimeSpan.FromSeconds(TnfsConstants.SESSION_TIMEOUT);
        _maxSessionsPerIP = maxSessionsPerIP;
        _maxSessions = maxSessions;

        // Start cleanup timer (runs every minute)
        _cleanupTimer = new Timer(CleanupExpiredSessions, null,
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// Gets the most sessions open at one time.
    /// </summary>
    public int MaxSessions => _maxSessions;

    /// <summary>
    /// Gets the most sessions one IP address may hold.
    /// </summary>
    public int MaxSessionsPerIP => _maxSessionsPerIP;

    /// <summary>
    /// Get current session count.
    /// </summary>
    public int SessionCount => _sessions.Count;

    /// <summary>
    /// Create new session for client with optional user binding.
    /// Enforces the per-address limit (the oldest session of that address is recycled, as tnfsd does) and the
    /// total limit (a new session is refused with <see cref="TnfsSessionLimitException"/>).
    /// </summary>
    /// <exception cref="TnfsSessionLimitException">
    /// The session table is full and no expired session could be removed.
    /// </exception>
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

        // Total limit (tnfsd MAX_SESSIONS). tnfsd drops expired sessions while it scans for a free slot, so
        // do the same before refusing. The per-address recycle above frees a slot first when it applies.
        if (_sessions.Count >= _maxSessions)
        {
            CleanupExpiredSessions(null);
        }

        if (_sessions.Count >= _maxSessions)
        {
            _logger.LogWarning("Refusing a new session for IP {IP}: {Max} sessions are open", clientIP, _maxSessions);
            throw new TnfsSessionLimitException($"The session table is full ({_maxSessions} sessions)");
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

    /// <summary>
    /// Stops the cleanup timer and removes all sessions.
    /// </summary>
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
/// Thrown when no new session can be created because the session table is full.
/// </summary>
public sealed class TnfsSessionLimitException : InvalidOperationException
{
    /// <summary>
    /// Creates the exception.
    /// </summary>
    /// <param name="message">
    /// The message.
    /// </param>
    public TnfsSessionLimitException(string message) : base(message)
    {
    }
}

/// <summary>
/// Session statistics for monitoring.
/// </summary>
public record SessionStatistics
{
    /// <summary>
    /// Gets the number of sessions.
    /// </summary>
    public int TotalSessions { get; init; }
    /// <summary>
    /// Gets the number of sessions without a TCP connection ID.
    /// </summary>
    public int UdpSessions { get; init; }
    /// <summary>
    /// Gets the number of sessions with a TCP connection ID.
    /// </summary>
    public int TcpSessions { get; init; }
    /// <summary>
    /// Gets the number of distinct client IP addresses.
    /// </summary>
    public int UniqueIPs { get; init; }
    /// <summary>
    /// Gets the time since the oldest last contact of any session, or zero when there are no sessions.
    /// </summary>
    public TimeSpan OldestSessionAge { get; init; }
}
