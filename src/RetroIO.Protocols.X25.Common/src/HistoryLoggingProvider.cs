//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace RetroIO.Protocols.X25.Common
{
    /// <summary>
    /// Logging provider that captures all log messages in a circular buffer for GUI display
    /// </summary>
    public class HistoryLoggingProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> _logHistory = new();
        private readonly int _maxEntries;
        private readonly object _lock = new();

        /// <summary>
        /// Occurs after an entry is added to the history.
        /// </summary>
        public event Action<LogEntry>? LogEntryAdded;

        /// <summary>
        /// Creates a provider that keeps a limited number of log entries.
        /// </summary>
        /// <param name="maxEntries">
        /// The number of entries to keep. When it is exceeded, the oldest entries are dropped.
        /// </param>
        public HistoryLoggingProvider(int maxEntries = 1000)
        {
            _maxEntries = maxEntries;
        }

        /// <summary>
        /// Creates a logger for a category that writes into this provider.
        /// </summary>
        /// <param name="categoryName">
        /// The category name stored with each entry.
        /// </param>
        /// <returns>
        /// A new logger.
        /// </returns>
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName)
        {
            return new HistoryLogger(categoryName, this);
        }

        /// <summary>
        /// Does nothing. The provider holds no resources.
        /// </summary>
        public void Dispose()
        {
            // Nothing to dispose
        }

        internal void AddLogEntry(LogEntry entry)
        {
            _logHistory.Enqueue(entry);

            // Maintain max entries limit
            while (_logHistory.Count > _maxEntries)
            {
                _logHistory.TryDequeue(out _);
            }

            LogEntryAdded?.Invoke(entry);
        }

        /// <summary>
        /// Returns the most recent entries.
        /// </summary>
        /// <param name="count">
        /// The largest number of entries to return.
        /// </param>
        /// <returns>
        /// A new list with at most <paramref name="count"/> entries, oldest first.
        /// </returns>
        public IReadOnlyList<LogEntry> GetRecentLogs(int count = 1000)
        {
            var allEntries = new List<LogEntry>(_logHistory);
            int skipCount = Math.Max(0, allEntries.Count - count);
            var result = new List<LogEntry>(Math.Min(count, allEntries.Count));
            for (int i = skipCount; i < allEntries.Count; i++)
            {
                result.Add(allEntries[i]);
            }
            return result;
        }

        /// <summary>
        /// Returns a copy of all stored entries.
        /// </summary>
        /// <returns>
        /// A new list with the entries, oldest first.
        /// </returns>
        public IReadOnlyList<LogEntry> GetAllLogs()
        {
            return new List<LogEntry>(_logHistory);
        }

        /// <summary>
        /// Removes all stored entries.
        /// </summary>
        public void ClearHistory()
        {
            lock (_lock)
            {
                while (_logHistory.TryDequeue(out _))
                {
                    // Clear all entries
                }
            }
        }

        /// <summary>
        /// Returns a copy of all stored entries.
        /// </summary>
        /// <returns>
        /// A new list with the entries, oldest first.
        /// </returns>
        public List<LogEntry> GetLogEntries()
        {
            return new List<LogEntry>(_logHistory);
        }
    }

    /// <summary>
    /// Logger implementation that writes to the history provider
    /// </summary>
    internal class HistoryLogger : Microsoft.Extensions.Logging.ILogger
    {
        private readonly string _categoryName;
        private readonly HistoryLoggingProvider _provider;

        public HistoryLogger(string categoryName, HistoryLoggingProvider provider)
        {
            _categoryName = categoryName;
            _provider = provider;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null; // Scopes not supported
        }

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel)
        {
            return true; // Capture all log levels
        }

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var message = formatter(state, exception);
            if (exception != null)
            {
                message += Environment.NewLine + exception.ToString();
            }

            var entry = new LogEntry
            {
                Timestamp = DateTime.UtcNow,
                Level = MapLogLevel(logLevel),
                Category = _categoryName,
                Message = message
            };

            _provider.AddLogEntry(entry);
        }

        private static Common.LogLevel MapLogLevel(Microsoft.Extensions.Logging.LogLevel logLevel)
        {
            return logLevel switch
            {
                Microsoft.Extensions.Logging.LogLevel.Debug => Common.LogLevel.Debug,
                Microsoft.Extensions.Logging.LogLevel.Information => Common.LogLevel.Info,
                Microsoft.Extensions.Logging.LogLevel.Warning => Common.LogLevel.Warning,
                Microsoft.Extensions.Logging.LogLevel.Error => Common.LogLevel.Error,
                Microsoft.Extensions.Logging.LogLevel.Critical => Common.LogLevel.Error,
                _ => Common.LogLevel.Info
            };
        }
    }
}