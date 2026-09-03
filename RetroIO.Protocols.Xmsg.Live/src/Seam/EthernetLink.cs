using System;

using RetroIO.Protocols.Xmsg.Ethernet;
using RetroIO.Protocols.Xmsg.Node.Seam;   // ILink + seam types live in the portable RetroIO.Protocols.Xmsg.Node half

namespace RetroIO.Protocols.Xmsg.Live.Seam
{
    /// <summary>
    /// An <see cref="ILink"/> over a COSMOS Ethernet segment: wraps an
    /// <see cref="IEthernetBackend"/> (UDP multicast / TCP / loopback) and an
    /// <see cref="NdLinkLayer"/>, delivers each received SINTRAN datagram UP as
    /// <see cref="PayloadReceived"/>, and turns <see cref="SendData"/> into an ND data frame DOWN.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the Ethernet sibling of <see cref="LapbLayerAdapter"/>. Both present the identical
    /// seam, so everything above the link - the codec, the node, the servers - is unchanged whether
    /// the peer is reached over HDLC or over Ethernet. That is exactly what the wire evidence says
    /// should be true: the SINTRAN header and its word-6 checksum are transport-independent
    /// (128/128 frames including relayed ones), so no byte above this class depends on which
    /// transport carried it.
    /// </para>
    /// <para><b>One link is one peer</b></para>
    /// <para>
    /// <see cref="ILink"/> models a point-to-point pipe, but an Ethernet segment is shared. A link
    /// is therefore BOUND to one peer system number at construction and ignores every frame from
    /// any other station.
    /// </para>
    /// <para>
    /// Without that filter a single <see cref="NdLinkLayer"/> would re-learn its peer from any
    /// frame ADDRESSED TO US, whoever sent it, and so flip-flop between the stations on the
    /// segment. That is not a hypothetical: D19999 is defined as a remote system on both D100 and
    /// D102, so both will unicast to it. (This has nothing to do with broadcast - no captured
    /// COSMOS frame uses a broadcast destination, and whether COSMOS ever sends one is UNKNOWN.)
    /// To speak to several peers, create several links sharing one backend.
    /// </para>
    /// <para><b>The link cannot speak first - by design, not by oversight</b></para>
    /// <para>
    /// A data frame carries the peer's link id in its receiver field, and that id is UNKNOWN in
    /// origin: it is neither the node number nor the system number in the MAC, so it can only be
    /// learned from a frame the peer sends (see <see cref="NdLinkLayer"/>). The peer's MAC we could
    /// derive from its system number; its link id we could not, and putting an invented value on
    /// the wire is the "constant that merely happens to work" failure this project has already paid
    /// for. So the link stays <see cref="LinkStatus.Starting"/> and <see cref="SendData"/> refuses
    /// until the peer has been heard from, at which point it goes <see cref="LinkStatus.Active"/>.
    /// </para>
    /// <para>
    /// <b>UNVERIFIED:</b> whether a real ND node will spontaneously address a newly defined remote
    /// system, and so how long that wait is in practice, has not been observed. If it turns out a
    /// node never speaks first, learning the peer's link id needs a separate experiment - not a
    /// guessed constant here.
    /// </para>
    /// <para><b>Buffer ownership</b></para>
    /// <para>
    /// The <see cref="PayloadReceived"/> payload is a fresh array per datagram today (
    /// <see cref="NdLinkLayer"/> copies it out of the frame buffer), but consumers must not rely on
    /// that: the seam contract says copy inside the callback if you retain.
    /// </para>
    /// </remarks>
    public sealed class EthernetLink : ILink
    {
        /// <summary>
        /// Link id placed in the sender field of our outgoing frames when the caller does not choose
        /// one. The origin of these ids is UNKNOWN - they are learned from the peer, never derived -
        /// so this value is ARBITRARY and carries no meaning. It is non-zero only because zero is
        /// <see cref="NdLinkLayer.UnknownPeerLinkId"/> and would be ambiguous in a trace.
        /// </summary>
        public const ushort DefaultLocalLinkId = 0x0001;

        private readonly string _name;
        private readonly IEthernetBackend _backend;
        private readonly bool _ownsBackend;
        private readonly NdLinkLayer _linkLayer;
        private readonly NdMacAddress _peerMac;

        // NdLinkLayer owns a single reused frame buffer and mutable sequence state, so it is not
        // thread-safe. Receives arrive on the backend's receive thread while SendData is called from
        // the caller's; both funnel through here, so both take this lock.
        private readonly object _gate = new object();

        private LinkStatus _status;
        private bool _stopRequested;

