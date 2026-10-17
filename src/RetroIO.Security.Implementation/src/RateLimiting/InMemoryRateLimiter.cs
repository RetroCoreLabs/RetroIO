//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RetroIO.Security.Implementation.RateLimiting
{
    /// <summary>
    /// In-memory rate limiter with JSON persistence
    /// Tracks per-user, per-IP, and server-wide limits
    /// </summary>
    public sealed class InMemoryRateLimiter : IDisposable
    {
        private readonly RateLimitConfig _config;
        private readonly RateLimitState _state;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly Timer _persistenceTimer;

        public InMemoryRateLimiter(RateLimitConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));

            // Restore state from disk on startup
            _state = _config.RestoreStateOnStartup
                ? LoadStateFromDisk() ?? new RateLimitState()
                : new RateLimitState();

            // Start background persistence timer (saves to JSON every minute)
            _persistenceTimer = new Timer(
                callback: _ => SaveStateToDisk(),
                state: null,
                dueTime: _config.PersistenceInterval,
                period: _config.PersistenceInterval);
        }

        public async ValueTask<RateLimitResult> CheckRateLimitAsync(
            Guid userId,
            string ipAddress,
            int bytesToTransfer,
            bool isAdmin,
            CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                DateTime now = DateTime.UtcNow;

                // ============================================================
                // LEVEL 1: Server-Wide Limits
                // ============================================================

                if (!CheckServerLimits(now, bytesToTransfer, out RateLimitResult serverDenial))
                {
                    return serverDenial; // Server limit exceeded (503)
                }

                // ============================================================
                // LEVEL 2: Per-User or Per-IP Limits
                // ============================================================

                bool isAuthenticated = userId != Guid.Empty;

                // Skip limits for admins
                if (isAdmin && _config.SkipLimitsForAdmins)
                {
                    UpdateCounters(userId, ipAddress, now, bytesToTransfer);
                    return RateLimitResult.Allowed();
                }

                // Check per-user limits (authenticated users)
                if (isAuthenticated)
                {
                    if (!CheckUserLimits(userId, now, bytesToTransfer, out RateLimitResult userDenial))
                    {
                        return userDenial; // User limit exceeded (429)
                    }
                }
                // Check per-IP limits (guest users)
                else
                {
                    if (!CheckIpLimits(ipAddress, now, bytesToTransfer, out RateLimitResult ipDenial))
                    {
                        return ipDenial; // IP limit exceeded (429)
                    }
                }

                // All checks passed - update counters and allow
                UpdateCounters(userId, ipAddress, now, bytesToTransfer);
                return RateLimitResult.Allowed();
            }
            finally
            {
                _lock.Release();
            }
        }

        private bool CheckServerLimits(DateTime now, int bytesToTransfer, out RateLimitResult denial)
        {
            // Reset counters if time window expired
            if (now - _state.ServerState.CurrentSecondStart >= TimeSpan.FromSeconds(1))
            {
                _state.ServerState.RequestsThisSecond = 0;
                _state.ServerState.CurrentSecondStart = now;
            }

            if (now - _state.ServerState.CurrentHourStart >= TimeSpan.FromHours(1))
            {
                _state.ServerState.BytesThisHour = 0;
                _state.ServerState.CurrentHourStart = now;
            }

            // Check requests per second limit
            if (_config.ServerRequestsPerSecond.HasValue &&
                _state.ServerState.RequestsThisSecond >= _config.ServerRequestsPerSecond.Value)
            {
                denial = RateLimitResult.Denied(
                    reason: "Server request rate limit exceeded",
                    retryAfter: TimeSpan.FromSeconds(1),
                    statusCode: 503); // Service Unavailable
                return false;
            }

            // Check megabytes per hour limit (convert limit to bytes to avoid integer division truncation)
            if (_config.ServerMegabytesPerHour.HasValue)
            {
                long limitBytes = (long)_config.ServerMegabytesPerHour.Value * 1024 * 1024;
                if (_state.ServerState.BytesThisHour + bytesToTransfer > limitBytes)
                {
                    denial = RateLimitResult.Denied(
                        reason: "Server bandwidth limit exceeded",
                        retryAfter: TimeSpan.FromHours(1) - (now - _state.ServerState.CurrentHourStart),
                        statusCode: 503);
                    return false;
                }
            }

            denial = RateLimitResult.Allowed();
            return true;
        }

        private bool CheckUserLimits(Guid userId, DateTime now, int bytesToTransfer, out RateLimitResult denial)
        {
            // Get or create user state
            if (!_state.UserStates.TryGetValue(userId, out UserRateLimitState? userState))
            {
                userState = new UserRateLimitState { UserId = userId };
                _state.UserStates[userId] = userState;
            }

            // Reset counters if time window expired
            if (now - userState.CurrentSecondStart >= TimeSpan.FromSeconds(1))
            {
                userState.RequestsThisSecond = 0;
                userState.CurrentSecondStart = now;
            }

            if (now - userState.CurrentHourStart >= TimeSpan.FromHours(1))
            {
                userState.BytesThisHour = 0;
                userState.CurrentHourStart = now;
            }

            // Check requests per second limit
            if (_config.PerUserRequestsPerSecond.HasValue &&
                userState.RequestsThisSecond >= _config.PerUserRequestsPerSecond.Value)
            {
                denial = RateLimitResult.Denied(
                    reason: $"User request rate limit exceeded ({_config.PerUserRequestsPerSecond.Value}/sec)",
                    retryAfter: TimeSpan.FromSeconds(1),
                    statusCode: 429);
                return false;
            }

            // Check megabytes per hour limit (convert limit to bytes to avoid integer division truncation)
            if (_config.PerUserMegabytesPerHour.HasValue)
            {
                long limitBytes = (long)_config.PerUserMegabytesPerHour.Value * 1024 * 1024;
                if (userState.BytesThisHour + bytesToTransfer > limitBytes)
                {
                    denial = RateLimitResult.Denied(
                        reason: $"User bandwidth limit exceeded ({_config.PerUserMegabytesPerHour.Value} MB/hour)",
                        retryAfter: TimeSpan.FromHours(1) - (now - userState.CurrentHourStart),
                        statusCode: 429);
                    return false;
                }
            }

            denial = RateLimitResult.Allowed();
            return true;
        }

        private bool CheckIpLimits(string ipAddress, DateTime now, int bytesToTransfer, out RateLimitResult denial)
        {
            // Get or create IP state
            if (!_state.IpStates.TryGetValue(ipAddress, out IpRateLimitState? ipState))
            {
                ipState = new IpRateLimitState { IpAddress = ipAddress };
                _state.IpStates[ipAddress] = ipState;
            }

            // Reset counters if time window expired
            if (now - ipState.CurrentSecondStart >= TimeSpan.FromSeconds(1))
            {
                ipState.RequestsThisSecond = 0;
                ipState.CurrentSecondStart = now;
            }

            if (now - ipState.CurrentHourStart >= TimeSpan.FromHours(1))
            {
                ipState.BytesThisHour = 0;
                ipState.CurrentHourStart = now;
            }

            // Check requests per second limit
            if (_config.PerIpRequestsPerSecond.HasValue &&
                ipState.RequestsThisSecond >= _config.PerIpRequestsPerSecond.Value)
            {
                denial = RateLimitResult.Denied(
                    reason: $"IP request rate limit exceeded ({_config.PerIpRequestsPerSecond.Value}/sec)",
                    retryAfter: TimeSpan.FromSeconds(1),
                    statusCode: 429);
                return false;
            }

            // Check megabytes per hour limit (convert limit to bytes to avoid integer division truncation)
            if (_config.PerIpMegabytesPerHour.HasValue)
            {
                long limitBytes = (long)_config.PerIpMegabytesPerHour.Value * 1024 * 1024;
                if (ipState.BytesThisHour + bytesToTransfer > limitBytes)
                {
                    denial = RateLimitResult.Denied(
                        reason: $"IP bandwidth limit exceeded ({_config.PerIpMegabytesPerHour.Value} MB/hour)",
                        retryAfter: TimeSpan.FromHours(1) - (now - ipState.CurrentHourStart),
                        statusCode: 429);
                    return false;
                }
            }

            denial = RateLimitResult.Allowed();
            return true;
        }

        private void UpdateCounters(Guid userId, string ipAddress, DateTime now, int bytesToTransfer)
        {
            // Update server-wide counters
            _state.ServerState.RequestsThisSecond++;
            _state.ServerState.BytesThisHour += bytesToTransfer;

            // Update per-user counters (if authenticated)
            if (userId != Guid.Empty && _state.UserStates.TryGetValue(userId, out var userState))
            {
                userState.RequestsThisSecond++;
                userState.BytesThisHour += bytesToTransfer;
            }

            // Update per-IP counters (if guest)
            if (userId == Guid.Empty && _state.IpStates.TryGetValue(ipAddress, out var ipState))
            {
                ipState.RequestsThisSecond++;
                ipState.BytesThisHour += bytesToTransfer;
            }
        }

        private void SaveStateToDisk()
        {
            try
            {
                _state.LastSavedAt = DateTime.UtcNow;
                string json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_config.StatePersistencePath, json);
            }
            catch (Exception ex)
            {
                // Log error but don't crash (rate limiting continues in-memory)
                Console.WriteLine($"Failed to save rate limit state: {ex.Message}");
            }
        }

        private RateLimitState? LoadStateFromDisk()
        {
            try
            {
                if (!File.Exists(_config.StatePersistencePath))
                    return null;

                string json = File.ReadAllText(_config.StatePersistencePath);
                return JsonSerializer.Deserialize<RateLimitState>(json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load rate limit state: {ex.Message}");
                return null;
            }
        }

        public void Dispose()
        {
            _persistenceTimer?.Dispose();
            SaveStateToDisk(); // Final save before shutdown
            _lock?.Dispose();
        }
    }
}
