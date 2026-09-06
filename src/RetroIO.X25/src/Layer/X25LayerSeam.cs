using System;

namespace RetroIO.Protocols.X25.PacketLayer
{
    /// <summary>
    /// Raised when a remote party places a call to this endpoint. The upper layer responds with
    /// <see cref="X25Layer.AcceptCall"/> or <see cref="X25Layer.ClearCall"/>.
    /// </summary>
    /// <param name="layer">
    /// The layer that received the call.
    /// </param>
    /// <param name="lci">
    /// The logical channel the incoming call occupies.
    /// </param>
    /// <param name="calledAddress">
    /// The dialled X.121 address.
    /// </param>
    /// <param name="callingAddress">
    /// The originating X.121 address.
    /// </param>
    /// <param name="callUserData">
    /// Call user data carried in the request.
    /// </param>
    public delegate void X25IncomingCall(X25Layer layer, ushort lci, string calledAddress, string callingAddress, byte[] callUserData);

    /// <summary>
    /// Raised when a call this endpoint placed is accepted by the remote party.
    /// </summary>
    /// <param name="layer">
    /// The layer whose call connected.
    /// </param>
    /// <param name="lci">
    /// The logical channel now in the data-transfer state.
    /// </param>
    public delegate void X25CallConnected(X25Layer layer, ushort lci);

    /// <summary>
    /// Raised when a data packet is received on an established circuit.
    /// </summary>
    /// <param name="layer">
    /// The layer that received the data.
    /// </param>
    /// <param name="lci">
    /// The logical channel the data belongs to.
    /// </param>
    /// <param name="data">
    /// Buffer holding the received user data.
    /// </param>
    /// <param name="length">
    /// Number of valid bytes in <paramref name="data"/>.
    /// </param>
    /// <param name="qBit">
    /// Qualifier bit.
    /// </param>
    /// <param name="mBit">
    /// More-data bit.
    /// </param>
    /// <param name="dBit">
    /// Delivery-confirmation bit.
    /// </param>
    public delegate void X25DataReceived(X25Layer layer, ushort lci, byte[] data, int length, bool qBit, bool mBit, bool dBit);

    /// <summary>
    /// Raised when a circuit is cleared.
    /// </summary>
    /// <param name="layer">
    /// The layer whose circuit cleared.
    /// </param>
    /// <param name="lci">
    /// The logical channel that cleared.
    /// </param>
    /// <param name="cause">
    /// Clearing cause code.
    /// </param>
    /// <param name="diagnostic">
    /// Diagnostic code.
    /// </param>
    public delegate void X25CallCleared(X25Layer layer, ushort lci, byte cause, byte diagnostic);

    /// <summary>
    /// Raised when a circuit is reset (sequence state re-initialised without clearing the call).
    /// </summary>
    /// <param name="layer">
    /// The layer whose circuit reset.
    /// </param>
    /// <param name="lci">
    /// The logical channel that reset.
    /// </param>
    /// <param name="cause">
    /// Reset cause code.
    /// </param>
    /// <param name="diagnostic">
    /// Diagnostic code.
    /// </param>
    public delegate void X25CallReset(X25Layer layer, ushort lci, byte cause, byte diagnostic);

    /// <summary>
    /// Raised when the link is restarted (all switched circuits return to the ready state).
    /// </summary>
    /// <param name="layer">
    /// The layer that restarted.
    /// </param>
    /// <param name="cause">
    /// Restart cause code.
    /// </param>
    /// <param name="diagnostic">
    /// Diagnostic code.
    /// </param>
    public delegate void X25RestartOccurred(X25Layer layer, byte cause, byte diagnostic);

    /// <summary>
    /// Raised when our own Restart Request went unanswered through every retransmission (T20 x
    /// MaxRetransmissions). X.25 Table D.2 note 1: "recovery decisions should be taken at higher
    /// layers" - the link is most likely dead, and the owner should tear it down and re-open it.
    /// </summary>
    /// <param name="layer">
    /// The layer whose restart failed.
    /// </param>
    public delegate void X25RestartTimedOut(X25Layer layer);

    /// <summary>
    /// Raised when the peer sends a Diagnostic packet (X.25 5.6): it saw something wrong with a
    /// packet of ours and is telling us which packet and why. Nothing changes state; the owner may
    /// want to log it, because it is the only word a DCE gives about a discarded packet.
    /// </summary>
    /// <param name="layer">
    /// The layer that received it.
    /// </param>
    /// <param name="diagnostic">
    /// The Annex E diagnostic code.
    /// </param>
    /// <param name="explanation">
    /// The diagnostic explanation field: the first three octets of the offending packet's header
    /// (5.6.2), or empty.
    /// </param>
    public delegate void X25DiagnosticReceived(X25Layer layer, byte diagnostic, byte[] explanation);

    /// <summary>
    /// Raised when the peer sends an Interrupt (X.25 4.4.2): 1 to 32 octets that bypass the data
    /// flow control. The engine confirms it after this handler returns.
    /// </summary>
    /// <param name="layer">
    /// The layer.
    /// </param>
    /// <param name="lci">
    /// The logical channel.
    /// </param>
    /// <param name="data">
    /// The interrupt user data.
    /// </param>
    public delegate void X25InterruptReceived(X25Layer layer, ushort lci, byte[] data);

    /// <summary>
    /// Raised when the peer confirms an interrupt this side sent; another may now be sent.
    /// </summary>
    /// <param name="layer">
    /// The layer.
    /// </param>
    /// <param name="lci">
    /// The logical channel.
    /// </param>
    public delegate void X25InterruptConfirmed(X25Layer layer, ushort lci);

    /// <summary>
    /// The down interface the X.25 layer calls to hand fully-built packet bytes to the link below for
    /// framing and transmission. This is the "interfaces down" half of the layer seam.
    /// </summary>
    public interface IX25PacketSink
    {
        /// <summary>
        /// Frames and transmits a complete X.25 packet, and says whether the link took it.
        /// </summary>
        /// <param name="packet">
        /// The packet bytes (GFI + LCN + PTI + body).
        /// </param>
        /// <returns>
        /// <see cref="X25SendResult.Sent"/> when the link has accepted the packet for transmission;
        /// <see cref="X25SendResult.Refused"/> when it cannot take it right now (a full transmit
        /// queue, a LAPB window that is shut); <see cref="X25SendResult.LinkDown"/> when there is no
        /// link to send on. Never a byte count.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Until 2026-09-06 this returned <c>void</c>, so the engine could not know whether a packet
        /// had left, spent its P(S) regardless, and the peer saw a gap in the sequence numbers the
        /// next time a packet did get through - which X.25 4.4.1.3 makes a local procedure error and
        /// a reset. The same shape (a send that can be refused and a caller that does not look) was
        /// found five times over in the C reference switch and cost it packets on every trunk.
        /// </para>
        /// <para>
        /// The engine treats anything but <see cref="X25SendResult.Sent"/> as "the packet did not go"
        /// and rolls back the state change that assumed it had.
        /// </para>
        /// </remarks>
        X25SendResult SendPacket(ReadOnlySpan<byte> packet);
    }

    /// <summary>
    /// What the link below did with a packet the X.25 layer handed it.
    /// </summary>
    public enum X25SendResult
    {
        /// <summary>The link accepted the packet for transmission.</summary>
        Sent,

        /// <summary>The link cannot take the packet right now; the caller may retry later.</summary>
        Refused,

        /// <summary>There is no link to send on.</summary>
        LinkDown,
    }
}
