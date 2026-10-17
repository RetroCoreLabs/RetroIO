//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace RetroIO.Protocols.X25.Services
{
    /// <summary>
    /// Manages named timers with thread-safe operations.
    /// Extracted from ProtocolRuntime to reduce God class complexity.
    /// </summary>
    public class TimerManager : IDisposable
    {
        private readonly ILogger _logger;
        private readonly Dictionary<string, Timer> _timers = new();
        private readonly object _timerLock = new();
        private bool _disposed;

        public TimerManager(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Start a timer with callback. Replaces existing timer with same ID.
        /// </summary>
        public void StartTimer(string timerId, TimeSpan duration, Action callback)
        {
            if (string.IsNullOrEmpty(timerId))
                throw new ArgumentException("Timer ID cannot be null or empty", nameof(timerId));

            if (callback == null)
                throw new ArgumentNullException(nameof(callback));

            if (_disposed)
                return;

            lock (_timerLock)
            {
                // Stop existing timer if running
                if (_timers.TryGetValue(timerId, out var existingTimer))
                {
                    existingTimer.Dispose();
                    _timers.Remove(timerId);
                }

                // Create new timer (one-shot, not repeating)
                var timer = new Timer(_ =>
                {
                    try
                    {
                        _logger.LogDebug("[Timer] Timer {TimerId} expired, executing callback", timerId);
                        callback();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError("[Timer] Error in timer {TimerId} callback: {Message}", timerId, ex.Message);
                    }
                }, null, duration, Timeout.InfiniteTimeSpan);

                _timers[timerId] = timer;
                _logger.LogDebug("[Timer] Started timer {TimerId} with duration {Duration}", timerId, duration);
            }
        }

        /// <summary>
        /// Stop a timer by ID.
        /// </summary>
        public void StopTimer(string timerId)
        {
            if (string.IsNullOrEmpty(timerId))
                throw new ArgumentException("Timer ID cannot be null or empty", nameof(timerId));

            if (_disposed)
                return;

            lock (_timerLock)
            {
                if (_timers.TryGetValue(timerId, out var timer))
                {
                    timer.Dispose();
                    _timers.Remove(timerId);
                    _logger.LogDebug("[Timer] Stopped timer {TimerId}", timerId);
                }
            }
        }

        /// <summary>
        /// Check if timer is currently running.
        /// </summary>
        public bool IsTimerRunning(string timerId)
        {
            if (string.IsNullOrEmpty(timerId))
                return false;

            if (_disposed)
                return false;

            lock (_timerLock)
            {
                return _timers.ContainsKey(timerId);
            }
        }

        /// <summary>
        /// Get count of running timers.
        /// </summary>
        public int RunningTimerCount
        {
            get
            {
                lock (_timerLock)
                {
                    return _timers.Count;
                }
            }
        }

        /// <summary>
        /// Stop all running timers.
        /// </summary>
        public void StopAllTimers()
        {
            lock (_timerLock)
            {
                foreach (var timer in _timers.Values)
                {
                    timer?.Dispose();
                }
                _timers.Clear();
                _logger.LogDebug("[Timer] Stopped all timers");
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            lock (_timerLock)
            {
                foreach (var timer in _timers.Values)
                {
                    timer?.Dispose();
                }
                _timers.Clear();
            }

            GC.SuppressFinalize(this);
        }
    }
}
