//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Codec;
using RetroIO.Protocols.Xmsg.ListRouting;
using RetroIO.Protocols.Xmsg.Node.Services;
using RetroIO.Protocols.Xmsg.Packet;

namespace RetroIO.Protocols.Xmsg.Node.Seam
{
    /// <summary>
    /// A complete XMSG node sitting on ONE link: the codec, the layer, the routing table, the
    /// server host and the registered servers, wired together and pumped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything here depends only on <see cref="ILink"/>, so the same node runs unchanged over
    /// HDLC (<c>LapbLayerAdapter</c>) or over an Ethernet segment (<c>EthernetLink</c>). That is not
    /// a hopeful claim: the SINTRAN header and its word-6 checksum were verified transport-
    /// independent on 128/128 frames including relayed ones, so no byte above the link depends on
    /// which transport carried it.
    /// </para>
    /// <para>
    /// This type exists so the second transport did not become a second copy of the composition.
    /// What it deliberately does NOT own is anything transport-specific - LAPB frame logging, SABM
    /// storm collapse, the Ethernet peer-learning wait. Those belong to the link and to whoever
    /// composes it.
    /// </para>
    /// <para>
    /// Servers are passed IN rather than constructed here, so this stays in the portable half and
    /// does not drag in <c>RetroIO.Protocols.Xmsg.Servers</c>. The caller builds its TAD server, its file server and
    /// whatever else it offers, and hands them over.
    /// </para>
    /// </remarks>
    public sealed class XmsgNodeHost
    {
        private readonly ILink _link;
        private readonly BoundProtocolDetector _detector;
        private readonly LinkXmsgTransport _transport;
        private readonly XmsgCodec _codec;
        private readonly XmsgLayer _layer;
        private readonly XmsgServerHost _serverHost;

        /// <summary>
        /// Initialises the node on a link and registers its servers.
        /// </summary>
        /// <param name="link">
        /// The link this node speaks over.
        /// </param>
        /// <param name="nodeNumber">
        /// This node's ND system number.
        /// </param>
        /// <param name="routingEntries">
        /// The routes this node advertises through XSGSY.
        /// </param>
        /// <param name="sequenceStore">
        /// Persists the outgoing datagram sequence per remote node across restarts.
        /// </param>
        /// <param name="servers">
        /// The servers to register (TAD, file, and so on). May be empty.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when any reference argument is null.
        /// </exception>
        public XmsgNodeHost(
            ILink link,
            ushort nodeNumber,
            IReadOnlyList<RoutingTableEntry> routingEntries,
            IResponderSequenceStore sequenceStore,
            params IXmsgServer[] servers)
        {
            _link = link ?? throw new ArgumentNullException(nameof(link));
            if (routingEntries == null)
            {
                throw new ArgumentNullException(nameof(routingEntries));
            }

            if (sequenceStore == null)
            {
                throw new ArgumentNullException(nameof(sequenceStore));
            }

            if (servers == null)
            {
                throw new ArgumentNullException(nameof(servers));
            }

            NodeNumber = nodeNumber;
            RoutingEntries = routingEntries;

            // This link carries XMSG by CONFIG (what the ND machine has installed), never by
            // sniffing bytes. The detector is the per-link-binding stub that says so.
            _detector = new BoundProtocolDetector(LinkBinding.Xmsg);

            _transport = new LinkXmsgTransport(link);
            _codec = new XmsgCodec(link.Name, _transport);
            _layer = new XmsgLayer(_codec, nodeNumber, 0x00);

            _layer.AcknowledgeData = false;

            // Secure-ACK (subtype 0x03) each incoming session data frame. REQUIRED for the
            // multi-chunk terminal-output handshake (TAD-Message-Formats.md 22.6): the host streams
            // <=2 output BDATs then waits for ACKs, and MUST secure-ACK the 7DUMM/data frames sent
            // between output pairs. Without this, long output never completes the handshake and the
            // peer discards the continuation chunk.
            _layer.AcknowledgeTadFrames = true;
            _layer.RoutingTable = new InMemoryRoutingTable(routingEntries);

            _serverHost = new XmsgServerHost(nodeNumber, sequenceStore);
            for (int i = 0; i < servers.Length; i++)
            {
                _serverHost.Register(servers[i]);
            }

            _layer.ServerHost = _serverHost;

            // UP wiring: a delivered link payload is classified, then (for XMSG) parsed by the
            // codec, which raises PacketReceived to the layer. An X.25-bound link would go
            // elsewhere here.
            _link.PayloadReceived += OnPayloadReceived;
        }

