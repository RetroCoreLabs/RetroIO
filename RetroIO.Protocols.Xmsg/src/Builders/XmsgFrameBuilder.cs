using System;

using RetroIO.Protocols.Xmsg.Packet;

namespace RetroIO.Protocols.Xmsg
{
    /// <summary>
    /// Fluent builder for a full XMSG wire frame (SINTRAN header, XMSG sub-header,
    /// and optional XROUT body).
    /// </summary>
    /// <remarks>
    /// Assembles the layers described in XMSG-PROTOCOL.md sections 4-5 for a data
    /// message (subtype <see cref="SintranPacketSubtype.Data"/>). Use
    /// <see cref="ReachabilityRequest(ushort, ushort, byte)"/> and related helpers
    /// for the short control frames.
    /// </remarks>
    public sealed class XmsgFrameBuilder
    {
        private readonly SintranHeader _header;
        private readonly XmsgSubHeader _subHeader;
        private XroutMessage? _body;

        /// <summary>
        /// The first word of the message body (wire 28-29) as supplied through
        /// <see cref="WithControlService"/>: the XROUT serial byte and service byte.
        /// </summary>
        private ushort _bodyLeadWord;

        /// <summary>
        /// Initialises a builder for a normal data frame.
        /// </summary>
        public XmsgFrameBuilder()
        {
            _header = new SintranHeader();
            _header.Marker1 = SintranHeader.Marker1Value;
            _header.Marker2 = SintranHeader.Marker2Normal;
            _header.Subtype = SintranPacketSubtype.Data;
            _header.Flags2 = 0x0400; // data-frame class word observed in the corpus
            _header.ProtocolId = SintranProtocolId.Routing;

            _subHeader = new XmsgSubHeader();
        }

        /// <summary>
        /// Sets the destination and source node numbers in the SINTRAN header.
        /// </summary>
        /// <param name="destinationNode">
        /// The destination node number.
        /// </param>
        /// <param name="sourceNode">
        /// The source node number.
        /// </param>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        public XmsgFrameBuilder Between(ushort destinationNode, ushort sourceNode)
        {
            _header.DestinationNode = destinationNode;
            _header.SourceNode = sourceNode;
            return this;
        }

        /// <summary>
        /// Sets the datagram sequence number (Flags 1) of the data frame.
        /// </summary>
        /// <param name="datagramSequence">
        /// The per-direction datagram sequence number.
        /// </param>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        public XmsgFrameBuilder WithDatagramSequence(ushort datagramSequence)
        {
            _header.Flags1 = datagramSequence;
            return this;
        }

        /// <summary>
        /// Sets the sub-protocol selector (Protocol ID).
        /// </summary>
        /// <param name="protocolId">
        /// The sub-protocol carried by the frame.
        /// </param>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        public XmsgFrameBuilder WithProtocol(SintranProtocolId protocolId)
        {
            _header.ProtocolId = protocolId;
            return this;
        }

        /// <summary>
        /// Sets the Flags 2 frame-class word of the SINTRAN header.
        /// </summary>
        /// <param name="flags2">
        /// The frame-class word.
        /// </param>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        public XmsgFrameBuilder WithFlags2(ushort flags2)
        {
            _header.Flags2 = flags2;
            return this;
        }

        /// <summary>
        /// Sets the XMSG sub-header per-direction counter and frame flags.
        /// </summary>
        /// <param name="counter">
        /// The per-direction counter byte.
        /// </param>
        /// <param name="frameFlags">
        /// The frame-flags byte.
        /// </param>
        /// <param name="role">
        /// The role byte (low nibble 4 = asker, 0 = responder).
        /// </param>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        public XmsgFrameBuilder WithSubHeaderControl(byte counter, byte frameFlags, byte role)
        {
            // The "counter" is the SINTRAN header checksum's LOW byte (offset 13), not a
            // sub-header field - see SintranHeader.Checksum. Kept on this signature so callers
            // compile; the header facade routes it to the right place.
            _header.Counter = counter;
            _subHeader.FrameFlags = frameFlags;
            _subHeader.Role = role;
            return this;
        }

        /// <summary>
        /// Sets the XMSG endpoint addressing (XMDSY/XMDPT/XMSSY/XMSPT).
        /// </summary>
        /// <param name="destinationSystem">
        /// The destination system number (XMDSY).
        /// </param>
        /// <param name="destinationPort">
        /// The destination port (XMDPT).
        /// </param>
        /// <param name="sourceSystem">
        /// The source system number (XMSSY).
        /// </param>
        /// <param name="sourcePort">
        /// The source port (XMSPT).
        /// </param>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        public XmsgFrameBuilder WithEndpoints(ushort destinationSystem, ushort destinationPort, ushort sourceSystem, ushort sourcePort)
        {
            _subHeader.DestinationSystem = destinationSystem;
            _subHeader.DestinationPort = destinationPort;
            _subHeader.SourceSystem = sourceSystem;
            _subHeader.SourcePort = sourcePort;
            return this;
        }

        /// <summary>
        /// Sets the XMCSM control/service dispatch word.
        /// </summary>
        /// <param name="controlService">
        /// The control/service word.
        /// </param>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        public XmsgFrameBuilder WithControlService(uint controlService)
        {
            // High half = the real one-word XMCSM at wire 26-27. Low half = the first word of the
            // MESSAGE BODY at wire 28-29, which on a letter is the XROUT serial and service bytes
            // and is therefore already carried by the XroutMessage passed to WithBody.
            _subHeader.Xmcsm = (ushort)(controlService >> 16);
            _bodyLeadWord = (ushort)controlService;
            return this;
        }

