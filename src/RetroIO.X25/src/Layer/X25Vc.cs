using RetroIO.Protocols.X25.Models;

namespace RetroIO.Protocols.X25.PacketLayer
{
    /// <summary>
    /// The control block for one virtual circuit: its identity, negotiated parameters, sequence and
    /// flow-control state, and pending-timer expiries.
    /// </summary>
    public sealed class X25Vc
    {
        /// <summary>
        /// Logical channel number (1–4095).
        /// </summary>
        public ushort Lci { get; }

        /// <summary>
        /// Whether this circuit is switched or permanent.
        /// </summary>
        public X25VcKind Kind { get; }

        /// <summary>
        /// Current channel state.
        /// </summary>
        public X25VcState State { get; set; }

        /// <summary>
        /// Calling (origin) X.121 address.
        /// </summary>
        public string CallingAddress { get; set; } = string.Empty;

        /// <summary>
        /// Called (destination) X.121 address.
        /// </summary>
        public string CalledAddress { get; set; } = string.Empty;

        /// <summary>
        /// Send sequence number P(S), modulo 8.
        /// </summary>
        public byte SendSeq { get; set; }

        /// <summary>
        /// Receive sequence number P(R), modulo 8.
        /// </summary>
        public byte RecvSeq { get; set; }

        /// <summary>
        /// Last send sequence the peer has acknowledged, modulo 8. Used to enforce the send window.
        /// </summary>
        public byte AckedSeq { get; set; }

        /// <summary>
        /// True when the peer has signalled receive-not-ready (we must stop sending data).
        /// </summary>
        public bool PeerBusy { get; set; }

        /// <summary>
        /// True when we have signalled receive-not-ready to the peer.
        /// </summary>
        public bool LocalBusy { get; set; }

        /// <summary>
        /// Negotiated outbound window size.
        /// </summary>
        public byte WindowSizeOut { get; set; }

        /// <summary>
        /// Negotiated inbound window size.
        /// </summary>
        public byte WindowSizeIn { get; set; }

        /// <summary>
        /// Negotiated maximum data field length for packets we send (X.25 7.3.1.1). A send longer than
        /// this is refused until M-bit segmentation exists (plan B11).
        /// </summary>
        public int PacketSizeOut { get; set; } = X25Facilities.DefaultPacketSize;

        /// <summary>
        /// Negotiated maximum data field length for packets we receive. A longer one is a local
        /// procedure error, diagnostic 39 "Packet too long" (Annex E).
        /// </summary>
        public int PacketSizeIn { get; set; } = X25Facilities.DefaultPacketSize;

        /// <summary>
        /// True when the peer's call set-up packet carried flow control facilities, so our answer must
        /// state the values too (X.25 7.2.2.1: the negotiated values are indicated in the Call
        /// Accepted / Call Connected).
        /// </summary>
        public bool PeerSentFacilities { get; set; }

        /// <summary>
        /// True from our Interrupt until the peer's Interrupt Confirmation. X.25 4.4.2: "The DTE should
        /// not transmit a second DTE interrupt packet until the first one is confirmed."
        /// </summary>
        public bool InterruptOutstanding { get; set; }

        /// <summary>
        /// Absolute millisecond expiry of the active per-circuit timer, or 0 when no timer is armed.
        /// The applicable timer depends on the state: call-request (T21), reset-request (T22),
        /// clear-request (T23).
        /// </summary>
        public long TimerExpiryMs { get; set; }

        /// <summary>
        /// Last cause code seen on this circuit.
        /// </summary>
        public byte Cause { get; set; }

        /// <summary>
        /// Last diagnostic code seen on this circuit.
        /// </summary>
        public byte Diagnostic { get; set; }

        /// <summary>
        /// How many times the packet the current timer guards (Reset or Clear Request) has been
        /// retransmitted. Reset whenever the circuit enters a new requesting state.
        /// </summary>
        public int Retries { get; set; }

        /// <summary>
        /// True once the application has been told this call is over. A call can end in several
        /// steps (a T21 expiry that starts a clear, then the confirmation of that clear) and the
        /// application must hear about it exactly once.
        /// </summary>
        public bool ClearReported { get; set; }

        /// <summary>
        /// True while the Call Request is being held back because our Restart Request has not yet
        /// been confirmed (X.25 4.1.1: no call set-up in state r2). Sent when the restart completes.
        /// </summary>
        public bool CallRequestHeld { get; set; }

        /// <summary>
        /// True on a permanent virtual circuit whose Reset Request has gone unanswered through every
        /// retransmission (X.25 Table D.2 note 2: "the logical channel should be considered out of
        /// order"). Cleared when a reset finally completes or a restart runs.
        /// </summary>
        public bool OutOfOrder { get; set; }

        /// <summary>
        /// Call user data associated with the circuit.
        /// </summary>
        public byte[] CallUserData { get; set; } = System.Array.Empty<byte>();

        /// <summary>
        /// Arbitrary per-circuit context an application may attach.
        /// </summary>
        public object? UserContext { get; set; }

        /// <summary>
        /// Creates a virtual-circuit control block in the ready (or PVC-ready) state.
        /// </summary>
        /// <param name="lci">
        /// Logical channel number.
        /// </param>
        /// <param name="kind">
        /// Switched or permanent circuit.
        /// </param>
        public X25Vc(ushort lci, X25VcKind kind)
        {
            Lci = lci;
            Kind = kind;
            State = kind == X25VcKind.Pvc ? X25VcState.PvcReady : X25VcState.Ready;
        }

        /// <summary>
        /// Resets the sequence and flow-control state to initial values (used on connect and on reset).
        /// </summary>
        public void ResetFlow()
        {
            SendSeq = 0;
            RecvSeq = 0;
            AckedSeq = 0;
            PeerBusy = false;
            LocalBusy = false;
            InterruptOutstanding = false;   // X.25 4.4.3.1: a reset also discards an unconfirmed interrupt
        }
    }
}
