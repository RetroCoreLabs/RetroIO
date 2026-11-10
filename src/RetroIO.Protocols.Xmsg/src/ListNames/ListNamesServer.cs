//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.ListNames
{
    /// <summary>
    /// Server side of the stateless XROUT <c>XSGNI</c> "get next name" service: answers one step of
    /// a SINTRAN LIST-NAMES walk from a name table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// VERIFIED 2026-11-10 from a complete LIST-NAMES walk, D102 asking D100 on the COSMOS Ethernet
    /// hub. Every reply has:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>the SINTRAN nodes swapped, Flags 2 <c>0x0100</c>;</description></item>
    ///   <item><description>sub-header frame flags <c>0x86</c>, role <c>0x60</c>, XMCSM <c>0x0100</c>;</description></item>
    ///   <item><description>BOTH the destination and the source endpoint set to the ASKER's system and port (the request's source);</description></item>
    ///   <item><description>Flags 1 taken from the ANSWERING node's own outgoing sequence, not copied from the request. D100 answered request 0x0012 with 0x0011, 0x0013 with 0x0012, and so on - the two counters run side by side and never match. So <see cref="Handle"/> takes the reply's Flags 1 from the caller and has no default.</description></item>
    /// </list>
    /// <para>
    /// The request is answered whatever its role byte: D102's request carried <c>0x84</c> and
    /// D100's own request to a gateway carried <c>0xC4</c>, with the same body.
    /// </para>
    /// <para>
    /// The server keeps no state. A repeated request (a retransmit) is answered with the same row.
    /// The secure ACK of the request is NOT built here; the node sends it as for any datagram.
    /// </para>
    /// </remarks>
    public sealed class ListNamesServer
    {
        /// <summary>
        /// The composite XMCSM-and-body-word of a request, <see cref="XmcsmService.XsgniRequest"/>.
        /// </summary>
        public const uint XmcsmXsgniRequest = (uint)XmcsmService.XsgniRequest;

        // OBSERVED on every reply of the 2026-11-10 walk; overridable on BuildResponse.
        private const byte DefaultResponseFrameFlags = 0x86;
        private const byte DefaultResponseRole = 0x60;
        private const ushort DefaultResponseFlags2 = 0x0100;

        /// <summary>
        /// Reads the serial and the lower bound from an <c>XSGNI</c> request datagram.
        /// </summary>
        /// <param name="request">
        /// The request datagram.
        /// </param>
        /// <param name="serial">
        /// When this method returns <c>true</c>, the request's XROUT serial byte.
        /// </param>
        /// <param name="fromMagic">
        /// When this method returns <c>true</c>, the lower bound asked for.
        /// </param>
        /// <returns>
        /// <c>true</c> when the datagram is an <c>XSGNI</c> request; otherwise <c>false</c>.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="request"/> is null.
        /// </exception>
        public static bool TryReadRequest(XmsgFrame request, out byte serial, out uint fromMagic)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            serial = 0;
            fromMagic = 0;
            if (request.SubHeader == null || request.SubHeader.Xmcsm != XsgniWire.Xmcsm)
            {
                return false;
            }

            return XsgniWire.TryReadRequestBody(XsgniWire.ExtractBody(request), out serial, out fromMagic);
        }

        /// <summary>
        /// Builds the reply datagram for a request and one row (or the end of the table).
        /// </summary>
        /// <param name="request">
        /// The request being answered; gives the node swap, the asker's system and port, and the
        /// serial byte to echo.
        /// </param>
        /// <param name="entry">
        /// The row to report, or <c>null</c> for the end-of-table reply.
        /// </param>
        /// <param name="flags1">
        /// The reply's Flags 1: the ANSWERING node's own next outgoing sequence number.
        /// </param>
        /// <param name="flags2">
        /// The SINTRAN Flags 2 word; defaults to the observed <c>0x0100</c>.
        /// </param>
        /// <param name="frameFlags">
        /// The sub-header frame-flags byte; defaults to the observed <c>0x86</c>.
        /// </param>
        /// <param name="role">
        /// The sub-header role byte; defaults to the observed <c>0x60</c>.
        /// </param>
        /// <returns>
        /// A new array holding the whole reply datagram from byte 0.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="request"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="request"/> is not an <c>XSGNI</c> request.
        /// </exception>
        public byte[] BuildResponse(
            XmsgFrame request,
            NameTableEntry? entry,
            ushort flags1,
            ushort flags2 = DefaultResponseFlags2,
            byte frameFlags = DefaultResponseFrameFlags,
            byte role = DefaultResponseRole)
        {
            if (!TryReadRequest(request, out byte serial, out uint _))
            {
                throw new ArgumentException("Not an XSGNI (get next name) request.", nameof(request));
            }

            SintranHeader header = new SintranHeader();
            header.Marker1 = SintranHeader.Marker1Value;
            header.Marker2 = SintranHeader.Marker2Normal;
            header.PacketType = 0x00;
            header.Subtype = SintranPacketSubtype.Data;
            header.DestinationNode = request.Header.SourceNode;
            header.SourceNode = request.Header.DestinationNode;
            header.Flags1 = flags1;
            header.Flags2 = flags2;

            // VERIFIED (2026-11-10 walk): both endpoints are the asker's - 00 66 06B2 | 00 66 06B2.
            // The same shape the XSGSY reply has.
            ushort askerSystem = request.SubHeader!.SourceSystem;
            ushort askerPort = request.SubHeader.SourcePort;

            XmsgSubHeader sub = new XmsgSubHeader();
            sub.FrameFlags = frameFlags;
            sub.Role = role;
            sub.DestinationSystem = askerSystem;
            sub.DestinationPort = askerPort;
            sub.SourceSystem = askerSystem;
            sub.SourcePort = askerPort;

            return XsgniWire.BuildDatagram(header, sub, XsgniWire.BuildReplyBody(serial, entry));
        }

        /// <summary>
        /// Answers an <c>XSGNI</c> request from a name table.
        /// </summary>
        /// <param name="request">
        /// The request datagram.
        /// </param>
        /// <param name="table">
        /// The name table to consult.
        /// </param>
        /// <param name="flags1">
        /// The reply's Flags 1: the ANSWERING node's own next outgoing sequence number.
        /// </param>
        /// <returns>
        /// A new array holding the reply datagram: the first row at or after the asked-for magic
        /// number, or the end-of-table reply when there is none.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="request"/> or <paramref name="table"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="request"/> is not an <c>XSGNI</c> request.
        /// </exception>
        public byte[] Handle(XmsgFrame request, INameTable table, ushort flags1)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (table == null)
            {
                throw new ArgumentNullException(nameof(table));
            }

            if (!TryReadRequest(request, out byte _, out uint fromMagic))
            {
                throw new ArgumentException("Not an XSGNI (get next name) request.", nameof(request));
            }

            NameTableEntry? reply = null;
            if (table.TryLookupFrom(fromMagic, out NameTableEntry found))
            {
                reply = found;
            }

            return BuildResponse(request, reply, flags1);
        }
    }
}