        /// <inheritdoc />
        public event LinkPayloadReceived? PayloadReceived;

        /// <inheritdoc />
        public event LinkStatusChanged? StatusChanged;

        /// <summary>
        /// Occurs when a frame arrives carrying a kind other than data or acknowledgement
        /// (diagnostic). Only <c>0x20</c> and <c>0x3F</c> have ever been captured; anything else is
        /// surfaced rather than silently dropped.
        /// </summary>
        public event UnknownFrameKindReceived? OnUnknownFrameKindReceived;

        /// <summary>
        /// Occurs when the peer re-sends a data frame this link has already taken.
        /// </summary>
        /// <remarks>
        /// <b>Never normal.</b> A peer repeats because it has not seen our acknowledgement, which
        /// on 2026-08-10 meant we were sending far ahead of the link's window and every repeat
        /// reached the file server as a fresh request. It looked like a pile of file-access defects
        /// and was one link-layer fault. Log it loudly.
        /// </remarks>
        public event DuplicateDataFrameReceived? OnDuplicateDataFrameReceived;

        /// <summary>
        /// Occurs when the peer sends a disconnect request - any ND frame kind whose high nibble
        /// is 6. The argument is the raw kind byte.
        /// </summary>
        /// <remarks>
        /// <c>0x6F</c> is the network-service disconnect request, VERIFIED. <c>0x60</c> is
        /// classified by its high nibble only and D100 sends it repeatedly once it gives up; what
        /// its low nibble means is UNVERIFIED. See <c>NdLinkFrameKind</c>.
        /// </remarks>
        public event DisconnectRequested? OnDisconnectRequested;

        /// <summary>
        /// Occurs for every complete Ethernet frame this link transmits, before it reaches the
        /// backend (diagnostic).
        /// </summary>
        /// <remarks>
        /// The segment sniffer skips our own frames and a host-local hub does not echo them, so this
        /// is the only way to see what we actually put on the wire. The buffer is the link's own and
        /// is reused, so a subscriber must consume it inside the callback rather than retain it.
        /// </remarks>
        public event FrameTransmitted? OnFrameTransmitted;

        /// <summary>
        /// Initialises a link to one peer over an Ethernet backend.
        /// </summary>
        /// <param name="name">
        /// The link identity stamped on every up-event and log line.
        /// </param>
        /// <param name="localSystemNumber">
        /// This node's ND system number; its station address is derived from it.
        /// </param>
        /// <param name="peerSystemNumber">
        /// The peer's ND system number. Frames from any other station are ignored.
        /// </param>
        /// <param name="backend">
        /// The transport carrying raw Ethernet frames.
        /// </param>
        /// <param name="ownsBackend">
        /// True when this link should start and dispose the backend with itself. Pass false when
        /// several links share one backend, so that stopping one does not silence the others.
        /// </param>
        /// <param name="localLinkId">
        /// The link id placed in the sender field of outgoing frames; arbitrary (see
        /// <see cref="DefaultLocalLinkId"/>).
        /// </param>
        /// <param name="physicalUser">
        /// The physical user byte of both station addresses. Every captured COSMOS frame used
        /// <c>0x00</c>, which does NOT match the "bits 7-6 = 11 for ND" family encoding in
        /// ND-60.197.01; the hardware ignores those bits on the receive match, so the capture cannot
        /// settle which rule applies. Defaulting to the observed value rather than the documented
        /// one.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="name"/> or <paramref name="backend"/> is null.
        /// </exception>
        public EthernetLink(
            string name,
            ushort localSystemNumber,
            ushort peerSystemNumber,
            IEthernetBackend backend,
            bool ownsBackend = true,
            ushort localLinkId = DefaultLocalLinkId,
            byte physicalUser = 0)
        {
            _name = name ?? throw new ArgumentNullException(nameof(name));
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _ownsBackend = ownsBackend;

            LocalSystemNumber = localSystemNumber;
            PeerSystemNumber = peerSystemNumber;
            _peerMac = NdMacAddress.FromSystemNumber(peerSystemNumber, physicalUser);

            _linkLayer = new NdLinkLayer(localSystemNumber, localLinkId, SendFrame);
            _linkLayer.PayloadReceived += DeliverPayload;
            _linkLayer.OnUnknownFrameKindReceived += RaiseUnknownFrameKind;
            _linkLayer.OnDisconnectRequested += RaiseDisconnectRequested;
            _linkLayer.StaleAcknowledgement += delegate (byte acknowledged, byte expected, byte next, int queued)
            {
                StaleAcknowledgement?.Invoke(this, acknowledged, expected, queued);
            };
            _linkLayer.OnDuplicateDataFrameReceived += RaiseDuplicateDataFrame;

            // Go Active the INSTANT the peer is known, which is before the frame that taught us is
            // delivered upward. Waiting for HandleFrame to return instead was a real defect: the
            // reply to that first datagram was built, refused because the link still read Starting,
            // and silently lost - so a peer that reused an existing connection (its first frame to
            // us being data, not a connection request) never got an answer at all.
            _linkLayer.OnPeerLearned += HandlePeerLearned;

            _status = LinkStatus.Stopped;
        }

