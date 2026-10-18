//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.ListRouting;
using RetroIO.Protocols.Xmsg.Node.Services;
using RetroIO.Protocols.Xmsg.Node.Tad;
using RetroIO.Protocols.Xmsg.Packet;

namespace RetroIO.Protocols.Xmsg.Node
{
    /// <summary>
    /// The XMSG application-layer runtime for one node: it answers reachability requests,
    /// acknowledges data frames with the secure-delivery <c>0x03</c> ACK, and holds the
    /// configurable remote-name alias table (the DEF-REMOTE / XSDRN model).
    /// </summary>
    /// <remarks>
    /// <para><b>Provenance</b></para>
    /// The reachability handshake (request subtype <c>0x19</c> answered by reply subtype
    /// <c>0x13</c> with swapped Dest/Src, <c>Flags1 = 0xFFFF</c>, <c>Flags2 = 0x0001</c>)
    /// and the ACK mechanics (subtype <c>0x03</c>, <c>Flags1</c> echoes the data frame's
    /// datagram sequence, <c>Flags2 = 0x0001</c>) are VERIFIED against captures
    /// (XMSG-PROTOCOL.md sections 5.1 and 6). The reachability-reply command byte
    /// (<c>0x0E</c>) is OBSERVED from the single captured request/reply pair. Port/magic
    /// allocation is not modelled here - INFERRED / stubbed.
    /// </remarks>
    public sealed class XmsgNode
    {
        /// <summary>
        /// The trailing command byte placed in a reachability reply.
        /// </summary>
        /// <remarks>
        /// OBSERVED: the captured 100->102 request carries trailing byte <c>0x08</c>
        /// (Sync-Request) and the 102->100 reply carries <c>0x0E</c> (Sync-Response)
        /// (XMSG-PROTOCOL.md sections 5.1 and 9.1). Only one request/reply pair exists in
        /// the corpus, so this is a fixed constant rather than a proven function of the
        /// request.
        /// </remarks>
        public const byte ReachabilityReplyCommand = 0x0E;

        /// <summary>
        /// When true (default), a received data frame is answered with the <c>0x03</c> secure
        /// ACK. Set false for observe-only operation against a live kernel that crashes on an
        /// unexpected/malformed ACK (see the note in <see cref="HandleFrame"/>).
        /// </summary>
        public bool AcknowledgeData { get; set; } = true;

        /// <summary>
        /// Optional routing table. When set, an incoming list-route (<c>XSGSY</c>) request
        /// (<c>XMCSM 0x0100014B</c>) is answered with the byte-validated
        /// <see cref="ListRoutingServer"/> reply instead of a secure ACK.
        /// </summary>
        public IRoutingTable? RoutingTable { get; set; }

        /// <summary>
        /// Optional TAD terminal responder. When set, an incoming SYSTEM-TAD connect request
        /// (<c>XMCSM 0x04000041</c>) opens a simulated remote-machine terminal session, and
        /// subsequent terminal-data (BDAT) frames drive its menu.
        /// </summary>
        public TadTerminalResponder? TadResponder { get; set; }

        /// <summary>
        /// Optional framework server host. When set, incoming server traffic (XSLET connect letters and
        /// session data) is dispatched to the registered <see cref="IXmsgServer"/>s (the TAD server now,
        /// XM-FIDO later) instead of the legacy <see cref="TadResponder"/>, and each such frame is
        /// secure-ACKed via the closed-form model. Reachability, list-route and ACK/XENSE stay in the
        /// node. This is the replacement path for <see cref="TadResponder"/>.
        /// </summary>
        public XmsgServerHost? ServerHost { get; set; }

        /// <summary>
        /// When true, secure-ACK (subtype <c>0x03</c>, echoing Flags1) each TAD connect and
        /// session frame from the peer. DEFAULT FALSE: the experiment showed that sending a
        /// <c>0x03</c> ACK (echoing the connect's <c>DA</c> channel) CRASHES 100 (XXPER) - a real
        /// responder ACKs the connect on the <c>DD</c> (TAD) channel, so a naive echo-channel ACK
        /// is malformed. Left as a toggle for a future correctly-channelled ACK experiment.
        /// </summary>
        public bool AcknowledgeTadFrames { get; set; } = false;

        private readonly ListRoutingServer _routingServer = new ListRoutingServer();

