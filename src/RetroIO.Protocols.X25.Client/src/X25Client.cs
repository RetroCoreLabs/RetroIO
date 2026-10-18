//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetroIO.Link;
using RetroIO.Link.Framing;
using RetroIO.Protocols.X25.PacketLayer;

namespace RetroIO.Protocols.X25.Client
{
    /// <summary>
    /// A thin X.25 client that composes transport, XOT framing, and the packet layer into a single object,
    /// so console tools (echo, pad, traffic, ...) can place/accept calls, send/receive data, and react to
    /// circuit events without re-implementing the stack wiring.
    /// </summary>
    /// <remarks>
    /// The facade owns three collaborators wired as the "events up, interfaces down" seam:
    ///  - a <see cref="TcpLinkTransport"/> that connects to the switch and pumps raw bytes.
    ///  - an <see cref="XotFrameCodec"/> that turns the TCP byte stream into whole X.25 packets and back.
    ///  - an <see cref="X25Layer"/> that runs the call/data/clear state machine and raises up-events.
    ///
    /// Threading: the transport reads on its own background thread and drives the codec and the packet layer
    /// from there, while application calls (<see cref="Call"/>, <see cref="Send"/>, <see cref="Clear"/>) come
    /// from the application thread. A single lock serializes every access to the packet layer, so its
    /// non-thread-safe state stays consistent. Application callbacks fire on whichever thread advanced the
    /// state machine; an application blocks in <see cref="Run"/> until <see cref="Close"/> is called.
    /// </remarks>
    public sealed class X25Client : IDisposable
    {
        private readonly object _lock = new object();
        private readonly Dictionary<ushort, X25Vc> _vcs = new Dictionary<ushort, X25Vc>();
        private readonly Dictionary<ushort, ManualResetEventSlim> _connectWaiters = new Dictionary<ushort, ManualResetEventSlim>();
        private readonly ManualResetEventSlim _shutdown = new ManualResetEventSlim(false);

        private ILogger _logger = NullLogger.Instance;
        private X25ClientConfig _config = new X25ClientConfig();
        private TcpLinkTransport? _transport;
        private XotFrameCodec? _codec;
        private X25Layer? _x25;
        private Timer? _tick;
        private bool _disposed;

        private X25IncomingCallHandler? _onIncoming;
        private X25CallConnectedHandler? _onConnected;
        private X25DataHandler? _onData;
        private X25ClearHandler? _onClear;

        /// <summary>
        /// A friendly name identifying this client, used as the transport/link label in diagnostics.
        /// </summary>
        public string ServiceName { get; set; } = "X25Client";

        /// <summary>
        /// True once the client is open and its transport is connected.
        /// </summary>
        public bool IsOpen => _transport != null && _transport.IsOpen;

        /// <summary>
        /// The X.121 address this client uses as the calling address on every call it places.
        /// </summary>
        /// <value>
        /// The local address from the configuration given to <see cref="Open"/>; empty before the client is opened.
        /// </value>
        public string LocalAddress => _config.LocalAddress;

        /// <summary>
        /// Opens the client: connects an XOT transport to the configured switch, wires the framing codec
        /// and packet layer, and starts servicing the link.
        /// </summary>
        /// <param name="config">
        /// Connection and X.25 parameters.
        /// </param>
        /// <param name="logger">
        /// Logger passed to the transport; pass <see cref="NullLogger.Instance"/> to silence it.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="config"/> is null.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// Thrown when <see cref="X25ClientConfig.Framing"/> is not <see cref="X25ClientFraming.Xot"/> (SLIP/HDLC
        /// framing is not yet wired into the facade).
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the transport connection to the switch cannot be established.
        /// </exception>
        public void Open(X25ClientConfig config, ILogger? logger = null)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            if (config.Framing != X25ClientFraming.Xot)
            {
                throw new NotSupportedException(
                    "X25Client currently supports XOT framing only; SLIP/HDLC over TCP is a later pass.");
            }

            _config = config;
            _logger = logger ?? NullLogger.Instance;

            string name = string.IsNullOrWhiteSpace(ServiceName) ? "x25client" : ServiceName;

            // Transport: connect out to the switch (never listen for the console tools).
            _transport = new TcpLinkTransport(_logger, name, isServer: false, config.Host, config.Port);

            // Framing: XOT length-prefixed frames over the TCP byte stream.
            _codec = new XotFrameCodec(name, new FrameSink(_transport));

