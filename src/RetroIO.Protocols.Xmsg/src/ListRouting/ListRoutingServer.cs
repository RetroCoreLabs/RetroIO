//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Packet;

namespace RetroIO.Protocols.Xmsg.ListRouting
{
    /// <summary>
    /// Server side of the stateless XROUT <c>XSGSY</c> list-routing service: answers a
    /// "get routing info for system N" request from a routing table.
    /// </summary>
    /// <remarks>
    /// The reply is a subtype-<c>0x0E</c> data frame carrying four parameter blocks
    /// describing one routing-table entry. See XMSG-PROTOCOL.md section 9.1.
    /// </remarks>
    public sealed class ListRoutingServer
    {
        /// <summary>
        /// XMCSM control/service word identifying an XSGSY request.
        /// </summary>
        public const uint XmcsmXsgsyRequest = ListRoutingClient.XmcsmXsgsyRequest;

        /// <summary>
        /// XMCSM control/service word identifying an XSGSY reply.
        /// </summary>
        public const uint XmcsmXsgsyReply = ListRoutingClient.XmcsmXsgsyReply;

        // OBSERVED-from-capture default framing bytes for a freshly built response.
        // NOT documented requirements; the values seen on the wire in the canonical
        // capture (responder 100 -> 102). Every one is overridable via a BuildResponse
        // parameter so a caller can reproduce any capture byte-identically.
        private const byte DefaultResponseFrameFlags = 0x86; // INFERRED: frame-flags byte observed on the response.
        private const byte DefaultResponseRole = 0x60;       // INFERRED: responder role byte observed on the response.
        private const ushort DefaultResponseFlags2 = 0x0100; // INFERRED: SINTRAN flags2 observed on the response.

        /// <summary>
        /// Builds the information field of an XSGSY reply for a given request and entry.
        /// </summary>
        /// <param name="request">
        /// The request being answered; supplies the node swap and the asker's
        /// system/port for the stateless-RPC echo.
        /// </param>
        /// <param name="entry">
        /// The routing-table entry to encode into the four reply parameters.
        /// </param>
        /// <param name="flags1">
        /// The SINTRAN Flags 1 word (datagram sequence).
        /// </param>
        /// <param name="flags2">
        /// The SINTRAN Flags 2 frame-class word; defaults to the observed response value.
        /// </param>
        /// <param name="frameFlags">
        /// The XMSG sub-header frame-flags byte; defaults to the observed response value.
        /// </param>
        /// <param name="role">
        /// The XMSG sub-header role byte; defaults to the observed responder value.
        /// </param>
        /// <param name="controlService">
        /// The XMCSM control/service word; defaults to <see cref="XmcsmXsgsyReply"/>.
        /// </param>
        /// <returns>
        /// A new array holding the reply information field.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="request"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="request"/> carries no XMSG sub-header.
        /// </exception>
        public byte[] BuildResponse(
            XmsgFrame request,
            RoutingTableEntry entry,
            ushort flags1,
            ushort flags2 = DefaultResponseFlags2,
            byte frameFlags = DefaultResponseFrameFlags,
            byte role = DefaultResponseRole,
            uint controlService = XmcsmXsgsyReply)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (request.SubHeader == null)
            {
                throw new ArgumentException("XSGSY request has no XMSG sub-header.", nameof(request));
            }

            SintranHeader header = new SintranHeader();
            header.Marker1 = SintranHeader.Marker1Value;
            header.Marker2 = SintranHeader.Marker2Normal;
            header.PacketType = 0x00;
            header.Subtype = SintranPacketSubtype.Data;
            // Swap dest/src node: the reply goes back to the asker.
            header.DestinationNode = request.Header.SourceNode;
            header.SourceNode = request.Header.DestinationNode;
            header.Flags1 = flags1;
            header.Flags2 = flags2;
            // WORD 6 IS COMPUTED - CORRECTED 2026-08-06. See ListRoutingClient.BuildRequest for the
            // full reasoning; measured here as 0xDC66 where the carved checksum is 0x8F15
            // (ListRoutingHeaderChecksumTests). The protocolId and counter parameters are GONE
            // (2026-08-06) - they only ever wrote these two bytes.
            XmsgEnvelope.StampChecksum(header);

