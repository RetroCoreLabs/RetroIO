//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

// The buffer holds the contract type directly. It used to hold an identical
// private copy, so every entry was translated on the way out for no reason.
using RetroIO.Management.Contracts;

namespace RetroIO.Management.Api.Services;

/// <summary>
/// In-memory log provider that captures log entries for API access
/// </summary>
public class InMemoryLogProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntryDto> _logEntries = new();
    private readonly int _maxEntries;

    /// <summary>
    /// Creates a provider that keeps a limited number of log entries.
    /// </summary>
    /// <param name="maxEntries">
    /// The number of entries to keep. When it is exceeded, the oldest entries are dropped.
    /// </param>
    public InMemoryLogProvider(int maxEntries = 1000)
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
    public ILogger CreateLogger(string categoryName)
    {
        return new InMemoryLogger(categoryName, this);
    }

    /// <summary>
    /// Does nothing. The provider holds no resources.
    /// </summary>
    public void Dispose()
    {
        // No resources to dispose
    }

    internal void AddLogEntry(LogEntryDto entry)
    {
        _logEntries.Enqueue(entry);

        // Trim old entries if needed
        while (_logEntries.Count > _maxEntries)
        {
            _logEntries.TryDequeue(out _);
        }
    }

    /// <summary>
    /// Returns a copy of the stored entries.
    /// </summary>
    /// <returns>
    /// The entries, oldest first.
    /// </returns>
    public IReadOnlyList<LogEntryDto> GetLogEntries()
    {
        return _logEntries.ToArray();
    }

    /// <summary>
    /// Removes all stored entries.
    /// </summary>
    public void Clear()
    {
        _logEntries.Clear();
    }
}

/// <summary>
/// A logger that adds every entry to an InMemoryLogProvider.
/// </summary>
public class InMemoryLogger : ILogger
{
    private readonly string _categoryName;
    private readonly InMemoryLogProvider _provider;

    /// <summary>
    /// Creates a logger.
    /// </summary>
    /// <param name="categoryName">
    /// The category name stored with each entry.
    /// </param>
    /// <param name="provider">
    /// The provider that receives the entries.
    /// </param>
    public InMemoryLogger(string categoryName, InMemoryLogProvider provider)
    {
        _categoryName = categoryName;
        _provider = provider;
    }

    /// <summary>
    /// Does not start a scope.
    /// </summary>
    /// <typeparam name="TState">
    /// The type of the scope state.
    /// </typeparam>
    /// <param name="state">
    /// The scope state, which is ignored.
    /// </param>
    /// <returns>
    /// Always null.
    /// </returns>
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return null;
    }

    /// <summary>
    /// Returns whether the level is enabled, which is true for every level.
    /// </summary>
    /// <param name="logLevel">
    /// The log level.
    /// </param>
    /// <returns>
    /// Always true.
    /// </returns>
    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    /// <summary>
    /// Formats the message and adds an entry with the current UTC time, level name, category, message and exception text to the provider.
    /// </summary>
    /// <typeparam name="TState">
    /// The type of the state.
    /// </typeparam>
    /// <param name="logLevel">
    /// The level of the entry.
    /// </param>
    /// <param name="eventId">
    /// The event id, which is not stored.
    /// </param>
    /// <param name="state">
    /// The state passed to the formatter.
    /// </param>
    /// <param name="exception">
    /// The exception to log, or null.
    /// </param>
    /// <param name="formatter">
    /// The function that builds the message text.
    /// </param>
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var entry = new LogEntryDto
        {
            Timestamp = DateTime.UtcNow,
            Level = logLevel.ToString(),
            Category = _categoryName,
            Message = formatter(state, exception),
            Exception = exception?.ToString()
        };

        _provider.AddLogEntry(entry);
    }
}