        private readonly ushort _nodeNumber;
        private readonly Dictionary<string, ushort> _remoteNames;
        private readonly SecureDatagramReceiver _receiver;

        /// <summary>
        /// Joins received fragment pairs back into whole data frames.
        /// </summary>
        /// <remarks>
        /// A file-content message is 1032 bytes and arrives as two frames. Every path below the
        /// dispatch in <see cref="HandleFrames"/> is gated on a data frame with a sub-header, so a
        /// fragment reaches none of them - it has to be rejoined first. See
        /// <see cref="SintranFragmentReassembler"/>.
        /// </remarks>
        // Its Log is wired in the constructor. Without that, a fragment pair that arrives and is
        // silently dropped looks exactly like one that never arrived - which is what a live write
        // against D100 on 2026-08-06 looked like: both halves of the client's data message on the
        // wire, nothing stored, and a retransmit 39 seconds later.
        private readonly SintranFragmentReassembler _fragments = new SintranFragmentReassembler();

        private ushort _outgoingDatagramSequence;

        // The per-link seed for the session ACK model, learned ONCE from the first valid data frame.
        // It is a per-link CONSTANT (VERIFIED across every session/reconnect/reboot in the corpus), so
        // it must never be re-learned per frame: a single out-of-model received frame would poison the
        // seed for the next frames we originate (measured live 2026-07-07 - a burst chunk went out with
        // the Counter for seed 0x16 instead of 0x14; same bug class as the historical per-connect ACK
        // re-seed that 24B-crashed 100).
        private bool _linkSeedLearned;
        private byte _linkSeed;

        /// <summary>
        /// Optional diagnostics sink for envelope anomalies (a received frame whose implied seed
        /// disagrees with the learned link seed - the out-of-model frame to hunt in a capture).
        /// </summary>
        public XmsgLogHandler? Log { get; set; }

        /// <summary>
        /// Raised when a data frame is delivered to this node's application layer.
        /// </summary>
        /// <param name="datagramSequence">
        /// The datagram sequence (Flags 1) of the delivered frame.
        /// </param>
        public delegate void DataDelivered(ushort datagramSequence);

        /// <summary>
        /// Occurs when a data frame is delivered locally (before its ACK is built).
        /// </summary>
        public event DataDelivered? OnDataDelivered;

        /// <summary>
        /// Initialises a node with its number and the seed for its ACK counter.
        /// </summary>
        /// <param name="nodeNumber">
        /// This node's number (for example 100, 102 or 103).
        /// </param>
        /// <param name="ackCounter">
        /// The starting value of the per-direction counter placed in each secure ACK. It
        /// decrements per ACK (XMSG-PROTOCOL.md section 6).
        /// </param>
        public XmsgNode(ushort nodeNumber, byte ackCounter)
        {
            _nodeNumber = nodeNumber;
            _remoteNames = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
            _receiver = new SecureDatagramReceiver(ackCounter);
            _receiver.OnReceived += RaiseDelivered;

            // Forward the reassembler's own diagnostics to this node's sink, so a refused or held
            // fragment says so instead of vanishing.
            _fragments.Log += line => Log?.Invoke(line);
        }

        /// <summary>
        /// Gets this node's number.
        /// </summary>
        public ushort NodeNumber
        {
            get { return _nodeNumber; }
        }

        /// <summary>
        /// Gets the current per-direction ACK counter value (the byte placed in the next ACK).
        /// </summary>
        public byte AckCounter
        {
            get { return _receiver.Counter; }
        }

        /// <summary>
        /// Gets the next outgoing datagram-sequence value that a locally originated data
        /// frame would carry.
        /// </summary>
        /// <remarks>
        /// The sequence increments per data message on a direction (XMSG-PROTOCOL.md
        /// section 4.2). This node is currently receive/reply oriented, so the counter is
        /// advanced through <see cref="NextDatagramSequence"/> rather than automatically.
        /// </remarks>
        public ushort OutgoingDatagramSequence
        {
            get { return _outgoingDatagramSequence; }
        }

        /// <summary>
        /// Defines (or re-points) a remote-node name alias.
        /// </summary>
        /// <param name="name">
        /// The alias, matched case-insensitively (for example <c>"D102"</c> or <c>"main"</c>).
        /// </param>
        /// <param name="systemNumber">
        /// The system (node) number the alias resolves to. Many aliases may map to the same
        /// system number.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="name"/> is null.
        /// </exception>
        public void DefineRemote(string name, ushort systemNumber)
        {
            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            _remoteNames[name] = systemNumber;
        }

