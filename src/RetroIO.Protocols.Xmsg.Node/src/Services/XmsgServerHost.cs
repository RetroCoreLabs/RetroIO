//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Packet;

namespace RetroIO.Protocols.Xmsg.Node.Services
{
    /// <summary>
    /// The node-side host that routes XMSG server traffic to registered <see cref="IXmsgServer"/>s and
    /// gives them the low-level <see cref="IXmsgServerTransport"/> to reply with. It owns the per-remote-node
    /// links (seed + continuous Flags 1), allocates session ports and numbers, and performs the XROUT
    /// port-0 dispatch on the XMCSM low byte.
    /// </summary>
    /// <remarks>
    /// <para><b>Dispatch (XMSG-PROTOCOL.md section 7 / 22).</b></para>
    /// Server requests arrive at port 0 and fork on the XMCSM low "service" byte:
    ///  - <c>0x41</c> (XSLET, "send a letter") - the letter is parsed for its target name and routed to
    ///    the matching registered server. A connect-to and a list-systems query are both XSLET letters.
    ///  - <c>0x4B</c> (XSGSY, "get routing info") - list-route; answered by the node's routing server, not
    ///    a named server, so this host returns nothing for it and the node handles it.
    /// After the accept, session traffic is ports-only: a datagram to a non-zero port is routed to the
    /// server that <see cref="IXmsgServer.OwnsPort"/>s it (the server's well-known reply-from port for the
    /// session-setup, then the session port the accept advertised for terminal data).
    /// </remarks>
    public sealed class XmsgServerHost : IXmsgServerTransport
    {
        private readonly ushort _nodeNumber;
        private readonly IResponderSequenceStore _store;
        private readonly List<IXmsgServer> _servers;
        private readonly Dictionary<ushort, XmsgLink> _links;

        // The last un-ACKed connect-accept per remote node, kept so a XENSE sequencing reject can be
        // recovered by rebuilding the accept one Flags 1 lower (ResyncAcceptDown). Cleared on the ACK.
        private readonly Dictionary<ushort, PendingAccept> _pendingAccepts = new Dictionary<ushort, PendingAccept>();

        /// <summary>
        /// Consecutive XENSE step-downs applied to ORIGINATED frames, per remote node. Cleared the
        /// moment the peer acknowledges anything, because an ACK proves the two sides are back in
        /// step and the next drift must be counted from scratch rather than inheriting a stale
        /// count that would send it straight to the zero fallback.
        /// </summary>
        private readonly Dictionary<ushort, int> _originationResyncs = new Dictionary<ushort, int>();

        // Session port allocation preserves the live-verified first-session value 0x0211 and increments
        // the incarnation for each further session, so ports are unique across servers and sessions.
        private ushort _nextSessionPort;
        private int _nextSessionNumber;

        /// <summary>
        /// How many times an accept may be stepped down before we stop trying.
        /// </summary>
        /// <remarks>
        /// <para><b>Sized from a measured drift, no longer from a guess</b></para>
        /// <para>
        /// This was 8, on the premise that a genuine drift is only ever a step or two. A live run
        /// on 2026-08-09 measured the real thing against D100 and the premise was wrong. Our
        /// stored sequence for that node was <c>0x001F</c>, and the two failure modes bracket the
        /// answer from both sides:
        /// </para>
        /// <code>
        /// ours 0x001F   D100 answers XENSE          - we are AHEAD of what it expects
        /// ours 0x0000   D100 answers nothing at all - we are BEHIND, and it drops us silently
        /// </code>
        /// <para>
        /// So the value it wants lies between them, and stepping down one per XENSE walks onto it
        /// - the recovery works, it just needs enough room to cross a drift that had reached
        /// thirty-odd. Eight steps could not, and stopping short looks exactly like a protocol
        /// fault: the peer keeps retrying and nothing explains why.
        /// </para>
        /// <para>
        /// Still bounded, because the cost of not stopping is a flood that hides the actual fault
        /// - measured at 127 rejects in sixteen seconds when the bound was missing. Forty steps
        /// is under a second of traffic and covers the whole range a stored sequence can hold
        /// before a person would restart the runner anyway.
        /// </para>
        /// </remarks>
        private const int MaxAcceptResyncAttempts = 40;

        /// <summary>
        /// How many one-step-down corrections an ORIGINATED frame gets after a XENSE before
        /// <see cref="ResyncOriginationDown"/> stops walking and drops to the post-restart
        /// baseline of zero. Deliberately small: a real drift between two machines that were in
        /// step is a handful of frames, so needing more than this says the cause is a peer restart
        /// we did not witness, and walking a hundred-plus steps at one reject each would take
        /// minutes of hammering the machine to reach the same place.
        /// </summary>
        private const int MaxOriginationResyncAttempts = 8;

        // XMCSM low-byte "service" code (XMSG-PROTOCOL.md section 9.1): send a letter (connect / list-systems).
        private const byte XsletServiceByte = 0x41;

        // The connect letter (and every server request) is addressed to XROUT's well-known port 0.
        private const ushort XroutRequestPort = 0x0000;

        /// <summary>
        /// Initialises the host for a node.
        /// </summary>
        /// <param name="nodeNumber">
        /// This node's number (for example 102).
        /// </param>
        /// <param name="store">
        /// The persistent outgoing-sequence store (per remote node). When null, a non-persisting store is
        /// used (every link starts at 0x0000).
        /// </param>
        public XmsgServerHost(ushort nodeNumber, IResponderSequenceStore? store = null)
        {
            _nodeNumber = nodeNumber;
            _store = store ?? new NullResponderSequenceStore();
            _servers = new List<IXmsgServer>();
            _links = new Dictionary<ushort, XmsgLink>();
            // Fixed port word, kept deliberately. Its SHAPE is correct - port number 4 in the
            // high nine bits, 0x11 in the low seven - and a responder may answer on any
            // well-formed port word, which is VERIFIED live with this exact value. A real kernel
            // would draw the low seven bits from ZRAND instead; XmsgPortWordAllocator does that
            // if we ever want the traffic to look kernel-minted, but switching is a live-behaviour
            // change and buys fidelity, not function.
            _nextSessionPort = (ushort)((4 << 7) | 0x11);   // 0x0211, the live-verified layout
            _nextSessionNumber = 1;
        }

        /// <summary>
        /// Gets this node's number.
        /// </summary>
        public ushort NodeNumber
        {
            get { return _nodeNumber; }
        }

        /// <summary>
        /// Optional diagnostics sink. Used for envelope anomalies (for example a received frame whose
        /// implied seed disagrees with the established link seed - an out-of-model frame worth capturing).
        /// </summary>
        public XmsgLogHandler? Log { get; set; }

        /// <summary>
        /// Registers a server (for example the TAD <c>*TADADM</c> server).
        /// </summary>
        /// <param name="server">
        /// The server to register.
        /// </param>
        public void Register(IXmsgServer server)
        {
            if (server == null)
            {
                throw new ArgumentNullException(nameof(server));
            }

            _servers.Add(server);
        }

        /// <summary>
        /// Routes an incoming datagram to the owning server and returns its reply frames. Returns an
        /// empty list when no server owns the datagram (for example an XSGSY list-route request, which
        /// the node answers itself).
        /// </summary>
        /// <param name="incoming">
        /// The received datagram (subtype Data).
        /// </param>
        /// <returns>
        /// The reply frames, in order (empty when unrouted).
        /// </returns>
        public IReadOnlyList<XmsgFrame> Route(XmsgFrame incoming)
        {
            if (incoming == null || incoming.Header == null || incoming.SubHeader == null)
            {
                return Array.Empty<XmsgFrame>();
            }

            // Learn / refresh the link seed from any data frame so BuildDatagram can derive counters.
            EnsureLink(incoming);

            ushort destPort = incoming.SubHeader.DestinationPort;
            if (destPort == XroutRequestPort)
            {
                // Port 0: fork on the XMCSM low "service" byte.
                byte serviceByte = (byte)(incoming.XmcsmWithBodyWord & 0xFF);
                if (serviceByte == XsletServiceByte)
                {
                    // XSLET letter - route by the target name inside the letter.
                    string name = ExtractLetterName(incoming);
                    IXmsgServer? byName = FindByName(name);
                    if (byName != null)
                    {
                        return byName.Handle(incoming, this);
                    }
                }

                // XSGSY (list-route) and unknown letters are not server-routed here.
                return Array.Empty<XmsgFrame>();
            }

            // Non-zero port: session traffic, routed to the server that owns that port.
            IXmsgServer? byPort = FindByPort(destPort);
            if (byPort != null)
            {
                return byPort.Handle(incoming, this);
            }

            return Array.Empty<XmsgFrame>();
        }

