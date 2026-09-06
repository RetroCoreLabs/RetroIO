using System;

namespace RetroIO.Protocols.X25.PacketLayer
{
    /// <summary>
    /// The abstract base of the X.25 packet layer: it owns the up-facing events and the down-facing packet
    /// sink, so the concrete <see cref="X25Layer"/> only implements the state machine.
    /// </summary>
    /// <remarks>
    /// This is the "events up, interfaces down" seam: applications subscribe to the events; the layer sends
    /// through the injected <see cref="IX25PacketSink"/>. Concrete subclasses raise events only through the
    /// protected helpers, never by touching the event fields.
    /// </remarks>
    public abstract class X25LayerBase
    {
        private readonly IX25PacketSink _sink;

        /// <summary>
        /// Raised when a remote party calls this endpoint.
        /// </summary>
        public event X25IncomingCall? IncomingCall;

        /// <summary>
        /// Raised when an outgoing call is accepted.
        /// </summary>
        public event X25CallConnected? CallConnected;

        /// <summary>
        /// Raised when data arrives on an established circuit.
        /// </summary>
        public event X25DataReceived? DataReceived;

        /// <summary>
        /// Raised when a circuit is cleared.
        /// </summary>
        public event X25CallCleared? Cleared;

        /// <summary>
        /// Raised when a circuit is reset.
        /// </summary>
        public event X25CallReset? Reset;

        /// <summary>
        /// Raised when the link is restarted.
        /// </summary>
        public event X25RestartOccurred? RestartOccurred;

        /// <summary>
        /// Creates the base with the down-facing packet sink.
        /// </summary>
        /// <param name="sink">
        /// The link below, called to frame and transmit packets.
        /// </param>
        protected X25LayerBase(IX25PacketSink sink)
        {
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        }

        /// <summary>
        /// The down-facing packet sink the concrete layer sends through.
        /// </summary>
        protected IX25PacketSink Sink => _sink;

        /// <summary>
        /// How many packets the sink has refused or dropped since the layer was created. Every
        /// refusal the engine could roll back was rolled back; the ones it could not (an RR after
        /// data was already accepted, a confirmation) are only counted here.
        /// </summary>
        public long SendFailures { get; private set; }

        /// <summary>
        /// Hands a packet to the sink and reports whether it went.
        /// </summary>
        /// <param name="packet">
        /// The complete packet.
        /// </param>
        /// <returns>
        /// True when the sink returned <see cref="X25SendResult.Sent"/>.
        /// </returns>
        protected bool Send(ReadOnlySpan<byte> packet)
        {
            if (_sink.SendPacket(packet) == X25SendResult.Sent)
            {
                return true;
            }

            SendFailures++;
            return false;
        }

        /// <summary>
        /// Raises the incoming-call event.
        /// </summary>
        /// <param name="lci">Logical channel.</param>
        /// <param name="calledAddress">Called address.</param>
        /// <param name="callingAddress">Calling address.</param>
        /// <param name="callUserData">Call user data.</param>
        protected void RaiseIncomingCall(ushort lci, string calledAddress, string callingAddress, byte[] callUserData)
        {
            IncomingCall?.Invoke((X25Layer)this, lci, calledAddress, callingAddress, callUserData);
        }

        /// <summary>
        /// Raises the call-connected event.
        /// </summary>
        /// <param name="lci">Logical channel.</param>
        protected void RaiseCallConnected(ushort lci)
        {
            CallConnected?.Invoke((X25Layer)this, lci);
        }

        /// <summary>
        /// Raises the data-received event.
        /// </summary>
        /// <param name="lci">Logical channel.</param>
        /// <param name="data">Data buffer.</param>
        /// <param name="length">Valid byte count.</param>
        /// <param name="qBit">Qualifier bit.</param>
        /// <param name="mBit">More-data bit.</param>
        /// <param name="dBit">Delivery-confirmation bit.</param>
        protected void RaiseDataReceived(ushort lci, byte[] data, int length, bool qBit, bool mBit, bool dBit)
        {
            DataReceived?.Invoke((X25Layer)this, lci, data, length, qBit, mBit, dBit);
        }

        /// <summary>
        /// Raises the cleared event.
        /// </summary>
        /// <param name="lci">Logical channel.</param>
        /// <param name="cause">Clearing cause.</param>
        /// <param name="diagnostic">Diagnostic code.</param>
        protected void RaiseCleared(ushort lci, byte cause, byte diagnostic)
        {
            Cleared?.Invoke((X25Layer)this, lci, cause, diagnostic);
        }

        /// <summary>
        /// Raises the reset event.
        /// </summary>
        /// <param name="lci">Logical channel.</param>
        /// <param name="cause">Reset cause.</param>
        /// <param name="diagnostic">Diagnostic code.</param>
        protected void RaiseReset(ushort lci, byte cause, byte diagnostic)
        {
            Reset?.Invoke((X25Layer)this, lci, cause, diagnostic);
        }

        /// <summary>
        /// Raises the restart event.
        /// </summary>
        /// <param name="cause">Restart cause.</param>
        /// <param name="diagnostic">Diagnostic code.</param>
        protected void RaiseRestartOccurred(byte cause, byte diagnostic)
        {
            RestartOccurred?.Invoke((X25Layer)this, cause, diagnostic);
        }
    }
}
