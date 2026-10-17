//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.Xmsg.SubProtocol
{
    /// <summary>
    /// A decoded ROUTING (protocol <c>0xDE</c>) short-command frame: a one-byte command
    /// followed by any command-specific data.
    /// </summary>
    /// <remarks>
    /// This models the routing control frames handled by hdlc_tcp.lua
    /// <c>dissect_routing</c> - proxy terminal-parameter negotiation, routing-table
    /// propagation, and connection-step ACKs. Full XSGSY routing-table replies travel as
    /// XROUT letters and are decoded through <see cref="XmsgFrame.Body"/> instead. This is
    /// a read-only view; byte identity is guaranteed by the owning frame's raw bytes.
    /// </remarks>
    public sealed class RoutingCommandInfo
    {
        private static readonly Dictionary<byte, string> CommandNames = BuildCommandTable();

        /// <summary>
        /// Initialises a routing-command view.
        /// </summary>
        /// <param name="command">
        /// The routing command byte.
        /// </param>
        /// <param name="data">
        /// Any bytes following the command byte.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="data"/> is null.
        /// </exception>
        public RoutingCommandInfo(byte command, byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            Command = command;
            Data = data;
        }

        /// <summary>
        /// Gets the routing command byte.
        /// </summary>
        public byte Command { get; }

        /// <summary>
        /// Gets the mnemonic name of <see cref="Command"/>, or <c>0xNN</c> when unknown.
        /// </summary>
        public string CommandName
        {
            get
            {
                if (CommandNames.TryGetValue(Command, out string? name))
                {
                    return name;
                }

                return "0x" + Command.ToString("X2");
            }
        }

        /// <summary>
        /// Gets the bytes following the command byte.
        /// </summary>
        public byte[] Data { get; }

        /// <summary>
        /// Decodes a routing short-command frame from a payload span.
        /// </summary>
        /// <param name="payload">
        /// The bytes following the SINTRAN header for a routing command frame; must
        /// contain at least the command byte.
        /// </param>
        /// <returns>
        /// The decoded <see cref="RoutingCommandInfo"/>.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="payload"/> is empty.
        /// </exception>
        public static RoutingCommandInfo Parse(ReadOnlySpan<byte> payload)
        {
            if (payload.Length < 1)
            {
                throw new ArgumentException("A routing command needs at least one byte.", nameof(payload));
            }

            byte command = payload[0];
            byte[] data = new byte[payload.Length - 1];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = payload[i + 1];
            }

            return new RoutingCommandInfo(command, data);
        }

        /// <summary>
        /// Builds the routing command-to-name table.
        /// </summary>
        /// <returns>
        /// A dictionary mapping each known routing command byte to its label.
        /// </returns>
        private static Dictionary<byte, string> BuildCommandTable()
        {
            // Verbatim from hdlc_tcp.lua vs_routing_cmd.
            Dictionary<byte, string> table = new Dictionary<byte, string>();
            table.Add(0x00, "TermParam-Step4");
            table.Add(0x01, "TermParam-Step3");
            table.Add(0x02, "TermParam-Step2");
            table.Add(0x03, "TermParam-Step1");
            table.Add(0x04, "TermParam-Step0");
            table.Add(0x05, "Propagate-Request");
            table.Add(0x07, "Bootstrap-Request");
            table.Add(0x08, "Sync-Request");
            table.Add(0x0B, "Propagate-Response");
            table.Add(0x0C, "RouteInfo-Exchange");
            table.Add(0x0D, "Bootstrap-Response");
            table.Add(0x0E, "Sync-Response");
            table.Add(0x11, "PAD-Resp");
            table.Add(0x12, "PAD-Resp");
            table.Add(0x13, "ConnStep-ACK(0x09)");
            table.Add(0x14, "ConnStep-ACK(0x0A)");
            table.Add(0x15, "ConnStep-ACK(0x0B)");
            table.Add(0x16, "ConnStep-ACK(0x0C)");
            table.Add(0x17, "ConnStep-ACK(0x0D)");
            table.Add(0x18, "PAD-Resp");
            table.Add(0x19, "PAD-Resp");
            table.Add(0x1A, "ConnStep-ACK(0x10)");
            table.Add(0x1B, "ConnStep-ACK(0x11)");
            table.Add(0x1C, "ConnStep-ACK(0x12)");
            table.Add(0x1D, "ConnStep-ACK(0x13)");
            table.Add(0x1E, "ConnStep-ACK(0x14)");
            return table;
        }
    }
}
