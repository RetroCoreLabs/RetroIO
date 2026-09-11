//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
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

    public InMemoryLogProvider(int maxEntries = 1000)
    {
        _maxEntries = maxEntries;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new InMemoryLogger(categoryName, this);
    }

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

    public IReadOnlyList<LogEntryDto> GetLogEntries()
    {
        return _logEntries.ToArray();
    }

    public void Clear()
    {
        _logEntries.Clear();
    }
}

public class InMemoryLogger : ILogger
{
    private readonly string _categoryName;
    private readonly InMemoryLogProvider _provider;

    public InMemoryLogger(string categoryName, InMemoryLogProvider provider)
    {
        _categoryName = categoryName;
        _provider = provider;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

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
