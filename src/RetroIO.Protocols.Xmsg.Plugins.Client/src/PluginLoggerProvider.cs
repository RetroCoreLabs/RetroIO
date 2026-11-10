//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using Microsoft.Extensions.Logging;

namespace RetroIO.Protocols.Xmsg.Plugins.Client
{
    /// <summary>
    /// Sends a program's ordinary .NET log lines to RetroServer over its connection.
    /// </summary>
    /// <remarks>
    /// Add it to the program's logging, and every <c>logger.LogWarning(...)</c> also shows in
    /// RetroServer's log view, under the category <c>XmsgProgram.&lt;program name&gt;.&lt;category&gt;</c>:
    /// <code>
    /// using ILoggerFactory logs = LoggerFactory.Create(b => b.AddConsole().AddProvider(new PluginLoggerProvider(connection)));
    /// </code>
    /// Lines below the level RetroServer asked for (<see cref="PluginConnection.NodeLogMinimum"/>) are
    /// not sent. The provider never blocks and never throws into the code that logged.
    /// </remarks>
    public sealed class PluginLoggerProvider : ILoggerProvider
    {
        private readonly PluginConnection _connection;

        /// <summary>
        /// Initialises the provider on a connection.
        /// </summary>
        /// <param name="connection">
        /// The connection to RetroServer.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="connection"/> is null.
        /// </exception>
        public PluginLoggerProvider(PluginConnection connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        }

        /// <inheritdoc/>
        public ILogger CreateLogger(string categoryName)
        {
            return new PluginLogger(_connection, categoryName ?? string.Empty);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            // The connection belongs to the program, not to the provider.
        }

        /// <summary>
        /// One category's logger.
        /// </summary>
        private sealed class PluginLogger : ILogger
        {
            private readonly PluginConnection _connection;
            private readonly string _category;

            internal PluginLogger(PluginConnection connection, string category)
            {
                _connection = connection;
                _category = category;
            }

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                // Scopes are not carried over the connection.
                return null;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return logLevel != LogLevel.None
                    && _connection.NodeLogMinimum != LogLevel.None
                    && logLevel >= _connection.NodeLogMinimum;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel) || formatter == null)
                {
                    return;
                }

                _connection.Log(logLevel, _category, formatter(state, exception), exception);
            }
        }
    }
}