            // Packet layer: the state machine, sending down through the codec.
            X25Config x25config = new X25Config
            {
                LocalAddress = config.LocalAddress,
                IsDte = config.IsDte,
                LciBase = 1,
                LciCount = config.NumVcs == 0 ? (ushort)1 : config.NumVcs,
                DefaultPacketSize = config.PacketSize,
                DefaultWindowSize = config.WindowSize == 0 ? (byte)2 : config.WindowSize
            };
            _x25 = new X25Layer(new PacketSink(_codec), x25config);

            // Wire the seam: transport bytes -> codec -> whole packets -> layer; layer up-events -> callbacks.
            _transport.DataReceived += OnTransportBytes;
            _codec.FrameReceived += OnFrame;
            _x25.IncomingCall += OnLayerIncomingCall;
            _x25.CallConnected += OnLayerCallConnected;
            _x25.DataReceived += OnLayerData;
            _x25.Cleared += OnLayerCleared;

            if (!_transport.Open())
            {
                throw new InvalidOperationException(
                    $"X25Client could not connect to {config.Host}:{config.Port}.");
            }

            // X.25 packet-layer initialisation: send a Restart Request so the peer resets its virtual
            // circuits before we place any call. Some switches require this handshake on a fresh XOT
            // session and will ignore calls placed before it.
            lock (_lock)
            {
                _x25.Restart(0, 0);
            }

            // Pump the layer's protocol timers (call/clear/reset timeouts) on a light periodic tick.
            _tick = new Timer(OnTick, null, 250, 250);
        }

        /// <summary>
        /// Sets the callback invoked when a remote party calls our local address.
        /// </summary>
        /// <param name="handler">
        /// The incoming-call handler.
        /// </param>
        public void SetOnIncoming(X25IncomingCallHandler? handler)
        {
            _onIncoming = handler;
        }

        /// <summary>
        /// Sets the callback invoked when a call we placed becomes connected.
        /// </summary>
        /// <param name="handler">
        /// The call-connected handler.
        /// </param>
        public void SetOnConnected(X25CallConnectedHandler? handler)
        {
            _onConnected = handler;
        }

        /// <summary>
        /// Sets the callback invoked when data arrives on a circuit.
        /// </summary>
        /// <param name="handler">
        /// The data handler.
        /// </param>
        public void SetOnData(X25DataHandler? handler)
        {
            _onData = handler;
        }

        /// <summary>
        /// Sets the callback invoked when a circuit is cleared.
        /// </summary>
        /// <param name="handler">
        /// The clear handler.
        /// </param>
        public void SetOnClear(X25ClearHandler? handler)
        {
            _onClear = handler;
        }

        /// <summary>
        /// Places an X.25 call to the given address. Non-blocking: the returned circuit is not yet
        /// connected; wait for the connected callback, or use <see cref="CallWait"/>.
        /// </summary>
        /// <param name="calledAddress">
        /// Destination X.121 address.
        /// </param>
        /// <param name="callUserData">
        /// Optional call user data.
        /// </param>
        /// <returns>
        /// The new (not-yet-connected) circuit, or null if no channel was free.
        /// </returns>
        public X25Vc? Call(string calledAddress, byte[]? callUserData = null)
        {
            X25Layer x25 = RequireLayer();

            lock (_lock)
            {
                ushort lci = x25.PlaceCall(calledAddress ?? string.Empty, _config.LocalAddress, callUserData);
                if (lci == 0)
                {
                    return null;
                }

                X25Vc vc = new X25Vc(lci, calledAddress ?? string.Empty, _config.LocalAddress, incoming: false);
                _vcs[lci] = vc;
                return vc;
            }
        }

        /// <summary>
        /// Places a call and blocks until it connects or the timeout elapses.
        /// </summary>
        /// <param name="calledAddress">
        /// Destination X.121 address.
        /// </param>
        /// <param name="timeoutMs">
        /// Maximum time to wait for connection, in milliseconds.
        /// </param>
        /// <param name="callUserData">
        /// Optional call user data.
        /// </param>
        /// <returns>
        /// The connected circuit, or null on timeout, rejection, or failure.
        /// </returns>
        public X25Vc? CallWait(string calledAddress, int timeoutMs, byte[]? callUserData = null)
        {
            X25Vc? vc = Call(calledAddress, callUserData);
            if (vc == null)
            {
                return null;
            }

            ManualResetEventSlim waiter = new ManualResetEventSlim(false);
            lock (_lock)
            {
                if (vc.Connected)
                {
                    return vc;
                }

                _connectWaiters[vc.Lcn] = waiter;
            }

            bool signalled = waiter.Wait(timeoutMs);

            lock (_lock)
            {
                _connectWaiters.Remove(vc.Lcn);
            }

            return (signalled && vc.Connected) ? vc : null;
        }