        /// <summary>
        /// Drains every registered server's queued asynchronous output (tty inject / wall) into frames.
        /// The node calls this each pump cycle so queued text flushes to the remote clients.
        /// </summary>
        /// <returns>
        /// The queued frames from all servers, in order (empty when nothing is pending).
        /// </returns>
        public IReadOnlyList<XmsgFrame> DrainPending()
        {
            List<XmsgFrame> all = new List<XmsgFrame>();
            for (int i = 0; i < _servers.Count; i++)
            {
                IReadOnlyList<XmsgFrame> part = _servers[i].DrainPending(this);
                for (int j = 0; j < part.Count; j++)
                {
                    all.Add(part[j]);
                }
            }

            return all;
        }

        /// <summary>
        /// Drains only the servers whose windowed output advances on an ACK
        /// (<see cref="IXmsgServer.AdvancesOutputOnAck"/>). Called by the node right after it applies an
        /// incoming ACK (<see cref="ConfirmDelivered"/>) so a window-of-1 segmented reply releases its next
        /// segment on the ACK. Servers paced by another in-band signal (the TAD sentinel stream, driven by
        /// 100's 7DUMM commit) are skipped, so an ACK never prematurely releases their next frame.
        /// </summary>
        /// <returns>
        /// The frames released by ACK-advancing servers, in order (possibly empty).
        /// </returns>
        public IReadOnlyList<XmsgFrame> DrainOnAck()
        {
            List<XmsgFrame> all = new List<XmsgFrame>();
            for (int i = 0; i < _servers.Count; i++)
            {
                if (!_servers[i].AdvancesOutputOnAck)
                {
                    continue;
                }

                IReadOnlyList<XmsgFrame> part = _servers[i].DrainPending(this);
                for (int j = 0; j < part.Count; j++)
                {
                    all.Add(part[j]);
                }
            }

            return all;
        }

        /// <summary>
        /// Describes every registered server (name, ports, sessions, free slots) for the
        /// <c>list servers</c> command - the COSMOS <c>list-servers</c> shape.
        /// </summary>
        /// <returns>
        /// One <see cref="XmsgServerInfo"/> per registered server.
        /// </returns>
        public IReadOnlyList<XmsgServerInfo> DescribeServers()
        {
            List<XmsgServerInfo> list = new List<XmsgServerInfo>(_servers.Count);
            for (int i = 0; i < _servers.Count; i++)
            {
                IXmsgServer server = _servers[i];
                list.Add(new XmsgServerInfo(server.Name, server.LogicalPort, server.WirePort, server.SessionCount, server.SessionCapacity));
            }

            return list;
        }

        /// <summary>
        /// Records that a remote node ACKed one of our frames, advancing the persisted next-sequence for
        /// that link to <c>ackedFlags1 + 1</c> (never past what was actually received).
        /// </summary>
        /// <param name="remoteNode">
        /// The node that ACKed.
        /// </param>
        /// <param name="ackedFlags1">
        /// The Flags 1 the ACK echoes.
        /// </param>
        public void ConfirmDelivered(ushort remoteNode, ushort ackedFlags1)
        {
            if (ackedFlags1 == 0xFFFF)
            {
                return;
            }

            // The peer acknowledged something, so our outgoing sequence is in step with it again.
            // Forget any XENSE step-downs counted against this node - the next drift, if there is
            // one, starts its own walk. Without this the count survives a recovery and the second
            // drift of a long session would skip the walk and go straight to the zero fallback.
            _originationResyncs.Remove(remoteNode);

            ushort next = (ushort)(ackedFlags1 + 1);
            ushort current = _store.LoadNextFlags1(remoteNode);
            if (next > current)
            {
                _store.SaveNextFlags1(remoteNode, next);
                Log?.Invoke($"[seq] node {remoteNode}: ACK 0x{ackedFlags1:X4} PERSISTED next 0x{next:X4} (was 0x{current:X4})");
            }
            else
            {
                // Worth logging too: it means the peer acknowledged something at or below what we
                // have already stored, so the store does NOT move. A run where every ACK lands here
                // would leave the persisted sequence frozen, which is one of the two explanations
                // task #28 is trying to tell apart.
                Log?.Invoke($"[seq] node {remoteNode}: ACK 0x{ackedFlags1:X4} did NOT advance the store (still 0x{current:X4})");
            }

            // The peer ACKed our accept -> the sequence was accepted; no XENSE resync possible/needed.
            if (_pendingAccepts.TryGetValue(remoteNode, out PendingAccept? pending) && pending.Flags1 == ackedFlags1)
            {
                _pendingAccepts.Remove(remoteNode);
            }

            // Let a server that is streaming a windowed multi-chunk reply release its flow-control window
            // for the acked output frame. Harmless for servers that do no windowed output.
            for (int i = 0; i < _servers.Count; i++)
            {
                _servers[i].NotifyAck(remoteNode, ackedFlags1);
            }
        }

        /// <summary>
        /// Resets a link's outgoing sequence to 0x0000 (a peer XMSG restart), dropping any in-memory
        /// link state so the next contact reloads from the reset store.
        /// </summary>
        /// <param name="remoteNode">
        /// The node that (re)started.
        /// </param>
        public void ResetSequence(ushort remoteNode)
        {
            // Logged with the value being discarded, because a reset is not always right: it is
            // correct when the PEER restarted (it announces, its expectation of us really is zero),
            // and it is what put the one working conversation of 2026-08-08 in step. Whether OUR
            // announce persuades the peer to do the same is the open half of task #28, and that
            // cannot be read off the wire - so record what we threw away.
            ushort discarded = _store.LoadNextFlags1(remoteNode);
            Log?.Invoke(
                $"[seq] node {remoteNode}: RESET to 0x0000 (was 0x{discarded:X4}); in-memory link dropped");

            _store.SaveNextFlags1(remoteNode, 0x0000);
            _links.Remove(remoteNode);
            _pendingAccepts.Remove(remoteNode);
        }

        /// <summary>
        /// Gets or sets the store that remembers learned link seeds. Optional; without one the
        /// node behaves exactly as before and can only address a peer that has spoken to it.
        /// </summary>
        public ILinkSeedStore? SeedStore { get; set; }

        /// <summary>
        /// Opens a link to a node we have met before, using the seed remembered from that meeting.
        /// </summary>
        /// <param name="remoteNode">
        /// The node to open a link to.
        /// </param>
        /// <returns>
        /// <c>true</c> when a link now exists - either it already did, or a remembered
        /// seed was found and used. <c>false</c> when this node has never been heard
        /// from, in which case it still cannot be addressed.
        /// </returns>
        /// <remarks>
        /// <para><b>What this fixes</b></para>
        /// <para>
        /// The seed is learned from an inbound datagram and never derived, so a fresh process could
        /// not originate to a peer that had not spoken since it started. Fine for a server;
        /// useless for a daemon. MEASURED 2026-08-11: the folder-watch sync sat with a file queued
        /// reporting "the XMSG layer cannot address node 100 yet" until somebody typed a command on
        /// the far machine.
        /// </para>
        /// <para><b>What it does NOT do</b></para>
        /// <para>
        /// It never invents a seed. A node we have genuinely never heard from returns false and
        /// stays unreachable, because guessing the value would put a Counter on the wire that the
        /// peer cannot make sense of. This only stops us forgetting one we were told.
        /// </para>
        /// <para>
        /// The outgoing sequence still comes from the sequence store, exactly as it does when an
        /// inbound frame creates the link - the two stores answer different questions and neither
        /// is derived from the other.
        /// </para>
        /// </remarks>
        public bool OpenLinkFromRememberedSeed(ushort remoteNode)
        {
            if (_links.ContainsKey(remoteNode))
            {
                return true;
            }

            byte seed;
            if (SeedStore == null || !SeedStore.TryLoadSeed(remoteNode, out seed))
            {
                return false;
            }

            ushort startingFlags1 = _store.LoadNextFlags1(remoteNode);
            _links[remoteNode] = new XmsgLink(remoteNode, seed, startingFlags1);

            Log?.Invoke(
                $"[seq] node {remoteNode}: link opened from the REMEMBERED seed 0x{seed:X2}, "
                + $"starting Flags1 0x{startingFlags1:X4} from the store (nothing was received first)");

            return true;
        }

