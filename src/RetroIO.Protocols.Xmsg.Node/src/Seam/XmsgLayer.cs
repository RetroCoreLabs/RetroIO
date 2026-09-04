using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Codec;
using RetroIO.Protocols.Xmsg.ListRouting;
using RetroIO.Protocols.Xmsg.Node.Tad;
using RetroIO.Protocols.Xmsg.Packet;
using RetroIO.Protocols.Xmsg.SubProtocol;

namespace RetroIO.Protocols.Xmsg.Node.Seam
{
    /// <summary>
    /// The XMSG L3 layer: sits above the codec seam, dispatches each received packet to the right
    /// service (reachability, list-route/XSGSY, TAD session, secure-delivery ACK), sends the
    /// responses back down through the codec, and raises application up-events.
    /// </summary>
    /// <remarks>
    /// <para><b>Placement.</b> The plan's ideal home for <c>XmsgLayer</c> is <c>RetroIO.Protocols.Xmsg</c>,
    /// but the TAD session service (<see cref="TadTerminalResponder"/>, its terminal menu and frame
    /// context) and the byte-verified multi-frame orchestration (<see cref="XmsgNode.HandleFrames"/>)
    /// live in <c>RetroIO.Protocols.Xmsg.Live</c>, and <c>RetroIO.Protocols.Xmsg</c> cannot depend on <c>RetroIO.Protocols.Xmsg.Live</c>. Rather
    /// than relocate all of TAD (a large, risky churn against the locked "facade over proven
    /// internals" decision), <c>XmsgLayer</c> lives here and reuses the proven services unchanged;
    /// the reachability / XSGSY / secure-ACK logic it drives remains pure in <c>RetroIO.Protocols.Xmsg</c>
    /// (<see cref="ListRoutingServer"/>, <see cref="SecureDatagramReceiver"/>). At migration,
    /// <c>XmsgLayer</c> moves together with the TAD service into the X25Emulator XMSG sibling.</para>
    /// <para><b>Seam contract.</b> The layer knows only <see cref="IXmsgCodec"/> downward and raises
    /// named-delegate up-events (sender/link-id first). It never touches HDLC/LAPB. Reliability
    /// (secure-delivery sequencing + <c>0x03</c> ACK) lives here, in the layer, per the plan.</para>
    /// </remarks>
    public sealed class XmsgLayer
    {
        private readonly IXmsgCodec _codec;
        private readonly XmsgNode _node;

        /// <summary>
        /// Up-event: a packet was received and dispatched on the given link.
        /// </summary>
        /// <param name="linkId">
        /// The link the packet arrived on (sender-first).
        /// </param>
        /// <param name="packet">
        /// The decoded packet.
        /// </param>
        public delegate void XmsgMessageReceived(string linkId, XmsgPacketInfo packet);

        /// <summary>
        /// Up-event: a TAD terminal session was opened by a remote connect-to.
        /// </summary>
        /// <param name="linkId">
        /// The link the connect arrived on (sender-first).
        /// </param>
        /// <param name="clientSystem">
        /// The connecting system (node) number.
        /// </param>
        /// <param name="clientPort">
        /// The connecting client's port.
        /// </param>
        public delegate void XmsgSessionOpened(string linkId, ushort clientSystem, ushort clientPort);

        /// <summary>
        /// Up-event: terminal input text arrived on an open TAD session.
        /// </summary>
        /// <param name="linkId">
        /// The link the terminal data arrived on (sender-first).
        /// </param>
        /// <param name="text">
        /// The decoded ASCII terminal text (BDAT), high bit stripped.
        /// </param>
        public delegate void XmsgTerminalDataReceived(string linkId, string text);

        /// <summary>
        /// Up-event: an inbound packet finished dispatch, reporting how many response frames it
        /// produced.
        /// </summary>
        /// <param name="linkId">
        /// The link the packet arrived on (sender-first).
        /// </param>
        /// <param name="packet">
        /// The inbound packet that was dispatched.
        /// </param>
        /// <param name="responsesProduced">
        /// The number of response frames dispatch returned. Zero means NOTHING was answered.
        /// </param>
        /// <remarks>
        /// <para>
        /// This exists because "received but unanswered" was invisible: the live D19999 node logged
        /// an inbound ReachabilityRequest and D100 then aborted with
        /// <c>NO ANSWER FROM REMOTE SYSTEM</c>, with nothing in between to say whether the node had
        /// built no reply, or had built one the link then dropped. Those two faults live in
        /// completely different code and the log could not tell them apart. A node that accepts a
        /// link and does not answer HANGS the calling SINTRAN terminal - ESC will not abort it - so
        /// an unanswered request is never benign.
        /// </para>
        /// <para>
        /// This reports only what the layer knows: how many frames dispatch BUILT. Whether the link
        /// then carried them is reported separately by
        /// <c>LinkXmsgTransport.SendRefused</c>, because only the transport can see a refusal.
        /// </para>
        /// </remarks>
        public delegate void XmsgDispatchCompleted(
            string linkId, XmsgPacketInfo packet, int responsesProduced);

