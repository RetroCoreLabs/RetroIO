//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

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
        /// Raised when a circuit on this port is reset (X.25 4.4).
        /// </summary>
        event PortReset CircuitReset;

        /// <summary>
        /// Raised when an interrupt arrives on a circuit (X.25 4.3.2).
        /// </summary>
        event PortInterrupt InterruptReceived;

        /// <summary>
        /// Raised when a circuit that had refused a send can take data again.
        /// </summary>
        event PortReadyToSend ReadyToSend;

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
        /// <see cref="PortSendResult.Sent"/> when the packet is on its way,
        /// <see cref="PortSendResult.WindowFull"/> when it must wait for the window, and
        /// <see cref="PortSendResult.Failed"/> when it cannot be sent at all. The caller has to
        /// read this: dropping a refused packet leaves a gap in P(S) that resets the circuit.
        /// </returns>
        PortSendResult SendData(ushort lci, ReadOnlySpan<byte> data, bool qBit, bool mBit, bool dBit);

        /// <summary>
        /// Resets a circuit (X.25 4.4): sequence numbers back to zero, everything in flight
        /// discarded.
        /// </summary>
        /// <param name="lci">
        /// Logical channel to reset.
        /// </param>
        /// <param name="cause">
        /// Resetting cause code (Table 5-8).
        /// </param>
        /// <param name="diagnostic">
        /// Diagnostic code (Annex E).
        /// </param>
        void ResetCircuit(ushort lci, byte cause, byte diagnostic);

        /// <summary>
        /// Sends an interrupt on a circuit (X.25 4.3.2), past the flow control of the data it
        /// overtakes.
        /// </summary>
        /// <param name="lci">
        /// Logical channel to interrupt.
        /// </param>
        /// <param name="userData">
        /// One to 32 octets of interrupt user data.
        /// </param>
        void SendInterrupt(ushort lci, byte[] userData);

        /// <summary>
        /// Tells the far end on this circuit whether we can take data (X.25 4.4.2: RR when we can,
        /// RNR when we cannot).
        /// </summary>
        /// <remarks>
        /// This is how back pressure reaches the sender. When a switch cannot pass a packet on
        /// because the other leg's window is shut, it says so on the leg the data came from rather
        /// than queueing without limit or dropping.
        /// </remarks>
        /// <param name="lci">
        /// Logical channel to signal on.
        /// </param>
        /// <param name="busy">
        /// True to send RNR, false to send RR.
        /// </param>
        void SetLocalBusy(ushort lci, bool busy);

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
