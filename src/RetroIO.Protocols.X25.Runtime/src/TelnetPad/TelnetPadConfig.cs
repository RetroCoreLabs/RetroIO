//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Net;

namespace RetroIO.Protocols.X25.Runtime
{
    /// <summary>
    /// Settings of the telnet PAD: a TCP port where an ordinary terminal program (telnet, PuTTY, ...)
    /// gets an X.28 PAD of its own. Off by default.
    /// </summary>
    /// <remarks>
    /// The section is optional in a JSON configuration file. A file without it loads as before and the
    /// telnet PAD stays off.
    /// </remarks>
    public class TelnetPadConfig
    {
        /// <summary>
        /// The default TCP port.
        /// </summary>
        public const int DefaultPort = 2323;

        /// <summary>
        /// The default bind address: the loopback interface, so that only programs on this machine can
        /// connect until the person chooses otherwise.
        /// </summary>
        public const string DefaultBindAddress = "127.0.0.1";

        /// <summary>
        /// The default limit for simultaneous telnet sessions.
        /// </summary>
        public const int DefaultMaxSessions = 8;

        /// <summary>
        /// The largest session limit that is accepted.
        /// </summary>
        public const int MaxSessionsLimit = 256;

        /// <summary>
        /// True to listen for telnet connections. Default false.
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// The TCP port to listen on, 1 to 65535. Default 2323.
        /// </summary>
        public int Port { get; set; } = DefaultPort;

        /// <summary>
        /// The IP address to listen on. Default <c>127.0.0.1</c> (this machine only). Use <c>0.0.0.0</c> to
        /// accept connections from other machines; the telnet PAD has no login.
        /// </summary>
        public string BindAddress { get; set; } = DefaultBindAddress;

        /// <summary>
        /// The most telnet sessions open at once. Further connections are told so and closed. Default 8.
        /// </summary>
        public int MaxSessions { get; set; } = DefaultMaxSessions;

        /// <summary>
        /// Checks the settings.
        /// </summary>
        /// <returns>
        /// Null when the settings are usable, otherwise a sentence saying what is wrong.
        /// </returns>
        public string? Validate()
        {
            if (Port < 1 || Port > 65535)
            {
                return "Telnet PAD port must be between 1 and 65535 (it is " + Port + ").";
            }

            if (MaxSessions < 1 || MaxSessions > MaxSessionsLimit)
            {
                return "Telnet PAD maximum sessions must be between 1 and " + MaxSessionsLimit + " (it is " + MaxSessions + ").";
            }

            if (!TryParseBindAddress(BindAddress, out _))
            {
                return "Telnet PAD bind address '" + BindAddress + "' is not an IP address (for example 127.0.0.1 or 0.0.0.0).";
            }

            return null;
        }

        /// <summary>
        /// Turns a bind address text into an IP address. The word <c>localhost</c> means the loopback address.
        /// </summary>
        /// <param name="text">
        /// The address text.
        /// </param>
        /// <param name="address">
        /// The parsed address.
        /// </param>
        /// <returns>
        /// True when the text is usable.
        /// </returns>
        public static bool TryParseBindAddress(string? text, out IPAddress address)
        {
            address = IPAddress.Loopback;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string trimmed = text.Trim();
            if (string.Equals(trimmed, "localhost", System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (IPAddress.TryParse(trimmed, out IPAddress? parsed))
            {
                address = parsed;
                return true;
            }

            return false;
        }
    }
}
