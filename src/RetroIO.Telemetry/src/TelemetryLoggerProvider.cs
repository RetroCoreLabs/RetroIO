//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace RetroIO.Telemetry;

/// <summary>
/// A logging provider that keeps every line a part logs in that part's <see cref="RetroTelemetry"/>
/// log, so the log sits next to the metrics without changing each log call in the part.
/// </summary>
/// <remarks>
/// <para>
/// A line belongs to a part when its logger category (the full name of the class that logs) starts
/// with the part's namespace, for example <c>RetroIO.Protocols.Tnfs</c>. The provider only keeps the
/// line; the host's other providers still write it to the console and files as before, so nothing is
/// written twice.
/// </para>
/// <para>
/// Which levels are kept follows the host's logging filter, as for every other provider.
/// </para>
/// </remarks>
public sealed class TelemetryLoggerProvider : ILoggerProvider
{
    private readonly string _categoryPrefix;
    private readonly Func<RetroTelemetry> _telemetry;

    /// <summary>
    /// Initialises a provider for one part.
    /// </summary>
    /// <param name="categoryPrefix">The namespace of the part's classes.</param>
    /// <param name="telemetry">The part's telemetry, whose log the lines go into.</param>
    public TelemetryLoggerProvider(string categoryPrefix, RetroTelemetry telemetry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryPrefix);
        _categoryPrefix = categoryPrefix;
        ArgumentNullException.ThrowIfNull(telemetry);
        _telemetry = () => telemetry;
    }

    /// <summary>
    /// Initialises a provider for one part whose telemetry is looked up only when the first line is
    /// kept.
    /// </summary>
    /// <param name="categoryPrefix">The namespace of the part's classes, or one class's full name.</param>
    /// <param name="telemetry">Gives the part's telemetry; called on each kept line, so it must be cheap
    /// (a singleton lookup).</param>
    /// <remarks>
    /// Needed when the part itself takes a logger: building the part needs the logger factory, the
    /// factory needs this provider, and a provider that wanted the part at once would need the part
    /// first. That loop hung RetroServer's start for ever with DriveWire on (2026-11-23).
    /// </remarks>
    public TelemetryLoggerProvider(string categoryPrefix, Func<RetroTelemetry> telemetry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryPrefix);
        _categoryPrefix = categoryPrefix;
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName)
    {
        // Category exactly the prefix, or the prefix followed by a dot: "RetroIO.Protocols.Tnfs"
        // must not take "RetroIO.Protocols.TnfsX".
        bool ours = categoryName != null
            && categoryName.StartsWith(_categoryPrefix, StringComparison.Ordinal)
            && (categoryName.Length == _categoryPrefix.Length || categoryName[_categoryPrefix.Length] == '.');
        return ours ? new KeepingLogger(_telemetry) : NotOurs.Instance;
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }

    /// <summary>
    /// Keeps each line in the telemetry log.
    /// </summary>
    private sealed class KeepingLogger : ILogger
    {
        private readonly Func<RetroTelemetry> _telemetry;

        public KeepingLogger(Func<RetroTelemetry> telemetry)
        {
            _telemetry = telemetry;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel != LogLevel.None;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            string message = formatter(state, exception);

            // The structured fields of a message template become the record's attributes; the
            // template itself ("{OriginalFormat}") is left out, the text already holds it filled in.
            int count = 0;
            IReadOnlyList<KeyValuePair<string, object?>>? fields = state as IReadOnlyList<KeyValuePair<string, object?>>;
            if (fields != null)
            {
                for (int i = 0; i < fields.Count; i++)
                {
                    if (fields[i].Key != "{OriginalFormat}")
                    {
                        count++;
                    }
                }
            }

            KeyValuePair<string, string>[] attributes = new KeyValuePair<string, string>[count + (exception != null ? 1 : 0)];
            int n = 0;
            if (fields != null)
            {
                for (int i = 0; i < fields.Count; i++)
                {
                    if (fields[i].Key != "{OriginalFormat}")
                    {
                        attributes[n++] = new KeyValuePair<string, string>(fields[i].Key, fields[i].Value?.ToString() ?? string.Empty);
                    }
                }
            }

            if (exception != null)
            {
                // The OpenTelemetry attribute name, as RetroTelemetry.Log uses.
                attributes[n] = new KeyValuePair<string, string>("exception.type", exception.GetType().FullName ?? exception.GetType().Name);
            }

            _telemetry().Keep(logLevel, message, attributes);
        }
    }

    /// <summary>
    /// The logger for categories of other parts: it keeps nothing.
    /// </summary>
    private sealed class NotOurs : ILogger
    {
        public static readonly NotOurs Instance = new NotOurs();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return false;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
        }
    }
}
