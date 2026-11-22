//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using RetroIO.Protocols.X25.PacketLayer;

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
        /// Places an outgoing call that asks for the given window and packet sizes.
        /// </summary>
        /// <remarks>
        /// The switch passes on what the calling side asked for (X.25 6.9 and 6.12 negotiate end to end). A port that does
        /// not carry these values places the call as <see cref="PlaceCall(string, string, byte[])"/> does.
        /// </remarks>
        /// <param name="calledAddress">
        /// Destination X.121 address.
        /// </param>
        /// <param name="callingAddress">
        /// Originating X.121 address.
        /// </param>
        /// <param name="callUserData">
        /// Optional call user data.
        /// </param>
        /// <param name="requested">
        /// The values to ask for, "out" being the direction this port sends in.
        /// </param>
        /// <returns>
        /// The allocated logical channel, or 0 if the call could not be placed.
        /// </returns>
        ushort PlaceCall(string calledAddress, string callingAddress, byte[]? callUserData, X25CallParameters requested)
        {
            return PlaceCall(calledAddress, callingAddress, callUserData);
        }

        /// <summary>
        /// Places an outgoing call and, when it cannot be placed, says why.
        /// </summary>
        /// <remarks>
        /// The switch clears the calling side with the cause given here. A port that knows only that the call failed
        /// gives <c>null</c>, and the switch then judges from whether the port is in service. The XOT port says "out of
        /// order" when the far end cannot be reached (X.25 Table C.3 a) item 30) and "number busy" when it has no
        /// channel left (item 31); before 2026-11-20 every failure on a port with a connection up was called busy.
        /// </remarks>
        /// <param name="calledAddress">
        /// Destination X.121 address.
        /// </param>
        /// <param name="callingAddress">
        /// Originating X.121 address.
        /// </param>
        /// <param name="callUserData">
        /// Optional call user data.
        /// </param>
        /// <param name="requested">
        /// The window and packet sizes to ask for, or <c>null</c> for the port's own.
        /// </param>
        /// <param name="refusal">
        /// When the call could not be placed: the cause and diagnostic to clear the caller with, or <c>null</c> when
        /// the port cannot tell.
        /// </param>
        /// <returns>
        /// The allocated logical channel, or 0 if the call could not be placed.
        /// </returns>
        ushort PlaceCall(string calledAddress, string callingAddress, byte[]? callUserData, X25CallParameters? requested, out X25CallDecision? refusal)
        {
            refusal = null;
            return requested.HasValue
                ? PlaceCall(calledAddress, callingAddress, callUserData, requested.Value)
                : PlaceCall(calledAddress, callingAddress, callUserData);
        }

        /// <summary>
        /// Returns the window and packet sizes a call on a logical channel runs with.
        /// </summary>
        /// <remarks>
        /// For an incoming call these are what the caller asked for; for a placed call that has been connected, what the far
        /// end agreed to. A port that does not carry these values returns false.
        /// </remarks>
        /// <param name="lci">
        /// The logical channel.
        /// </param>
        /// <param name="parameters">
        /// The values, "out" being the direction this port sends in.
        /// </param>
        /// <returns>
        /// True when the port knows the values.
        /// </returns>
        bool TryGetCallParameters(ushort lci, out X25CallParameters parameters)
        {
            parameters = default;
            return false;
        }

        /// <summary>
        /// Accepts an incoming call with the window and packet sizes the call was agreed to run with.
        /// </summary>
        /// <param name="lci">
        /// The logical channel to accept.
        /// </param>
        /// <param name="agreed">
        /// The values to run with, "out" being the direction this port sends in.
        /// </param>
        void AcceptCall(ushort lci, X25CallParameters agreed)
        {
            AcceptCall(lci);
        }

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
        /// Creates a permanent virtual circuit on a channel of this port (X.25 4.2): in data transfer from the start, with
        /// the port's default window and packet size, never set up or cleared.
        /// </summary>
        /// <param name="lci">
        /// The channel, one of the port's PVC channels.
        /// </param>
        /// <returns>
        /// True when the circuit exists; false when this kind of port cannot hold a PVC (the default).
        /// </returns>
        bool AddPvc(ushort lci)
        {
            return false;
        }

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

        /// <summary>
        /// Clears a circuit with a Clear Request that carries a facility field (X.25 5.2.4.2, the extended format): what
        /// the other leg's clear carried, passed on by the switch (PLAN B1).
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
        /// <param name="facilities">
        /// The facility field, byte for byte; empty sends the basic format.
        /// </param>
        void ClearCall(ushort lci, byte cause, byte diagnostic, byte[] facilities);

        /// <summary>
        /// Gets the facility field of the clear that ended the last call on a channel, for the switch to pass to the other
        /// leg. Read it when <see cref="CallCleared"/> is raised; a new call on the channel empties it.
        /// </summary>
        /// <param name="lci">
        /// Logical channel.
        /// </param>
        /// <returns>
        /// The field byte for byte, or empty.
        /// </returns>
        byte[] GetClearFacilities(ushort lci);

        /// <summary>
        /// Gets the X.75 utility field of the clear that ended the last call on a channel (a refusal's transit list, X.75
        /// 5.3.1); empty on a port that is not between networks, or when it had none. Added 2026-11-22.
        /// </summary>
        /// <param name="lci">
        /// Logical channel.
        /// </param>
        /// <returns>
        /// The field byte for byte, or empty.
        /// </returns>
        byte[] GetClearUtilities(ushort lci);

        /// <summary>
        /// Clears a circuit with a facility field, and between networks names the transit networks of
        /// <paramref name="transitList"/> in a refusal (X.75 5.3.1). Added 2026-11-22.
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
        /// <param name="facilities">
        /// The facility field, byte for byte; empty sends the basic format.
        /// </param>
        /// <param name="transitList">
        /// The utility field of the next network's refusal, or null for the Call Request's own list.
        /// </param>
        void ClearCall(ushort lci, byte cause, byte diagnostic, byte[] facilities, byte[]? transitList);

        /// <summary>
        /// Asked for the facility field of the DCE Clear Confirmation when the DTE on this port clears a call: the
        /// charging answer (X.25 5.2.5, 6.22). Null, or a result that is null or empty, sends the basic confirmation.
        /// </summary>
        System.Func<ushort, byte[]?>? ClearConfirmationFacilities { get; set; }
    }
}
