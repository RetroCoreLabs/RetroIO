using System;

namespace RetroIO.Protocols.X25.Client
{
    /// <summary>
    /// Raised when a remote party places a call to our local address. The handler accepts the circuit
    /// by calling <see cref="X25Client.Accept"/>; not accepting rejects the call.
    /// </summary>
    /// <param name="client">
    /// The client that received the call.
    /// </param>
    /// <param name="vc">
    /// The proposed virtual circuit.
    /// </param>
    public delegate void X25IncomingCallHandler(X25Client client, X25Vc vc);

    /// <summary>
    /// Raised when a call we placed is accepted by the remote party and the circuit is established.
    /// </summary>
    /// <param name="client">
    /// The client that placed the call.
    /// </param>
    /// <param name="vc">
    /// The now-connected virtual circuit.
    /// </param>
    public delegate void X25CallConnectedHandler(X25Client client, X25Vc vc);

    /// <summary>
    /// Raised when data arrives on an established circuit.
    /// </summary>
    /// <param name="client">
    /// The client that received the data.
    /// </param>
    /// <param name="vc">
    /// The virtual circuit the data arrived on.
    /// </param>
    /// <param name="data">
    /// Buffer holding the received data.
    /// </param>
    /// <param name="length">
    /// Number of valid bytes in <paramref name="data"/>.
    /// </param>
    /// <param name="qBit">
    /// X.25 Qualifier bit.
    /// </param>
    /// <param name="mBit">
    /// X.25 More-data bit.
    /// </param>
    /// <param name="dBit">
    /// X.25 Delivery-confirmation bit.
    /// </param>
    public delegate void X25DataHandler(X25Client client, X25Vc vc, byte[] data, int length, bool qBit, bool mBit, bool dBit);

    /// <summary>
    /// Raised when a circuit is cleared (torn down).
    /// </summary>
    /// <param name="client">
    /// The client whose circuit was cleared.
    /// </param>
    /// <param name="vc">
    /// The virtual circuit that was cleared.
    /// </param>
    /// <param name="cause">
    /// X.25 clear cause code.
    /// </param>
    /// <param name="diagnostic">
    /// X.25 diagnostic code.
    /// </param>
    public delegate void X25ClearHandler(X25Client client, X25Vc vc, byte cause, byte diagnostic);
}