        /// <summary>
        /// Gets this node's ND system number.
        /// </summary>
        public ushort NodeNumber { get; }

        /// <summary>
        /// Gets the routes this node advertises.
        /// </summary>
        public IReadOnlyList<RoutingTableEntry> RoutingEntries { get; }

        /// <summary>
        /// Gets or sets a value indicating whether datagrams addressed to another node are ignored
        /// rather than processed. Off by default.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this exists</b></para>
        /// Nothing in the receive path has ever looked at the destination node: every datagram
        /// delivered on the link is processed as if it were ours. On a point-to-point link that is
        /// harmless, because everything arriving IS for us - which is why it has never shown up.
        /// <para><b>Why it matters for relaying</b></para>
        /// The moment this node carries traffic for somebody else, processing a transit datagram
        /// means secure-ACKing on the real destination's behalf, which corrupts the end-to-end
        /// sequence the captures show a relay must leave alone. A relaying node therefore turns this
        /// on, and the ordering between the relay and this host stops mattering: whichever sees the
        /// payload first, only one of them acts on it.
        /// <para><b>Why it defaults to OFF</b></para>
        /// The single-link paths are verified working against a real D100 - file access, TAD, the
        /// listing walks. Enabling a new filter under them on the strength of reasoning alone is
        /// exactly the kind of change that breaks something already proven. Off preserves today's
        /// behaviour byte for byte; the relaying node opts in.
        /// </remarks>
        public bool IgnoreDatagramsForOtherNodes { get; set; }

        /// <summary>
        /// Gets the link this node speaks over.
        /// </summary>
        public ILink Link
        {
            get { return _link; }
        }

        /// <summary>
        /// Gets the XMSG layer, for callers that need to observe frames or open sessions.
        /// </summary>
        public XmsgLayer Layer
        {
            get { return _layer; }
        }

        /// <summary>
        /// Gets the downward transport, whose <c>SendRefused</c> event reports a reply the stack
        /// built but the link would not carry.
        /// </summary>
        public LinkXmsgTransport Transport
        {
            get { return _transport; }
        }

        /// <summary>
        /// Gets the server host, for describing or registering servers after construction.
        /// </summary>
        public XmsgServerHost ServerHost
        {
            get { return _serverHost; }
        }

        /// <summary>
        /// Releases frames whose send time has arrived and sends them down the codec.
        /// </summary>
        /// <returns>
        /// The number of frames sent.
        /// </returns>
        /// <remarks>
        /// A multi-chunk terminal reply is streamed as 255-byte continuation PAIRS spaced about
        /// 46 ms apart (TAD-Message-Formats.md 22.16). The SECOND chunk of each pair has no inbound
        /// trigger, so this must be re-driven on a timer to release it once the gap has elapsed.
        /// Call it from whatever loop the transport already runs - the HDLC adapter's LoopTick, or
        /// a timer alongside an Ethernet backend.
        /// </remarks>
        public int Pump()
        {
            IReadOnlyList<XmsgFrame> pending = _serverHost.DrainPending();
            for (int i = 0; i < pending.Count; i++)
            {
                _codec.SendPacket(new XmsgPacket(pending[i]));
            }

            return pending.Count;
        }

