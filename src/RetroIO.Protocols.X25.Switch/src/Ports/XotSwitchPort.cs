//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using RetroIO.Link.Framing;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.PacketLayer;

namespace RetroIO.Protocols.X25.Switch.Ports
{
    /// <summary>
    /// A switch port that carries X.25 over TCP (XOT, RFC 1613).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first switch port with a real transport under it. Until this existed the switch could
    /// only be driven by <see cref="LoopbackSwitchPort"/>, so nothing outside the process could call
    /// into it and the C emulator's client had nothing to talk to.
    /// </para>
    /// <para>
    /// It is built out of the same two pieces as everything else that speaks X.25 here: one
    /// <see cref="X25Layer"/> for the packet layer and one <see cref="XotFrameCodec"/> for the
    /// framing. Nothing about X.25 is decoded in this file. The port's job is to own the socket, to
    /// turn the engine's events into the port contract's events, and to answer a send with what the
    /// engine actually did.
    /// </para>
    /// <para>
    /// One connection at a time, which is what a port is: <see cref="ISwitchPort.PeerConnected"/> is
    /// a single flag and a stitch names a port and a channel. A DCE port listens and takes the first
    /// caller; a DTE port dials out and reconnects when the connection drops.
    /// </para>
    /// <para>
    /// Threading: the socket reader thread, the tick timer and the switch's own calls all go through
    /// one lock. Events are raised inside it, so a handler must not block or call back into the same
    /// port. That is the same rule <c>LapbStation</c> follows for the link layer.
    /// </para>
    /// </remarks>
    public sealed class XotSwitchPort : ISwitchPort, IDisposable
    {
        /// <summary>
        /// How often the packet layer's timers are given the time.
        /// </summary>
        private const int TickIntervalMs = 100;

        /// <summary>
        /// How long to wait before dialling again after a connection is lost.
        /// </summary>
        private const int RedialDelayMs = 2000;

        private readonly object _sync = new object();
        private readonly PortConfig _config;
        private readonly X25Layer _layer;
        private readonly XotFrameCodec _codec;
        private readonly Timer _ticker;

        private TcpListener? _listener;
        private TcpClient? _client;
        private NetworkStream? _stream;
        private Thread? _reader;
        private volatile bool _running;
        private bool _active;
        private bool _peerConnected;
        private bool _restartSent;
        private bool _disposed;

