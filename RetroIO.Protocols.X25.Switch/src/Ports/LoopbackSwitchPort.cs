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

        /// <inheritdoc/>
        public int Id { get; }

        /// <inheritdoc/>
        public PortConfig Config { get; }

        /// <inheritdoc/>
        public bool Active => _active;

        /// <inheritdoc/>
        public bool PeerConnected => _active;

        /// <inheritdoc/>
        public PortCounters Counters { get; } = new PortCounters();

        /// <inheritdoc/>
        public event PortIncomingCall? IncomingCall;

        /// <inheritdoc/>
        public event PortCallConnected? CallConnected;

        /// <inheritdoc/>
        public event PortDataReceived? DataReceived;

        /// <inheritdoc/>
        public event PortCallCleared? CallCleared;

        /// <inheritdoc/>
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

        /// <inheritdoc/>
        public bool Start()
        {
            _active = true;
            StateChanged?.Invoke(this, true);
            return true;
        }

        /// <inheritdoc/>
        public void Stop()
        {
            _active = false;
            StateChanged?.Invoke(this, false);
        }

        /// <inheritdoc/>
        public ushort PlaceCall(string calledAddress, string callingAddress, byte[]? callUserData)
        {
            ushort lci = AllocateLci();
            PlacedCalls.Add((calledAddress, callingAddress));
            return lci;
        }

        /// <inheritdoc/>
        public void AcceptCall(ushort lci)
        {
            AcceptedCalls.Add(lci);
        }

        /// <inheritdoc/>
        public bool SendData(ushort lci, ReadOnlySpan<byte> data, bool qBit, bool mBit, bool dBit)
        {
            SentData.Add((lci, data.ToArray(), qBit, mBit, dBit));
            return true;
        }

        /// <inheritdoc/>
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
