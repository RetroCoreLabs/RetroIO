//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace RetroIO.Security.Implementation.Auditing
{
    /// <summary>
    /// High-performance security event emitter with batching
    /// Buffers events in memory and flushes periodically
    /// </summary>
    public sealed class SecurityEventEmitter : IDisposable
    {
        private readonly FileAuditLogger _logger;
        private readonly ConcurrentQueue<SecurityAuditEntry> _buffer;
        private readonly Timer _flushTimer;
        private readonly int _batchSize;
        private readonly SemaphoreSlim _flushLock = new(1, 1);
        private bool _disposed;

        public SecurityEventEmitter(
            FileAuditLogger logger,
            int batchSize = 1000,
            TimeSpan? flushInterval = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _buffer = new ConcurrentQueue<SecurityAuditEntry>();
            _batchSize = batchSize;

            var interval = flushInterval ?? TimeSpan.FromSeconds(5);
            _flushTimer = new Timer(
                callback: async _ => await FlushAsync(),
                state: null,
                dueTime: interval,
                period: interval);
        }

        /// <summary>
        /// Emit security event (async, non-blocking)
        /// </summary>
        public async ValueTask EmitAsync(SecurityAuditEntry entry)
        {
            if (entry == null)
                throw new ArgumentNullException(nameof(entry));

            _buffer.Enqueue(entry);

            // Auto-flush when buffer is full
            if (_buffer.Count >= _batchSize)
            {
                await FlushAsync();
            }
        }

        /// <summary>
        /// Emit connection event
        /// </summary>
        public async ValueTask EmitConnectionEventAsync(
            string ipAddress,
            Guid userId,
            bool accepted,
            string reason)
        {
            var entry = new SecurityAuditEntry
            {
                EventType = accepted ? SecurityEventType.ConnectionAccepted : SecurityEventType.ConnectionRejected,
                Severity = accepted ? AuditSeverity.Info : AuditSeverity.Warning,
                PrincipalId = userId,
                PrincipalName = userId == Guid.Empty ? "UNKNOWN" : null,
                IpAddress = ipAddress,
                Message = $"Connection {(accepted ? "accepted" : "rejected")}: {reason}",
                AccessGranted = accepted,
                DenialReason = accepted ? null : reason
            };

            await EmitAsync(entry);
        }

        /// <summary>
        /// Emit firewall event
        /// </summary>
        public async ValueTask EmitFirewallEventAsync(
            string ipAddress,
            bool allowed,
            string reason)
        {
            var entry = new SecurityAuditEntry
            {
                EventType = allowed ? SecurityEventType.FirewallAllowed : SecurityEventType.FirewallBlocked,
                Severity = allowed ? AuditSeverity.Info : AuditSeverity.Warning,
                PrincipalId = Guid.Empty,
                PrincipalName = "UNKNOWN",
                IpAddress = ipAddress,
                Message = $"Firewall {(allowed ? "allowed" : "blocked")} {ipAddress}: {reason}",
                AccessGranted = allowed,
                DenialReason = allowed ? null : reason
            };

            await EmitAsync(entry);
        }

        /// <summary>
        /// Emit authentication event
        /// </summary>
        public async ValueTask EmitAuthEventAsync(
            Guid userId,
            string? username,
            bool success,
            string method,
            string ipAddress)
        {
            var entry = new SecurityAuditEntry
            {
                EventType = success ? SecurityEventType.AuthenticationSuccess : SecurityEventType.AuthenticationFailure,
                Severity = success ? AuditSeverity.Info : AuditSeverity.Warning,
                PrincipalId = userId,
                PrincipalName = username,
                IpAddress = ipAddress,
                Message = $"Authentication {(success ? "succeeded" : "failed")} via {method}",
                AccessGranted = success,
                Metadata = new System.Collections.Generic.Dictionary<string, object>
                {
                    { "AuthMethod", method }
                }
            };

            await EmitAsync(entry);
        }

        /// <summary>
        /// Flush buffered events to disk
        /// </summary>
        public async Task FlushAsync()
        {
            if (_buffer.IsEmpty)
                return;

            await _flushLock.WaitAsync();
            try
            {
                // Drain buffer
                int count = 0;
                while (_buffer.TryDequeue(out var entry) && count < _batchSize)
                {
                    await _logger.LogSecurityEventAsync(entry);
                    count++;
                }
            }
            finally
            {
                _flushLock.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _flushTimer?.Dispose();

            // Final flush
            FlushAsync().GetAwaiter().GetResult();

            _flushLock?.Dispose();
            _disposed = true;
        }
    }
}