        /// <summary>
        /// Attaches an XROUT message body. Its 4-byte serial / service / length header IS on the
        /// wire, at absolute offset 28.
        /// </summary>
        /// <param name="body">
        /// The XROUT message body.
        /// </param>
        /// <returns>
        /// This builder, for chaining.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="body"/> is null.
        /// </exception>
        public XmsgFrameBuilder WithBody(XroutMessage body)
        {
            if (body == null)
            {
                throw new ArgumentNullException(nameof(body));
            }

            _body = body;
            return this;
        }

        /// <summary>
        /// Builds the data-frame <see cref="XmsgFrame"/>.
        /// </summary>
        /// <returns>
        /// The constructed frame.
        /// </returns>
        public XmsgFrame Build()
        {
            XmsgFrame frame = new XmsgFrame();
            frame.Header = _header;
            frame.SubHeader = _subHeader;

            // DERIVE word 6. This path used to leave whatever the ProtocolId/Counter facades had
            // written there - a fabricated value - while the sibling BuildShort computed the real
            // checksum. BuildShort's own comment records what a fabricated word 6 did on
            // 2026-08-04: every link-up killed D100 with XMSG ERROR CODE 24.
            //
            // Only tests construct this builder today, so nothing on a wire was wrong. It was a
            // loaded gun: the first production caller would have shipped invented checksums.
            XmsgEnvelope.StampChecksum(frame.Header);

            if (_body != null && (_body.Serial != 0 || _body.Service != 0))
            {
                // The letter names its own serial/service; leave them alone.
                frame.Body = _body;
            }
            else if (_body != null)
            {
                // A letter built without a serial/service takes them from the low half of the
                // control/service word the caller passed - the two are the SAME two wire bytes at
                // absolute 28-29, and this is where that identity is made explicit.
                _body.Serial = (byte)(_bodyLeadWord >> 8);
                _body.Service = (byte)_bodyLeadWord;
                frame.Body = _body;
            }

            return frame;
        }

        /// <summary>
        /// Builds a reachability-request control frame (subtype <c>0x19</c>).
        /// </summary>
        /// <param name="destinationNode">
        /// The node whose reachability is being probed.
        /// </param>
        /// <param name="sourceNode">
        /// The probing node.
        /// </param>
        /// <param name="counterByte">
        /// The trailing per-direction counter byte.
        /// </param>
        /// <returns>
        /// The constructed reachability-request frame.
        /// </returns>
        public static XmsgFrame ReachabilityRequest(ushort destinationNode, ushort sourceNode, byte counterByte)
        {
            return BuildShort(SintranPacketSubtype.ReachabilityRequest, destinationNode, sourceNode, 0xFFFF, 0x0001, counterByte);
        }

        /// <summary>
        /// Builds a reachability-reply control frame (subtype <c>0x13</c>).
        /// </summary>
        /// <param name="destinationNode">
        /// The node the reply is sent to (the original requester).
        /// </param>
        /// <param name="sourceNode">
        /// The replying node.
        /// </param>
        /// <param name="counterByte">
        /// The trailing per-direction counter byte.
        /// </param>
        /// <returns>
        /// The constructed reachability-reply frame.
        /// </returns>
        public static XmsgFrame ReachabilityReply(ushort destinationNode, ushort sourceNode, byte counterByte)
        {
            return BuildShort(SintranPacketSubtype.ReachabilityReply, destinationNode, sourceNode, 0xFFFF, 0x0001, counterByte);
        }

        private static XmsgFrame BuildShort(SintranPacketSubtype subtype, ushort destinationNode, ushort sourceNode, ushort flags1, ushort flags2, byte counterByte)
        {
            XmsgFrame frame = new XmsgFrame();
            frame.Header.Subtype = subtype;
            frame.Header.DestinationNode = destinationNode;
            frame.Header.SourceNode = sourceNode;
            frame.Header.Flags1 = flags1;
            frame.Header.Flags2 = flags2;

            // COMPUTE word 6. It is a checksum over the other six words, and the PEER VALIDATES IT.
            //
            // This used to fabricate it as (Routing << 8) | counterByte, from the superseded
            // channel/counter model, with a comment claiming "the emitted bytes are unchanged".
            // That only held while every caller happened to pass a counterByte equal to the real
            // checksum's low half - which the reachability REPLY did, and the REQUEST did not.
            //
            // Cost of that on 2026-08-04: our restart announce went out as
            //     2113 0019 0064 0067 FFFF 0001 DE00      (correct: DE07)
            // and D100 answered it by dying:
            //     ERROR 46 IN DUMMY; XMSG FATAL ERROR - INTERNAL ERROR OR INCONSISTENCY
            //     XMSG ERROR CODE: 24
            // which is the documented PERF_CONNCT / XMSG 24B crash - a peer rejecting a corrupt
            // header checksum. Every link-up crashed the machine we were trying to talk to.
            // Reads the header rather than the locals it was just built from. They are the same
            // values - the markers default to Marker1Value/Marker2Normal and the five fields were
            // assigned above - but taking them off the header means this cannot drift from the
            // other stamping sites if the assignments above ever change.
            XmsgEnvelope.StampChecksum(frame.Header);

            return frame;
        }
    }
}