        /// <summary>
        /// Creates the port. Nothing happens on the network until <see cref="Start"/>.
        /// </summary>
        /// <param name="config">
        /// The port's configuration. <c>Mode</c> decides whether it listens or dials, <c>Port</c>
        /// and <c>Host</c> say where, and <c>LciBase</c> and <c>LciCount</c> give the engine its
        /// range of logical channels.
        /// </param>
        public XotSwitchPort(PortConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            Id = config.Id;

            X25Config layerConfig = new X25Config
            {
                // A DCE port faces a calling DTE, so it is the DCE; a DTE port dials a network.
                // This decides the direction the engine searches for a free channel (Annex A) and
                // which side wins a call collision (4.1.3), so it follows the port's own mode
                // rather than being assumed.
                IsDte = config.Mode != PortMode.Dce,
                LciBase = config.LciBase,
                LciCount = config.LciCount,
                DefaultWindowSize = config.DefaultWindowSize,
                DefaultPacketSize = config.DefaultPacketSize,
            };

            _layer = new X25Layer(new PacketSink(this), layerConfig);
            _layer.IncomingCall += OnLayerIncomingCall;
            _layer.CallConnected += OnLayerCallConnected;
            _layer.DataReceived += OnLayerData;
            _layer.Cleared += OnLayerCleared;
            _layer.Reset += OnLayerReset;
            _layer.InterruptReceived += OnLayerInterrupt;
            _layer.WindowOpened += OnLayerWindowOpened;

            _codec = new XotFrameCodec("port" + config.Id, new SocketSink(this));
            _codec.FrameReceived += OnXotFrame;
            _codec.FramingFailed += OnFramingFailed;

            _ticker = new Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>
        /// Gets the port number, taken from <see cref="PortConfig.Id"/> of the configuration.
        /// </summary>
        public int Id { get; }

        /// <summary>
        /// Gets the port's configuration.
        /// </summary>
        public PortConfig Config => _config;

        /// <summary>
        /// Gets a value indicating whether the port is up: <c>true</c> once <see cref="Start"/>
        /// has started the transport, <c>false</c> after <see cref="Stop"/> or a failed start.
        /// </summary>
        public bool Active
        {
            get { lock (_sync) { return _active; } }
        }

        /// <summary>
        /// Gets a value indicating whether a TCP connection to a peer is open: for a DCE port an
        /// accepted caller, for a DTE port a completed dial. It is independent of
        /// <see cref="Active"/>, which only says the port is running.
        /// </summary>
        public bool PeerConnected
        {
            get { lock (_sync) { return _peerConnected; } }
        }

        /// <summary>
        /// Gets the port's lifetime counters: calls in, calls cleared, packets in and packets out.
        /// </summary>
        public PortCounters Counters { get; } = new PortCounters();

        /// <summary>
        /// Raised when a remote party calls into this port. Raised inside the port's lock.
        /// </summary>
        public event PortIncomingCall? IncomingCall;

        /// <summary>
        /// Raised when an outgoing call this port placed is accepted. Raised inside the port's
        /// lock.
        /// </summary>
        public event PortCallConnected? CallConnected;

        /// <summary>
        /// Raised when data arrives on an established circuit. Raised inside the port's lock.
        /// </summary>
        public event PortDataReceived? DataReceived;

        /// <summary>
        /// Raised when the packet layer reports a circuit on this port cleared. Raised inside the
        /// port's lock.
        /// </summary>
        public event PortCallCleared? CallCleared;

        /// <summary>
        /// Raised when a circuit on this port is reset (X.25 4.4). Raised inside the port's lock.
        /// </summary>
        public event PortReset? CircuitReset;

        /// <summary>
        /// Raised when an interrupt arrives on a circuit (X.25 4.3.2). Raised inside the port's
        /// lock.
        /// </summary>
        public event PortInterrupt? InterruptReceived;

        /// <summary>
        /// Raised when the packet layer reports that a circuit's window opened, so a refused send
        /// may be tried again. Raised inside the port's lock.
        /// </summary>
        public event PortReadyToSend? ReadyToSend;

        /// <summary>
        /// Raised when the port's state changes: when <see cref="Active"/> changes, and when the
        /// TCP connection to the peer is lost, in which case it is raised with <c>false</c> and
        /// then again with the current value of <see cref="Active"/>.
        /// </summary>
        public event PortStateChanged? StateChanged;

        /// <summary>
        /// The port the listener actually bound to. Zero until a DCE port has started.
        /// </summary>
        /// <remarks>
        /// A configured port of zero asks the operating system to pick one, which is how a test
        /// avoids a fixed port number and the flakes that come with one.
        /// </remarks>
        public ushort BoundPort { get; private set; }

        /// <summary>
        /// Starts the port: a DCE port opens a TCP listener on the configured port; both modes then
        /// start the transport thread (which accepts or dials) and the packet layer's tick timer.
        /// </summary>
        /// <returns>
        /// <c>true</c> if the port started. When it is already running, the current value of
        /// <see cref="Active"/>. <c>false</c> when starting threw, for example when the listener
        /// could not bind.
        /// </returns>
        public bool Start()
        {
            lock (_sync)
            {
                if (_running)
                {
                    return _active;
                }

                _running = true;
            }

            try
            {
                if (_config.Mode == PortMode.Dce)
                {
                    TcpListener listener = new TcpListener(IPAddress.Any, _config.Port);
                    listener.Start();
                    _listener = listener;
                    BoundPort = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
                }

                _reader = new Thread(RunTransport) { IsBackground = true, Name = "XotSwitchPort" + Id };
                _reader.Start();
                _ticker.Change(TickIntervalMs, TickIntervalMs);

                SetActive(true);
                return true;
            }
            catch (Exception)
            {
                _running = false;
                SetActive(false);
                return false;
            }
        }

        /// <summary>
        /// Stops the port: halts the tick timer, closes the listener and the connection, waits up
        /// to one second for the transport thread to finish, and marks the port down.
        /// </summary>
        public void Stop()
        {
            _running = false;
            _ticker.Change(Timeout.Infinite, Timeout.Infinite);

            try
            {
                _listener?.Stop();
            }
            catch (Exception)
            {
                // Already down; the reader thread leaves on its own.
            }

            CloseConnection();

            Thread? reader = _reader;
            _reader = null;
            reader?.Join(1000);

            SetActive(false);
        }

        /// <summary>
        /// Places an outgoing call through the packet layer, allocating a logical channel for it.
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
        /// The logical channel the packet layer allocated, or 0 if the call could not be placed;
        /// always 0 when no peer is connected.
        /// </returns>
        /// <remarks>
        /// The first call on a new connection first sends a Restart (X.25 4.1); the packet layer
        /// holds the Call Request until the Restart Confirmation comes back.
        /// </remarks>
        public ushort PlaceCall(string calledAddress, string callingAddress, byte[]? callUserData)
        {
            lock (_sync)
            {
                if (!_peerConnected)
                {
                    return 0;
                }

                // X.25 4.1: the link is restarted once before the first call on it, and the engine
                // holds the Call Request until the Restart Confirmation comes back.
                if (!_restartSent)
                {
                    _restartSent = true;
                    _layer.Restart(0, 0);
                }

                return _layer.PlaceCall(calledAddress, callingAddress, callUserData);
            }
        }

        /// <summary>
        /// Accepts an incoming call on a logical channel through the packet layer.
        /// </summary>
        /// <param name="lci">
        /// The logical channel to accept.
        /// </param>
        public void AcceptCall(ushort lci)
        {
            lock (_sync)
            {
                _layer.AcceptCall(lci);
            }
        }

        /// <summary>
        /// Sends data on an established circuit through the packet layer.
        /// </summary>
        /// <param name="lci">
        /// Logical channel to send on.
        /// </param>
        /// <param name="data">
        /// Data to send; it is copied before being handed to the packet layer.
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
        /// <see cref="PortSendResult.Sent"/> when the packet layer took the data;
        /// <see cref="PortSendResult.WindowFull"/> when it refused only because of flow control
        /// (window shut, peer busy, or segments still waiting); <see cref="PortSendResult.Failed"/>
        /// when no peer is connected, the circuit is not in data transfer, the data is longer than
        /// the negotiated packet size, or the circuit does not exist. The caller has to read this:
        /// dropping a refused packet leaves a gap in P(S) that resets the circuit.
        /// </returns>
        public PortSendResult SendData(ushort lci, ReadOnlySpan<byte> data, bool qBit, bool mBit, bool dBit)
        {
            lock (_sync)
            {
                if (!_peerConnected)
                {
                    return PortSendResult.Failed;
                }

                if (_layer.SendData(lci, data.ToArray(), qBit, mBit, dBit))
                {
                    return PortSendResult.Sent;
                }

                return WouldFitLater(lci, data.Length) ? PortSendResult.WindowFull : PortSendResult.Failed;
            }
        }

        /// <summary>
        /// Clears a circuit on a logical channel through the packet layer.
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
            lock (_sync)
            {
                _layer.ClearCall(lci, cause, diagnostic);
            }
        }

