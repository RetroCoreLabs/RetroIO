using System;

namespace RetroIO.Protocols.X25.Switch.Ports
{
    /// <summary>
    /// A transport-agnostic switch port: one X.25 access or trunk endpoint, decoupled from the medium
    /// (XOT, HDLC-over-TCP, serial, or an in-memory loopback for tests).
    /// </summary>
    /// <remarks>
    /// The switching engine drives ports through the methods (place/accept/clear calls, send data) and
    /// reacts to their events (incoming call, connected, data, cleared, state). This "events up, methods
    /// down" seam is what lets a switch be composed from different port kinds and tested without sockets.
    /// </remarks>
    public interface ISwitchPort
    {
        /// <summary>
        /// The port number.
        /// </summary>
        int Id { get; }

        /// <summary>
        /// The port's configuration.
        /// </summary>
        PortConfig Config { get; }

        /// <summary>
        /// Whether the port is currently up.
        /// </summary>
        bool Active { get; }

        /// <summary>
        /// Whether a peer is actually connected over the port (dialed in for a DCE trunk, or the
        /// outbound connection is up for a DTE trunk). For a trunk, false here means the trunk is not
        /// linked, independently of <see cref="Active"/> (which only reflects the transport being up).
        /// </summary>
        bool PeerConnected { get; }

        /// <summary>
        /// The port's lifetime counters.
        /// </summary>
        PortCounters Counters { get; }

        /// <summary>
        /// Raised when a remote party calls into this port.
        /// </summary>
        event PortIncomingCall IncomingCall;

        /// <summary>
        /// Raised when an outgoing call this port placed is accepted.
        /// </summary>
        event PortCallConnected CallConnected;

        /// <summary>
        /// Raised when data arrives on an established circuit.
        /// </summary>
        event PortDataReceived DataReceived;

        /// <summary>
        /// Raised when a circuit on this port is cleared.
        /// </summary>
        event PortCallCleared CallCleared;

        /// <summary>
        /// Raised when the port's up/down state changes.
        /// </summary>
        event PortStateChanged StateChanged;

        /// <summary>
        /// Starts the port (opens its transport / begins listening or connecting).
        /// </summary>
        /// <returns>
        /// True if the port started.
        /// </returns>
        bool Start();

        /// <summary>
        /// Stops the port and releases its transport.
        /// </summary>
        void Stop();

        /// <summary>
        /// Places an outgoing call, allocating a logical channel for it.
        /// </summary>
        /// <param name="calledAddress">
        /// Destination X.121 address.
        /// </param>
        /// <param name="callingAddress">
        /// Originating X.121 address.
        /// </param>
        /// <param name="callUserData">
        /// Optional call user data.
        /// </param>
        /// <returns>
        /// The allocated logical channel, or 0 if the call could not be placed.
        /// </returns>
        ushort PlaceCall(string calledAddress, string callingAddress, byte[]? callUserData);

        /// <summary>
        /// Accepts an incoming call on a logical channel.
        /// </summary>
        /// <param name="lci">
        /// The logical channel to accept.
        /// </param>
        void AcceptCall(ushort lci);

        /// <summary>
        /// Sends data on an established circuit.
        /// </summary>
        /// <param name="lci">
        /// Logical channel to send on.
        /// </param>
        /// <param name="data">
        /// Data to send.
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
        /// <returns>
        /// True if the data was accepted for transmission.
        /// </returns>
        bool SendData(ushort lci, ReadOnlySpan<byte> data, bool qBit, bool mBit, bool dBit);

        /// <summary>
        /// Clears a circuit on a logical channel.
        /// </summary>
        /// <param name="lci">
        /// Logical channel to clear.
        /// </param>
        /// <param name="cause">
        /// Clearing cause code.
        /// </param>
        /// <param name="diagnostic">
        /// Diagnostic code.
        /// </param>
        void ClearCall(ushort lci, byte cause, byte diagnostic);
    }
}
