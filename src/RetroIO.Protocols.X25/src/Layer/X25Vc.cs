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
        }
    }
}
