//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

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
        /// Data packets put on the wire on this call (each packet once, a refused send not counted). A reset does not
        /// zero it: the count is for the call. Read by a PAD that prints packet counts on a clear (BT PSS).
        /// </summary>
        public long DataPacketsSent { get; set; }

        /// <summary>
        /// Data packets taken in sequence on this call (one rejected for a procedure error not counted). A reset does
        /// not zero it.
        /// </summary>
        public long DataPacketsReceived { get; set; }

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
        /// Every facility element other than window and packet size, byte for byte (<see cref="Models.X25Facilities.PassThrough"/>):
        /// for an incoming call what the caller asked for, for an outgoing call first what we ask for and, once connected, what
        /// the called side answered. A switch hands them from one leg of a call to the other (PLAN B1); this layer does not
        /// act on them.
        /// </summary>
        public byte[] OtherFacilities { get; set; } = System.Array.Empty<byte>();

        /// <summary>
        /// The X.75 network utility field (on a port between networks only): for an incoming call what the calling STE
        /// sent, for an outgoing call first what we send and, once connected, what the called STE answered. Carried, not
        /// acted on, by this layer.
        /// </summary>
        public byte[] NetworkUtilities { get; set; } = System.Array.Empty<byte>();

        /// <summary>
        /// The facility field of the Clear Request / Clear Indication that ended the last call on this channel (X.25 5.2.4.2:
        /// charging information, called line address modified notification and the like); empty when it had none. Kept
        /// after the channel is free so that a switch, told of the clear, can pass the field to the other leg of the call;
        /// replaced by the next clear and emptied when the channel's next call starts.
        /// </summary>
        public byte[] ClearFacilities { get; set; } = System.Array.Empty<byte>();

        /// <summary>
        /// The X.75 utility field of the clear that ended the call (X.75 5.3.1: a refusal's transit list); empty when none.
        /// </summary>
        public byte[] ClearUtilities { get; set; } = System.Array.Empty<byte>();

        /// <summary>
        /// An Interrupt received from the peer and not yet confirmed to it, while the far end has it (X25Config.ConfirmInterruptsFromFarEnd).
        /// </summary>
        public bool InterruptToConfirm { get; set; }

        /// <summary>
        /// True while a reset this engine started for a procedure error is in progress; its owner is told local procedure
        /// error when it completes, whatever cause went on the wire.
        /// </summary>
        public bool ResetForProcedureError { get; set; }

        /// <summary>
        /// True from our Interrupt until the peer's Interrupt Confirmation. X.25 4.4.2: "The DTE should
        /// not transmit a second DTE interrupt packet until the first one is confirmed."
        /// </summary>
        public bool InterruptOutstanding { get; set; }

        /// <summary>
        /// Segments of messages accepted by <c>SendMessage</c> that the window or the peer's busy
        /// condition has not yet let out, in order. Drained as acknowledgements arrive.
        /// </summary>
        public System.Collections.Generic.Queue<X25PendingSegment> PendingSegments { get; } = new System.Collections.Generic.Queue<X25PendingSegment>();

        /// <summary>
        /// Category A packets (full, M=1, D=0) received so far in the current complete packet
        /// sequence (X.25 4.3.5), awaiting the category B packet that ends it.
        /// </summary>
        public System.IO.MemoryStream? Reassembly { get; set; }

        /// <summary>
        /// True while the engine is inside the loop that sends queued segments; a re-entrant call
        /// (the peer's RR arriving synchronously through a loopback sink) must not start a second.
        /// </summary>
        public bool Draining { get; set; }

        /// <summary>
        /// The Q bit of the sequence being reassembled; every packet in it must carry the same
        /// value (4.3.3).
        /// </summary>
        public bool ReassemblyQBit { get; set; }

        /// <summary>
        /// Size at which a partly joined complete packet sequence is handed to the consumer as a part
        /// (with "more follows"). It is the size of one hand-over, not a limit on the sequence: X.25
        /// (4.3.4, 4.3.5) puts no bound on a sequence and names no reset for a long one.
        /// </summary>
        public const int MaxMessagePart = 65536;

        /// <summary>
        /// Forgets any partly sent message and any partly received sequence.
        /// </summary>
        public void ClearSequences()
        {
            PendingSegments.Clear();
            Reassembly?.Dispose();
            Reassembly = null;
        }

        /// <summary>
        /// Absolute millisecond expiry of the active per-circuit timer, or 0 when no timer is armed.
        /// The applicable timer depends on the state: call-request (T21), incoming call (T11), reset-request (T22),
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
            InterruptToConfirm = false;     // in both directions
            ResetForProcedureError = false;
            ClearSequences();               // and everything in flight in both directions
        }
    }

    /// <summary>
    /// One data packet's worth of a message waiting for the window to open.
    /// </summary>
    public readonly struct X25PendingSegment
    {
        /// <summary>
        /// Creates a segment.
        /// </summary>
        public X25PendingSegment(byte[] data, bool mBit, bool qBit, bool dBit)
        {
            Data = data;
            MBit = mBit;
            QBit = qBit;
            DBit = dBit;
        }

        /// <summary>
        /// The user data, at most the circuit's PacketSizeOut.
        /// </summary>
        public byte[] Data { get; }

        /// <summary>
        /// True on every segment but the last of a message.
        /// </summary>
        public bool MBit { get; }

        /// <summary>
        /// The message's qualifier bit.
        /// </summary>
        public bool QBit { get; }

        /// <summary>
        /// The message's delivery confirmation bit (carried, not honoured).
        /// </summary>
        public bool DBit { get; }
    }
}
