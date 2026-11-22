//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Telnet
{
    /// <summary>
    /// Telnet command bytes. Every value is from the command table in RFC 854 ("TELNET COMMAND
    /// STRUCTURE"), except <see cref="EndOfRecord"/>, which is from RFC 885.
    /// </summary>
    public static class TelnetCommand
    {
        /// <summary>
        /// End of record, 239. RFC 885: "The code for EOR is 239 (decimal)". TN3270 ends every
        /// 3270 record with IAC EOR (RFC 1576 section 3).
        /// </summary>
        public const byte EndOfRecord = 239;

        /// <summary>
        /// End of subnegotiation parameters, 240 (RFC 854).
        /// </summary>
        public const byte SubnegotiationEnd = 240;

        /// <summary>
        /// No operation, 241 (RFC 854).
        /// </summary>
        public const byte NoOperation = 241;

        /// <summary>
        /// Data Mark, 242: the data stream part of a Synch (RFC 854).
        /// </summary>
        public const byte DataMark = 242;

        /// <summary>
        /// Break, 243 (RFC 854). TN3270 sends it for the 3270 Attn key in plain TN3270.
        /// </summary>
        public const byte Break = 243;

        /// <summary>
        /// Interrupt Process, 244 (RFC 854). TN3270E sends it for the Attn key.
        /// </summary>
        public const byte InterruptProcess = 244;

        /// <summary>
        /// Abort Output, 245 (RFC 854). TN3270E sends it for Sys Req when SYSREQ was agreed.
        /// </summary>
        public const byte AbortOutput = 245;

        /// <summary>
        /// Are You There, 246 (RFC 854).
        /// </summary>
        public const byte AreYouThere = 246;

        /// <summary>
        /// Erase Character, 247 (RFC 854).
        /// </summary>
        public const byte EraseCharacter = 247;

        /// <summary>
        /// Erase Line, 248 (RFC 854).
        /// </summary>
        public const byte EraseLine = 248;

        /// <summary>
        /// Go Ahead, 249 (RFC 854).
        /// </summary>
        public const byte GoAhead = 249;

        /// <summary>
        /// Start of subnegotiation, 250 (RFC 854).
        /// </summary>
        public const byte SubnegotiationBegin = 250;

        /// <summary>
        /// WILL option, 251 (RFC 854): the sender wants to, or agrees to, perform the option.
        /// </summary>
        public const byte Will = 251;

        /// <summary>
        /// WON'T option, 252 (RFC 854): the sender refuses, or stops, performing the option.
        /// </summary>
        public const byte Wont = 252;

        /// <summary>
        /// DO option, 253 (RFC 854): the sender asks the other side to perform the option.
        /// </summary>
        public const byte Do = 253;

        /// <summary>
        /// DON'T option, 254 (RFC 854): the sender demands the other side stops performing it.
        /// </summary>
        public const byte Dont = 254;

        /// <summary>
        /// Interpret As Command, 255 (RFC 854). A data byte of 255 is sent as IAC IAC.
        /// </summary>
        public const byte Iac = 255;
    }
}