        /// <summary>
        /// Sends frames a server built outside the request/response flow. For SHUTDOWN only.
        /// </summary>
        /// <param name="frames">
        /// The frames to send, in order.
        /// </param>
        /// <returns>
        /// The number of frames handed to the codec.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="frames"/> is null.
        /// </exception>
        /// <remarks>
        /// <para><b>Deliberately narrow, and why it exists at all</b></para>
        /// <para>
        /// A server normally answers an incoming frame or queues pending output for
        /// <see cref="Pump"/>; there is no general out-of-band send, and that restraint is worth
        /// keeping. Shutdown is the one case it cannot cover: the teardown is not a reply to
        /// anything and there is no later tick to drain it on.
        /// </para>
        /// <para>
        /// MEASURED 2026-08-17: stopping the runner with three TAD sessions open killed D100's XMSG
        /// outright - a fatal internal inconsistency from holding half a session - and cost an
        /// emulator restart. Use this from a <c>Stopping</c> handler and nowhere else; for anything
        /// during normal running, queue the output and let <see cref="Pump"/> carry it.
        /// </para>
        /// </remarks>
        public int SendFrames(IReadOnlyList<XmsgFrame> frames)
        {
            if (frames == null)
            {
                throw new ArgumentNullException(nameof(frames));
            }

            for (int i = 0; i < frames.Count; i++)
            {
                _codec.SendPacket(new XmsgPacket(frames[i]));
            }

            return frames.Count;
        }

        /// <summary>
        /// Tells a peer that OUR XMSG has just started, so it resets the datagram sequence it
        /// expects from us and both sides begin at <c>0x0000</c>.
        /// </summary>
        /// <param name="remoteNode">
        /// The node to announce ourselves to.
        /// </param>
        /// <remarks>
        /// <para>
        /// Flags 1 is each side's own count of the Data frames it has sent, and there is no way to
        /// discover the peer's expectation from the traffic - so if the two sides disagree, they
        /// stay disagreeing. A <b>ReachabilityRequest</b> is the signal that clears it: we already
        /// treat an inbound one as "the peer restarted, reset my stored sequence for it"
        /// (<c>XmsgNode.HandleFrame</c>), and D100 does the same with ours.
        /// </para>
        /// <para>
        /// We never sent one, which is the bug. Our runner restarting IS an XMSG restart as far as
        /// D100 is concerned, but D100 was never told, so it carried on from its old count while we
        /// started at zero. Observed live 2026-08-04: after our restart D100's connect letter
        /// arrived at Flags 1 <c>0x0011</c>, and the conversation died SILENTLY just after the
        /// connect confirmation - no XENSE, no error. Restarting XMSG on D100 does NOT reset it
        /// either, so waiting for the peer to announce first does not work.
        /// </para>
        /// <para>
        /// The request itself carries Flags 1 <c>0xFFFF</c>, outside the ordinary sequence, so it
        /// does not need a correct count to be sent - which is what makes it usable as the way out
        /// of a disagreement.
        /// </para>
        /// <para><b>It does NOT reset our own sequence, and that used to be the bug</b></para>
        /// <para>
        /// This method used to call <c>ResetSequence</c> on itself, on the reasoning that the peer
        /// would reset on receiving the request so both sides would start at zero together.
        /// MEASURED 2026-08-08, D103 does not:
        /// </para>
        /// <code>
        /// 21:03:02  [seq] node 103: RESET to 0x0000 (was 0x003D)      our announce zeroes US
        /// 21:03:29  [seq] node 103: link created, starting Flags1 0x0000
        ///                           (the frame that created it carried 0x003D)   peer still at 3D
        /// </code>
        /// <para>
        /// The two sides were IN STEP at <c>0x3D</c>. Announcing put us at zero and left the peer
        /// where it was, so every frame we originated afterwards was behind and was dropped in
        /// silence - the announce CAUSED the disagreement it exists to cure.
        /// </para>
        /// <para>
        /// Resetting on an INBOUND ReachabilityRequest stays, and is correct: that is the peer
        /// telling us it really has restarted, and it is what makes those conversations work. The
        /// asymmetry is deliberate - we only know our own count has meaning to the peer, never the
        /// reverse.
        /// </para>
        /// </remarks>
        public void AnnounceRestart(ushort remoteNode)
        {
            // Deliberately NOT ResetSequence - see the remarks. Our counter is kept; only the peer
            // announcing to us may reset it.
            XmsgFrame request = XmsgFrameBuilder.ReachabilityRequest(remoteNode, NodeNumber, 0x00);
            _codec.SendPacket(new XmsgPacket(request));
        }