        /// <summary>
        /// Resolves a remote-node name alias to its system number.
        /// </summary>
        /// <param name="name">
        /// The alias to resolve.
        /// </param>
        /// <param name="systemNumber">
        /// When this method returns <c>true</c>, the resolved system number; otherwise 0.
        /// </param>
        /// <returns>
        /// <c>true</c> when the alias is defined; otherwise <c>false</c>.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="name"/> is null.
        /// </exception>
        public bool TryResolveRemote(string name, out ushort systemNumber)
        {
            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            return _remoteNames.TryGetValue(name, out systemNumber);
        }

        /// <summary>
        /// Advances and returns the outgoing datagram-sequence counter (modulo 16 bits).
        /// </summary>
        /// <returns>
        /// The value the next locally originated data frame should carry.
        /// </returns>
        public ushort NextDatagramSequence()
        {
            ushort value = _outgoingDatagramSequence;
            _outgoingDatagramSequence = (ushort)(_outgoingDatagramSequence + 1);
            return value;
        }

        /// <summary>
        /// Drains queued asynchronous output from all registered servers (for example TAD tty inject /
        /// wall text) into frames to transmit. The live runner calls this each pump cycle so injected
        /// text flushes to the remote clients.
        /// </summary>
        /// <returns>
        /// The queued frames, in order (empty when nothing is pending or no server host is set).
        /// </returns>
        public IReadOnlyList<XmsgFrame> DrainServers()
        {
            return ServerHost != null ? ServerHost.DrainPending() : Array.Empty<XmsgFrame>();
        }

        /// <summary>
        /// Offers a received frame to this node's fragment reassembler, ahead of any dispatch.
        /// </summary>
        /// <param name="incoming">
        /// The frame as it arrived.
        /// </param>
        /// <returns>
        /// The frame to go on handling: <paramref name="incoming"/> unchanged when it is not a
        /// fragment, the joined data frame when a pair has just completed, or
        /// <c>null</c> when it was the first half and there is nothing to handle yet.
        /// </returns>
        /// <remarks>
        /// <para>Why this is public rather than private to <see cref="HandleFrames"/></para>
        /// <para>
        /// Anything watching inbound traffic has to see WHOLE messages, not halves. The file-access
        /// CLIENT is the case that proved it: a pull driver subscribed to the layer's
        /// message event, which fired before <see cref="HandleFrames"/> ran, so the driver was
        /// handed a first fragment and a continuation separately and never saw the file content
        /// they carry. The ladder ran perfectly to <c>ReadFile</c>, D100 sent the file, and it went
        /// on the floor - measured live 2026-08-11.
        /// </para>
        /// <para>
        /// So the rejoin belongs at the FRONT of the chain, and this exposes it there. It is still
        /// the node's one reassembler - two would pair fragments against two separate tables and
        /// join nothing. <see cref="HandleFrames"/> still calls it, which is harmless and keeps
        /// this type correct on its own: a frame that has already been joined is an ordinary data
        /// frame, and the reassembler returns those unchanged.
        /// </para>
        /// </remarks>
        public XmsgFrame? AcceptFragment(XmsgFrame incoming)
        {
            if (incoming == null)
            {
                throw new ArgumentNullException(nameof(incoming));
            }

            return _fragments.Accept(incoming);
        }