        /// <summary>
        /// Zeroes our outgoing sequence for a node while KEEPING the learned link.
        /// </summary>
        /// <param name="remoteNode">
        /// The node whose counter is being zeroed.
        /// </param>
        /// <remarks>
        /// <para>Why this is not <see cref="ResetSequence"/></para>
        /// <para>
        /// <see cref="ResetSequence"/> answers the PEER telling us it restarted, and it drops the
        /// in-memory link on purpose: a machine that has restarted may come back with different
        /// link state, so keeping ours would be keeping something stale. That is safe there because
        /// the peer is about to address us anyway, which rebuilds the link at once.
        /// </para>
        /// <para>
        /// This one answers OUR OWN announce, where nothing about the peer's identity has changed -
        /// only its expectation of our numbering. Dropping the link there strands us: we can only
        /// LEARN a link from an inbound datagram, so after zeroing we would sit unable to address a
        /// peer we were talking to a moment earlier. MEASURED 2026-08-11: the sync daemon did
        /// exactly that, reporting "the XMSG layer cannot address node 100 yet" once every five
        /// seconds with a file queued and a healthy link underneath it.
        /// </para>
        /// </remarks>
        public void ResetSequenceKeepingLink(ushort remoteNode)
        {
            ushort discarded = _store.LoadNextFlags1(remoteNode);
            _store.SaveNextFlags1(remoteNode, 0x0000);

            // The live link carries its own next value, so zeroing only the store would be undone
            // by the very next frame it stamps.
            XmsgLink? link;
            if (_links.TryGetValue(remoteNode, out link))
            {
                link.NextFlags1 = 0x0000;
            }

            _pendingAccepts.Remove(remoteNode);

            Log?.Invoke(
                $"[seq] node {remoteNode}: RESET to 0x0000 (was 0x{discarded:X4}); link KEPT");
        }

        /// <summary>
        /// Recovers from a XENSE (network sequencing error, code <c>0xFFDE</c>) reject of our
        /// connect-accept: our Flags 1 was AHEAD of the peer's expected-from-us (typically the peer's
        /// XMSG restarted while our persisted sequence had climbed). Rebuilds the accept one Flags 1
        /// LOWER and corrects the link + persisted sequence; one step per XENSE converges on the
        /// peer's exact expected value, at which point it answers with the session-setup instead of
        /// another XENSE - the same learn-from-the-peer recovery the legacy responder used, with no
        /// restart or state-file surgery.
        /// </summary>
        /// <param name="remoteNode">
        /// The node that sent the XENSE.
        /// </param>
        /// <returns>The rebuilt accept at the next-lower sequence, or null when there is no
        /// un-ACKed accept to resync (the error concerns something else).</returns>
        public XmsgFrame? ResyncAcceptDown(ushort remoteNode)
        {
            if (!_pendingAccepts.TryGetValue(remoteNode, out PendingAccept? pending)
                || !_links.TryGetValue(remoteNode, out XmsgLink? link))
            {
                return null;
            }

            // GIVE UP RATHER THAN STORM. Stepping down one per XENSE only converges if the peer is
            // a small drift ahead of us. When the reject has some OTHER cause, every step draws
            // another reject and the two machines trade frames until somebody notices: measured
            // 2026-08-09, 127 rejects in sixteen seconds against a live D100, and it was still
            // going when the runner was killed. A recovery that cannot converge must stop and say
            // so - a stall is diagnosable, a flood buries the evidence and hammers the machine.
            if (pending.ResyncAttempts >= MaxAcceptResyncAttempts)
            {
                _pendingAccepts.Remove(remoteNode);
                Log?.Invoke(
                    $"[host] GIVING UP: node {remoteNode} rejected the accept {MaxAcceptResyncAttempts}"
                    + $" times running, last at Flags1 0x{pending.Flags1:X4}. Stepping down is not"
                    + " converging, so the reject is NOT a small sequence drift. Nothing more is"
                    + " sent for this accept.");
                return null;
            }

            pending.ResyncAttempts++;

            ushort f1 = (ushort)(pending.Flags1 - 1);
            pending.Flags1 = f1;
            link.NextFlags1 = (ushort)(f1 + 1);
            // Correct the stored value too, so a later restart uses the recovered base (ACK-driven
            // persistence then advances it as usual).
            _store.SaveNextFlags1(remoteNode, f1);
            Log?.Invoke($"[host] XENSE from node {remoteNode}: accept was ahead - resending at Flags1 0x{f1:X4}");

            return AssembleDatagram(link, remoteNode, pending.ClientSystem, pending.ClientPort,
                pending.SourcePort, pending.ControlService, pending.FrameFlags, pending.Role,
                pending.Payload, f1);
        }