        /// <summary>
        /// Sends data on a circuit.
        /// </summary>
        /// <param name="vc">
        /// The circuit to send on.
        /// </param>
        /// <param name="data">
        /// Buffer holding the data.
        /// </param>
        /// <param name="length">
        /// Number of valid bytes in <paramref name="data"/>, or -1 to use the whole buffer.
        /// </param>
        /// <param name="qBit">
        /// X.25 Qualifier bit.
        /// </param>
        /// <returns>
        /// True if the data was accepted for transmission (state and window permitting).
        /// </returns>
        public bool Send(X25Vc vc, byte[] data, int length = -1, bool qBit = false)
        {
            X25Layer x25 = RequireLayer();

            if (vc == null)
            {
                throw new ArgumentNullException(nameof(vc));
            }

            byte[] payload;
            if (length < 0 || length == data.Length)
            {
                payload = data;
            }
            else
            {
                // The layer takes a whole array; slice to the requested length without LINQ.
                payload = new byte[length];
                Array.Copy(data, 0, payload, 0, length);
            }

            lock (_lock)
            {
                return x25.SendData(vc.Lcn, payload, qBit, false, false);
            }
        }

        /// <summary>
        /// Sends a text string on a circuit (Q-bit clear), encoded as UTF-8.
        /// </summary>
        /// <param name="vc">
        /// The circuit to send on.
        /// </param>
        /// <param name="text">
        /// The text to send.
        /// </param>
        /// <returns>
        /// True if the data was accepted for transmission.
        /// </returns>
        public bool SendText(X25Vc vc, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
            return Send(vc, bytes, bytes.Length, false);
        }

        /// <summary>
        /// Accepts an inbound call. Call this from the incoming-call handler to establish the circuit;
        /// not calling it rejects the call.
        /// </summary>
        /// <param name="vc">
        /// The proposed circuit passed to the incoming-call handler.
        /// </param>
        public void Accept(X25Vc vc)
        {
            if (vc == null)
            {
                throw new ArgumentNullException(nameof(vc));
            }

            vc.AcceptRequested = true;
        }

        /// <summary>
        /// Clears (tears down) a circuit.
        /// </summary>
        /// <param name="vc">
        /// The circuit to clear.
        /// </param>
        /// <param name="cause">
        /// X.25 clear cause code.
        /// </param>
        /// <param name="diagnostic">
        /// X.25 diagnostic code.
        /// </param>
        public void Clear(X25Vc vc, byte cause = 0, byte diagnostic = 0)
        {
            X25Layer x25 = RequireLayer();

            if (vc == null)
            {
                throw new ArgumentNullException(nameof(vc));
            }

            lock (_lock)
            {
                x25.ClearCall(vc.Lcn, cause, diagnostic);
                _vcs.Remove(vc.Lcn);
            }

            vc.Connected = false;
        }

        /// <summary>
        /// Blocks the calling thread until <see cref="Close"/> is invoked (e.g. from a Ctrl+C handler).
        /// The transport services the wire on its own thread, so this simply blocks until
        /// <see cref="Close"/> is called.
        /// </summary>
        public void Run()
        {
            _shutdown.Wait();
        }

        /// <summary>
        /// Signals <see cref="Run"/> to return and releases the client.
        /// </summary>
        public void Close()
        {
            _shutdown.Set();
            Dispose();
        }

        /// <summary>
        /// Releases the client: lets <see cref="Run"/> return, stops the tick timer, closes and
        /// disposes the transport, and drops the codec and the packet layer. Calling it again does
        /// nothing.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _shutdown.Set();