        /// <summary>
        /// Processes a received frame and returns ALL frames to send in response (zero or more).
        /// This is the multi-frame entry point the live node uses: a TAD connect needs several
        /// frames (secure ACK + connect-accept + greeting), which the single-frame
        /// <see cref="HandleFrame"/> cannot express.
        /// </summary>
        /// <param name="incoming">
        /// The decoded received frame.
        /// </param>
        /// <returns>
        /// The response frames, in transmit order (possibly empty).
        /// </returns>
        public IReadOnlyList<XmsgFrame> HandleFrames(XmsgFrame incoming)
        {
            if (incoming == null)
            {
                throw new ArgumentNullException(nameof(incoming));
            }

            if (incoming.Header == null)
            {
                throw new ArgumentNullException(nameof(incoming), "Frame header is null.");
            }

            // REJOIN A SPLIT MESSAGE FIRST. A 1032-byte file-content message arrives as a first
            // fragment (0x0A) and a continuation (0x0C), and every dispatch below is gated on a
            // Data frame with a sub-header - so a fragment would reach none of them. Accept returns
            // the frame unchanged when it is not a fragment, so the ordinary paths are untouched.
            XmsgFrame? rejoined = _fragments.Accept(incoming);
            if (rejoined == null)
            {
                // The first half of a message, or a fragment we cannot use. Nothing to answer yet -
                // the reply belongs to the WHOLE message.
                return Array.Empty<XmsgFrame>();
            }

            incoming = rejoined;

            // FRAMEWORK SERVER DISPATCH (the replacement for the TadResponder path below): route server
            // traffic - XSLET connect letters and session data - to the registered servers, secure-ACKing
            // each via the closed-form model seeded from the link seed. Reachability, list-route (XSGSY)
            // and ACK/XENSE fall through to the node's own handling.
            if (ServerHost != null
                && incoming.Header.Subtype == SintranPacketSubtype.Data
                && incoming.SubHeader != null
                && incoming.XmcsmWithBodyWord != ListRoutingServer.XmcsmXsgsyRequest)
            {
                List<XmsgFrame> served = new List<XmsgFrame>();
                // Gate the secure-ACK on AcknowledgeTadFrames (the session-data ACK flag), NOT
                // AcknowledgeData. AcknowledgeData governs the LEGACY generic-data path below and is
                // deliberately false in the runner (it would double-ACK). The old TadResponder path
                // this block replaced ACKed every session frame via AcknowledgeTadFrames - dropping
                // that here left 100 un-ACKed, so it retransmitted its connect/terminal frames and
                // eventually desynced its magic-number window, crashing with XEIMA on a multi-frame
                // reply (the "stat Illegal element length / illegal port magic" crash). Same closed-form
                // stateless ACK the old path used: seed from the link seed, ride the 0xDE anchor channel.
                if (AcknowledgeTadFrames)
                {
                    _receiver.UseSessionAckModel(LearnLinkSeedOnce(incoming));
                    served.Add(_receiver.ReceiveDataFrame(incoming, (SintranProtocolId)XmsgEnvelope.ChannelAnchor));
                }

                IReadOnlyList<XmsgFrame> serverFrames = ServerHost.Route(incoming);
                for (int i = 0; i < serverFrames.Count; i++)
                {
                    served.Add(serverFrames[i]);
                }

                // Drain any window-permitted output chunks. A command reply begins its burst in Route
                // above; a 7DUMM from 100 between chunks lets the next chunk out here (the flow-control
                // handshake, TAD-Message-Formats.md 22.6).
                IReadOnlyList<XmsgFrame> drained = ServerHost.DrainPending();
                for (int i = 0; i < drained.Count; i++)
                {
                    served.Add(drained[i]);
                }

                return served;
            }

            List<XmsgFrame> result = new List<XmsgFrame>();

            // TAD terminal-responder path: only for data frames while a responder is attached.
            if (TadResponder != null
                && incoming.Header.Subtype == SintranPacketSubtype.Data
                && incoming.SubHeader != null)
            {
                // (a) A SYSTEM-TAD connect request opens the session: secure-ACK it, then send the
                //     connect-accept and the terminal greeting.
                if (TadTerminalResponder.IsConnectRequest(incoming))
                {
                    // Run OnConnect FIRST so the responder learns the per-session ACK channel
                    // (connect-channel + 4) from this frame; the ACK we build below must ride that
                    // channel, NOT the connect's own channel (echoing the connect channel is the
                    // malformed +0 ACK that crashed 100 with XXPER).
                    IReadOnlyList<XmsgFrame> setup = TadResponder.OnConnect(incoming);

                    // Seed the ACK counter to connect-counter + 0x0A so the first ACK trailing byte
                    // matches the captured value (VALIDATED: 8/13 captures, both connect captures:
                    // 0x0D->0x17, 0xCE->0xD8) and the per-ACK decrement reproduces the captured
                    // sequence. Without this the trailing byte is 0x00 - a malformed ACK that
                    // crashed 100 (XXPER).
                    if (incoming.SubHeader != null)
                    {
                        // The TAD-session ACK is the capture-verified STATELESS closed form: Counter and
                        // channel are a pure function of the acknowledged Flags1, via the envelope
                        // arithmetic with ACK seed = link-seed + 0x0B. One continuous sequence across
                        // every connect - never re-seeded per connect (the old connect-Counter+0x0A
                        // re-seed reset the channel to DE where the real 102 rode DD past the ACK baseLow,
                        // crashing 100 at PERF_CONNCT on the third connect). Learn the link seed here
                        // (once - see LearnLinkSeedOnce).
                        _receiver.UseSessionAckModel(LearnLinkSeedOnce(incoming));
                    }

                    // Secure-ACK the connect (subtype 0x03, echoes Flags1) on the session ACK
                    // channel so 100 stops retransmitting and advances. ACK first, then the
                    // byte-verified connect-accept.
                    if (AcknowledgeTadFrames)
                    {
                        result.Add(_receiver.ReceiveDataFrame(incoming, TadResponder.AckChannel));
                    }

                    for (int i = 0; i < setup.Count; i++)
                    {
                        result.Add(setup[i]);
                    }

                    return result;
                }

                // (a2) The session-setup that follows an accepted connect: ACK it, then reply with
                //      the port-assignment (tells 100 our session port).
                if (TadResponder.IsSessionSetup(incoming))
                {
                    // ACK on the session-constant channel (connect+4) learned at connect time.
                    if (AcknowledgeTadFrames)
                    {
                        result.Add(_receiver.ReceiveDataFrame(incoming, TadResponder.AckChannel));
                    }

                    IReadOnlyList<XmsgFrame> assign = TadResponder.OnSessionSetup(incoming);
                    for (int i = 0; i < assign.Count; i++)
                    {
                        result.Add(assign[i]);
                    }

                    return result;
                }

                // (b) Any session data frame while connected (terminal negotiation or the user's
                //     typed line), except list-route which has its own reply: secure-ACK it so the
                //     handshake proceeds, and if it carries terminal input (BDAT) send the menu
                //     response too.
                if (TadResponder.IsConnected
                    && incoming.XmcsmWithBodyWord != ListRoutingServer.XmcsmXsgsyRequest)
                {
                    // Session data frames arrive on various channels (DC/DD/DE) but every ACK
                    // rides the one session-constant channel (connect+4), so pass it explicitly.
                    result.Add(_receiver.ReceiveDataFrame(incoming, TadResponder.AckChannel));
                    if (TadResponder.IsDisconnect(incoming))
                    {
                        // 100's graceful teardown (DCON) - e.g. its 1-minute "TAD not logged in" idle
                        // timeout. ACK it (above) and close our session so the next connect is clean.
                        TadResponder.CloseSession();
                    }
                    else if (HasBdat(incoming))
                    {
                        IReadOnlyList<XmsgFrame> replies = TadResponder.OnTerminalInput(incoming, out bool _);
                        for (int i = 0; i < replies.Count; i++)
                        {
                            result.Add(replies[i]);
                        }
                    }
                    else if (TadResponder.IsTerminalSetup(incoming))
                    {
                        // 100's TMOD/TTYP negotiation (arrives after our DUMM). Answer with the burst
                        // that produces the login screen: control 0x20, RESE, RESE, then the MOTD.
                        IReadOnlyList<XmsgFrame> bringup = TadResponder.OnTerminalSetup(incoming);
                        for (int i = 0; i < bringup.Count; i++)
                        {
                            result.Add(bringup[i]);
                        }
                    }

                    return result;
                }
            }

            // Fallback: the single-frame handler covers reachability, list-route (XSGSY) and the
            // optional secure-ACK behaviour.
            XmsgFrame? single = HandleFrame(incoming);
            if (single != null)
            {
                result.Add(single);
            }

            // On an ACK, drain ONLY the servers whose output advances on the ACK (segmented mode,
            // window-of-1: the next segment is released now that the prior one is delivered). ConfirmDelivered
            // (in HandleFrame above) has already released the window. Servers paced by another signal - the
            // TAD SENTINEL stream, which advances on 100's 7DUMM commit via the dispatch path above - are NOT
            // drained here: draining THEM on the ACK sent the terminator before 100's DUMM committed the
            // preceding continuation, so 100 dropped it from display (the historical multi-chunk bug). The
            // opt-in (IXmsgServer.AdvancesOutputOnAck) keeps each mode on its correct pacing signal.
            if (ServerHost != null && incoming.Header.Subtype == SintranPacketSubtype.Ack)
            {
                IReadOnlyList<XmsgFrame> ackDrained = ServerHost.DrainOnAck();
                for (int i = 0; i < ackDrained.Count; i++)
                {
                    result.Add(ackDrained[i]);
                }
            }

            return result;
        }