        /// <summary>
        /// Resets a circuit (X.25 4.4) through the packet layer.
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
        public void ResetCircuit(ushort lci, byte cause, byte diagnostic)
        {
            lock (_sync)
            {
                _layer.ResetCall(lci, cause, diagnostic);
            }
        }

        /// <summary>
        /// Sends an interrupt on a circuit (X.25 4.3.2) through the packet layer.
        /// </summary>
        /// <param name="lci">
        /// Logical channel to interrupt.
        /// </param>
        /// <param name="userData">
        /// The interrupt user data.
        /// </param>
        public void SendInterrupt(ushort lci, byte[] userData)
        {
            lock (_sync)
            {
                _layer.SendInterrupt(lci, userData);
            }
        }

        /// <summary>
        /// Tells the far end on this circuit, through the packet layer, whether we can take data.
        /// </summary>
        /// <param name="lci">
        /// Logical channel to signal on.
        /// </param>
        /// <param name="busy">
        /// <c>true</c> when we cannot take data (RNR), <c>false</c> when we can (RR).
        /// </param>
        public void SetLocalBusy(ushort lci, bool busy)
        {
            lock (_sync)
            {
                _layer.SetBusy(lci, busy);
            }
        }

        /// <summary>
        /// Stops the port and disposes its tick timer. Calling it again does nothing.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Stop();
            _ticker.Dispose();
        }

