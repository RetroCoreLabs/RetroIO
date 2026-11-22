//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Telnet
{
    /// <summary>
    /// Telnet option codes this library names. Any other option byte is still carried and
    /// negotiated; these are just the ones callers refer to by name.
    /// </summary>
    public static class TelnetOption
    {
        /// <summary>
        /// BINARY TRANSMISSION, option 0 (RFC 856). TN3270 needs it in both directions.
        /// </summary>
        public const byte Binary = 0;

        /// <summary>
        /// ECHO, option 1 (RFC 857, not kept in the spec folder).
        /// </summary>
        public const byte Echo = 1;

        /// <summary>
        /// SUPPRESS-GO-AHEAD, option 3 (RFC 858, not kept in the spec folder).
        /// </summary>
        public const byte SuppressGoAhead = 3;

        /// <summary>
        /// TERMINAL-TYPE, option 24 (RFC 1091).
        /// </summary>
        public const byte TerminalType = 24;

        /// <summary>
        /// END-OF-RECORD, option 25 (RFC 885). TN3270 needs it in both directions.
        /// </summary>
        public const byte EndOfRecord = 25;

        /// <summary>
        /// TN3270E, option 40 (RFC 2355 section 3).
        /// </summary>
        public const byte Tn3270E = 40;

        /// <summary>
        /// TERMINAL-TYPE subnegotiation code IS, 0 (RFC 1091): "here is my terminal type".
        /// </summary>
        public const byte TerminalTypeIs = 0;

        /// <summary>
        /// TERMINAL-TYPE subnegotiation code SEND, 1 (RFC 1091): "send me your terminal type".
        /// </summary>
        public const byte TerminalTypeSend = 1;
    }
}
