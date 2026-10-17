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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    /// <para>
    /// What a monitoring switch needs from it: <see cref="PacketCaptured"/> shows every packet in
    /// both directions, <see cref="Counters"/> carries the packet and byte totals, and
    /// <see cref="ConnectAttempts"/>, <see cref="LastConnectedMs"/> and <see cref="LastDisconnectedMs"/>
    /// describe the link. They are counted as the C emulator counts them, so the two report the same
    /// numbers for the same traffic. The optional logger gets one line for each thing that happens to
    /// the link: listening, dialling, connection up, connection down, and a port that cannot start.
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
        private readonly ILogger _logger;
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
        private bool _disposed;
        private int _connectAttempts;
        private long _lastConnectedMs;
        private long _lastDisconnectedMs;

        /// <summary>
        /// Creates the port. Nothing happens on the network until <see cref="Start"/>.
        /// </summary>
        /// <param name="config">
        /// The port's configuration. <c>Mode</c> decides whether it listens or dials, <c>Port</c>
        /// and <c>Host</c> say where (for a listening port <c>Host</c> is the local address to bind,
        /// and empty or <c>0.0.0.0</c> means every address), and <c>LciBase</c> and <c>LciCount</c>
        /// give the engine its range of logical channels.
        /// </param>
        /// <param name="logger">
        /// Where the port reports what happens to its link. Null reports nothing.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="config"/> is null.
        /// </exception>
        public XotSwitchPort(PortConfig config, ILogger? logger = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? NullLogger.Instance;
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

        /// <inheritdoc/>
        public int Id { get; }

        /// <inheritdoc/>
        public PortConfig Config => _config;

        /// <inheritdoc/>
        public bool Active
        {
            get { lock (_sync) { return _active; } }
        }

        /// <inheritdoc/>
        public bool PeerConnected
        {
            get { lock (_sync) { return _peerConnected; } }
        }

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
        public event PortReset? CircuitReset;

        /// <inheritdoc/>
        public event PortInterrupt? InterruptReceived;

        /// <inheritdoc/>
        public event PortReadyToSend? ReadyToSend;

        /// <inheritdoc/>
        public event PortStateChanged? StateChanged;

        /// <summary>
        /// Raised for every X.25 packet the port receives or writes, for a monitor to show.
        /// </summary>
        /// <remarks>
        /// See <see cref="PortPacketCaptured"/> for what the handler may do. Nothing is copied while
        /// no handler is attached.
        /// </remarks>
        public event PortPacketCaptured? PacketCaptured;

        /// <summary>
        /// Gets how many times the port has tried to make a connection: each dial for a DTE port
        /// and each accepted connection for a DCE port.
        /// </summary>
        public int ConnectAttempts => Volatile.Read(ref _connectAttempts);

        /// <summary>
        /// Gets the time, as <see cref="Environment.TickCount64"/>, at which a peer last connected;
        /// 0 if none has.
        /// </summary>
        public long LastConnectedMs => Interlocked.Read(ref _lastConnectedMs);

        /// <summary>
        /// Gets the time, as <see cref="Environment.TickCount64"/>, at which a connected peer last
        /// went away; 0 if none has.
        /// </summary>
        public long LastDisconnectedMs => Interlocked.Read(ref _lastDisconnectedMs);

        /// <summary>
        /// The port the listener actually bound to. Zero until a DCE port has started.
        /// </summary>
        /// <remarks>
        /// A configured port of zero asks the operating system to pick one, which is how a test
        /// avoids a fixed port number and the flakes that come with one.
        /// </remarks>
        public ushort BoundPort { get; private set; }

        /// <inheritdoc/>
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
                    TcpListener listener = new TcpListener(ParseBindAddress(_config.Host), _config.Port);
                    listener.Start();
                    _listener = listener;
                    BoundPort = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
                    _logger.LogInformation("Port {Id} ({Mode} {Type}): listening on {Address}:{Port}",
                        Id, _config.Mode, _config.Type, ((IPEndPoint)listener.LocalEndpoint).Address, BoundPort);
                }
                else
                {
                    _logger.LogInformation("Port {Id} ({Mode} {Type}): dialling {Host}:{Port}",
                        Id, _config.Mode, _config.Type, _config.Host, _config.Port);
                }

                _reader = new Thread(RunTransport) { IsBackground = true, Name = "XotSwitchPort" + Id };
                _reader.Start();
                _ticker.Change(TickIntervalMs, TickIntervalMs);

                SetActive(true);
                return true;
            }
            catch (Exception ex)
            {
                // A port that cannot start is reported, not hidden: a switch that goes on without its
                // listener still answers its management console and looks healthy while every call
                // routed to the port goes nowhere.
                _logger.LogError(ex, "Port {Id} ({Mode} {Type}): could NOT start on {Host}:{Port} - the port is down",
                    Id, _config.Mode, _config.Type, _config.Host, _config.Port);
                _running = false;
                SetActive(false);
                return false;
            }
        }

        /// <summary>
        /// Works out the local address a listening port binds to.
        /// </summary>
        /// <param name="host">
        /// The configured <c>Host</c>: empty or <c>0.0.0.0</c> for every address, otherwise an address
        /// such as <c>127.0.0.1</c>.
        /// </param>
        /// <returns>
        /// The address to listen on.
        /// </returns>
        /// <exception cref="FormatException">
        /// Thrown when <paramref name="host"/> is not an address. A host name is not accepted, because
        /// the C emulator takes the same setting as an address.
        /// </exception>
        private static IPAddress ParseBindAddress(string host)
        {
            if (string.IsNullOrEmpty(host) || host == "0.0.0.0")
            {
                return IPAddress.Any;
            }

            if (!IPAddress.TryParse(host, out IPAddress? address))
            {
                throw new FormatException("'" + host + "' is not an address the port can listen on");
            }

            return address;
        }

        /// <inheritdoc/>
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

            CloseConnection("port stopped");

            Thread? reader = _reader;
            _reader = null;
            reader?.Join(1000);

            SetActive(false);
        }

        /// <inheritdoc/>
        public ushort PlaceCall(string calledAddress, string callingAddress, byte[]? callUserData)
        {
            lock (_sync)
            {
                if (!_peerConnected)
                {
                    return 0;
                }

                // No restart first. X.25 4.1 restarts a link before its first call, but this link is a
                // TCP connection, and RFC 1613 6.4 says Restart "MUST NOT be sent over the TCP
                // connection" and, if received, "MUST be silently discarded". A conforming XOT peer
                // therefore never answers one, and the packet layer holds the Call Request until a
                // Restart Confirmation that cannot come. That is how a call from this switch to the C
                // emulator across a trunk failed after five seconds, although the same call worked on
                // the port this one replaces, which never sent a restart.
                return _layer.PlaceCall(calledAddress, callingAddress, callUserData);
            }
        }

        /// <inheritdoc/>
        public void AcceptCall(ushort lci)
        {
            lock (_sync)
            {
                _layer.AcceptCall(lci);
            }
        }

        /// <inheritdoc/>
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

        /// <inheritdoc/>
        public void ClearCall(ushort lci, byte cause, byte diagnostic)
        {
            lock (_sync)
            {
                _layer.ClearCall(lci, cause, diagnostic);
            }
        }

        /// <inheritdoc/>
        public void ResetCircuit(ushort lci, byte cause, byte diagnostic)
        {
            lock (_sync)
            {
                _layer.ResetCall(lci, cause, diagnostic);
            }
        }

        /// <inheritdoc/>
        public void SendInterrupt(ushort lci, byte[] userData)
        {
            lock (_sync)
            {
                _layer.SendInterrupt(lci, userData);
            }
        }

        /// <inheritdoc/>
        public void SetLocalBusy(ushort lci, bool busy)
        {
            lock (_sync)
            {
                _layer.SetBusy(lci, busy);
            }
        }

        /// <inheritdoc/>
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
                string reason = "closed by peer";
                try
                {
                    TcpClient? client = _config.Mode == PortMode.Dce ? Accept() : Dial();
                    if (client == null)
                    {
                        continue;
                    }

                    OpenConnection(client, _config.Mode == PortMode.Dce ? "accepted" : "dialled");
                    ReadUntilClosed(client);
                }
                catch (Exception ex)
                {
                    // A dropped connection is ordinary. The loop makes or waits for the next one; the
                    // reason is kept for the "connection down" line.
                    reason = ex.Message;
                }
                finally
                {
                    CloseConnection(reason);
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
                TcpClient accepted = listener.AcceptTcpClient();
                Interlocked.Increment(ref _connectAttempts);
                return accepted;
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
                Interlocked.Increment(ref _connectAttempts);
                TcpClient client = new TcpClient();
                client.Connect(_config.Host, _config.Port);
                return client;
            }
            catch (Exception ex)
            {
                // A peer that is down is ordinary and is retried, so this is not worth a line at the
                // default level every two seconds.
                _logger.LogDebug("Port {Id}: dial to {Host}:{Port} failed ({Reason})", Id, _config.Host, _config.Port, ex.Message);

                // Wait before dialling again, so a port whose peer is down does not spin.
                for (int waited = 0; waited < RedialDelayMs && _running; waited += 100)
                {
                    Thread.Sleep(100);
                }

                return null;
            }
        }

        private void OpenConnection(TcpClient client, string how)
        {
            lock (_sync)
            {
                _client = client;
                _stream = client.GetStream();
                _codec.Reset();
                _peerConnected = true;
            }

            Interlocked.Exchange(ref _lastConnectedMs, Environment.TickCount64);
            _logger.LogInformation("Port {Id} ({Mode} {Type}): connection up ({How})", Id, _config.Mode, _config.Type, how);
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

        /// <summary>
        /// Drops the connection, if there is one. The "connection down" line and the disconnect time
        /// are recorded only when a connected peer actually went away, so several paths that see the
        /// same drop report it once.
        /// </summary>
        /// <param name="reason">
        /// Why the connection ended, for the log line.
        /// </param>
        private void CloseConnection(string reason)
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
                Interlocked.Exchange(ref _lastDisconnectedMs, Environment.TickCount64);
                _logger.LogInformation("Port {Id} ({Mode} {Type}): connection down ({Reason})", Id, _config.Mode, _config.Type, reason);

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
            //
            // The packet is counted and shown to the tap before the engine sees it, so a packet the
            // engine then refuses still appears in the monitor and in the byte total, as it crossed
            // the wire.
            Counters.AddBytesIn(frame.Length);
            PortPacketCaptured? tap = PacketCaptured;
            if (tap != null)
            {
                tap(Id, true, frame.ToArray());
            }

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
            _logger.LogInformation("Port {Id}: incoming call {Calling} -> {Called} LCI={Lci}", Id, calling, called, lci);
            IncomingCall?.Invoke(this, lci, called, calling, cud);
        }

        private void OnLayerCallConnected(X25Layer layer, ushort lci)
        {
            _logger.LogInformation("Port {Id}: call accepted LCI={Lci}", Id, lci);
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
            _logger.LogInformation("Port {Id}: clear LCI={Lci} cause={Cause} diag={Diag}", Id, lci, cause, diagnostic);
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

            /// <inheritdoc/>
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
                    _owner.Counters.AddBytesOut(packet.Length);

                    // Only a packet that was written is shown and counted as sent.
                    PortPacketCaptured? tap = _owner.PacketCaptured;
                    if (tap != null)
                    {
                        tap(_owner.Id, false, packet.ToArray());
                    }

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

            /// <inheritdoc/>
            public void Send(ReadOnlySpan<byte> bytes)
            {
                _owner.WriteToSocket(bytes);
            }
        }
    }
}