        /// <inheritdoc />
        public string Name
        {
            get { return _name; }
        }

        /// <inheritdoc />
        public LinkStatus Status
        {
            get { return _status; }
        }

        /// <summary>
        /// Gets this node's ND system number.
        /// </summary>
        public ushort LocalSystemNumber { get; }

        /// <summary>
        /// Gets the ND system number of the peer this link is bound to.
        /// </summary>
        public ushort PeerSystemNumber { get; }

        /// <summary>
        /// Gets this node's station address.
        /// </summary>
        public NdMacAddress LocalMac
        {
            get { return _linkLayer.LocalMac; }
        }

        /// <summary>
        /// Gets the peer's station address, as derived from its system number.
        /// </summary>
        public NdMacAddress PeerMac
        {
            get { return _peerMac; }
        }

        /// <summary>
        /// Gets a value indicating whether the peer has been heard from and its link id learned.
        /// Until this is true the link cannot send (see the class remarks).
        /// </summary>
        public bool HasLearnedPeer
        {
            get { return _linkLayer.HasLearnedPeer; }
        }

        /// <summary>
        /// Gets the number of data frames received from the peer.
        /// </summary>
        public long DataFramesReceived
        {
            get { return _linkLayer.DataFramesReceived; }
        }

        /// <summary>
        /// Gets the number of acknowledgements received from the peer.
        /// </summary>
        public long AcknowledgementsReceived
        {
            get { return _linkLayer.AcknowledgementsReceived; }
        }

        /// <summary>
        /// Gets the number of frames discarded because they came from a station other than the peer
        /// this link is bound to. On a shared segment this is expected to be non-zero and is not an
        /// error; it is exposed so a trace can show the filter working rather than a silent link.
        /// </summary>
        public long FramesFromOtherStations { get; private set; }

        /// <inheritdoc />
        public bool Start()
        {
            _stopRequested = false;
            _backend.OnPacketReceived += HandlePacket;

            if (_ownsBackend)
            {
                _backend.Start();
            }

            // Starting, not Active: the segment is up but we have not heard the peer yet, so we
            // cannot address it. HandlePacket flips us to Active on the peer's first frame.
            SetStatus(LinkStatus.Starting, "backend started, awaiting peer");
            return _backend.IsActive;
        }

        /// <summary>
        /// Asks the peer to open the link from scratch, so it forgets what it remembers about us.
        /// </summary>
        /// <returns>
        /// True when the request was sent.
        /// </returns>
        /// <remarks>
        /// The peer's station address is derived from its system number, so this can be sent before
        /// the peer has said anything - which is the point, because the state that needs clearing
        /// is the state it kept from our LAST run. See <c>NdLinkLayer.SendConnectionRequest</c> for
        /// what it is for and what about it is unverified.
        /// </remarks>
        public bool RequestConnection()
        {
            return _linkLayer.SendConnectionRequest(_peerMac);
        }

        /// <inheritdoc />
        public void Stop()
        {
            // Idempotent: a second Stop (or a Stop after Dispose) is a no-op.
            if (_status == LinkStatus.Stopped || _status == LinkStatus.Stopping)
            {
                return;
            }

            // Both teardown transitions bypass the _stopRequested guard deliberately. That guard
            // exists to stop a LATE INBOUND FRAME resurrecting the link, and it must not suppress
            // the teardown's own transitions - setting the flag first and then calling SetStatus
            // silently swallowed the Stopping edge.
            _stopRequested = true;
            ForceStatus(LinkStatus.Stopping, "stop requested");

            _backend.OnPacketReceived -= HandlePacket;
            if (_ownsBackend)
            {
                _backend.Stop();
            }

            ForceStatus(LinkStatus.Stopped, "stopped");
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Stop();
            if (_ownsBackend)
            {
                _backend.Dispose();
            }
        }

