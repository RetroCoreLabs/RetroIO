using System;

namespace RetroIO.Protocols.X25.Switch.Ports
{
    /// <summary>
    /// Raised when a remote party places a call into this port.
    /// </summary>
    /// <param name="port">
    /// The port the call arrived on.
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
    /// Call user data carried with the request.
    /// </param>
    public delegate void PortIncomingCall(ISwitchPort port, ushort lci, string calledAddress, string callingAddress, byte[] callUserData);

    /// <summary>
    /// Raised when an outgoing call this port placed is accepted by the far end.
    /// </summary>
    /// <param name="port">
    /// The port whose outgoing call connected.
    /// </param>
    /// <param name="lci">
    /// The logical channel of the now-established circuit.
    /// </param>
    public delegate void PortCallConnected(ISwitchPort port, ushort lci);

    /// <summary>
    /// Raised when data arrives on an established circuit.
    /// </summary>
    /// <param name="port">
    /// The port the data arrived on.
    /// </param>
    /// <param name="lci">
    /// The logical channel the data belongs to.
    /// </param>
    /// <param name="data">
    /// Buffer holding the received data.
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
    public delegate void PortDataReceived(ISwitchPort port, ushort lci, byte[] data, int length, bool qBit, bool mBit, bool dBit);

    /// <summary>
    /// Raised when a circuit on this port is cleared.
    /// </summary>
    /// <param name="port">
    /// The port whose circuit cleared.
    /// </param>
    /// <param name="lci">
    /// The logical channel that was cleared.
    /// </param>
    /// <param name="cause">
    /// Clearing cause code.
    /// </param>
    /// <param name="diagnostic">
    /// Diagnostic code.
    /// </param>
    public delegate void PortCallCleared(ISwitchPort port, ushort lci, byte cause, byte diagnostic);

    /// <summary>
    /// Raised when a port's up/down state changes.
    /// </summary>
    /// <param name="port">
    /// The port whose state changed.
    /// </param>
    /// <param name="active">
    /// True if the port is now up.
    /// </param>
    public delegate void PortStateChanged(ISwitchPort port, bool active);

    /// <summary>
    /// Raised when a circuit on this port is reset.
    /// </summary>
    /// <remarks>
    /// X.25 4.4: a reset returns the circuit to the state it had at the start of data transfer.
    /// Both sequence numbers go back to zero and everything in flight is discarded, so a switch
    /// that carries the circuit must pass the reset on and throw away what it had queued. Nothing
    /// on the port contract could report one before Phase E1.
    /// </remarks>
    /// <param name="port">
    /// The port whose circuit was reset.
    /// </param>
    /// <param name="lci">
    /// The logical channel that was reset.
    /// </param>
    /// <param name="cause">
    /// Resetting cause code (Table 5-8).
    /// </param>
    /// <param name="diagnostic">
    /// Diagnostic code (Annex E).
    /// </param>
    public delegate void PortReset(ISwitchPort port, ushort lci, byte cause, byte diagnostic);

    /// <summary>
    /// Raised when an interrupt packet arrives on a circuit.
    /// </summary>
    /// <remarks>
    /// X.25 4.3.2: an interrupt carries one to 32 octets past the flow control of the data it
    /// overtakes, and only one may be outstanding in each direction at a time.
    /// </remarks>
    /// <param name="port">
    /// The port the interrupt arrived on.
    /// </param>
    /// <param name="lci">
    /// The logical channel it belongs to.
    /// </param>
    /// <param name="userData">
    /// The interrupt user data.
    /// </param>
    public delegate void PortInterrupt(ISwitchPort port, ushort lci, byte[] userData);

    /// <summary>
    /// Raised when a circuit that had refused a send can take data again.
    /// </summary>
    /// <remarks>
    /// This is what makes a queue in the switch drain: the window opened, or the peer's busy
    /// condition cleared. Without it the only way to retry a refused packet would be a timer.
    /// </remarks>
    /// <param name="port">
    /// The port whose circuit can send again.
    /// </param>
    /// <param name="lci">
    /// The logical channel that can send again.
    /// </param>
    public delegate void PortReadyToSend(ISwitchPort port, ushort lci);
}
