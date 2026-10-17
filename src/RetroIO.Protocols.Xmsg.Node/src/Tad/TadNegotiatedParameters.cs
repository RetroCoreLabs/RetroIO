//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.Node.Tad
{
    /// <summary>
    /// The terminal-session parameters a <see cref="TadSession"/> captures as it observes
    /// the negotiation messages of a connect-to exchange.
    /// </summary>
    /// <remarks>
    /// <para><b>Provenance</b></para>
    /// Every field here is populated from an OBSERVED negotiation message in the captured
    /// connect-to. The field meanings are VERIFIED against
    /// <c>TAD/TAD-Message-Formats.md</c> (the opcode table and per-message layouts), but the
    /// concrete VALUES are OBSERVED (single capture) - for example only terminal type
    /// <c>0x0000</c> and OS/proto version <c>4C 01 04</c> were ever seen. A nullable field
    /// that is still <c>null</c> means that message has not been observed on this session.
    /// </remarks>
    public sealed class TadNegotiatedParameters
    {
        /// <summary>
        /// Gets or sets the terminal type id from a TTYP (<c>0x0D</c>) message, or
        /// <c>null</c> if none has been observed.
        /// </summary>
        /// <remarks>
        /// OBSERVED (single capture): the only value seen is <c>0x0000</c>. VERIFIED layout:
        /// TTYP carries a 16-bit big-endian id (TAD-Message-Formats.md 4.2).
        /// </remarks>
        public ushort? TerminalType { get; set; }

        /// <summary>
        /// Gets or sets the terminal mode flags from a TMOD (<c>0x0C</c>) message, or
        /// <c>null</c> if none has been observed.
        /// </summary>
        /// <remarks>
        /// OBSERVED (single capture): the client sent flags <c>0x08</c>. The bit meanings are now
        /// decoded from the version J NPL source - see <see cref="TerminalModeFlags"/>, under which
        /// <c>0x08</c> reads as "log me out if the carrier drops".
        /// </remarks>
        public byte? TerminalMode { get; set; }

        /// <summary>
        /// Gets the terminal mode as decoded flag bits, or <c>null</c> when no TMOD has been observed.
        /// </summary>
        /// <remarks>
        /// A convenience view over <see cref="TerminalMode"/>; the raw byte stays the stored value
        /// because bits 4-7 have no known meaning and must survive a round trip untouched.
        /// </remarks>
        public TerminalModeFlags? TerminalModeFlagsValue
        {
            get
            {
                if (!TerminalMode.HasValue)
                {
                    return null;
                }

                return (TerminalModeFlags)TerminalMode.Value;
            }
        }

        /// <summary>
        /// Gets or sets the escape character from a DESC (<c>0x0F</c>) message, or
        /// <c>null</c> if none has been observed.
        /// </summary>
        /// <remarks>
        /// OBSERVED (single capture): the client defined escape <c>0x1B</c> (ASCII ESC).
        /// VERIFIED layout: TAD-Message-Formats.md 4.4.
        /// </remarks>
        public byte? EscapeCharacter { get; set; }

        /// <summary>
        /// Gets or sets the echo strategy code from an ECKM (<c>0x03</c>) message, or
        /// <c>null</c> if none has been observed.
        /// </summary>
        /// <remarks>
        /// OBSERVED (single capture): the server sent strategies <c>0x01</c> and <c>0xFF</c>.
        /// VERIFIED layout: TAD-Message-Formats.md 5.2.
        /// </remarks>
        public byte? EchoStrategy { get; set; }

        /// <summary>
        /// Gets or sets the OS/protocol version bytes from an OPSV (<c>0x1F</c>) message,
        /// or <c>null</c> if none has been observed.
        /// </summary>
        /// <remarks>
        /// OBSERVED (single capture): the client sent <c>4C 01 04</c> (OS version
        /// <c>0x4C</c>, sub-version <c>0x01</c>, TAD protocol <c>0x04</c>) and the server
        /// answered <c>4C 00 00</c>. VERIFIED layout: TAD-Message-Formats.md 4.7.
        /// </remarks>
        public byte[]? OsVersion { get; set; }

        /// <summary>
        /// Gets a value indicating whether the core negotiation (terminal type and
        /// OS/protocol version) has been observed.
        /// </summary>
        public bool IsNegotiationComplete
        {
            get { return TerminalType.HasValue && OsVersion != null; }
        }

        /// <summary>
        /// Copies the OS/protocol version bytes into a new array, or returns an empty array
        /// when none have been observed.
        /// </summary>
        /// <returns>
        /// A new array holding the OS/protocol version bytes.
        /// </returns>
        public byte[] GetOsVersionCopy()
        {
            if (OsVersion == null)
            {
                return Array.Empty<byte>();
            }

            byte[] copy = new byte[OsVersion.Length];
            Array.Copy(OsVersion, copy, OsVersion.Length);
            return copy;
        }
    }
}