        /// <summary>
        /// Announces our restart AND zeroes our own stored sequence for that node - the recovery
        /// for a pair that is already diverged with us AHEAD.
        /// </summary>
        /// <param name="remoteNode">
        /// The node to announce to and reset against.
        /// </param>
        /// <remarks>
        /// <para><b>This is the experiment, not the settled answer. Read before using.</b></para>
        /// <para>
        /// <see cref="AnnounceRestart"/> deliberately keeps our counter, because D103 did NOT reset
        /// its expectation when told, so zeroing ourselves put us BEHIND and every frame afterwards
        /// was dropped in silence. That measurement stands and is why the two methods are separate.
        /// </para>
        /// <para>
        /// But it leaves no way out of the opposite disagreement. When our counter is AHEAD - the
        /// peer's XMSG restarted and ours did not - every frame draws a XENSE, and the only
        /// step-down recovery we have moves one number per reject. MEASURED 2026-08-09: against a
        /// drift of that size it does not converge, it storms - 127 resends walking 0x009E, 0x009D,
        /// 0x009C down, and still going when the runner was killed.
        /// </para>
        /// <para>
        /// So this method exists to answer ONE question against a real machine: <b>does D100 reset
        /// its expected-from-us when it receives our reachability request?</b> If it does, the frame
        /// we send at <c>0x0000</c> afterwards is accepted and this is the cure. If it does not, the
        /// frame is dropped in silence exactly as D103's was, and we have learned that cheaply
        /// instead of by inference. Do NOT promote this to automatic until a run says which.
        /// </para>
        /// <para>
        /// The request carries Flags 1 <c>0xFFFF</c>, outside the ordinary sequence, so it can
        /// always be sent whatever state the counters are in.
        /// </para>
        /// </remarks>
        public void AnnounceRestartAndResetOurs(ushort remoteNode)
        {
            AnnounceRestart(remoteNode);

            // KEEPING the link matters: we can only learn a link from an inbound datagram, so
            // dropping it here leaves us unable to address a peer we were talking to a moment ago.
            ServerHost?.ResetSequenceKeepingLink(remoteNode);
        }

        /// <summary>
        /// Formats the routing table for the TAD "list route" command: one line per system with its
        /// connection type and hop count.
        /// </summary>
        /// <returns>
        /// The route report text.
        /// </returns>
        public string FormatRouteReport()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(256);
            sb.Append("  System  Route       Hops\r\n");
            for (int i = 0; i < RoutingEntries.Count; i++)
            {
                RoutingTableEntry entry = RoutingEntries[i];
                sb.Append("  ").Append(entry.System.ToString().PadRight(8))
                  .Append(entry.ConnectionType.ToString().PadRight(12))
                  .Append(entry.Hops).Append("\r\n");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Classifies a payload delivered up by the link and feeds XMSG traffic to the codec.
        /// </summary>
        /// <param name="deliveringLink">
        /// The link that delivered the payload.
        /// </param>
        /// <param name="payload">
        /// The payload buffer.
        /// </param>
        /// <param name="length">
        /// The number of valid bytes.
        /// </param>
        private void OnPayloadReceived(ILink deliveringLink, byte[] payload, int length)
        {
            ReadOnlySpan<byte> span = payload.AsSpan(0, length);

            // A datagram addressed to somebody else is not ours to answer. Only checked when the
            // caller asks for it - see IgnoreDatagramsForOtherNodes for why the default is off.
            if (IgnoreDatagramsForOtherNodes && IsForAnotherNode(span))
            {
                return;
            }

            if (_detector.Classify(deliveringLink.Name, span) == LinkBinding.Xmsg)
            {
                _codec.ProcessBytes(span);
            }
        }

        /// <summary>
        /// Reports whether a payload is a SINTRAN datagram addressed to a node other than this one.
        /// </summary>
        /// <param name="span">
        /// The payload, starting at Marker 1.
        /// </param>
        /// <returns>
        /// True only when the destination could be read AND it is not this node.
        /// </returns>
        /// <remarks>
        /// Anything too short to carry a header returns false, so a malformed payload keeps taking
        /// the ordinary path and is rejected there rather than silently vanishing here.
        /// </remarks>
        private bool IsForAnotherNode(ReadOnlySpan<byte> span)
        {
            if (span.Length < SintranDatagramRelay.HeaderSize)
            {
                return false;
            }

            return SintranDatagramRelay.GetDestinationNode(span) != NodeNumber;
        }
    }
}