        /// <summary>
        /// Recovers from a XENSE reject of a frame we ORIGINATED (a push connect letter, an FA
        /// request - anything that is not the connect-accept <see cref="ResyncAcceptDown"/> covers).
        /// </summary>
        /// <remarks>
        /// <para><b>The defect this repairs, measured twice</b></para>
        /// <para>
        /// <see cref="ChooseFlags1"/> persists on send, which is right - it is what the kernel does.
        /// But a frame the peer then refuses with XENSE has ALREADY consumed and persisted its
        /// number, and nothing anywhere stepped it back. So every rejection left us one FURTHER
        /// ahead of the peer, and the retry that followed was refused for the same reason at a worse
        /// number. Measured 2026-08-11 (0x003B -> 0x0040) and again live on 2026-08-24, where four
        /// connect-letter attempts against D100 walked 0x007B -> 0x008C while nothing got through.
        /// The recovery existed only for accepts; originations ran off the end.
        /// </para>
        /// <para><b>Why stepping DOWN is the right move</b></para>
        /// <para>
        /// XENSE (-34, <c>0xFFDE</c>) has exactly one meaning: our number is AHEAD of the value the
        /// peer expects from us. The peer never transmits that value, so it cannot be read off the
        /// wire - but the direction is unambiguous, so one step down per reject walks onto it, which
        /// is the same convergence <see cref="ResyncAcceptDown"/> already used.
        /// </para>
        /// <para><b>And why there is a floor of zero rather than only a step</b></para>
        /// <para>
        /// A one-per-reject walk converges for a small drift. It does NOT converge in useful time
        /// for the common large one: the peer's XMSG restarted while we were not connected, which
        /// sets its expected-from-us back to 0 and sends a ReachabilityRequest we were not there to
        /// hear. Our store then holds a number that may be hundreds ahead. That case is why the
        /// manual fix in the notes is "write 0 into xmsg-sequence.state", after which the whole
        /// ladder climbs on the very next attempt. So when stepping stops converging, this drops to
        /// 0 - the value the protocol says the peer holds after a restart - and says so loudly
        /// rather than stalling with the operator none the wiser.
        /// </para>
        /// </remarks>
        /// <param name="remoteNode">
        /// The node that sent the XENSE.
        /// </param>
        /// <param name="refusedFlags1">
        /// The Flags 1 the peer refused, taken from the rejecting frame's header - it echoes the
        /// number it would not accept.
        /// </param>
        /// <returns>
        /// True when the outgoing sequence was moved, so the caller's next attempt uses the
        /// corrected number; false when there is no link for the node.
        /// </returns>
        public bool ResyncOriginationDown(ushort remoteNode, ushort refusedFlags1)
        {
            if (!_links.TryGetValue(remoteNode, out XmsgLink? link))
            {
                return false;
            }

            if (!_originationResyncs.TryGetValue(remoteNode, out int attempts))
            {
                attempts = 0;
            }

            attempts++;
            _originationResyncs[remoteNode] = attempts;

            ushort corrected;
            if (attempts >= MaxOriginationResyncAttempts || refusedFlags1 == 0)
            {
                // GIVE UP RATHER THAN GUESS.
                //
                // This used to drop the sequence to 0 here, on the theory that a walk which will
                // not converge means the peer's XMSG restarted. MEASURED 2026-08-24 against D100,
                // and it was WRONG - actively worse than doing nothing. D100 had not restarted:
                // LIST-SYSTEMS showed it wanted 122 (0x007A) while we sat at 0x008C, a drift of
                // eighteen. The reset threw us from eighteen AHEAD to a hundred and twenty-two
                // BEHIND, and behind is the direction SINTRAN drops in SILENCE - so a loud,
                // diagnosable refusal became no answer at all.
                //
                // The genuine peer-restart case does NOT need guessing for: the peer announces it
                // with a ReachabilityRequest, and ResetSequence already zeroes us on that. If we
                // were not connected to hear it, the number is simply not knowable from here - the
                // peer's expectation of US is never transmitted. So stop, keep the sequence where
                // stepping left it, and name the one command that answers it.
                corrected = link.NextFlags1;
                Log?.Invoke(
                    $"[seq] node {remoteNode}: GIVING UP after {attempts} XENSE step(s), last refused"
                    + $" 0x{refusedFlags1:X4}. Stepping down is not converging, and this does NOT"
                    + " reset the sequence - guessing 0 here once turned an 18-frame drift into a"
                    + " 122-frame one, which the peer then dropped in silence. The peer's expected"
                    + " value is never sent on the wire; READ IT with X-COMM LIST-SYSTEMS on the"
                    + " machine - the RECEIVE column for this node is the next Flags1 it wants from"
                    + " us - and put that number in the sequence store.");
            }
            else
            {
                // One below the number that was refused: the peer wants something lower, and this
                // is the largest value that is certainly lower.
                corrected = (ushort)(refusedFlags1 - 1);
                Log?.Invoke(
                    $"[seq] node {remoteNode}: XENSE at Flags1 0x{refusedFlags1:X4} - we are AHEAD;"
                    + $" stepping our outgoing sequence down to 0x{corrected:X4} (step {attempts})");
            }

            link.NextFlags1 = corrected;
            _store.SaveNextFlags1(remoteNode, corrected);
            return true;
        }

        /// <summary>
        /// The build inputs of an un-ACKed connect-accept, retained so <see cref="ResyncAcceptDown"/>
        /// can rebuild it at a lower Flags 1 after a XENSE reject.
        /// </summary>
        private sealed class PendingAccept
        {
            /// <summary>
            /// Initialises the retained accept inputs.
            /// </summary>
            /// <param name="clientSystem">
            /// The client's system number.
            /// </param>
            /// <param name="clientPort">
            /// The client's port.
            /// </param>
            /// <param name="sourcePort">
            /// Our source port.
            /// </param>
            /// <param name="controlService">
            /// The XMCSM control/service word.
            /// </param>
            /// <param name="frameFlags">
            /// The sub-header frame-flags byte.
            /// </param>
            /// <param name="role">
            /// The sub-header role byte.
            /// </param>
            /// <param name="payload">
            /// The trailer payload bytes.
            /// </param>
            /// <param name="flags1">
            /// The Flags 1 the accept was sent with.
            /// </param>
            public PendingAccept(
                ushort clientSystem, ushort clientPort, ushort sourcePort, uint controlService,
                byte frameFlags, byte role, byte[] payload, ushort flags1)
            {
                ClientSystem = clientSystem;
                ClientPort = clientPort;
                SourcePort = sourcePort;
                ControlService = controlService;
                FrameFlags = frameFlags;
                Role = role;
                Payload = payload;
                Flags1 = flags1;
            }

            /// <summary>
            /// Gets the client's system number.
            /// </summary>
            public ushort ClientSystem { get; }

            /// <summary>
            /// Gets the client's port.
            /// </summary>
            public ushort ClientPort { get; }

            /// <summary>
            /// Gets our source port.
            /// </summary>
            public ushort SourcePort { get; }

            /// <summary>
            /// Gets the XMCSM control/service word.
            /// </summary>
            public uint ControlService { get; }

            /// <summary>
            /// Gets the sub-header frame-flags byte.
            /// </summary>
            public byte FrameFlags { get; }

            /// <summary>
            /// Gets the sub-header role byte.
            /// </summary>
            public byte Role { get; }

            /// <summary>
            /// Gets the trailer payload bytes.
            /// </summary>
            public byte[] Payload { get; }

            /// <summary>
            /// Gets or sets the Flags 1 of the (re)sent accept.
            /// </summary>
            public ushort Flags1 { get; set; }

            /// <summary>
            /// Gets or sets how many times this accept has been stepped down after a XENSE.
            /// </summary>
            /// <remarks>
            /// Counted per accept rather than per node, so a fresh accept starts with a full
            /// allowance: the previous one giving up says nothing about this one.
            /// </remarks>
            public int ResyncAttempts { get; set; }
        }

        /// <summary>
        /// Allocates a globally-unique session port.
        /// </summary>
        /// <returns>
        /// A fresh session port.
        /// </returns>
        public ushort AllocateSessionPort()
        {
            ushort port = _nextSessionPort;
            _nextSessionPort = (ushort)(_nextSessionPort + 1);
            return port;
        }

        /// <summary>
        /// Allocates a monotonic session number (the operator-visible ttyN / TAD number).
        /// </summary>
        /// <returns>
        /// A fresh 1-based session number.
        /// </returns>
        public int AllocateSessionNumber()
        {
            int number = _nextSessionNumber;
            _nextSessionNumber++;
            return number;
        }

        /// <summary>
        /// Decides whether a datagram can be built for a node yet.
        /// </summary>
        /// <param name="remoteNode">
        /// The node we want to send to.
        /// </param>
        /// <returns>
        /// <c>true</c> when a link to that node has been seen and its seed learned.
        /// </returns>
        /// <remarks>
        /// The same lookup <see cref="BuildDatagram"/> makes, offered as a question instead of as
        /// an exception, so a caller that ORIGINATES can wait rather than crash.
        /// </remarks>
        public bool CanReach(ushort remoteNode)
        {
            return _links.ContainsKey(remoteNode);
        }

