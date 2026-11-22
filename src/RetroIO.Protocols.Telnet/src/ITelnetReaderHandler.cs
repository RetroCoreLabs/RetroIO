//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Telnet
{
    /// <summary>
    /// Receives what <see cref="TelnetReader"/> finds in the byte stream, in the order it was
    /// received. Every span is only valid during the call; copy it to keep it.
    /// </summary>
    public interface ITelnetReaderHandler
    {
        /// <summary>
        /// Plain data. A received IAC IAC has already been turned back into one 255 byte.
        /// One run of data may arrive in several calls; the split carries no meaning.
        /// </summary>
        /// <param name="data">
        /// The data bytes.
        /// </param>
        void OnData(ReadOnlySpan<byte> data);

        /// <summary>
        /// IAC EOR was received: the data before it ends one record (RFC 885).
        /// </summary>
        void OnEndOfRecord();

        /// <summary>
        /// IAC WILL / WONT / DO / DONT with its option byte.
        /// </summary>
        /// <param name="verb">
        /// One of <see cref="TelnetCommand.Will"/>, <see cref="TelnetCommand.Wont"/>,
        /// <see cref="TelnetCommand.Do"/>, <see cref="TelnetCommand.Dont"/>.
        /// </param>
        /// <param name="option">
        /// The option code.
        /// </param>
        void OnNegotiation(byte verb, byte option);

        /// <summary>
        /// A complete IAC SB option ... IAC SE. Doubled IAC bytes inside it are already undoubled.
        /// </summary>
        /// <param name="option">
        /// The option code, the first byte after SB.
        /// </param>
        /// <param name="parameters">
        /// The bytes between the option code and IAC SE.
        /// </param>
        void OnSubnegotiation(byte option, ReadOnlySpan<byte> parameters);

        /// <summary>
        /// Any other two-byte command: NOP, Data Mark, Break, IP, AO, AYT, EC, EL, GA, or a
        /// command byte RFC 854 does not define.
        /// </summary>
        /// <param name="command">
        /// The byte after IAC.
        /// </param>
        void OnCommand(byte command);
    }
}
