using System;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Packet;

namespace RetroIO.Protocols.Xmsg.ListRouting
{
    /// <summary>
    /// Client side of the stateless XROUT <c>XSGSY</c> list-routing service: builds a
    /// "get routing info for system N" request and decodes the reply.
    /// </summary>
    /// <remarks>
    /// The request is a subtype-<c>0x0E</c> data frame carrying a single parameter
    /// block (the system number being queried). See XMSG-PROTOCOL.md section 9.1 and
    /// XMSG-API.md section 4.3.
    /// </remarks>
    public sealed class ListRoutingClient
    {
        /// <summary>
        /// XMCSM control/service word identifying an XSGSY request.
        /// </summary>
        /// <remarks>
        /// VERIFIED (XMSG-PROTOCOL.md section 9.1): the low byte <c>0x4B</c> (75) is the
        /// XROUT service code for <c>XSGSY</c> "get routing info". Value from <see cref="XmcsmService.XsgsyRequest"/>.
        /// </remarks>
        public const uint XmcsmXsgsyRequest = (uint)XmcsmService.XsgsyRequest;

        /// <summary>
        /// XMCSM control/service word identifying an XSGSY reply.
        /// </summary>
        public const uint XmcsmXsgsyReply = (uint)XmcsmService.XsgsyReply;

        // OBSERVED-from-capture default framing bytes for a freshly built request.
        // These are NOT documented requirements; they are the values seen on the wire
        // in the canonical capture (asker 102 -> 100). Every one is overridable via a
        // BuildRequest parameter so a caller can reproduce any capture byte-identically.
        private const byte DefaultRequestFrameFlags = 0x86; // INFERRED: frame-flags byte observed on the request.
        private const byte DefaultRequestRole = 0x84;       // INFERRED: role low-nibble 4 = asker.
        private const ushort DefaultRequestFlags2 = 0x0100; // INFERRED: SINTRAN flags2 observed on the request.

        /// <summary>
        /// Builds the information field of an XSGSY request that queries routing info
        /// for a given system.
        /// </summary>
        /// <param name="querySystem">
        /// The system number to look up.
        /// </param>
        /// <param name="destinationNode">
        /// The SINTRAN destination node number.
        /// </param>
        /// <param name="sourceNode">
        /// The SINTRAN source (asker) node number.
        /// </param>
        /// <param name="destinationSystem">
        /// The XMDSY destination system number.
        /// </param>
        /// <param name="destinationPort">
        /// The XMDPT destination port.
        /// </param>
        /// <param name="sourceSystem">
        /// The XMSSY source (asker) system number.
        /// </param>
        /// <param name="sourcePort">
        /// The XMSPT source (asker) port.
        /// </param>
        /// <param name="flags1">
        /// The SINTRAN Flags 1 word (datagram sequence).
        /// </param>
        /// <param name="flags2">
        /// The SINTRAN Flags 2 frame-class word; defaults to the observed request value.
        /// </param>
        /// <param name="frameFlags">
        /// The XMSG sub-header frame-flags byte; defaults to the observed request value.
        /// </param>
        /// <param name="role">
        /// The XMSG sub-header role byte; defaults to the observed asker value.
        /// </param>
        /// <param name="controlService">
        /// The XMCSM control/service word; defaults to <see cref="XmcsmXsgsyRequest"/>.
        /// </param>
        /// <returns>
        /// A new array holding the request information field.
        /// </returns>
        public byte[] BuildRequest(
            ushort querySystem,
            ushort destinationNode,
            ushort sourceNode,
            ushort destinationSystem,
            ushort destinationPort,
            ushort sourceSystem,
            ushort sourcePort,
            ushort flags1,
            ushort flags2 = DefaultRequestFlags2,
            byte frameFlags = DefaultRequestFrameFlags,
            byte role = DefaultRequestRole,
            uint controlService = XmcsmXsgsyRequest)
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
            // WORD 6 IS COMPUTED - CORRECTED 2026-08-06.
            //
            // This used to be header.ProtocolId = protocolId and header.Counter = counter. Those two
            // properties are compatibility views over the checksum's HIGH and LOW bytes, so between
            // them they FABRICATED word 6 from the caller's arguments - measured as 0xDB55 where the
            // carved checksum is 0x8F16 (ListRoutingHeaderChecksumTests).
            //
            // Word 6 is a ones-complement checksum over words 0-5, confirmed on 3595/3595 captured
            // frames, and a wrong one kills D100 with XMSG ERROR CODE 24.
            //
            // The protocolId and counter PARAMETERS are gone (2026-08-06). Their doc even carried the
            // wrong model - "the XMSG sub-header per-direction counter byte" - when offset 13 is the
            // checksum's low byte and belongs to the SINTRAN header, not the sub-header.
            //
            // Removing them loses nothing: ListRoutingTests still rebuilds a CAPTURED request byte
            // for byte, which means the computed checksum equals the real frame's word 6. On that
            // capture (nodes 100 and 102) the old arguments 0xDB / 0xE9 happened to BE the checksum,
            // which is precisely how small node numbers hide this class of bug.
            XmsgEnvelope.StampChecksum(header);

