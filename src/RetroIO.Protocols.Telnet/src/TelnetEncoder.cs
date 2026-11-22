//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Buffers;

namespace RetroIO.Protocols.Telnet
{
    /// <summary>
    /// Builds the bytes to send: escaped data, commands, negotiation and subnegotiation.
    /// Everything is written into an <see cref="IBufferWriter{T}"/>, so the caller decides where the
    /// bytes go and nothing is allocated per call.
    /// </summary>
    public static class TelnetEncoder
    {
        /// <summary>
        /// Writes data with every 255 byte doubled to IAC IAC (RFC 854).
        /// </summary>
        /// <param name="output">
        /// Where the bytes go.
        /// </param>
        /// <param name="data">
        /// The data to send.
        /// </param>
        public static void WriteData(IBufferWriter<byte> output, ReadOnlySpan<byte> data)
        {
            ArgumentNullException.ThrowIfNull(output);

            // Worst case every byte is 255 and doubles.
            Span<byte> span = output.GetSpan(data.Length * 2);
            int n = 0;
            for (int i = 0; i < data.Length; i++)
            {
                byte b = data[i];
                span[n++] = b;
                if (b == TelnetCommand.Iac)
                {
                    span[n++] = TelnetCommand.Iac;
                }
            }

            output.Advance(n);
        }

        /// <summary>
        /// Writes IAC and one command byte, for example IAC EOR or IAC BRK.
        /// </summary>
        /// <param name="output">
        /// Where the bytes go.
        /// </param>
        /// <param name="command">
        /// The command byte, from <see cref="TelnetCommand"/>.
        /// </param>
        public static void WriteCommand(IBufferWriter<byte> output, byte command)
        {
            ArgumentNullException.ThrowIfNull(output);
            Span<byte> span = output.GetSpan(2);
            span[0] = TelnetCommand.Iac;
            span[1] = command;
            output.Advance(2);
        }

        /// <summary>
        /// Writes IAC verb option, for example IAC WILL TERMINAL-TYPE.
        /// </summary>
        /// <param name="output">
        /// Where the bytes go.
        /// </param>
        /// <param name="verb">
        /// WILL, WONT, DO or DONT.
        /// </param>
        /// <param name="option">
        /// The option code.
        /// </param>
        public static void WriteNegotiation(IBufferWriter<byte> output, byte verb, byte option)
        {
            ArgumentNullException.ThrowIfNull(output);
            if (verb < TelnetCommand.Will || verb > TelnetCommand.Dont)
            {
                throw new ArgumentOutOfRangeException(nameof(verb), verb, "Must be WILL, WONT, DO or DONT.");
            }

            Span<byte> span = output.GetSpan(3);
            span[0] = TelnetCommand.Iac;
            span[1] = verb;
            span[2] = option;
            output.Advance(3);
        }

        /// <summary>
        /// Writes IAC SB option parameters IAC SE, doubling any 255 inside the parameters (RFC 855).
        /// </summary>
        /// <param name="output">
        /// Where the bytes go.
        /// </param>
        /// <param name="option">
        /// The option code.
        /// </param>
        /// <param name="parameters">
        /// The bytes between the option code and IAC SE.
        /// </param>
        public static void WriteSubnegotiation(IBufferWriter<byte> output, byte option, ReadOnlySpan<byte> parameters)
        {
            ArgumentNullException.ThrowIfNull(output);
            Span<byte> head = output.GetSpan(3);
            head[0] = TelnetCommand.Iac;
            head[1] = TelnetCommand.SubnegotiationBegin;
            head[2] = option;
            output.Advance(3);
            WriteData(output, parameters);
            WriteCommand(output, TelnetCommand.SubnegotiationEnd);
        }
    }
}