        /// <summary>
        /// Builds one outgoing datagram to a client endpoint, assigning the per-link Flags 1 and deriving
        /// the Counter and channel from the envelope model.
        /// </summary>
        /// <param name="remoteNode">
        /// The client's node number.
        /// </param>
        /// <param name="clientSystem">
        /// The client's system number.
        /// </param>
        /// <param name="clientPort">
        /// The client's port.
        /// </param>
        /// <param name="sourcePort">
        /// Our source port.
        /// </param>
        /// <param name="controlService">
        /// The XMCSM control/service word.
        /// </param>
        /// <param name="frameFlags">
        /// The sub-header frame-flags byte.
        /// </param>
        /// <param name="role">
        /// The sub-header role byte.
        /// </param>
        /// <param name="payload">
        /// The trailer payload bytes.
        /// </param>
        /// <param name="answeredFlags1">
        /// The Flags 1 of the request this frame ANSWERS, to be echoed, or
        /// <see cref="XmsgAnsweredFlags1.None"/> when it is originated. See
        /// <see cref="ChooseFlags1"/>.
        /// </param>
        /// <returns>
        /// The assembled datagram.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no link to <paramref name="remoteNode"/> exists.
        /// </exception>
        public XmsgFrame BuildDatagram(
            ushort remoteNode,
            ushort clientSystem,
            ushort clientPort,
            ushort sourcePort,
            uint controlService,
            byte frameFlags,
            byte role,
            byte[] payload,
            int answeredFlags1)
        {
            if (!_links.TryGetValue(remoteNode, out XmsgLink? link))
            {
                throw new InvalidOperationException($"No XMSG link to node {remoteNode} (no seed learned).");
            }

            ushort f1 = ChooseFlags1(link, answeredFlags1);

            // Retain the accept so a XENSE reject (our sequence AHEAD of the peer's expected-from-us,
            // e.g. after the peer's XMSG restarted while our persisted sequence had climbed) can be
            // recovered by ResyncAcceptDown - the same step-down-one-per-XENSE convergence the legacy
            // responder used. Cleared when the peer ACKs the accept (ConfirmDelivered).
            //
            // THE SERVICE BYTE ALONE IS NOT ENOUGH TO IDENTIFY THE ACCEPT. This used to read
            // "the only XSLET-class frame the host originates", which was true when it was written
            // and stopped being true the moment the file push began ORIGINATING a connect letter of
            // its own - also XSLET-class. Every XENSE then resent that letter, stepping its Flags 1
            // down one at a time: measured 2026-08-09, 127 resends against a live D100, the descent
            // 0x009E, 0x009D, 0x009C ... visible in the capture.
            //
            // The two differ in WHO they are addressed to. A letter WE send asks a server, so it goes
            // to XROUT's well-known port 0; an accept answers a client and goes back to that client's
            // own session port. Port 0 therefore means "we are asking", never "we are accepting".
            if ((controlService & 0xFF) == XsletServiceByte && clientPort != XroutRequestPort)
            {
                _pendingAccepts[remoteNode] = new PendingAccept(
                    clientSystem, clientPort, sourcePort, controlService, frameFlags, role, payload, f1);
            }

            return AssembleDatagram(link, remoteNode, clientSystem, clientPort, sourcePort,
                controlService, frameFlags, role, payload, f1);
        }

        /// <summary>
        /// Builds a data datagram whose MESSAGE BODY goes onto the wire verbatim at offset 28.
        /// </summary>
        /// <param name="remoteNode">
        /// The node to send to.
        /// </param>
        /// <param name="clientSystem">
        /// The peer's XMSG system number.
        /// </param>
        /// <param name="clientPort">
        /// The peer's port.
        /// </param>
        /// <param name="sourcePort">
        /// Our source port.
        /// </param>
        /// <param name="xmcsm">
        /// XMCSM, the one word at wire 26-27; Flags 2 gets the same value.
        /// </param>
        /// <param name="frameFlags">
        /// The sub-header frame-flags byte.
        /// </param>
        /// <param name="role">
        /// The sub-header role byte.
        /// </param>
        /// <param name="body">
        /// The message body from wire offset 28.
        /// </param>
        /// <param name="answeredFlags1">
        /// The Flags 1 of the request this frame ANSWERS, to be echoed, or
        /// <see cref="XmsgAnsweredFlags1.None"/> when it is originated. See
        /// <see cref="ChooseFlags1"/>.
        /// </param>
        /// <returns>
        /// The assembled datagram.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no link to <paramref name="remoteNode"/> exists.
        /// </exception>
        /// <remarks>
        /// The envelope arithmetic below is the SAME as <see cref="BuildDatagram"/>'s - only the
        /// body differs, because a file-access message carries no XROUT header. The 32-bit form the
        /// counter/channel derivation still wants is rebuilt here from XMCSM and the body's first
        /// word, which is exactly what those two halves are.
        /// </remarks>
        public XmsgFrame BuildBodyDatagram(
            ushort remoteNode,
            ushort clientSystem,
            ushort clientPort,
            ushort sourcePort,
            ushort xmcsm,
            byte frameFlags,
            byte role,
            byte[] body,
            int answeredFlags1)
        {
            XmsgLink bodyLink = LinkFor(remoteNode);
            byte[] safeBody = body != null ? body : Array.Empty<byte>();
            ushort f1b = ChooseFlags1(bodyLink, answeredFlags1);

            return BuildAddressedFrame(
                bodyLink, SintranPacketSubtype.Data, remoteNode, clientSystem, clientPort, sourcePort,
                xmcsm, frameFlags, role, safeBody, f1b);
        }

        /// <summary>
        /// Builds the datagram - or the fragment pair - that carries one message body.
        /// </summary>
        /// <param name="remoteNode">
        /// The node to send to.
        /// </param>
        /// <param name="clientSystem">
        /// The peer's XMSG system number.
        /// </param>
        /// <param name="clientPort">
        /// The peer's port.
        /// </param>
        /// <param name="sourcePort">
        /// Our source port.
        /// </param>
        /// <param name="frameFlags">
        /// The sub-header frame-flags byte.
        /// </param>
        /// <param name="role">
        /// The sub-header role byte.
        /// </param>
        /// <param name="body">
        /// The whole message body from wire offset 28.
        /// </param>
        /// <param name="answeredFlags1">
        /// The Flags 1 this message answers, or <see cref="XmsgAnsweredFlags1.None"/>.
        /// </param>
        /// <returns>
        /// One frame, or the first fragment followed by its continuation.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no link to <paramref name="remoteNode"/> exists.
        /// </exception>
        /// <remarks>
        /// <para><b>One Flags 1 for the pair</b></para>
        /// Both fragments carry the SAME Flags 1, and that is what ties them together at the far
        /// end - <c>TransferFragmentationTests</c> matches captured pairs on nothing else. So the
        /// number is allocated once, before either frame is built.
        /// <para><b>The continuation carries no addressing</b></para>
        /// It is the 14-byte SINTRAN header and then the rest of the message, with no XMSG
        /// sub-header at all, so its Flags 2 is the resume offset rather than a length or a class.
        /// <para><b>Header word 6 is the carved checksum, on both fragments</b></para>
        /// Both frames go out through <c>BuildAddressedFrame</c> / <c>BuildContinuationFrame</c>,
        /// and every one of those paths ends in <see cref="StampChecksum"/>. So a fragment pair
        /// carries the same derived word 6 as any other frame this host builds.
        /// <para>
        /// CORRECTED 2026-08-05. This used to say the arithmetic here was known NOT to be the
        /// checksum, and pointed at a test measuring the gap. Both statements died with the fix -
        /// the arithmetic was replaced, and the test with it. See <see cref="StampChecksum"/> for
        /// what the old model got wrong and why it went unnoticed.
        /// </para>
        /// </remarks>
        public IReadOnlyList<XmsgFrame> BuildFragmentedBodyDatagram(
            ushort remoteNode,
            ushort clientSystem,
            ushort clientPort,
            ushort sourcePort,
            byte frameFlags,
            byte role,
            byte[] body,
            int answeredFlags1)
        {
            XmsgLink link = LinkFor(remoteNode);
            byte[] safeBody = body != null ? body : Array.Empty<byte>();
            ushort f1 = ChooseFlags1(link, answeredFlags1);

            List<XmsgFrame> frames = new List<XmsgFrame>(2);

            if (!SintranMessageFragment.NeedsFragmenting(safeBody.Length))
            {
                // Short enough to travel whole: exactly what BuildBodyDatagram would produce, with
                // the body's own length as XMCSM.
                frames.Add(BuildAddressedFrame(
                    link, SintranPacketSubtype.Data, remoteNode, clientSystem, clientPort, sourcePort,
                    (ushort)safeBody.Length, frameFlags, role, safeBody, f1));
                return frames;
            }

            ReadOnlySpan<byte> head;
            ReadOnlySpan<byte> tail;
            SintranMessageFragment.Split(safeBody, out head, out tail);

            // The FIRST fragment declares the TOTAL message length, not its own share of it.
            frames.Add(BuildAddressedFrame(
                link, SintranPacketSubtype.MessageFirstFragment, remoteNode, clientSystem, clientPort,
                sourcePort, (ushort)safeBody.Length, frameFlags, role, head.ToArray(), f1));

            // The CONTINUATION declares the offset it resumes at, which is the first fragment's
            // share. offset + its own length is the total again, which is the arithmetic the
            // captured pairs satisfy.
            frames.Add(BuildContinuationFrame(
                link, remoteNode, (ushort)head.Length, tail.ToArray(), f1));

            return frames;
        }