            XmsgSubHeader sub = new XmsgSubHeader();
            sub.FrameFlags = frameFlags;
            sub.Role = role;
            sub.DestinationSystem = destinationSystem;
            sub.DestinationPort = destinationPort;
            sub.SourceSystem = sourceSystem;
            sub.SourcePort = sourcePort;

            // VERIFIED (captures): a request carries exactly one parameter block,
            // param#1 = the system number being queried.
            Span<byte> trailer = stackalloc byte[XsgsyWire.ParamBlockSize];
            XsgsyWire.WriteParamBlock(trailer, 1, querySystem);

            return XsgsyWire.BuildInfoField(header, sub, controlService, trailer);
        }

        /// <summary>
        /// Reads the queried system number from a decoded XSGSY request frame.
        /// </summary>
        /// <param name="request">
        /// The request frame.
        /// </param>
        /// <returns>
        /// The system number carried in parameter 1.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="request"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when the frame carries no parameter 1.
        /// </exception>
        public ushort ParseRequestQuery(XmsgFrame request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            byte[] trailer = XsgsyWire.ExtractTrailer(request);
            if (!XsgsyWire.TryReadParam(trailer, 1, out ushort system))
            {
                throw new ArgumentException("XSGSY request has no parameter 1 (system number).", nameof(request));
            }

            return system;
        }

        /// <summary>
        /// Decodes an XSGSY reply frame into a routing-table entry.
        /// </summary>
        /// <param name="response">
        /// The reply frame.
        /// </param>
        /// <returns>
        /// The decoded <see cref="RoutingTableEntry"/>; a system number of <c>0</c>
        /// with <see cref="XroutConnectionType.Unavailable"/> means no entry.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="response"/> is null.
        /// </exception>
        public RoutingTableEntry ParseResponse(XmsgFrame response)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            byte[] trailer = XsgsyWire.ExtractTrailer(response);

            // VERIFIED param meanings (COSMOS Programmer Guide ND-60.164, XSGSY;
            // XMSG-PROTOCOL.md section 9.1):
            //   p1 = system number, p2 = connection-type enum, p3 = extra info,
            //   p4 = network info (low byte = #hops, high byte = #WANs).
            XsgsyWire.TryReadParam(trailer, 1, out ushort system);
            XsgsyWire.TryReadParam(trailer, 2, out ushort connectionType);
            XsgsyWire.TryReadParam(trailer, 3, out ushort extraInfo);
            XsgsyWire.TryReadParam(trailer, 4, out ushort networkInfo);

            byte hops = (byte)(networkInfo & 0xFF);
            byte wans = (byte)((networkInfo >> 8) & 0xFF);
            return new RoutingTableEntry(system, (XroutConnectionType)connectionType, extraInfo, hops, wans);
        }
    }
}