        /// <summary>
        /// Raised when the link accepted a datagram but has NOT sent it.
        /// </summary>
        /// <param name="link">
        /// The link holding the datagram.
        /// </param>
        /// <param name="byteCount">
        /// How many bytes were parked.
        /// </param>
        /// <param name="queueDepth">
        /// How many datagrams are now waiting, this one included.
        /// </param>
        public delegate void NdDatagramParked(EthernetLink link, int byteCount, int queueDepth);

        /// <summary>
        /// Raised when the peer acknowledges something behind where we already believe it to be.
        /// </summary>
        /// <param name="link">
        /// The link that refused it.
        /// </param>
        /// <param name="acknowledged">
        /// The sequence the peer acknowledged.
        /// </param>
        /// <param name="peerNextExpected">
        /// Where we already believe the peer to be.
        /// </param>
        /// <param name="queued">
        /// How many datagrams are parked behind the window.
        /// </param>
        public delegate void NdStaleAcknowledgement(
            EthernetLink link, byte acknowledged, byte peerNextExpected, int queued);

        /// <summary>
        /// Raised when an acknowledgement is refused for being stale.
        /// </summary>
        /// <remarks>
        /// Paired with <see cref="DatagramParked"/> on purpose: one of these alone is a duplicate in
        /// flight and means nothing, but a run of them WITH a climbing park queue is a deadlock -
        /// the frames the peer needs in order to move forward are the ones stuck behind the window.
        /// </remarks>
        public event NdStaleAcknowledgement? StaleAcknowledgement;

        /// <summary>
        /// The window state at the moment of the most recent park.
        /// </summary>
        /// <remarks>
        /// "The window is full" says nothing on its own: full at six is a busy link, full at ONE is
        /// a peer that has never placed us. This carries the numbers so the log line can tell them
        /// apart.
        /// </remarks>
        public string ParkedDetail { get; private set; } = string.Empty;

        /// <summary>
        /// Raised when a datagram is parked behind a full send window instead of being sent.
        /// </summary>
        /// <remarks>
        /// Queueing on its own is normal - it is how the send window is respected. What is worth
        /// watching is a depth that climbs and never falls, which means the peer has stopped
        /// acknowledging us and nothing further will leave this node.
        /// </remarks>
        public event NdDatagramParked? DatagramParked;

        /// <inheritdoc />
        public bool SendData(ReadOnlySpan<byte> payload)
        {
            // Contract: a not-Active link (or an empty payload) is a logged false, NEVER a throw.
            if (_status != LinkStatus.Active)
            {
                Console.WriteLine(
                    $"[link] {_name} SendData refused: link not Active (status {_status}"
                    + (_linkLayer.HasLearnedPeer ? ")" : ", peer not heard from yet)"));
                return false;
            }

            if (payload.Length == 0)
            {
                Console.WriteLine($"[link] {_name} SendData refused: empty payload");
                return false;
            }

            lock (_gate)
            {
                // SendDatagram copies the span into its own frame buffer before returning, so the
                // caller's buffer is free the moment this call ends.
                NdSendOutcome outcome = _linkLayer.SendDatagram(payload);

                // PARKED IS NOT SENT, and this is the only place that can tell. ILink.SendData
                // answers "did you take it", which is true either way, so a frame waiting behind a
                // closed window would otherwise leave no trace anywhere - that is exactly how a live
                // TAD connect came to hang while every log line said the reply had gone out.
                if (outcome == NdSendOutcome.Queued)
                {
                    DatagramParked?.Invoke(this, payload.Length, _linkLayer.QueuedDatagrams);
                    ParkedDetail = $"window {_linkLayer.Window}, outstanding {_linkLayer.OutstandingFrames}, peer placed us: {_linkLayer.HasLearnedPeerPosition}";
                }

                return outcome != NdSendOutcome.Refused;
            }
        }

        /// <summary>
        /// Handles one raw frame from the backend: drops anything not sourced by this link's peer,
        /// then feeds the rest to the ND link layer.
        /// </summary>
        /// <param name="data">
        /// The buffer holding the frame, starting at the destination MAC.
        /// </param>
        /// <param name="length">
        /// The number of valid bytes.
        /// </param>
        private void HandlePacket(byte[] data, int length)
        {
            if (_stopRequested || data == null || length <= 0)
            {
                return;
            }

            // Bind to one peer. NdLinkLayer learns its peer from whatever frame arrives, and lets
            // broadcast destinations through, so on a shared segment a second station's broadcast
            // would steal this link's peer identity. Parse only far enough to read the source.
            if (!Ieee8023Frame.TryParse(
                    new ReadOnlySpan<byte>(data, 0, length),
                    out NdMacAddress _,
                    out NdMacAddress source,
                    out int _,
                    out int _))
            {
                return;
            }

            if (!source.Equals(_peerMac))
            {
                FramesFromOtherStations++;
                return;
            }

            lock (_gate)
            {
                // The transition to Active happens inside here, via HandlePeerLearned, so that a reply
                // to this very frame can already be sent. Do NOT move it back out after the call.
                _linkLayer.HandleFrame(data, length);
            }
        }