        /// <summary>
        /// Occurs when a packet is received and dispatched.
        /// </summary>
        /// <remarks>
        /// A split message is rejoined BEFORE this fires, so a subscriber always sees the whole
        /// message and never a fragment - see <c>XmsgNode.AcceptFragment</c> for what it cost when
        /// that was not true.
        /// </remarks>
        public event XmsgMessageReceived? MessageReceived;

        /// <summary>
        /// Occurs when the first half of a split message has been taken and held, so nothing can be
        /// reported about it until its continuation arrives.
        /// </summary>
        /// <remarks>
        /// Purely so the frame is not invisible. It is followed by an ordinary
        /// <see cref="MessageReceived"/> for the joined message once the continuation lands, or by
        /// a reassembler log line if it never does.
        /// </remarks>
        public event XmsgMessageReceived? FragmentHeld;

        /// <summary>
        /// Occurs when a TAD terminal session is opened.
        /// </summary>
        public event XmsgSessionOpened? SessionOpened;

        /// <summary>
        /// Occurs after an inbound packet has been dispatched, reporting whether it was answered.
        /// </summary>
        public event XmsgDispatchCompleted? DispatchCompleted;

        /// <summary>
        /// Occurs when terminal input text arrives on an open session.
        /// </summary>
        public event XmsgTerminalDataReceived? TerminalDataReceived;

        /// <summary>
        /// Initialises the layer over a codec, with this node's number and the secure-ACK counter seed.
        /// </summary>
        /// <param name="codec">
        /// The codec seam the layer sends packets to and receives packets from.
        /// </param>
        /// <param name="nodeNumber">
        /// This node's number (for example 102 or 103).
        /// </param>
        /// <param name="ackCounter">
        /// The starting value of the per-direction secure-ACK counter.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="codec"/> is null.
        /// </exception>
        public XmsgLayer(IXmsgCodec codec, ushort nodeNumber, byte ackCounter)
        {
            _codec = codec ?? throw new ArgumentNullException(nameof(codec));
            _node = new XmsgNode(nodeNumber, ackCounter);
            _codec.PacketReceived += OnPacketReceived;
        }

        /// <summary>
        /// Gets this node's number.
        /// </summary>
        public ushort NodeNumber
        {
            get { return _node.NodeNumber; }
        }

        /// <summary>
        /// Gets or sets the sink for the node's own diagnostics.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Without this the node's log went NOWHERE on the seam path - the layer wrapped
        /// <c>XmsgNode</c> privately and never offered a way to reach its <c>Log</c>, so its
        /// warnings and its network-error reports were built and dropped.
        /// </para>
        /// <para>
        /// That is the second diagnostic found unconnected in one day (the first was
        /// <c>XmsgServerHost.Log</c> on the Ethernet path). A live push looked like plain silence
        /// while the peer was in fact answering with a NAMED error, twice. When something looks
        /// silent, check that the log is plugged in before believing the wire.
        /// </para>
        /// </remarks>
        public XmsgLogHandler? Log
        {
            get { return _node.Log; }
            set { _node.Log = value; }
        }

        /// <summary>
        /// Gets or sets whether an ordinary data frame is answered with the <c>0x03</c> secure ACK.
        /// Default false (observe-only): an unrequested ACK crashed the live kernel (XXPER).
        /// </summary>
        public bool AcknowledgeData
        {
            get { return _node.AcknowledgeData; }
            set { _node.AcknowledgeData = value; }
        }

        /// <summary>
        /// Gets or sets whether TAD connect / session frames are secure-ACKed on the per-session
        /// channel (connect-channel + 4). Required for a live connect-to to proceed.
        /// </summary>
        public bool AcknowledgeTadFrames
        {
            get { return _node.AcknowledgeTadFrames; }
            set { _node.AcknowledgeTadFrames = value; }
        }

        /// <summary>
        /// Gets or sets the routing table that answers list-route (XSGSY) requests.
        /// </summary>
        public IRoutingTable? RoutingTable
        {
            get { return _node.RoutingTable; }
            set { _node.RoutingTable = value; }
        }

        /// <summary>
        /// Gets or sets the TAD terminal responder that answers connect-to sessions.
        /// </summary>
        public TadTerminalResponder? TadResponder
        {
            get { return _node.TadResponder; }
            set { _node.TadResponder = value; }
        }

        /// <summary>
        /// Gets or sets the framework server host that dispatches server traffic to the registered
        /// <see cref="Services.IXmsgServer"/>s (the replacement for <see cref="TadResponder"/>).
        /// </summary>
        public Services.XmsgServerHost? ServerHost
        {
            get { return _node.ServerHost; }
            set { _node.ServerHost = value; }
        }