            _tick?.Dispose();
            _tick = null;
            _transport?.Close();
            _transport?.Dispose();
            _transport = null;
            _codec = null;
            _x25 = null;
        }

        // ---- Seam wiring: transport -> codec -> layer (all on the transport read thread) ----

        private void OnTransportBytes(string linkId, ReadOnlySpan<byte> bytes)
        {
            _codec?.ProcessBytes(bytes);
        }

        private void OnFrame(string linkId, ReadOnlySpan<byte> frame)
        {
            X25Layer? x25 = _x25;
            if (x25 == null)
            {
                return;
            }

            lock (_lock)
            {
                x25.ProcessPacket(frame);
            }
        }

        private void OnTick(object? state)
        {
            X25Layer? x25 = _x25;
            if (x25 == null)
            {
                return;
            }

            lock (_lock)
            {
                x25.Tick(Environment.TickCount64);
            }
        }

        // ---- Layer up-events mapped to application callbacks ----

        private void OnLayerIncomingCall(X25Layer layer, ushort lci, string calledAddress, string callingAddress, byte[] callUserData)
        {
            // Track the circuit, let the application accept it (which sets AcceptRequested), then either
            // accept or reject on the layer. This runs under the frame lock, so layer calls are safe.
            X25Vc vc = new X25Vc(lci, calledAddress, callingAddress, incoming: true);
            _vcs[lci] = vc;

            _onIncoming?.Invoke(this, vc);

            if (vc.AcceptRequested)
            {
                vc.Connected = true;
                layer.AcceptCall(lci);
            }
            else
            {
                _vcs.Remove(lci);
                layer.ClearCall(lci, 0, 0);
            }
        }

        private void OnLayerCallConnected(X25Layer layer, ushort lci)
        {
            if (!_vcs.TryGetValue(lci, out X25Vc? vc))
            {
                return;
            }

            vc.Connected = true;
            if (_connectWaiters.TryGetValue(lci, out ManualResetEventSlim? waiter))
            {
                waiter.Set();
            }

            _onConnected?.Invoke(this, vc);
        }

        private void OnLayerData(X25Layer layer, ushort lci, byte[] data, int length, bool qBit, bool mBit, bool dBit)
        {
            if (!_vcs.TryGetValue(lci, out X25Vc? vc))
            {
                return;
            }

            _onData?.Invoke(this, vc, data, length, qBit, mBit, dBit);
        }

        private void OnLayerCleared(X25Layer layer, ushort lci, byte cause, byte diagnostic)
        {
            if (!_vcs.TryGetValue(lci, out X25Vc? vc))
            {
                return;
            }

            _vcs.Remove(lci);
            vc.Connected = false;

            // Wake any CallWait blocked on this channel so it returns promptly on a rejecting clear.
            if (_connectWaiters.TryGetValue(lci, out ManualResetEventSlim? waiter))
            {
                waiter.Set();
            }

            _onClear?.Invoke(this, vc, cause, diagnostic);
        }

        private X25Layer RequireLayer()
        {
            X25Layer? x25 = _x25;
            if (x25 == null)
            {
                throw new InvalidOperationException("X25Client is not open.");
            }

            return x25;
        }

        /// <summary>
        /// Adapts the framing codec to the packet layer's down interface: whole packets built by the layer
        /// are handed to the codec for XOT framing and transmission.
        /// </summary>
        private sealed class PacketSink : IX25PacketSink
        {
            private readonly XotFrameCodec _codec;

            /// <summary>
            /// Creates the sink over a framing codec.
            /// </summary>
            /// <param name="codec">
            /// The codec that frames and sends packets.
            /// </param>
            internal PacketSink(XotFrameCodec codec)
            {
                _codec = codec;
            }

            /// <summary>
            /// Frames a complete X.25 packet with the XOT codec and sends it.
            /// </summary>
            /// <param name="packet">
            /// The packet bytes (GFI + LCN + PTI + body).
            /// </param>
            /// <returns>
            /// <see cref="X25SendResult.Sent"/> when the codec took the packet;
            /// <see cref="X25SendResult.LinkDown"/> when it threw. This sink never returns
            /// <see cref="X25SendResult.Refused"/>.
            /// </returns>
            /// <remarks>
            /// The framing codec and the TCP transport below it report failure by throwing, so a
            /// throw here is "the link is gone", not a bug to surface: it becomes
            /// <see cref="X25SendResult.LinkDown"/> and the engine rolls the send back.
            /// </remarks>
            public X25SendResult SendPacket(ReadOnlySpan<byte> packet)
            {
                try
                {
                    _codec.SendFrame(packet);
                    return X25SendResult.Sent;
                }
                catch (Exception)
                {
                    return X25SendResult.LinkDown;
                }
            }
        }

        /// <summary>
        /// Adapts the link transport to the framing codec's send interface.
        /// </summary>
        private sealed class FrameSink : IFrameTransport
        {
            private readonly TcpLinkTransport _transport;

            /// <summary>
            /// Creates the sink over a transport.
            /// </summary>
            /// <param name="transport">
            /// The transport that writes framed bytes to the wire.
            /// </param>
            internal FrameSink(TcpLinkTransport transport)
            {
                _transport = transport;
            }

            /// <summary>
            /// Writes XOT-framed bytes to the TCP transport.
            /// </summary>
            /// <param name="bytes">
            /// The framed bytes to transmit. The span is only valid for the duration of the call.
            /// </param>
            /// <remarks>
            /// A failure in the transport is not caught here; it reaches the caller.
            /// </remarks>
            public void Send(ReadOnlySpan<byte> bytes)
            {
                _transport.Write(bytes);
            }
        }
    }
}