        /// <summary>
        /// True when a refused send was refused only because the window is shut or the peer is
        /// busy, so holding the packet and trying again can help.
        /// </summary>
        /// <param name="lci">
        /// The channel the send was refused on.
        /// </param>
        /// <param name="length">
        /// How many bytes were offered.
        /// </param>
        /// <returns>
        /// True for a flow control refusal, false for anything else.
        /// </returns>
        private bool WouldFitLater(ushort lci, int length)
        {
            if (!_layer.Circuits.TryGetValue(lci, out X25Vc? vc))
            {
                return false;
            }

            if (vc.State != X25VcState.DataTransfer && vc.State != X25VcState.PvcReady)
            {
                return false;
            }

            if (length > vc.PacketSizeOut)
            {
                // Too long for what was negotiated. Waiting will not make it fit.
                return false;
            }

            return vc.PeerBusy || vc.PendingSegments.Count > 0 || ((vc.SendSeq - vc.AckedSeq) & 0x07) >= vc.WindowSizeOut;
        }

        // ------------------------------------------------------------------ transport

        /// <summary>
        /// Owns the connection for the port's whole life: accept or dial, read until the connection
        /// ends, then do it again.
        /// </summary>
        private void RunTransport()
        {
            while (_running)
            {
                try
                {
                    TcpClient? client = _config.Mode == PortMode.Dce ? Accept() : Dial();
                    if (client == null)
                    {
                        continue;
                    }

                    OpenConnection(client);
                    ReadUntilClosed(client);
                }
                catch (Exception)
                {
                    // A dropped connection is ordinary. The loop makes or waits for the next one.
                }
                finally
                {
                    CloseConnection();
                }
            }
        }

        private TcpClient? Accept()
        {
            TcpListener? listener = _listener;
            if (listener == null)
            {
                return null;
            }

            try
            {
                return listener.AcceptTcpClient();
            }
            catch (Exception)
            {
                return null;   // Stop() closed the listener
            }
        }

        private TcpClient? Dial()
        {
            try
            {
                TcpClient client = new TcpClient();
                client.Connect(_config.Host, _config.Port);
                return client;
            }
            catch (Exception)
            {
                // Wait before dialling again, so a port whose peer is down does not spin.
                for (int waited = 0; waited < RedialDelayMs && _running; waited += 100)
                {
                    Thread.Sleep(100);
                }

                return null;
            }
        }

        private void OpenConnection(TcpClient client)
        {
            lock (_sync)
            {
                _client = client;
                _stream = client.GetStream();
                _codec.Reset();
                _restartSent = false;
                _peerConnected = true;
            }
        }

        private void ReadUntilClosed(TcpClient client)
        {
            byte[] buffer = new byte[XotFrameCodec.FrameMaxPayload];
            NetworkStream stream = client.GetStream();

            while (_running)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    return;
                }

