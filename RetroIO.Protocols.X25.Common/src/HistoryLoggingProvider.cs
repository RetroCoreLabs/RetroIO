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

        public event Action<LogEntry>? LogEntryAdded;

        public HistoryLoggingProvider(int maxEntries = 1000)
        {
            _maxEntries = maxEntries;
        }

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName)
        {
            return new HistoryLogger(categoryName, this);
        }

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

        public IReadOnlyList<LogEntry> GetAllLogs()
        {
            return new List<LogEntry>(_logHistory);
        }

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