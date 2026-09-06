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
    /// The down interface the X.25 layer calls to hand fully-built packet bytes to the link below for
    /// framing and transmission. This is the "interfaces down" half of the layer seam.
    /// </summary>
    public interface IX25PacketSink
    {
        /// <summary>
        /// Frames and transmits a complete X.25 packet.
        /// </summary>
        /// <param name="packet">
        /// The packet bytes (GFI + LCN + PTI + body).
        /// </param>
        void SendPacket(ReadOnlySpan<byte> packet);
    }
}