        /// <summary>
        /// Writes header word 6: the ones-complement checksum over the six words in front of it.
        /// </summary>
        /// <param name="header">
        /// The header, with everything except word 6 already filled in.
        /// </param>
        /// <remarks>
        /// <para><b>CORRECTED 2026-08-05. This used to fabricate word 6.</b></para>
        /// Every frame this host built took bytes 12-13 from the seed / counter / channel
        /// arithmetic in <c>XmsgEnvelope</c> - a model whose own remarks say it was FITTED to
        /// observations and is the wrong explanation. Word 6 is a checksum, carved from the kernel
        /// at <c>137314</c> and verified on 3595 of 3595 captured frames across every subtype.
        /// <para><b>What was actually wrong, and why it hid for so long</b></para>
        /// The LOW byte was always right: the seed is learned from a received frame's real word 6,
        /// so the counter arithmetic reproduces one by construction. The HIGH byte was anchored at
        /// a constant <c>0xDE</c>, which is the complement of a sum whose node numbers are small.
        /// Every capture the model was fitted to ran between nodes 100, 102 and 103 - all under
        /// 256, contributing nothing to the sum's high half. Our node is 19999 = <c>0x4E1F</c>, so
        /// every frame we sent went out with a word 6 high by exactly <c>0x4E00</c>.
        /// <para><b>D100 accepted those frames anyway</b></para>
        /// It listed our server with them, so it was plainly not rejecting them - which is why this
        /// was pinned as a measured defect rather than assumed fatal. A fabricated word 6 on a
        /// REACHABILITY frame did crash it once with <c>XMSG ERROR CODE 24</c>; whether the peer
        /// validates only some subtypes is UNKNOWN. Deriving it correctly makes the question moot.
        /// <para>
        /// The seed is still learned and still used for the receive-side envelope model; it is only
        /// no longer used to invent this word.
        /// </para>
        /// <para><b>The arithmetic no longer lives here</b></para>
        /// <para>
        /// This used to pack the six header words by hand, as eight other call sites also did. It
        /// now delegates to <see cref="XmsgEnvelope.StampChecksum"/>. The method is kept rather than
        /// inlined at its three call sites so the history above stays attached to the code it is
        /// about.
        /// </para>
        /// </remarks>
        private static void StampChecksum(SintranHeader header)
        {
            XmsgEnvelope.StampChecksum(header);
        }

        /// <summary>
        /// Looks up the link to a node, or refuses when none has been established.
        /// </summary>
        /// <param name="remoteNode">
        /// The node to send to.
        /// </param>
        /// <returns>
        /// The link, which owns the seed and the outgoing Flags 1.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no link to <paramref name="remoteNode"/> exists.
        /// </exception>
        private XmsgLink LinkFor(ushort remoteNode)
        {
            if (!_links.TryGetValue(remoteNode, out XmsgLink? link))
            {
                throw new InvalidOperationException($"No XMSG link to node {remoteNode} (no seed learned).");
            }

            return link!;
        }

        /// <summary>
        /// Builds a frame that carries the full addressing head: the SINTRAN header and the XMSG
        /// sub-header, then the body at wire offset 28.
        /// </summary>
        /// <param name="link">
        /// The link, which supplies the seed the envelope arithmetic needs.
        /// </param>
        /// <param name="subtype">
        /// The packet subtype - an ordinary <see cref="SintranPacketSubtype.Data"/> frame or the
        /// <see cref="SintranPacketSubtype.MessageFirstFragment"/> of a split message.
        /// </param>
        /// <param name="remoteNode">
        /// The node to send to.
        /// </param>
        /// <param name="clientSystem">
        /// The peer's XMSG system number.
        /// </param>
        /// <param name="clientPort">
        /// The peer's port.
        /// </param>
        /// <param name="sourcePort">
        /// Our source port.
        /// </param>
        /// <param name="xmcsm">
        /// The word at wire 26-27, which Flags 2 repeats. A body length on a data frame, the TOTAL
        /// message length on a first fragment.
        /// </param>
        /// <param name="frameFlags">
        /// The sub-header frame-flags byte.
        /// </param>
        /// <param name="role">
        /// The sub-header role byte.
        /// </param>
        /// <param name="body">
        /// The bytes this frame carries from wire offset 28.
        /// </param>
        /// <param name="flags1">
        /// The datagram sequence, already chosen.
        /// </param>
        /// <returns>
        /// The frame.
        /// </returns>
        /// <remarks>
        /// The 32-bit form the counter and channel derivation still wants is rebuilt here from XMCSM
        /// and the body's first word, which is exactly what those two halves are.
        /// </remarks>
        private XmsgFrame BuildAddressedFrame(
            XmsgLink link,
            SintranPacketSubtype subtype,
            ushort remoteNode,
            ushort clientSystem,
            ushort clientPort,
            ushort sourcePort,
            ushort xmcsm,
            byte frameFlags,
            byte role,
            byte[] body,
            ushort flags1)
        {
            XmsgFrame frame = new XmsgFrame();
            frame.Header.Marker1 = SintranHeader.Marker1Value;
            frame.Header.Marker2 = SintranHeader.Marker2Normal;
            frame.Header.PacketType = 0x00;
            frame.Header.Subtype = subtype;
            frame.Header.DestinationNode = remoteNode;
            frame.Header.SourceNode = _nodeNumber;
            frame.Header.Flags1 = flags1;
            frame.Header.Flags2 = xmcsm;
            StampChecksum(frame.Header);

            XmsgSubHeader sub = new XmsgSubHeader();
            sub.FrameFlags = frameFlags;
            sub.Role = role;
            sub.DestinationSystem = clientSystem;
            sub.DestinationPort = clientPort;
            sub.SourceSystem = _nodeNumber;
            sub.SourcePort = sourcePort;
            sub.Xmcsm = xmcsm;

            frame.SubHeader = sub;
            frame.TrailingBytes = body;
            frame.ClearRawBytes();
            return frame;
        }

        /// <summary>
        /// Builds the continuation of a split message: the SINTRAN header alone, then the rest of
        /// the body from wire offset 14.
        /// </summary>
        /// <param name="link">
        /// The link, which supplies the seed.
        /// </param>
        /// <param name="remoteNode">
        /// The node to send to.
        /// </param>
        /// <param name="resumeOffset">
        /// The byte offset into the message at which this fragment resumes, which Flags 2 carries.
        /// </param>
        /// <param name="body">
        /// The remainder of the message.
        /// </param>
        /// <param name="flags1">
        /// The datagram sequence - the SAME one the first fragment carries.
        /// </param>
        /// <returns>
        /// The continuation frame.
        /// </returns>
        /// <remarks>
        /// <see cref="XmsgFrame.SubHeader"/> is deliberately left null. That is what makes
        /// <see cref="XmsgFrame.ToArray"/> emit the header and then the body with no 14-byte
        /// sub-header between them, so the message resumes at offset 14 exactly as the captured
        /// continuations do.
        /// </remarks>
        private XmsgFrame BuildContinuationFrame(
            XmsgLink link,
            ushort remoteNode,
            ushort resumeOffset,
            byte[] body,
            ushort flags1)
        {
            XmsgFrame frame = new XmsgFrame();
            frame.Header.Marker1 = SintranHeader.Marker1Value;
            frame.Header.Marker2 = SintranHeader.Marker2Normal;
            frame.Header.PacketType = 0x00;
            frame.Header.Subtype = SintranPacketSubtype.MessageContinuation;
            frame.Header.DestinationNode = remoteNode;
            frame.Header.SourceNode = _nodeNumber;
            frame.Header.Flags1 = flags1;
            frame.Header.Flags2 = resumeOffset;
            StampChecksum(frame.Header);

            frame.SubHeader = null;
            frame.TrailingBytes = body;
            frame.ClearRawBytes();
            return frame;
        }