        /// <summary>
        /// Defines (or re-points) a remote-node name alias (the DEF-REMOTE / XSDRN model).
        /// </summary>
        /// <param name="name">
        /// The alias, matched case-insensitively.
        /// </param>
        /// <param name="systemNumber">
        /// The system number the alias resolves to.
        /// </param>
        public void DefineRemote(string name, ushort systemNumber)
        {
            _node.DefineRemote(name, systemNumber);
        }

        /// <summary>
        /// Handles one packet arriving up from the codec: dispatch it through the verified services,
        /// send every response back down through the codec, and raise the matching up-events.
        /// </summary>
        /// <param name="linkId">
        /// The link the packet arrived on.
        /// </param>
        /// <param name="packet">
        /// The decoded packet.
        /// </param>
        private void OnPacketReceived(string linkId, XmsgPacketInfo packet)
        {
            // REJOIN A SPLIT MESSAGE BEFORE ANYONE SEES IT. A file-content message is 1032 bytes
            // and arrives as a first fragment (0x0A) and a continuation (0x0C). Everything that
            // watches inbound traffic - the events below AND the dispatch - has to see the WHOLE
            // message, so the rejoin happens here, at the front, not inside HandleFrames.
            //
            // It used to happen inside HandleFrames, which runs AFTER MessageReceived. The
            // file-access CLIENT subscribes to MessageReceived, so a pull was handed a first
            // fragment and a continuation separately and never saw the content they carry.
            // Measured live on 2026-08-11: the read ladder ran perfectly, D100 sent the file, and
            // the bytes went on the floor. See XmsgNode.AcceptFragment.
            XmsgFrame? rejoined = _node.AcceptFragment(packet.Frame);
            if (rejoined == null)
            {
                // The first half. Nothing can be answered or reported until its continuation
                // arrives - the reply belongs to the whole message - but say so, because a frame
                // that vanishes without a line in the log is expensive to diagnose from outside.
                FragmentHeld?.Invoke(linkId, packet);
                return;
            }

            // Not a fragment at all, in the overwhelming majority of cases: Accept returns the
            // frame unchanged, and this is the same object the codec handed us.
            if (!ReferenceEquals(rejoined, packet.Frame))
            {
                packet = new XmsgPacketInfo(rejoined);
            }

            // Surface the message first, then act on it.
            MessageReceived?.Invoke(linkId, packet);
            RaiseSessionEvents(linkId, packet);

            // Dispatch through the byte-verified multi-frame orchestration (reachability, XSGSY,
            // TAD accept/port-assign/ACK). It returns the exact response frames the live node was
            // validated against machine 100 with; send each one down through the codec.
            IReadOnlyList<XmsgFrame> responses = _node.HandleFrames(packet.Frame);
            for (int i = 0; i < responses.Count; i++)
            {
                _codec.SendPacket(new XmsgPacket(responses[i]));
            }

            // Report the answer count LAST, after every response has been handed to the codec, so a
            // subscriber that logs this sees it in the same order the wire did.
            DispatchCompleted?.Invoke(linkId, packet, responses.Count);
        }

        /// <summary>
        /// Raises <see cref="SessionOpened"/> for a TAD connect request and
        /// <see cref="TerminalDataReceived"/> for terminal input on an open session.
        /// </summary>
        private void RaiseSessionEvents(string linkId, XmsgPacketInfo packet)
        {
            TadTerminalResponder? responder = _node.TadResponder;
            if (responder == null || packet.Type != XmsgPacketType.Data)
            {
                return;
            }

            // A connect request opens a session: report the connecting endpoint.
            if (TadTerminalResponder.IsConnectRequest(packet.Frame))
            {
                SessionOpened?.Invoke(linkId, packet.SourceSystem, packet.SourcePort);
                return;
            }

            // Terminal input on an already-open session: surface the typed text.
            if (responder.IsConnected)
            {
                string text = ExtractBdatText(packet.Frame);
                if (text.Length > 0)
                {
                    TerminalDataReceived?.Invoke(linkId, text);
                }
            }
        }

        /// <summary>
        /// Reads the concatenated ASCII text of every BDAT (terminal character-data, opcode 0x01)
        /// message in a frame's decoded TAD chain, stripping the high (parity) bit. Mirrors the
        /// responder's own extraction so the up-event carries the same text the menu sees.
        /// </summary>
        private static string ExtractBdatText(XmsgFrame frame)
        {
            if (frame.Tad == null)
            {
                return string.Empty;
            }

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            IReadOnlyList<TadMessage> messages = frame.Tad.Messages;
            for (int i = 0; i < messages.Count; i++)
            {
                if (messages[i].Opcode != 0x01)
                {
                    continue;
                }

                byte[] data = messages[i].Data;
                for (int j = 0; j < data.Length; j++)
                {
                    sb.Append((char)(data[j] & 0x7F));
                }
            }

            return sb.ToString();
        }
    }
}