            // INFERRED stateless-RPC echo (XMSG-PROTOCOL.md section 9.1 anomaly note):
            // observed in the captures, NOT stated in the ND docs. In a list-routing
            // reply the responder fills BOTH the destination AND the source endpoint
            // (XMDSY/XMDPT and XMSSY/XMSPT) with the ORIGINATOR's (asker's) system:port,
            // echoing the asker's identity as a transaction id rather than allocating
            // its own port. The asker's identity is the request's XMSSY/XMSPT.
            ushort askerSystem = request.SubHeader.SourceSystem;
            ushort askerPort = request.SubHeader.SourcePort;

            XmsgSubHeader sub = new XmsgSubHeader();
            sub.FrameFlags = frameFlags;
            sub.Role = role;
            sub.DestinationSystem = askerSystem;
            sub.DestinationPort = askerPort;
            sub.SourceSystem = askerSystem;
            sub.SourcePort = askerPort;

            // VERIFIED (captures + COSMOS Guide XSGSY): four parameter blocks, in order:
            //   p1 = system number, p2 = connection-type enum, p3 = extra info,
            //   p4 = network info (low byte = #hops, high byte = #WANs).
            Span<byte> trailer = stackalloc byte[4 * XsgsyWire.ParamBlockSize];
            XsgsyWire.WriteParamBlock(trailer.Slice(0, XsgsyWire.ParamBlockSize), 1, entry.System);
            XsgsyWire.WriteParamBlock(trailer.Slice(XsgsyWire.ParamBlockSize, XsgsyWire.ParamBlockSize), 2, (ushort)entry.ConnectionType);
            XsgsyWire.WriteParamBlock(trailer.Slice(2 * XsgsyWire.ParamBlockSize, XsgsyWire.ParamBlockSize), 3, entry.ExtraInfo);
            XsgsyWire.WriteParamBlock(trailer.Slice(3 * XsgsyWire.ParamBlockSize, XsgsyWire.ParamBlockSize), 4, entry.NetworkInfo);

            return XsgsyWire.BuildInfoField(header, sub, controlService, trailer);
        }

        /// <summary>
        /// Answers an XSGSY request by looking the queried system up in a routing table.
        /// </summary>
        /// <param name="request">
        /// The request frame to answer.
        /// </param>
        /// <param name="table">
        /// The routing table to consult.
        /// </param>
        /// <param name="flags1">
        /// The SINTRAN Flags 1 word for the reply.
        /// </param>
        /// <param name="flags2">
        /// The SINTRAN Flags 2 frame-class word; defaults to the observed response value.
        /// </param>
        /// <param name="frameFlags">
        /// The XMSG sub-header frame-flags byte; defaults to the observed response value.
        /// </param>
        /// <param name="role">
        /// The XMSG sub-header role byte; defaults to the observed responder value.
        /// </param>
        /// <returns>
        /// A new array holding the reply information field; when the query has no
        /// matching entry the reply carries system <c>0</c> /
        /// <see cref="XroutConnectionType.Unavailable"/>.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="request"/> or <paramref name="table"/> is null.
        /// </exception>
        public byte[] Handle(
            XmsgFrame request,
            IRoutingTable table,
            ushort flags1 = 0,
            ushort flags2 = DefaultResponseFlags2,
            byte frameFlags = DefaultResponseFrameFlags,
            byte role = DefaultResponseRole)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (table == null)
            {
                throw new ArgumentNullException(nameof(table));
            }

            ListRoutingClient client = new ListRoutingClient();
            ushort querySystem = client.ParseRequestQuery(request);

            RoutingTableEntry entry;
            if (!table.TryLookup(querySystem, out entry))
            {
                // VERIFIED miss convention (XMSG-PROTOCOL.md section 9.1): no entry is
                // reported as system 0 / Unavailable, all other fields zero.
                entry = new RoutingTableEntry(0, XroutConnectionType.Unavailable, 0, 0, 0);
            }

            return BuildResponse(request, entry, flags1, flags2, frameFlags, role, XmcsmXsgsyReply);
        }
    }
}
