//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.X25.Switch.Ports
{
    /// <summary>
    /// An in-memory switch port with no real transport. Outward operations (calls placed, data sent, calls
    /// cleared) are captured for inspection, and inward stimuli can be injected, so the switching engine can
    /// be exercised end-to-end without any sockets.
    /// </summary>
    public sealed class LoopbackSwitchPort : ISwitchPort
    {
        private ushort _nextLci;
        private bool _active;

        /// <summary>
        /// Data the switch sent out of this port, in order: (logical channel, bytes, Q, M, D).
        /// </summary>
        public List<(ushort Lci, byte[] Data, bool Q, bool M, bool D)> SentData { get; } = new List<(ushort, byte[], bool, bool, bool)>();

        /// <summary>
        /// Incoming calls the switch accepted on this port, by logical channel.
        /// </summary>
        public List<ushort> AcceptedCalls { get; } = new List<ushort>();

        /// <summary>
        /// Calls the switch cleared on this port: (logical channel, cause, diagnostic).
        /// </summary>
        public List<(ushort Lci, byte Cause, byte Diag)> ClearedCalls { get; } = new List<(ushort, byte, byte)>();

        /// <summary>
        /// Calls the switch placed out of this port: (called, calling).
        /// </summary>
        public List<(string Called, string Calling)> PlacedCalls { get; } = new List<(string, string)>();

        /// <summary>
        /// Circuits the switch reset on this port: (logical channel, cause, diagnostic).
        /// </summary>
        public List<(ushort Lci, byte Cause, byte Diag)> ResetCircuits { get; } = new List<(ushort, byte, byte)>();

        /// <summary>
        /// Interrupts the switch sent out of this port: (logical channel, user data).
        /// </summary>
        public List<(ushort Lci, byte[] UserData)> SentInterrupts { get; } = new List<(ushort, byte[])>();

        /// <summary>
        /// Busy signals the switch raised on this port: (logical channel, busy).
        /// </summary>
        public List<(ushort Lci, bool Busy)> BusySignals { get; } = new List<(ushort, bool)>();

        /// <summary>
        /// Channels this port refuses to send on, as a peer with a shut window would.
        /// </summary>
        public HashSet<ushort> WindowShut { get; } = new HashSet<ushort>();

        /// <summary>
        /// Channels this port fails outright, as a dead circuit would.
        /// </summary>
        public HashSet<ushort> SendFails { get; } = new HashSet<ushort>();

        /// <summary>
        /// Gets the port number, taken from <see cref="PortConfig.Id"/> of the configuration.
        /// </summary>
        public int Id { get; }

        /// <summary>
        /// Gets the port's configuration.
        /// </summary>
        public PortConfig Config { get; }

        /// <summary>
        /// Gets a value indicating whether the port is up: <c>true</c> after
        /// <see cref="Start"/>, <c>false</c> after <see cref="Stop"/>.
        /// </summary>
        public bool Active => _active;

        /// <summary>
        /// Gets a value indicating whether a peer is connected over the port. This in-memory port
        /// has no real peer, so it reports the same as <see cref="Active"/>.
        /// </summary>
        public bool PeerConnected => _active;

        /// <summary>
        /// Gets the port's lifetime counters. This port never updates them itself.
        /// </summary>
        public PortCounters Counters { get; } = new PortCounters();

        /// <summary>
        /// Raised when a remote party calls into this port; here only by
        /// <see cref="InjectIncomingCall"/>.
        /// </summary>
        public event PortIncomingCall? IncomingCall;

        /// <summary>
        /// Raised when an outgoing call this port placed is accepted; here only by
        /// <see cref="InjectConnected"/>.
        /// </summary>
        public event PortCallConnected? CallConnected;

        /// <summary>
        /// Raised when data arrives on a circuit; here only by <see cref="InjectData"/>.
        /// </summary>
        public event PortDataReceived? DataReceived;

        /// <summary>
        /// Raised when a circuit on this port is reset (X.25 4.4); here only by
        /// <see cref="InjectReset"/>.
        /// </summary>
        public event PortReset? CircuitReset;

        /// <summary>
        /// Raised when an interrupt arrives on a circuit (X.25 4.3.2); here only by
        /// <see cref="InjectInterrupt"/>.
        /// </summary>
        public event PortInterrupt? InterruptReceived;

        /// <summary>
        /// Raised when a circuit that had refused a send can take data again; here only by
        /// <see cref="OpenWindow"/>.
        /// </summary>
        public event PortReadyToSend? ReadyToSend;

        /// <summary>
        /// Raised when a circuit on this port is cleared by the far end; here only by
        /// <see cref="InjectClear"/>.
        /// </summary>
        public event PortCallCleared? CallCleared;

        /// <summary>
        /// Raised when the port's up/down state changes: by <see cref="Start"/> with <c>true</c>
        /// and by <see cref="Stop"/> with <c>false</c>.
        /// </summary>
        public event PortStateChanged? StateChanged;

        /// <summary>
        /// Creates an in-memory port.
        /// </summary>
        /// <param name="config">
        /// The port configuration (its <see cref="PortConfig.LciBase"/> seeds channel allocation).
        /// </param>
        public LoopbackSwitchPort(PortConfig config)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
            Id = config.Id;
            _nextLci = config.LciBase == 0 ? (ushort)1 : config.LciBase;
        }

        /// <summary>
        /// Marks the port up and raises <see cref="StateChanged"/>. There is no transport to open.
        /// </summary>
        /// <returns>
        /// Always <c>true</c>.
        /// </returns>
        public bool Start()
        {
            _active = true;
            StateChanged?.Invoke(this, true);
            return true;
        }

        /// <summary>
        /// Marks the port down and raises <see cref="StateChanged"/>. There is no transport to
        /// release.
        /// </summary>
        public void Stop()
        {
            _active = false;
            StateChanged?.Invoke(this, false);
        }

        /// <summary>
        /// Records an outgoing call in <see cref="PlacedCalls"/> and allocates a logical channel
        /// for it. Nothing is sent anywhere.
        /// </summary>
        /// <param name="calledAddress">
        /// Destination X.121 address.
        /// </param>
        /// <param name="callingAddress">
        /// Originating X.121 address.
        /// </param>
        /// <param name="callUserData">
        /// Optional call user data. It is not recorded.
        /// </param>
        /// <returns>
        /// The next logical channel number. This port never refuses a call.
        /// </returns>
        public ushort PlaceCall(string calledAddress, string callingAddress, byte[]? callUserData)
        {
            ushort lci = AllocateLci();
            PlacedCalls.Add((calledAddress, callingAddress));
            return lci;
        }

        /// <summary>
        /// Records an accepted incoming call in <see cref="AcceptedCalls"/>.
        /// </summary>
        /// <param name="lci">
        /// The logical channel to accept.
        /// </param>
        public void AcceptCall(ushort lci)
        {
            AcceptedCalls.Add(lci);
        }

        /// <summary>
        /// Records data the switch sends out of this port in <see cref="SentData"/>.
        /// </summary>
        /// <param name="lci">
        /// Logical channel to send on.
        /// </param>
        /// <param name="data">
        /// Data to send; a copy is recorded.
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
        /// <see cref="PortSendResult.Failed"/> when <paramref name="lci"/> is in
        /// <see cref="SendFails"/>; <see cref="PortSendResult.WindowFull"/> when it is in
        /// <see cref="WindowShut"/>; otherwise <see cref="PortSendResult.Sent"/>. Only a sent
        /// packet is recorded.
        /// </returns>
        public PortSendResult SendData(ushort lci, ReadOnlySpan<byte> data, bool qBit, bool mBit, bool dBit)
        {
            if (SendFails.Contains(lci))
            {
                return PortSendResult.Failed;
            }

            if (WindowShut.Contains(lci))
            {
                return PortSendResult.WindowFull;
            }

            SentData.Add((lci, data.ToArray(), qBit, mBit, dBit));
            return PortSendResult.Sent;
        }

        /// <summary>
        /// Records a circuit reset in <see cref="ResetCircuits"/>. No circuit state is changed.
        /// </summary>
        /// <param name="lci">
        /// Logical channel to reset.
        /// </param>
        /// <param name="cause">
        /// Resetting cause code.
        /// </param>
        /// <param name="diagnostic">
        /// Diagnostic code.
        /// </param>
        public void ResetCircuit(ushort lci, byte cause, byte diagnostic)
        {
            ResetCircuits.Add((lci, cause, diagnostic));
        }

        /// <summary>
        /// Records an interrupt the switch sends out of this port in
        /// <see cref="SentInterrupts"/>.
        /// </summary>
        /// <param name="lci">
        /// Logical channel to interrupt.
        /// </param>
        /// <param name="userData">
        /// The interrupt user data; recorded as given, without a length check.
        /// </param>
        public void SendInterrupt(ushort lci, byte[] userData)
        {
            SentInterrupts.Add((lci, userData));
        }

        /// <summary>
        /// Records a busy or not-busy signal in <see cref="BusySignals"/>.
        /// </summary>
        /// <param name="lci">
        /// Logical channel to signal on.
        /// </param>
        /// <param name="busy">
        /// <c>true</c> for busy (RNR), <c>false</c> for ready (RR).
        /// </param>
        public void SetLocalBusy(ushort lci, bool busy)
        {
            BusySignals.Add((lci, busy));
        }

        /// <summary>
        /// Opens a channel's window again and tells the switch, the way a real port does when the
        /// peer acknowledges or clears its busy condition.
        /// </summary>
        /// <param name="lci">
        /// The logical channel that can send again.
        /// </param>
        public void OpenWindow(ushort lci)
        {
            WindowShut.Remove(lci);
            ReadyToSend?.Invoke(this, lci);
        }

        /// <summary>
        /// Injects a reset, as if the far DTE on this port had sent one.
        /// </summary>
        /// <param name="lci">
        /// The logical channel that was reset.
        /// </param>
        /// <param name="cause">
        /// Resetting cause code.
        /// </param>
        /// <param name="diagnostic">
        /// Diagnostic code.
        /// </param>
        public void InjectReset(ushort lci, byte cause, byte diagnostic)
        {
            CircuitReset?.Invoke(this, lci, cause, diagnostic);
        }

        /// <summary>
        /// Injects an interrupt, as if the far DTE on this port had sent one.
        /// </summary>
        /// <param name="lci">
        /// The logical channel it belongs to.
        /// </param>
        /// <param name="userData">
        /// The interrupt user data.
        /// </param>
        public void InjectInterrupt(ushort lci, byte[] userData)
        {
            InterruptReceived?.Invoke(this, lci, userData);
        }

        /// <summary>
        /// Records a cleared call in <see cref="ClearedCalls"/>. No event is raised.
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
        public void ClearCall(ushort lci, byte cause, byte diagnostic)
        {
            ClearedCalls.Add((lci, cause, diagnostic));
        }

        /// <summary>
        /// Injects an incoming call, as if a remote party dialled into this port.
        /// </summary>
        /// <param name="calledAddress">
        /// Dialled X.121 address.
        /// </param>
        /// <param name="callingAddress">
        /// Originating X.121 address.
        /// </param>
        /// <param name="callUserData">
        /// Optional call user data.
        /// </param>
        /// <returns>
        /// The logical channel allocated for the incoming call.
        /// </returns>
        public ushort InjectIncomingCall(string calledAddress, string callingAddress, byte[]? callUserData = null)
        {
            ushort lci = AllocateLci();
            IncomingCall?.Invoke(this, lci, calledAddress, callingAddress, callUserData ?? Array.Empty<byte>());
            return lci;
        }

        /// <summary>
        /// Injects an outgoing-call acceptance from the far end.
        /// </summary>
        /// <param name="lci">
        /// The logical channel that connected.
        /// </param>
        public void InjectConnected(ushort lci)
        {
            CallConnected?.Invoke(this, lci);
        }

        /// <summary>
        /// Injects received data on a logical channel.
        /// </summary>
        /// <param name="lci">
        /// Logical channel the data belongs to.
        /// </param>
        /// <param name="data">
        /// The data bytes.
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
        public void InjectData(ushort lci, byte[] data, bool qBit = false, bool mBit = false, bool dBit = false)
        {
            DataReceived?.Invoke(this, lci, data, data.Length, qBit, mBit, dBit);
        }

        /// <summary>
        /// Injects a clear on a logical channel, as if the remote party tore the circuit down.
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
        public void InjectClear(ushort lci, byte cause = 0, byte diagnostic = 0)
        {
            CallCleared?.Invoke(this, lci, cause, diagnostic);
        }

        private ushort AllocateLci()
        {
            ushort lci = _nextLci;
            _nextLci++;
            return lci;
        }
    }
}