        /// <summary>
        /// Marks the link usable as soon as the peer's reference is known.
        /// </summary>
        /// <remarks>
        /// Raised from inside the link layer's frame handling, BEFORE the datagram in that frame is
        /// delivered upward, so the reply the datagram provokes finds an Active link.
        /// </remarks>
        private void HandlePeerLearned()
        {
            SetStatus(LinkStatus.Active, "peer learned");
        }

        /// <summary>
        /// Sends one complete Ethernet frame down to the backend.
        /// </summary>
        /// <param name="frame">
        /// The frame buffer, starting at the destination MAC.
        /// </param>
        /// <param name="length">
        /// The number of bytes to send.
        /// </param>
        private void SendFrame(byte[] frame, int length)
        {
            // TX VISIBILITY. The segment sniffer deliberately skips frames we sent ourselves, and a
            // host-local hub does not loop them back, so without this hook everything we transmit is
            // invisible - which made "we answered but the peer kept asking" impossible to diagnose
            // from the log alone. This is the ONE place every outbound frame passes through: the CC,
            // the acknowledges and every datagram.
            OnFrameTransmitted?.Invoke(frame, length);
            _backend.SendPacket(frame, 0, length);
        }

        /// <summary>
        /// Raises the seam's up-event for a datagram delivered by the ND link layer.
        /// </summary>
        /// <param name="payload">
        /// The datagram buffer.
        /// </param>
        /// <param name="length">
        /// The number of valid bytes.
        /// </param>
        private void DeliverPayload(byte[] payload, int length)
        {
            PayloadReceived?.Invoke(this, payload, length);
        }

        /// <summary>
        /// Re-raises the link layer's unknown-frame-kind diagnostic.
        /// </summary>
        /// <param name="kind">
        /// The unrecognised kind byte.
        /// </param>
        private void RaiseUnknownFrameKind(byte kind)
        {
            OnUnknownFrameKindReceived?.Invoke(kind);
        }

        /// <summary>
        /// Forwards the link layer's duplicate-frame diagnostic to this link's own event.
        /// </summary>
        /// <param name="sequence">
        /// The sequence the repeated frame carried.
        /// </param>
        /// <param name="expected">
        /// The sequence this link was waiting for.
        /// </param>
        private void RaiseDuplicateDataFrame(byte sequence, byte expected)
        {
            OnDuplicateDataFrameReceived?.Invoke(sequence, expected);
        }

        /// <summary>
        /// Forwards a peer disconnect request to this link's own event.
        /// </summary>
        /// <param name="kind">
        /// The raw ND frame kind byte (<c>0x60</c> or <c>0x6F</c>).
        /// </param>
        private void RaiseDisconnectRequested(byte kind)
        {
            OnDisconnectRequested?.Invoke(kind);
        }

        /// <summary>
        /// Sets the coarse status and raises <see cref="StatusChanged"/> only on an actual
        /// transition, carrying the previous status, the new status and a reason (sender-first).
        /// </summary>
        /// <param name="next">
        /// The new status.
        /// </param>
        /// <param name="reason">
        /// A short human-readable reason for the transition (for logs).
        /// </param>
        private void SetStatus(LinkStatus next, string reason)
        {
            // Once Stop() has been requested, a late frame from the backend must not resurrect the
            // link. Stop() performs its own final transition directly.
            if (_stopRequested)
            {
                return;
            }

            ForceStatus(next, reason);
        }

        /// <summary>
        /// Sets the coarse status and raises <see cref="StatusChanged"/> on an actual transition,
        /// WITHOUT the stop guard. Only the teardown path uses this, to announce its own
        /// transitions after the guard is already up.
        /// </summary>
        /// <param name="next">
        /// The new status.
        /// </param>
        /// <param name="reason">
        /// A short human-readable reason for the transition (for logs).
        /// </param>
        private void ForceStatus(LinkStatus next, string reason)
        {
            if (next != _status)
            {
                LinkStatus previous = _status;
                _status = next;
                StatusChanged?.Invoke(this, previous, next, reason);
            }
        }
    }
}
