//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using Microsoft.Extensions.Logging;

namespace RetroIO.Protocols.X25.Client
{
    /// <summary>
    /// Shared helpers for the X.25 console tools (echo, pad, traffic, ...): endpoint parsing and console
    /// logger construction, so each tool does not re-implement the same boilerplate.
    /// </summary>
    public static class ToolSupport
    {
        /// <summary>
        /// Parses a <c>host:port</c> specification (split on the last colon so bare hosts and host:port both
        /// work). Missing host keeps <paramref name="defaultHost"/>;
        /// missing/invalid port keeps <paramref name="defaultPort"/>.
        /// </summary>
        /// <param name="spec">
        /// The specification, e.g. "127.0.0.1:2006" or "myhost".
        /// </param>
        /// <param name="defaultHost">
        /// Host used when <paramref name="spec"/> is empty.
        /// </param>
        /// <param name="defaultPort">
        /// Port used when no valid port is present in <paramref name="spec"/>.
        /// </param>
        /// <param name="host">
        /// Receives the parsed host.
        /// </param>
        /// <param name="port">
        /// Receives the parsed port.
        /// </param>
        public static void ParseEndpoint(string spec, string defaultHost, ushort defaultPort, out string host, out ushort port)
        {
            host = defaultHost;
            port = defaultPort;

            if (string.IsNullOrEmpty(spec))
            {
                return;
            }

            int colon = spec.LastIndexOf(':');
            if (colon < 0)
            {
                host = spec;
                return;
            }

            if (colon > 0)
            {
                host = spec.Substring(0, colon);
            }

            string portText = spec.Substring(colon + 1);
            if (ushort.TryParse(portText, out ushort parsed))
            {
                port = parsed;
            }
        }

        /// <summary>
        /// Builds a console logger factory whose minimum level follows the tool's verbosity: 0 =
        /// Information, 1 = Debug, 2 or more = Trace. Follows the <c>-v</c> verbosity convention.
        /// </summary>
        /// <param name="verbosity">
        /// Verbosity level (count of <c>-v</c> flags).
        /// </param>
        /// <returns>
        /// A logger factory the caller owns and should dispose.
        /// </returns>
        public static ILoggerFactory CreateLoggerFactory(int verbosity)
        {
            LogLevel level;
            if (verbosity <= 0)
            {
                level = LogLevel.Information;
            }
            else if (verbosity == 1)
            {
                level = LogLevel.Debug;
            }
            else
            {
                level = LogLevel.Trace;
            }

            return LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(level);
                builder.AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.TimestampFormat = "HH:mm:ss ";
                });
            });
        }
    }
}