        /// <summary>
        /// Returns true when a frame's decoded TAD chain contains at least one BDAT (terminal
        /// character-data) message.
        /// </summary>
        private static bool HasBdat(XmsgFrame frame)
        {
            if (frame.Tad == null)
            {
                return false;
            }

            IReadOnlyList<SubProtocol.TadMessage> messages = frame.Tad.Messages;
            for (int i = 0; i < messages.Count; i++)
            {
                if (messages[i].Opcode == 0x01)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Processes a received XMSG frame and returns the single frame to send in response, if
        /// any (reachability reply, list-route reply, or secure ACK). Use
        /// <see cref="HandleFrames"/> for paths that emit more than one frame.
        /// </summary>
        /// <param name="incoming">
        /// The decoded received frame (its header selects the behaviour).
        /// </param>
        /// <returns>
        /// The response frame, or <c>null</c> when none is needed.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="incoming"/> or its header is null.
        /// </exception>
        public XmsgFrame? HandleFrame(XmsgFrame incoming)
        {
            if (incoming == null)
            {
                throw new ArgumentNullException(nameof(incoming));
            }

            if (incoming.Header == null)
            {
                throw new ArgumentNullException(nameof(incoming), "Frame header is null.");
            }

            switch (incoming.Header.Subtype)
            {
                case SintranPacketSubtype.Ack:
                    // 100's delivery ACK confirms it received our frame (its Flags1 echoes ours).
                    // Persist ackedFlags1 + 1 as our next sequence, so a restart continues exactly
                    // where 100 expects - never ahead of what it actually received. ConfirmDelivered also
                    // releases a server's output flow-control window; the OUTER HandleFrames drains the
                    // newly-permitted chunk(s) after this returns (see the Ack-drain block there).
                    TadResponder?.ConfirmDelivered(incoming.Header.SourceNode, incoming.Header.Flags1);
                    ServerHost?.ConfirmDelivered(incoming.Header.SourceNode, incoming.Header.Flags1);
                    return null;

                case SintranPacketSubtype.NetworkError:
                    // SAY WHAT THE ERROR WAS. Everything below acts on ONE code (XENSE) and drops
                    // the rest on the floor, which made a live push look like plain silence: the
                    // machine WAS answering, and answering with a named reason, and nothing printed
                    // it. Measured 2026-08-11 on a push to D100 - two NetworkError frames arrived
                    // and the log said only "NO REPLY BUILT". The code rides Flags2 as a negative
                    // XMSG error number (see XmsgError, from XMSG-VALUES-M.SYMB).
                    Log?.Invoke(
                        "[net] node " + incoming.Header.SourceNode.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " rejected our frame at Flags1 0x" + incoming.Header.Flags1.ToString("X4", System.Globalization.CultureInfo.InvariantCulture)
                        + " with " + DescribeNetworkError(incoming.Header.Flags2));

                    // A XENSE (Flags2 0xFFDE) reject of our accept means our sequence
                    // was AHEAD of 100's expected-from-us (drift). Step the accept DOWN and re-send;
                    // one step per XENSE converges on 100's exact value with no restart. Only while
                    // the accept is still un-confirmed (before the session-setup).
                    if (TadResponder != null && TadResponder.CanResyncAccept)
                    {
                        return TadResponder.ResyncAcceptDown();
                    }

                    // Same recovery on the framework ServerHost path (the live runner): the host keeps
                    // the un-ACKed accept and rebuilds it one Flags1 lower per XENSE. The error code
                    // rides the Flags2 field of the subtype-0x07 frame; only XENSE (-34 = 0xFFDE) is a
                    // sequencing reject we can recover from - other codes (e.g. XEIMA 0xFFED) are not.
                    if (ServerHost != null && incoming.Header.Flags2 == unchecked((ushort)XmsgError.XENSE))
                    {
                        XmsgFrame? rebuilt = ServerHost.ResyncAcceptDown(incoming.Header.SourceNode);
                        if (rebuilt != null)
                        {
                            return rebuilt;
                        }

                        // NO PENDING ACCEPT, SO THE REFUSED FRAME WAS ONE WE ORIGINATED - a push
                        // connect letter, an FA request. That case had NO recovery at all and this
                        // is the hole the drift fell through: the refused frame has already spent
                        // and persisted its Flags 1, so without stepping back, every retry is
                        // refused one number FURTHER ahead than the last. Measured live 2026-08-24
                        // against D100: four attempts walked 0x007B -> 0x008C and nothing ever
                        // landed. Correct the outgoing sequence here; the originating layer's own
                        // retry then goes out at the corrected number.
                        ServerHost.ResyncOriginationDown(incoming.Header.SourceNode, incoming.Header.Flags1);
                        return null;
                    }

                    return null;

                case SintranPacketSubtype.ReachabilityRequest:
                    // A ReachabilityRequest is the peer's XMSG (re)start signal: its per-node-pair
                    // expected-from-us has just zeroed. Reset our persisted outgoing sequence for that
                    // node so our next session starts at 0x0000 in step with it (LIVE-VERIFIED: after
                    // an XMSG restart 100 sends this, then connects at Flags1 0x0000). No-op for a bare
                    // link restart, where 100 continues its sequence and sends no reachability.
                    TadResponder?.ResetSequence(incoming.Header.SourceNode);
                    ServerHost?.ResetSequence(incoming.Header.SourceNode);
                    return BuildReachabilityReply(incoming);

                case SintranPacketSubtype.Data:
                    // A list-route (XSGSY) request is answered from the routing table with the
                    // byte-validated ListRoutingServer reply (this is the structurally correct
                    // response, unlike a bare 0x03 ACK).
                    if (RoutingTable != null
                        && incoming.SubHeader != null
                        && incoming.XmcsmWithBodyWord == ListRoutingServer.XmcsmXsgsyRequest)
                    {
                        // Match the real 103 XSGSY response (device-online capture): the reply carries
                        // the SAME Flags1 as the request, so the asker can correlate it.
                        //
                        // The capture ALSO shows the response carrying the same counter byte and
                        // Protocol ID as its request (req f1=0000/ctr=0x13 -> resp f1=0000/ctr=0x13),
                        // and this used to echo both explicitly. That is not an echo - it is
                        // arithmetic. Those two bytes are the halves of word 6, which is a SUM over
                        // words 0-5. A reply differs from its request only by swapping destination
                        // and source node, and swapping two addends does not change a sum, so the
                        // computed checksum comes out identical on its own.
                        //
                        // So the reply is now built with the checksum COMPUTED (2026-08-06) and the
                        // echo dropped. ListRoutingTests still rebuilds the captured response byte
                        // for byte, which is the proof that the arithmetic explanation is the right
                        // one.
                        byte[] reply = _routingServer.Handle(
                            incoming,
                            RoutingTable,
                            flags1: incoming.Header.Flags1);
                        return XmsgFrame.Parse(reply);
                    }

                    // Otherwise: the secure ACK echoes Flags 1 and swaps direction (section 6),
                    // but only when configured to respond. Against the LIVE kernel, injecting an
                    // unrequested 0x03 ACK crashed XMSG (XXPER), so observe-only returns null.
                    return AcknowledgeData ? _receiver.ReceiveDataFrame(incoming) : null;

                default:
                    // ACK (0x03) and reachability reply (0x13) are terminal here.
                    return null;
            }
        }

        /// <summary>
        /// Builds the reachability reply (subtype <c>0x13</c>) for a received request.
        /// </summary>
        /// <param name="requestFrame">
        /// The received reachability request frame (subtype <c>0x19</c>); its trailing
        /// routing-counter byte drives the reply counter.
        /// </param>
        /// <returns>
        /// The reply frame with Dest/Src swapped, <c>Flags1 = 0xFFFF</c>,
        /// <c>Flags2 = 0x0001</c> and the reachability-reply command trailing byte.
        /// </returns>
        private XmsgFrame BuildReachabilityReply(XmsgFrame requestFrame)
        {
            SintranHeader request = requestFrame.Header;
            XmsgFrame reply = new XmsgFrame();

            // Copy the fixed markers/type verbatim so re-encode is byte-identical.
            reply.Header.Marker1 = request.Marker1;
            reply.Header.Marker2 = request.Marker2;
            reply.Header.PacketType = request.PacketType;
            reply.Header.Subtype = SintranPacketSubtype.ReachabilityReply;   // 0x13
            reply.Header.DestinationNode = request.SourceNode;               // swap direction
            reply.Header.SourceNode = request.DestinationNode;
            reply.Header.Flags1 = 0xFFFF;                                    // broadcast marker
            reply.Header.Flags2 = 0x0001;
            // DERIVED 2026-08-04, and it explains the old rule. Offsets 12-13 are header word 6,
            // the ones-complement checksum over words 0-5, so the reply carries no trailing byte at
            // all - the whole frame is fourteen bytes.
            //
            // The rule this replaces was "INFERRED: the reply's trailing byte = request byte + 6",
            // fitted to both captured pairs (node 102: 0x08 -> 0x0E; node 103: 0x07 -> 0x0D). It is
            // now a consequence rather than a guess: a reply differs from its request only in the
            // subtype (0x19 request, 0x13 reply, difference 6) and in the swapped node numbers,
            // which cancel in a sum. A sum six smaller complements to a value six LARGER, so the
            // checksum low byte necessarily rises by 6 - exactly what both captures show.
            XmsgEnvelope.StampChecksum(reply.Header);
            return reply;
        }

        /// <summary>
        /// Forwards the receiver's delivery event to this node's own event.
        /// </summary>
        /// <param name="datagramSequence">
        /// The datagram sequence of the delivered data frame.
        /// </param>
        private void RaiseDelivered(ushort datagramSequence)
        {
            OnDataDelivered?.Invoke(datagramSequence);
        }

        /// <summary>
        /// Learns the per-link seed from the FIRST valid data frame and returns it; later frames only
        /// VALIDATE against it. The seed is a per-link constant (VERIFIED across every session,
        /// reconnect and reboot in the corpus - 0x14 for 100 to/from 102), so re-learning per frame is
        /// wrong: one out-of-model received frame would poison the seed for the frames we originate
        /// next (measured live 2026-07-07: a burst chunk carried the Counter for seed 0x16 instead of
        /// 0x14). A mismatch is logged with the offending frame's envelope - that frame is the thing
        /// to hunt in a capture.
        /// </summary>
        /// <param name="incoming">
        /// The received data frame (must have a sub-header).
        /// </param>
        /// <returns>
        /// The established link seed.
        /// </returns>
        private byte LearnLinkSeedOnce(XmsgFrame incoming)
        {
            byte implied = XmsgEnvelope.LearnSeed(
                incoming.Header!.Flags1, (byte)(incoming.Header!.Checksum & 0x00FF), incoming.Header.Flags2);

            if (!_linkSeedLearned)
            {
                _linkSeed = implied;
                _linkSeedLearned = true;
                return _linkSeed;
            }

            if (implied != _linkSeed)
            {
                Log?.Invoke(
                    $"[node] WARNING: frame from node {incoming.Header.SourceNode} F1=0x{incoming.Header.Flags1:X4} ctr=0x{(byte)(incoming.Header.Checksum & 0x00FF):X2} F2=0x{incoming.Header.Flags2:X4} implies seed 0x{implied:X2} but link seed is 0x{_linkSeed:X2} - keeping 0x{_linkSeed:X2} (out-of-model frame)");
            }

            return _linkSeed;
        }

        /// <summary>
        /// Names the XMSG error code carried in a subtype-<c>0x07</c> frame's Flags2 field.
        /// </summary>
        /// <param name="flags2">
        /// The Flags2 word, a negative XMSG error number in two's complement.
        /// </param>
        /// <returns>
        /// The symbol and number when the code is known, otherwise the raw value.
        /// </returns>
        /// <remarks>
        /// The two seen live are called out because they mean opposite things and the difference
        /// decides what to do next:
        ///  - <c>XENSE</c> (-34) says OUR sequence is ahead of what the peer expects, and stepping
        ///    down recovers it.
        ///  - <c>XEIMA</c> (-19) says the magic number was invalid - the wrong port or a closed
        ///    receiving port - and no amount of sequence work will fix it.
        /// Anything else is printed as a number rather than guessed at.
        /// </remarks>
        private static string DescribeNetworkError(ushort flags2)
        {
            short code = unchecked((short)flags2);
            string name;
            switch (code)
            {
                case (short)XmsgError.XENSE:
                    name = "XENSE (our sequence is AHEAD of what the peer expects)";
                    break;
                case (short)XmsgError.XEIMA:
                    name = "XEIMA (invalid magic number - wrong port, or the receiving port is closed)";
                    break;
                default:
                    name = "an error this node does not name";
                    break;
            }

            return name + " Flags2=0x" + flags2.ToString("X4", System.Globalization.CultureInfo.InvariantCulture)
                + " (" + code.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
        }
    }
}
