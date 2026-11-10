//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.Xmsg.ListNames
{
    /// <summary>
    /// Client side of SINTRAN's XMSG-COMMAND LIST-NAMES: walks a remote XROUT's name table one
    /// XROUT <c>XSGNI</c> ("get next name") request at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// VERIFIED 2026-11-10 from a complete walk, D102 asking D100 on the COSMOS Ethernet hub:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>every request goes to XROUT, port 0 of the chosen system, from one asker port (0x06B2 in the capture) that stays the same for the whole walk;</description></item>
    ///   <item><description>sub-header frame flags <c>0x86</c>, role <c>0x84</c>, XMCSM <c>0x0100</c>, Flags 2 <c>0x0100</c>;</description></item>
    ///   <item><description>Flags 1 is the asker's own counter, one step per request;</description></item>
    ///   <item><description>the first request asks from magic 0, each later one from the previous reply's magic plus one, until the end-of-table reply.</description></item>
    /// </list>
    /// </remarks>
    public sealed class ListNamesClient
    {
        /// <summary>
        /// The composite XMCSM-and-body-word of a request, <see cref="XmcsmService.XsgniRequest"/>.
        /// </summary>
        public const uint XmcsmXsgniRequest = (uint)XmcsmService.XsgniRequest;

        // OBSERVED on every request of the 2026-11-10 walk; overridable on BuildRequest.
        private const byte DefaultRequestFrameFlags = 0x86;
        private const byte DefaultRequestRole = 0x84;
        private const ushort DefaultRequestFlags2 = 0x0100;

        /// <summary>
        /// The XROUT port every request is addressed to (port 0).
        /// </summary>
        public const ushort XroutPort = 0x0000;

        /// <summary>
        /// Builds one request datagram.
        /// </summary>
        /// <param name="fromMagic">
        /// The lower bound; <c>0</c> starts a walk.
        /// </param>
        /// <param name="destinationNode">
        /// The SINTRAN node the datagram is sent to.
        /// </param>
        /// <param name="sourceNode">
        /// The asker's SINTRAN node.
        /// </param>
        /// <param name="targetSystem">
        /// The system whose XROUT is asked (sub-header destination system; the port is always 0).
        /// </param>
        /// <param name="sourceSystem">
        /// The asker's system.
        /// </param>
        /// <param name="sourcePort">
        /// The asker's port.
        /// </param>
        /// <param name="flags1">
        /// The asker's Flags 1 for this request.
        /// </param>
        /// <param name="serial">
        /// The XROUT serial byte; defaults to the <c>0x01</c> LIST-NAMES uses.
        /// </param>
        /// <param name="flags2">
        /// The SINTRAN Flags 2 word; defaults to the observed <c>0x0100</c>.
        /// </param>
        /// <param name="frameFlags">
        /// The sub-header frame-flags byte; defaults to the observed <c>0x86</c>.
        /// </param>
        /// <param name="role">
        /// The sub-header role byte; defaults to the observed <c>0x84</c>.
        /// </param>
        /// <returns>
        /// A new array holding the whole request datagram from byte 0.
        /// </returns>
        public byte[] BuildRequest(
            uint fromMagic,
            ushort destinationNode,
            ushort sourceNode,
            ushort targetSystem,
            ushort sourceSystem,
            ushort sourcePort,
            ushort flags1,
            byte serial = XsgniWire.DefaultSerial,
            ushort flags2 = DefaultRequestFlags2,
            byte frameFlags = DefaultRequestFrameFlags,
            byte role = DefaultRequestRole)
        {
            SintranHeader header = new SintranHeader();
            header.Marker1 = SintranHeader.Marker1Value;
            header.Marker2 = SintranHeader.Marker2Normal;
            header.PacketType = 0x00;
            header.Subtype = SintranPacketSubtype.Data;
            header.DestinationNode = destinationNode;
            header.SourceNode = sourceNode;
            header.Flags1 = flags1;
            header.Flags2 = flags2;

            XmsgSubHeader sub = new XmsgSubHeader();
            sub.FrameFlags = frameFlags;
            sub.Role = role;
            sub.DestinationSystem = targetSystem;
            sub.DestinationPort = XroutPort;
            sub.SourceSystem = sourceSystem;
            sub.SourcePort = sourcePort;

            return XsgniWire.BuildDatagram(header, sub, XsgniWire.BuildRequestBody(serial, fromMagic));
        }

        /// <summary>
        /// Reads one reply datagram.
        /// </summary>
        /// <param name="response">
        /// The reply datagram.
        /// </param>
        /// <param name="endOfTable">
        /// When this method returns <c>true</c>, whether this is the end-of-table reply.
        /// </param>
        /// <param name="entry">
        /// When this method returns <c>true</c> and <paramref name="endOfTable"/> is <c>false</c>,
        /// the row.
        /// </param>
        /// <returns>
        /// <c>true</c> when the datagram is a well-formed reply; otherwise <c>false</c>.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="response"/> is null.
        /// </exception>
        public bool TryParseResponse(XmsgFrame response, out bool endOfTable, out NameTableEntry entry)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            endOfTable = false;
            entry = default;
            if (response.SubHeader == null || response.SubHeader.Xmcsm != XsgniWire.Xmcsm)
            {
                return false;
            }

            return XsgniWire.TryReadReplyBody(XsgniWire.ExtractBody(response), out endOfTable, out entry);
        }

        /// <summary>
        /// Walks a remote XROUT's whole name table.
        /// </summary>
        /// <param name="exchange">
        /// The link to send over; supplies the asker's Flags 1 numbers.
        /// </param>
        /// <param name="destinationNode">
        /// The SINTRAN node the requests are sent to.
        /// </param>
        /// <param name="sourceNode">
        /// The asker's SINTRAN node.
        /// </param>
        /// <param name="targetSystem">
        /// The system whose XROUT is asked.
        /// </param>
        /// <param name="sourceSystem">
        /// The asker's system.
        /// </param>
        /// <param name="sourcePort">
        /// The asker's port, the same for every request of the walk.
        /// </param>
        /// <param name="timeout">
        /// How long to wait for each reply.
        /// </param>
        /// <returns>
        /// The rows received and how the walk ended. A timeout or a bad reply ends the walk and
        /// keeps the rows received before it.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="exchange"/> is null.
        /// </exception>
        public ListNamesWalkResult Walk(
            IListNamesExchange exchange,
            ushort destinationNode,
            ushort sourceNode,
            ushort targetSystem,
            ushort sourceSystem,
            ushort sourcePort,
            TimeSpan timeout)
        {
            if (exchange == null)
            {
                throw new ArgumentNullException(nameof(exchange));
            }

            List<NameTableEntry> rows = new List<NameTableEntry>();
            uint fromMagic = 0;
            while (true)
            {
                byte[] request = BuildRequest(
                    fromMagic, destinationNode, sourceNode, targetSystem, sourceSystem, sourcePort,
                    exchange.NextFlags1());

                if (!exchange.TryExchange(request, timeout, out XmsgFrame? reply) || reply == null)
                {
                    return new ListNamesWalkResult(ListNamesWalkStatus.TimedOut, rows);
                }

                if (!TryParseResponse(reply, out bool endOfTable, out NameTableEntry row))
                {
                    return new ListNamesWalkResult(ListNamesWalkStatus.BadReply, rows);
                }

                if (endOfTable)
                {
                    return new ListNamesWalkResult(ListNamesWalkStatus.Complete, rows);
                }

                // A row below the bound asked for would send the walk round forever.
                if (row.Magic < fromMagic)
                {
                    return new ListNamesWalkResult(ListNamesWalkStatus.BadReply, rows);
                }

                rows.Add(row);

                if (row.Magic == uint.MaxValue)
                {
                    // Nothing can follow the largest magic number; the next bound would wrap to 0.
                    return new ListNamesWalkResult(ListNamesWalkStatus.Complete, rows);
                }

                fromMagic = row.Magic + 1;
            }
        }
    }
}