        /// <summary>
        /// Decides one frame's Flags 1: ECHO the request's when we are answering, ALLOCATE the
        /// link's next number when we are originating.
        /// </summary>
        /// <param name="link">
        /// The per-remote-node link, which owns the running number.
        /// </param>
        /// <param name="answeredFlags1">
        /// The Flags 1 being answered, or <see cref="XmsgAnsweredFlags1.None"/> to allocate.
        /// </param>
        /// <returns>
        /// The Flags 1 to stamp on the frame.
        /// </returns>
        /// <remarks>
        /// <para>
        /// THE ONE PLACE Flags 1 is decided. CORRECTED 2026-08-04: Flags 1 is PER DIRECTION.
        /// Each side stamps its own running count of the Data frames IT has sent, incrementing by
        /// one per Data frame. Acks are different - an Ack echoes the number of the frame it
        /// acknowledges, because its job is to identify that frame.
        /// </para>
        /// <para>
        /// MEASURED over <c>DOC/captures/FA-READ-WRITE-2026-08-04/capture-list-files.txt</c>:
        /// seeding each side from its first Data frame and then predicting every later one from
        /// that side's own count gives 222 matches, 0 mismatches.
        /// </para>
        /// <para>
        /// This was previously modelled as ONE value per exchange, shared by both directions and
        /// ECHOED by the answer. That model reproduces the captures too - but only because the
        /// conversation strictly alternates, so two independent counters advance in lockstep and
        /// look like one shared counter. The two models are indistinguishable while both sides are
        /// in step, and the capture never leaves that state.
        /// </para>
        /// <para>
        /// They come apart the moment the counts DIFFER. Live on 2026-08-04, D100 opened a
        /// conversation at Flags 1 <c>0x000E</c> while our persisted outgoing count stood at
        /// <c>0x000B</c>; echoing sent our answer at <c>0x000E</c> and D100 rejected it with XENSE
        /// (-34, a sequencing reject). Our own count was the right answer.
        /// </para>
        /// <para>
        /// This also explains <see cref="ResyncAcceptDown"/>, the "step the accept down one per
        /// XENSE until it is accepted" recovery: echoing OVERSHOOTS whenever the peer is ahead of
        /// us, and stepping down walks back to our real count. That hack is a symptom of this bug,
        /// not a feature - it should be revisited now the model is right.
        /// </para>
        /// <para>
        /// Passing an <paramref name="answeredFlags1"/> still echoes, for the Ack case. It must NOT
        /// be used for a Data frame we originate, which includes every FA reply and short
        /// acknowledgement.
        /// </para>
        /// </remarks>
        private ushort ChooseFlags1(XmsgLink link, int answeredFlags1)
        {
            if (answeredFlags1 < 0)
            {
                // Originating: take the link's next number and advance it.
                ushort allocated = link.NextFlags1;
                link.NextFlags1 = (ushort)(allocated + 1);

                // PERSIST ON SEND, which is what the machine does. The kernel carve settles it:
                // the send path at ram:b3d6 reads XNSEQ, stamps it, increments and stores it back,
                // with no acknowledgement involved anywhere - XNSEQ has exactly one writer in
                // 23552 words. Our store used to advance only when the peer's ACK reached us, so
                // any run that lost acknowledgements left it BELOW what the peer had counted, and
                // being below is dropped in silence. Measured 2026-08-11: a run ended with the
                // store at 0x0007, the next opened at 0x0007, and D100 acknowledged nothing.
                //
                // The file write is throttled and biased ahead - see
                // FileResponderSequenceStore.LookaheadFrames - so this costs no disk write per
                // frame.
                _store.SaveNextFlags1(link.RemoteNode, link.NextFlags1);

                // Instrumented deliberately. Two attempted fixes to this area were guesses at
                // which of two explanations was right - "our number is behind the peer's
                // expectation" against "the peer dropped the frame for another reason" - and the
                // wire alone cannot tell them apart, because the peer's expectation of US is a
                // value it never transmits. Logging what we STAMP, next to the store value it came
                // from, is what splits them. See task #28.
                Log?.Invoke(
                    $"[seq] node {link.RemoteNode}: ORIGINATE at Flags1 0x{allocated:X4} (next becomes 0x{link.NextFlags1:X4})");
                return allocated;
            }

            // Echoing answers the PEER's frame, so it spends none of OUR numbers. Our own count
            // is left exactly where it was.
            //
            // MEASURED 2026-08-10, and this is what the live push had been dying of:
            // this branch used to ratchet our own counter up to the echoed number plus one -
            //
            //     ushort following = (ushort)(echoed + 1);
            //     if (following > link.NextFlags1) { link.NextFlags1 = following; }
            //
            // which is the "one sequence shared by both machines" model that the remarks above
            // already explain is WRONG. It only bites when the two sides are NOT level, and on
            // the hub they are not: D100 was running eight ahead of us, we echoed its 0x0028
            // while our own next was 0x0022, and the ratchet threw our count forward to 0x0029.
            // The wire shows exactly that jump - our originations ran
            //     001b 001c 001d 001e 001f 0020 0021 -> 0029
            // with no number ever reused, and D100 answered our 0x0029 with XENSE (-34, Flags 2
            // 0xFFDE) because it was still waiting for 0x0022. It then rejected everything after
            // it, 127 rejects in all, and finally tore the link down with ND frame kind 0x6F.
            // Capture and decoder: DOC/captures/ND-TO-ND-WRITE-2026-08-10/.
            //
            // The peer's expectation of US is a value it never transmits, which is why this hid
            // for so long: nothing on the wire states it, and while both sides are in step the
            // ratchet is a no-op. Do NOT reintroduce any coupling here - if our count ever needs
            // to move because of something the peer said, that is a separate, named decision
            // (see AnnounceRestart), not a side effect of answering a frame.
            ushort echoed = (ushort)answeredFlags1;

            Log?.Invoke(
                $"[seq] node {link.RemoteNode}: ECHO Flags1 0x{echoed:X4} (our next stays 0x{link.NextFlags1:X4})");
            return echoed;
        }

        /// <summary>
        /// Assembles a datagram at an EXPLICIT Flags 1 (no sequence advance) - the shared body of
        /// <see cref="BuildDatagram"/> and <see cref="ResyncAcceptDown"/>.
        /// </summary>
        /// <param name="link">
        /// The per-remote-node link (seed source).
        /// </param>
        /// <param name="remoteNode">
        /// The client's node number.
        /// </param>
        /// <param name="clientSystem">
        /// The client's system number.
        /// </param>
        /// <param name="clientPort">
        /// The client's port.
        /// </param>
        /// <param name="sourcePort">
        /// Our source port.
        /// </param>
        /// <param name="controlService">
        /// The XMCSM control/service word.
        /// </param>
        /// <param name="frameFlags">
        /// The sub-header frame-flags byte.
        /// </param>
        /// <param name="role">
        /// The sub-header role byte.
        /// </param>
        /// <param name="payload">
        /// The trailer payload bytes.
        /// </param>
        /// <param name="f1">
        /// The datagram sequence (Flags 1) to stamp.
        /// </param>
        /// <returns>
        /// The assembled datagram.
        /// </returns>
        private XmsgFrame AssembleDatagram(
            XmsgLink link,
            ushort remoteNode,
            ushort clientSystem,
            ushort clientPort,
            ushort sourcePort,
            uint controlService,
            byte frameFlags,
            byte role,
            byte[] payload,
            ushort f1)
        {
            // Frame class is the top 16 bits of the XMCSM word (VERIFIED, 601/601 data frames).
            ushort frameClass = (ushort)(controlService >> 16);

            XmsgFrame frame = new XmsgFrame();
            frame.Header.Marker1 = SintranHeader.Marker1Value;
            frame.Header.Marker2 = SintranHeader.Marker2Normal;
            frame.Header.PacketType = 0x00;
            frame.Header.Subtype = SintranPacketSubtype.Data;
            frame.Header.DestinationNode = remoteNode;
            frame.Header.SourceNode = _nodeNumber;
            frame.Header.Flags1 = f1;
            frame.Header.Flags2 = frameClass;
            StampChecksum(frame.Header);

            XmsgSubHeader sub = new XmsgSubHeader();
            sub.FrameFlags = frameFlags;
            sub.Role = role;
            sub.DestinationSystem = clientSystem;
            sub.DestinationPort = clientPort;
            sub.SourceSystem = _nodeNumber;
            sub.SourcePort = sourcePort;
            // XMCSM is ONE word at wire 26-27; the rest of the old 32-bit "control service",
            // the pad and the length byte are the first four bytes of the MESSAGE BODY at wire
            // 28-31. ComposeBody splits them through the single compatibility facade.
            ushort xmcsm;
            byte[] body = XmsgDataFields.ComposeBody(controlService, payload, out xmcsm);
            sub.Xmcsm = xmcsm;

            frame.SubHeader = sub;
            frame.TrailingBytes = body;
            frame.ClearRawBytes();
            return frame;
        }