                lock (_sync)
                {
                    _codec.ProcessBytes(new ReadOnlySpan<byte>(buffer, 0, read));
                }
            }
        }

        private void CloseConnection()
        {
            TcpClient? client;
            bool wasConnected;

            lock (_sync)
            {
                client = _client;
                wasConnected = _peerConnected;
                _client = null;
                _stream = null;
                _peerConnected = false;
                _restartSent = false;
            }

            try
            {
                client?.Close();
            }
            catch (Exception)
            {
                // Already closed.
            }

            if (wasConnected)
            {
                // The circuits on this port are gone with the connection. The switch clears the far
                // leg of each of them when it sees the port go down.
                StateChanged?.Invoke(this, false);
                StateChanged?.Invoke(this, Active);
            }
        }

        private void SetActive(bool active)
        {
            bool changed;
            lock (_sync)
            {
                changed = _active != active;
                _active = active;
            }

            if (changed)
            {
                StateChanged?.Invoke(this, active);
            }
        }

        private void OnTick(object? state)
        {
            lock (_sync)
            {
                _layer.Tick(Environment.TickCount64);
            }
        }

        /// <summary>
        /// Writes framed bytes to the socket. Only the codec calls this.
        /// </summary>
        /// <param name="bytes">
        /// The framed bytes.
        /// </param>
        private void WriteToSocket(ReadOnlySpan<byte> bytes)
        {
            NetworkStream? stream = _stream;
            if (stream == null)
            {
                return;
            }

            stream.Write(bytes);
        }

        private void OnXotFrame(string linkId, ReadOnlySpan<byte> frame)
        {
            // Already inside the lock: the reader thread takes it before calling the codec.
            _layer.ProcessPacket(frame);
        }

        private void OnFramingFailed(string linkId, string reason)
        {
            // RFC 1613 has no marker to resynchronize on. Closing the socket from here would
            // deadlock against the reader thread's own lock, so the connection is simply dropped by
            // closing the client; the reader's next read returns and the loop tidies up.
            try
            {
                _client?.Close();
            }
            catch (Exception)
            {
                // Already going.
            }
        }

        // ------------------------------------------------------------------ engine events

        private void OnLayerIncomingCall(X25Layer layer, ushort lci, string called, string calling, byte[] cud)
        {
            Counters.IncrementCallsIn();
            IncomingCall?.Invoke(this, lci, called, calling, cud);
        }

        private void OnLayerCallConnected(X25Layer layer, ushort lci)
        {
            CallConnected?.Invoke(this, lci);
        }

        private void OnLayerData(X25Layer layer, ushort lci, byte[] data, int length, bool qBit, bool mBit, bool dBit)
        {
            Counters.IncrementPacketsIn();
            DataReceived?.Invoke(this, lci, data, length, qBit, mBit, dBit);
        }

        private void OnLayerCleared(X25Layer layer, ushort lci, byte cause, byte diagnostic)
        {
            Counters.IncrementCallsCleared();
            CallCleared?.Invoke(this, lci, cause, diagnostic);
        }

        private void OnLayerReset(X25Layer layer, ushort lci, byte cause, byte diagnostic)
        {
            CircuitReset?.Invoke(this, lci, cause, diagnostic);
        }

        private void OnLayerInterrupt(X25Layer layer, ushort lci, byte[] userData)
        {
            InterruptReceived?.Invoke(this, lci, userData);
        }

        private void OnLayerWindowOpened(X25Layer layer, ushort lci)
        {
            ReadyToSend?.Invoke(this, lci);
        }

        /// <summary>
        /// Where the packet layer's output goes: one complete X.25 packet, framed by the codec and
        /// written to the socket.
        /// </summary>
        private sealed class PacketSink : IX25PacketSink
        {
            private readonly XotSwitchPort _owner;

            /// <summary>
            /// Creates the sink.
            /// </summary>
            /// <param name="owner">
            /// The port that owns the connection.
            /// </param>
            public PacketSink(XotSwitchPort owner)
            {
                _owner = owner;
            }

            /// <summary>
            /// Frames a complete X.25 packet with the XOT codec and writes it to the socket.
            /// </summary>
            /// <param name="packet">
            /// The packet bytes (GFI + LCN + PTI + body).
            /// </param>
            /// <returns>
            /// <see cref="X25SendResult.Sent"/> when the packet was written and counted as a packet
            /// out; <see cref="X25SendResult.LinkDown"/> when there is no connection or the write
            /// threw. This sink never returns <see cref="X25SendResult.Refused"/>.
            /// </returns>
            public X25SendResult SendPacket(ReadOnlySpan<byte> packet)
            {
                if (_owner._stream == null)
                {
                    return X25SendResult.LinkDown;
                }

                try
                {
                    _owner._codec.SendFrame(packet);
                    _owner.Counters.IncrementPacketsOut();
                    return X25SendResult.Sent;
                }
                catch (Exception)
                {
                    return X25SendResult.LinkDown;
                }
            }
        }

        /// <summary>
        /// Where the codec's output goes: XOT-framed bytes, straight to the socket.
        /// </summary>
        private sealed class SocketSink : IFrameTransport
        {
            private readonly XotSwitchPort _owner;

            /// <summary>
            /// Creates the sink.
            /// </summary>
            /// <param name="owner">
            /// The port that owns the socket.
            /// </param>
            public SocketSink(XotSwitchPort owner)
            {
                _owner = owner;
            }

            /// <summary>
            /// Writes XOT-framed bytes to the port's socket. Does nothing when there is no
            /// connection.
            /// </summary>
            /// <param name="bytes">
            /// The framed bytes to transmit. The span is only valid for the duration of the call.
            /// </param>
            public void Send(ReadOnlySpan<byte> bytes)
            {
                _owner.WriteToSocket(bytes);
            }
        }
    }
}