        /// <summary>
        /// Gets the seed the host learned for a link (for the node to seed its secure-ACK model), or a
        /// fallback of 0 when the link is unknown.
        /// </summary>
        /// <param name="remoteNode">
        /// The remote node.
        /// </param>
        /// <returns>
        /// The link seed, or 0 when unknown.
        /// </returns>
        public byte SeedFor(ushort remoteNode)
        {
            return _links.TryGetValue(remoteNode, out XmsgLink? link) ? link.Seed : (byte)0;
        }

        /// <summary>
        /// Ensures a link exists for the incoming frame's source node, learning the seed and loading the
        /// outgoing sequence from the store on first contact. The seed is learned ONCE and never
        /// overwritten: it is a per-link CONSTANT (VERIFIED - 0x14 for 100 to/from 102 across every session,
        /// reconnect and reboot in the corpus). The earlier per-frame refresh let a single out-of-model
        /// received frame poison the seed for the NEXT frame we originate (measured live 2026-07-07: a
        /// burst chunk went out with Counter for seed 0x16 instead of 0x14, violating the envelope
        /// invariant - the same bug class as the historical per-connect ACK re-seed 24B crash). A
        /// mismatch now only logs the offending frame, which is exactly the diagnostic needed to find
        /// out-of-model traffic.
        /// </summary>
        /// <param name="incoming">
        /// The received data frame.
        /// </param>
        private void EnsureLink(XmsgFrame incoming)
        {
            if (incoming.Header!.Subtype != SintranPacketSubtype.Data || incoming.SubHeader == null)
            {
                return;
            }

            if (incoming.Header.Flags1 == 0xFFFF)
            {
                return;
            }

            byte seed = XmsgEnvelope.LearnSeed(
                incoming.Header.Flags1, (byte)(incoming.Header.Checksum & 0x00FF), incoming.Header.Flags2);
            ushort node = incoming.Header.SourceNode;

            if (_links.TryGetValue(node, out XmsgLink? link))
            {
                // NEVER adopt a differing seed - report it instead. The frame that disagrees with the
                // established link seed is out-of-model (or corrupt) and is the thing to investigate.
                if (link.Seed != seed)
                {
                    Log?.Invoke(
                        $"[host] WARNING: node {node} frame F1=0x{incoming.Header.Flags1:X4} ctr=0x{(byte)(incoming.Header.Checksum & 0x00FF):X2} F2=0x{incoming.Header.Flags2:X4} implies seed 0x{seed:X2} but link seed is 0x{link.Seed:X2} - keeping 0x{link.Seed:X2} (out-of-model frame)");
                }

                return;
            }

            // The link's starting outgoing number comes from the PERSISTED store, never from the
            // frame that just arrived - deliberately, because the peer's Flags 1 is its own count
            // and says nothing about what it expects from us. Logged because this is the value a
            // restart inherits, and "what did we start from" is half of task #28's question.
            //
            // THE STORE IS ONLY RIGHT IF WE ARE THE ONLY PROCESS USING THIS NODE NUMBER, and that
            // is worth saying out loud because it cost an afternoon on 2026-08-24. The peer keeps
            // ONE counter per system number. Two runners both calling themselves 19999 - which is
            // easy to do, a Debug build and a Release build in different bin folders - each keep
            // their own store, but they share the peer's single counter. Whichever one sends
            // advances it, and the other is then hundreds behind:
            //
            //     bin\Debug\net9.0\xmsg-sequence.state     100=0247   driving the conversation
            //     bin\Release\net9.0\xmsg-sequence.state   100=0010   every letter discarded
            //
            // The symptom does NOT look like a sequence problem. The peer acknowledges every frame
            // at the link layer and answers nothing above it, while LIST-NAMES shows *FA-SERVER
            // with 30 free service points - so it reads as a dead or refusing file server. The
            // machine publishes the number it wants, in the RECEIVE column of
            //
            //     X-C: LIST-SYSTEMS -> XROUT system? <peer> -> System? <us>
            //
            // and setting the store to that value made an identical push succeed first try. Fix
            // the duplicate node number; do not paper over it by rewriting the store.
            ushort startingFlags1 = _store.LoadNextFlags1(node);
            Log?.Invoke(
                $"[seq] node {node}: link created, starting Flags1 0x{startingFlags1:X4} from the store "
                + $"(the frame that created it carried 0x{incoming.Header.Flags1:X4})");

            _links[node] = new XmsgLink(node, seed, startingFlags1);

            // Remember it. The seed is a per-link constant that survives the peer rebooting, so
            // storing it lets a later run address this machine without waiting to be spoken to -
            // see ILinkSeedStore for why that is sound and what it deliberately does not claim.
            SeedStore?.SaveSeed(node, seed);
        }

        /// <summary>
        /// Finds a registered server by name (case-insensitive), or null.
        /// </summary>
        /// <param name="name">
        /// The target name from an XSLET letter (for example <c>*TADADM</c>).
        /// </param>
        /// <returns>
        /// The matching server, or null.
        /// </returns>
        private IXmsgServer? FindByName(string name)
        {
            if (name.Length == 0)
            {
                return null;
            }

            for (int i = 0; i < _servers.Count; i++)
            {
                if (string.Equals(_servers[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return _servers[i];
                }
            }

            return null;
        }

        /// <summary>
        /// Finds the registered server that owns a given session/reply port, or null.
        /// </summary>
        /// <param name="port">
        /// The destination wire port.
        /// </param>
        /// <returns>
        /// The owning server, or null.
        /// </returns>
        private IXmsgServer? FindByPort(ushort port)
        {
            for (int i = 0; i < _servers.Count; i++)
            {
                if (_servers[i].OwnsPort(port))
                {
                    return _servers[i];
                }
            }

            return null;
        }

        /// <summary>
        /// Extracts the target server name from an XSLET letter's trailer (the first <c>*</c>-prefixed
        /// printable-ASCII run, for example <c>*TADADM</c>).
        /// </summary>
        /// <param name="incoming">
        /// The XSLET letter frame.
        /// </param>
        /// <returns>
        /// The name including the leading <c>*</c>, or an empty string when none is found.
        /// </returns>
        private static string ExtractLetterName(XmsgFrame incoming)
        {
            // The target name (for example *TADADM) is an ASCII run in the letter body. A parsed XSLET
            // letter decodes into Body (whose ToArray does not round-trip the name) while a freshly-built
            // one carries it in TrailingBytes - so scan the FULL serialized frame, which is byte-identical
            // for both and always contains the letter. The header/sub-header carry no leading-'*' run, so
            // the first '*'-prefixed run is the server name.
            byte[] bytes = incoming.ToArray();
            System.Text.StringBuilder run = new System.Text.StringBuilder();
            for (int i = 0; i <= bytes.Length; i++)
            {
                byte b = i < bytes.Length ? bytes[i] : (byte)0x00;
                if (b >= 0x20 && b <= 0x7E)
                {
                    run.Append((char)b);
                    continue;
                }

                if (run.Length >= 2 && run[0] == '*')
                {
                    return run.ToString();
                }

                run.Clear();
            }

            return string.Empty;
        }
    }
}
