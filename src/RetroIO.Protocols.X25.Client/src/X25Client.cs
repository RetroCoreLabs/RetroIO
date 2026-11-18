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
using RetroIO.Protocols.X25.Transport;
using Leg = RetroIO.Protocols.X25.Client.X25ClientLeg;

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
    /// from there, while application calls (<see cref="Call(string, byte[])"/>, <see cref="Send"/>, <see cref="Clear"/>) come
    /// from the application thread. A single lock serializes every access to the packet layer, so its
    /// non-thread-safe state stays consistent. Application callbacks fire on whichever thread advanced the
    /// state machine; an application blocks in <see cref="Run"/> until <see cref="Close"/> is called.
    /// </remarks>
    public sealed class X25Client : IDisposable
    {
        // First channel number given to a circuit on a connection opened for one call. The first connection (the one
        // Open makes) keeps the packet layer's own numbers, 1 to NumVcs, as before.
        private const ushort FirstExtraLcn = 4096;

        private readonly object _lock = new object();
        private readonly Dictionary<ushort, ManualResetEventSlim> _connectWaiters = new Dictionary<ushort, ManualResetEventSlim>();
        private readonly ManualResetEventSlim _shutdown = new ManualResetEventSlim(false);

        // RFC 1613 section 5: "A separate TCP connection MUST be used for each X.25 virtual circuit." Every connection
        // has its own transport, framing codec and packet layer (a Leg), and carries one call. Each call placed dials a
        // leg of its own. Calls that arrive need a connection the switch can put them on, so one unused leg - the spare
        // - is always kept open; Open makes the first. When a call arrives on the spare, a new spare is opened. A leg
        // whose call has ended is closed and never used again (the rule x25emu and our own switch follow since
        // 2026-11-18). Until then the leg Open made took every arriving call and the first call placed, and was kept
        // after its calls ended. All legs are guarded by _lock.
        private readonly List<Leg> _legs = new List<Leg>();
        private readonly HashSet<ushort> _extraLcns = new HashSet<ushort>();
        private ushort _nextExtraLcn = FirstExtraLcn;
        private Leg? _spare;

        // When the spare is dialled again after the switch closed it or a dial failed (XotSpareRedial). Guarded by
        // _lock, like the timer and the pending flag.
        private XotSpareRedial? _spareRedial;
        private Timer? _spareRedialTimer;
        private bool _spareRedialPending;
        private bool _opened;
        private int _legCounter;

        private ILogger _logger = NullLogger.Instance;
        private X25ClientConfig _config = new X25ClientConfig();
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
        /// True once the client is open and at least one of its connections is up (the spare that waits for arriving
        /// calls, or a connection carrying a call).
        /// </summary>
        public bool IsOpen
        {
            get
            {
                lock (_lock)
                {
                    if (!_opened || _disposed)
                    {
                        return false;
                    }

                    for (int i = 0; i < _legs.Count; i++)
                    {
                        if (!_legs[i].Dead && !_legs[i].Closing && _legs[i].Transport.IsOpen)
                        {
                            return true;
                        }
                    }

                    return false;
                }
            }
        }

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
            _spareRedial = new XotSpareRedial(config.SpareRedialFirstMs, config.SpareRedialMostMs);

            // The first connection: transport, XOT framing and packet layer. It is the first spare, which waits for an
            // arriving call; every call placed opens a connection of its own (see Call).
            Leg first = CreateLeg(isPrimary: true, SpareCircuits);
            lock (_lock)
            {
                _opened = true;
                _spare = first;
                _legs.Add(first);
            }

            if (!first.Transport.Open())
            {
                throw new InvalidOperationException(
                    $"X25Client could not connect to {config.Host}:{config.Port}.");
            }

            // No Restart Request is sent. RFC 1613 section 6.4: Restart, Restart Confirm, DTE Reject, Diagnostic and the
            // registration packets "MUST NOT be sent over the TCP connection", and a peer that receives one "MUST"
            // silently discard it, so nobody answers. The layer holds every call while its own Restart is unanswered
            // (X.25 4.1.1), so sending one made the first call wait for an answer that never comes (180 seconds), which
            // is exactly what happened against x25emu. The circuit is up when the TCP connection is. Measured against
            // x25emu (2026-11-12): a Call Request on a fresh connection is answered at once with Call Accepted, and a
            // Restart Request is never answered. This used to send a Restart here on the belief that some switches
            // ignore a call placed before it; a switch that does is not speaking XOT.

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
            return Call(calledAddress, callUserData, Array.Empty<byte>());
        }

        /// <summary>
        /// Places an X.25 call carrying further facility elements (X.25 7.1 encoding, other than window and packet size) -
        /// a NUI selection, for one. Otherwise the same as <see cref="Call(string, byte[])"/>.
        /// </summary>
        /// <param name="calledAddress">Destination X.121 address.</param>
        /// <param name="callUserData">Optional call user data.</param>
        /// <param name="facilities">The facility elements, byte for byte; empty for none.</param>
        /// <returns>The new (not-yet-connected) circuit, or null if no channel was free.</returns>
        public X25Vc? Call(string calledAddress, byte[]? callUserData, byte[] facilities)
        {
            RequireOpen();

            // RFC 1613 section 5: one TCP connection for each circuit. A call placed goes on the spare when the spare
            // is still unused (so the first call goes on the connection Open made, which a peer that takes a single
            // connection needs), and a new spare is opened for calls that arrive. Otherwise the call opens a
            // connection of its own.
            Leg? leg = null;
            lock (_lock)
            {
                Leg? spare = _spare;
                if (spare != null && !spare.Used && !spare.Dead && spare.Vcs.Count == 0)
                {
                    spare.Used = true;
                    _spare = null;
                    leg = spare;
                    ThreadPool.QueueUserWorkItem(_ => OpenSpare());
                }
            }

            if (leg == null)
            {
                // A new connection takes one circuit: the packet layer on it needs one channel.
                leg = CreateLeg(isPrimary: false, 1);
                bool connected;
                try
                {
                    connected = leg.Transport.Open();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "X25Client: could not open a connection for a call to {Called}", calledAddress);
                    connected = false;
                }

                if (!connected)
                {
                    CloseLeg(leg);
                    return null;
                }

                lock (_lock)
                {
                    leg.Used = true;
                    _legs.Add(leg);
                }
            }

            lock (_lock)
            {
                ushort lci = facilities.Length == 0
                    ? leg.Layer.PlaceCall(calledAddress ?? string.Empty, _config.LocalAddress, callUserData)
                    : leg.Layer.PlaceCall(calledAddress ?? string.Empty, _config.LocalAddress, callUserData, facilities);
                if (lci == 0)
                {
                    if (!leg.IsPrimary)
                    {
                        _legs.Remove(leg);
                        ThreadPool.QueueUserWorkItem(_ => CloseLeg(leg));
                    }

                    return null;
                }

                X25Vc vc = new X25Vc(leg.IsPrimary ? lci : AllocateExtraLcn(), calledAddress ?? string.Empty, _config.LocalAddress, incoming: false);
                vc.Leg = leg;
                vc.LegLci = lci;
                leg.Vcs[lci] = vc;
                leg.AwaitingAnswer.Add(lci);

                // The connection may have been dropped by the peer before the call was registered (a port with no room
                // for another connection closes it at once): the call can never be answered.
                if (leg.Dead || !leg.Transport.IsOpen)
                {
                    leg.Vcs.Remove(lci);
                    ReleaseExtraLcn(vc);
                    if (!leg.IsPrimary)
                    {
                        _legs.Remove(leg);
                        ThreadPool.QueueUserWorkItem(_ => CloseLeg(leg));
                    }

                    return null;
                }

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
        /// <param name="mBit">
        /// X.25 More-data bit (section 4.3): this packet is followed by more of the same message. Only a
        /// full packet may carry it unless <paramref name="dBit"/> is also set (4.3.3); the caller
        /// builds the chain, one call per packet, M set on all but the last.
        /// </param>
        /// <param name="dBit">
        /// X.25 Delivery-confirmation bit (section 4.4.1.4): the peer's acknowledgement of this packet
        /// then means the remote user has received it, not just the network.
        /// </param>
        /// <returns>
        /// True if the data was accepted for transmission (state and window permitting).
        /// </returns>
        public bool Send(X25Vc vc, byte[] data, int length = -1, bool qBit = false, bool mBit = false, bool dBit = false)
        {
            X25Layer x25 = LayerOf(vc);

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
                return x25.SendData(vc.LegLci, payload, qBit, mBit, dBit);
            }
        }

        /// <summary>
        /// The largest data field, in bytes, that can be sent on a circuit (the packet size negotiated for
        /// the sending direction).
        /// </summary>
        /// <param name="vc">
        /// The circuit.
        /// </param>
        /// <returns>
        /// The packet size in bytes, or 0 when the circuit is not known to the packet layer.
        /// </returns>
        public int GetPacketSizeOut(X25Vc vc)
        {
            X25Layer x25 = LayerOf(vc);

            if (vc == null)
            {
                throw new ArgumentNullException(nameof(vc));
            }

            lock (_lock)
            {
                return x25.Circuits.TryGetValue(vc.LegLci, out RetroIO.Protocols.X25.PacketLayer.X25Vc? circuit)
                    ? circuit.PacketSizeOut
                    : 0;
            }
        }

        /// <summary>
        /// Waits until the peer has acknowledged every data packet sent on a circuit, or the time
        /// runs out, or the circuit is cleared.
        /// </summary>
        /// <param name="vc">
        /// The circuit to wait on.
        /// </param>
        /// <param name="timeoutMs">
        /// The longest wait, in milliseconds.
        /// </param>
        /// <returns>
        /// True when every packet sent has been acknowledged (V(A) has reached V(S)). False when the
        /// time ran out or the circuit is gone or being cleared.
        /// </returns>
        /// <remarks>
        /// <para>
        /// X.25 section 4.5 lets the network discard data sent just before a clear, so a service that
        /// says goodbye and clears at once may never be heard. Waiting for the peer's acknowledgement
        /// (its P(R)) before the clear means the peer's packet layer has the packet. It is not a
        /// delivery confirmation to the user: that is the D bit, which not every peer honours.
        /// </para>
        /// <para>
        /// The wait spins and yields for the first moments, because an acknowledgement usually comes
        /// back in well under a millisecond, and only then sleeps: a <c>Thread.Sleep(1)</c> lasts about
        /// 15 ms on Windows.
        /// </para>
        /// </remarks>
        public bool WaitForAcknowledgement(X25Vc vc, int timeoutMs)
        {
            X25Layer x25 = LayerOf(vc);

            if (vc == null)
            {
                throw new ArgumentNullException(nameof(vc));
            }

            long deadline = Environment.TickCount64 + timeoutMs;
            int spins = 0;
            while (true)
            {
                lock (_lock)
                {
                    // A circuit that is gone, or is being cleared, will never be acknowledged. (The
                    // engine keeps a cleared circuit in its table until the clear is confirmed, so
                    // being in the table is not enough.)
                    if (!x25.Circuits.TryGetValue(vc.LegLci, out RetroIO.Protocols.X25.PacketLayer.X25Vc? circuit)
                        || circuit.State == X25VcState.Ready
                        || circuit.State == X25VcState.ClearRequest
                        || circuit.State == X25VcState.ClearIndication)
                    {
                        return false;
                    }

                    // Packets sent but not yet acknowledged: V(S) - V(A), modulo 8.
                    if (((circuit.SendSeq - circuit.AckedSeq) & 0x07) == 0)
                    {
                        return true;
                    }
                }

                if (Environment.TickCount64 >= deadline)
                {
                    return false;
                }

                if (++spins < 2000)
                {
                    Thread.Yield();
                }
                else
                {
                    Thread.Sleep(1);
                }
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
            X25Layer x25 = LayerOf(vc);

            if (vc == null)
            {
                throw new ArgumentNullException(nameof(vc));
            }

            lock (_lock)
            {
                x25.ClearCall(vc.LegLci, cause, diagnostic);
                vc.Leg!.Vcs.Remove(vc.LegLci);
                ReleaseExtraLcn(vc);
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
        /// Releases the client: lets <see cref="Run"/> return, stops the tick timer, and closes every
        /// connection with its framing codec and packet layer. Calling it again does nothing.
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

            Leg[] legs;
            lock (_lock)
            {
                legs = _legs.ToArray();
                _legs.Clear();
                _spare = null;
                _spareRedialTimer?.Dispose();
                _spareRedialTimer = null;
                _spareRedialPending = false;
            }

            for (int i = 0; i < legs.Length; i++)
            {
                CloseLeg(legs[i]);
            }
        }

        // ---- Connections (legs): one transport + codec + packet layer each ----

        /// <summary>
        /// Builds a connection (not yet opened): transport, XOT framing and packet layer, with the seam wired.
        /// </summary>
        private Leg CreateLeg(bool isPrimary, ushort circuits)
        {
            string name = string.IsNullOrWhiteSpace(ServiceName) ? "x25client" : ServiceName;
            if (!isPrimary)
            {
                name += "#" + Interlocked.Increment(ref _legCounter);
            }

            // Transport: connect out to the switch (never listen for the console tools).
            TcpLinkTransport transport = new TcpLinkTransport(_logger, name, isServer: false, _config.Host, _config.Port);

            // Framing: XOT length-prefixed frames over the TCP byte stream.
            XotFrameCodec codec = new XotFrameCodec(name, new FrameSink(transport));

            Leg leg = new Leg(isPrimary, transport, codec);

            // Packet layer: the state machine, sending down through the codec.
            X25Config x25config = new X25Config
            {
                LocalAddress = _config.LocalAddress,
                IsDte = _config.IsDte,
                LciBase = 1,
                LciCount = circuits,
                DefaultPacketSize = _config.PacketSize,
                DefaultWindowSize = _config.WindowSize == 0 ? (byte)2 : _config.WindowSize
            };
            leg.Layer = new X25Layer(new PacketSink(codec, leg), x25config);

            // Wire the seam: transport bytes -> codec -> whole packets -> layer; layer up-events -> callbacks.
            transport.DataReceived += (linkId, bytes) => codec.ProcessBytes(bytes);
            transport.Closed += (linkId, reason) => OnLegClosed(leg, reason);
            codec.FrameReceived += (linkId, frame) => OnFrame(leg, frame);
            leg.Layer.IncomingCall += (layer, lci, called, calling, userData) => OnLayerIncomingCall(leg, lci, called, calling, userData);
            leg.Layer.CallConnected += (layer, lci) => OnLayerCallConnected(leg, lci);
            leg.Layer.DataReceived += (layer, lci, data, length, q, m, d) => OnLayerData(leg, lci, data, length, q, m, d);
            leg.Layer.Cleared += (layer, lci, cause, diagnostic) => OnLayerCleared(leg, lci, cause, diagnostic);
            return leg;
        }

        /// <summary>
        /// Closes a connection's transport. Safe to call more than once.
        /// </summary>
        private static void CloseLeg(Leg leg)
        {
            leg.Closing = true;
            try
            {
                leg.Transport.Close();
                leg.Transport.Dispose();
            }
            catch (Exception)
            {
                // Already closed or closing: nothing more to release.
            }
        }

        /// <summary>
        /// A channel number for a circuit on a connection opened for one call. Called with the lock held.
        /// </summary>
        private ushort AllocateExtraLcn()
        {
            for (int tries = 0; tries < 65536; tries++)
            {
                ushort candidate = _nextExtraLcn;
                _nextExtraLcn = _nextExtraLcn == ushort.MaxValue ? FirstExtraLcn : (ushort)(_nextExtraLcn + 1);
                if (_extraLcns.Add(candidate))
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException("X25Client has no free circuit number.");
        }

        // ---- Seam wiring: transport -> codec -> layer (all on the transport read thread) ----

        private void OnFrame(Leg leg, ReadOnlySpan<byte> frame)
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                leg.Layer.ProcessPacket(leg.TranslateInbound(frame));
            }
        }

        /// <summary>
        /// The TCP connection of a leg dropped (the peer closed it or refused to keep it). Every circuit on it is gone:
        /// each is reported cleared with cause 9, out of order, so a caller waiting for an answer is not left hanging.
        /// </summary>
        private void OnLegClosed(Leg leg, string reason)
        {
            if (leg.Closing)
            {
                return;
            }

            lock (_lock)
            {
                if (_disposed || leg.Dead)
                {
                    return;
                }

                leg.Dead = true;
                _logger.LogDebug("X25Client: a connection dropped ({Reason}) with {Count} circuit(s) on it", reason, leg.Vcs.Count);
                ushort[] numbers = new ushort[leg.Vcs.Count];
                leg.Vcs.Keys.CopyTo(numbers, 0);
                for (int i = 0; i < numbers.Length; i++)
                {
                    OnLayerCleared(leg, numbers[i], X25Cause.OutOfOrder, 0);
                }

                // A dropped connection is never used again, the spare included. A spare the switch drops is dialled
                // again after a wait (XotSpareRedial): the client is a DTE behind the switch, and over XOT it learns the
                // switch is back only by reaching it (X.25 4.6.5 would send a Restart Indication, which RFC 1613 6.4
                // forbids over XOT). Until 2026-11-18 it was not dialled again, and x25echo answered no call after a
                // switch restart until it was started again.
                if (leg.Vcs.Count == 0)
                {
                    _legs.Remove(leg);
                    bool wasSpare = _spare == leg;
                    if (wasSpare)
                    {
                        _spare = null;
                        ScheduleSpareRedial("the switch closed the spare connection");
                    }

                    ThreadPool.QueueUserWorkItem(_ => CloseLeg(leg));
                }
            }
        }

        private void OnTick(object? state)
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                long now = Environment.TickCount64;
                for (int i = 0; i < _legs.Count; i++)
                {
                    _legs[i].Layer.Tick(now);
                }
            }
        }

        // ---- Layer up-events mapped to application callbacks ----

        private void OnLayerIncomingCall(Leg leg, ushort lci, string calledAddress, string callingAddress, byte[] callUserData)
        {
            // Track the circuit, let the application accept it (which sets AcceptRequested), then either
            // accept or reject on the layer. This runs under the frame lock, so layer calls are safe.
            X25Vc vc = new X25Vc(leg.IsPrimary ? lci : AllocateExtraLcn(), calledAddress, callingAddress, incoming: true)
            {
                CallUserData = callUserData ?? Array.Empty<byte>(),
            };
            vc.Leg = leg;
            vc.LegLci = lci;
            leg.Vcs[lci] = vc;

            // The call has taken the spare. The connection now belongs to this call, and the next arriving call
            // needs a fresh one: open a new spare (on a pool thread - this runs on the connection's read thread
            // with the lock held, and a dial blocks).
            leg.Used = true;
            if (_spare == leg)
            {
                _spare = null;
                ThreadPool.QueueUserWorkItem(_ => OpenSpare());
            }

            _onIncoming?.Invoke(this, vc);

            if (vc.AcceptRequested)
            {
                vc.Connected = true;
                leg.Layer.AcceptCall(lci);
            }
            else
            {
                leg.Vcs.Remove(lci);
                ReleaseExtraLcn(vc);
                leg.Layer.ClearCall(lci, 0, 0);
            }
        }

        private void OnLayerCallConnected(Leg leg, ushort lci)
        {
            if (!leg.Vcs.TryGetValue(lci, out X25Vc? vc))
            {
                return;
            }

            vc.Connected = true;
            if (_connectWaiters.TryGetValue(vc.Lcn, out ManualResetEventSlim? waiter))
            {
                waiter.Set();
            }

            _onConnected?.Invoke(this, vc);
        }

        private void OnLayerData(Leg leg, ushort lci, byte[] data, int length, bool qBit, bool mBit, bool dBit)
        {
            if (!leg.Vcs.TryGetValue(lci, out X25Vc? vc))
            {
                return;
            }

            _onData?.Invoke(this, vc, data, length, qBit, mBit, dBit);
        }

        private void OnLayerCleared(Leg leg, ushort lci, byte cause, byte diagnostic)
        {
            // A connection ends with its call (RFC 1613 section 5: one connection for each circuit), the one Open
            // made included; it is never used for another call. The close runs on a pool thread: this handler is on
            // the connection's own read thread.
            X25Vc? vc;
            if (leg.Vcs.TryGetValue(lci, out vc))
            {
                leg.Vcs.Remove(lci);
            }

            if (leg.Used && leg.Vcs.Count == 0)
            {
                _legs.Remove(leg);
                if (_spare == leg)
                {
                    _spare = null;
                }

                ThreadPool.QueueUserWorkItem(_ => CloseLeg(leg));
            }

            if (vc == null)
            {
                return;
            }

            ReleaseExtraLcn(vc);
            leg.AwaitingAnswer.Remove(lci);
            leg.ForgetChannel(lci);

            vc.Connected = false;

            // Wake any CallWait blocked on this channel so it returns promptly on a rejecting clear.
            if (_connectWaiters.TryGetValue(vc.Lcn, out ManualResetEventSlim? waiter))
            {
                waiter.Set();
            }

            _onClear?.Invoke(this, vc, cause, diagnostic);
        }

        private void ReleaseExtraLcn(X25Vc vc)
        {
            if (vc.Leg != null && !vc.Leg.IsPrimary)
            {
                _extraLcns.Remove(vc.Lcn);
            }
        }

        private void RequireOpen()
        {
            if (!_opened || _disposed)
            {
                throw new InvalidOperationException("X25Client is not open.");
            }
        }

        /// <summary>
        /// The number of channels the packet layer of a spare offers: a spare takes the call the switch puts on it,
        /// on whatever channel number the switch chose, so its layer needs the full range.
        /// </summary>
        private ushort SpareCircuits => _config.NumVcs == 0 ? (ushort)1 : _config.NumVcs;

        /// <summary>
        /// Test seam: called on the dialling thread after a new spare's connection is up and before
        /// <see cref="OpenSpare"/> records it. A test holds it there while a call arrives on the new connection, the
        /// order that left the client with no spare until 2026-11-18. Null outside tests.
        /// </summary>
        internal Action? SpareConnectedForTest { get; set; }

        /// <summary>
        /// Dials a new spare connection for the next arriving call. Runs on a pool thread. A failed dial is dialled again
        /// after the next wait of <see cref="XotSpareRedial"/>, for as long as the client is open.
        /// </summary>
        private void OpenSpare()
        {
            lock (_lock)
            {
                if (_disposed || _spare != null)
                {
                    return;
                }
            }

            Leg spare = CreateLeg(isPrimary: false, SpareCircuits);
            bool connected;
            try
            {
                connected = spare.Transport.Open();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "X25Client: could not open a spare connection to {Host}:{Port}", _config.Host, _config.Port);
                connected = false;
            }

            if (!connected)
            {
                CloseLeg(spare);
                ScheduleSpareRedial("could not open a spare connection");
                return;
            }

            lock (_lock)
            {
                _spareRedial?.Reset();
            }

            SpareConnectedForTest?.Invoke();

            // The connection is up and its read thread runs from the moment Open returns, so the switch can put a call on
            // it BEFORE this lock is taken. That call ran OnLayerIncomingCall while the connection was not yet the spare:
            // it marked the leg used but opened no new spare, because the leg was not _spare. Until 2026-11-18 this then
            // made the used connection the spare, and no further spare was ever opened: the next call that arrived found
            // no fresh connection and was never answered (SwitchPortFullTests.MoreCallsThanChannels failed now and then
            // with two calls connected of three; the switch saw two connections, never a third). Now a connection that a
            // call has already taken stays that call's connection, and another spare is opened for the next call.
            bool keep;
            bool takenByACall;
            lock (_lock)
            {
                takenByACall = spare.Used && !_disposed;

                // A call that took it and has already ended was closed by OnLayerCleared; only a call still on it keeps it.
                keep = !_disposed && !spare.Dead && (spare.Used ? spare.Vcs.Count > 0 : _spare == null);
                if (keep)
                {
                    _legs.Add(spare);
                    if (!spare.Used)
                    {
                        _spare = spare;
                    }
                }
            }

            if (!keep && !spare.Used)
            {
                CloseLeg(spare);
            }

            if (takenByACall)
            {
                ThreadPool.QueueUserWorkItem(_ => OpenSpare());
            }
        }

        /// <summary>
        /// Dials the spare again after the next wait of <see cref="XotSpareRedial"/>, unless the client is closed, a spare
        /// is already there or a redial is already waiting. Safe to call with <c>_lock</c> held.
        /// </summary>
        /// <param name="why">Words for the log.</param>
        private void ScheduleSpareRedial(string why)
        {
            int wait;
            lock (_lock)
            {
                if (_disposed || !_opened || _spare != null || _spareRedialPending || _spareRedial == null)
                {
                    return;
                }

                _spareRedialPending = true;
                wait = _spareRedial.Next();
                _spareRedialTimer ??= new Timer(_ => OnSpareRedialDue(), null, Timeout.Infinite, Timeout.Infinite);
                _spareRedialTimer.Change(wait, Timeout.Infinite);
            }

            _logger.LogWarning("X25Client: {Why} ({Host}:{Port}); dialling it again in {Wait} ms", why, _config.Host, _config.Port, wait);
        }

        private void OnSpareRedialDue()
        {
            lock (_lock)
            {
                if (!_spareRedialPending || _disposed)
                {
                    return;
                }

                _spareRedialPending = false;
            }

            OpenSpare();
        }

        /// <summary>
        /// The packet layer of the connection that carries a circuit.
        /// </summary>
        private X25Layer LayerOf(X25Vc vc)
        {
            if (vc == null)
            {
                throw new ArgumentNullException(nameof(vc));
            }

            RequireOpen();
            return vc.Leg?.Layer ?? throw new InvalidOperationException("The circuit does not belong to this client.");
        }

        /// <summary>
        /// Adapts the framing codec to the packet layer's down interface: whole packets built by the layer
        /// are handed to the codec for XOT framing and transmission.
        /// </summary>
        private sealed class PacketSink : IX25PacketSink
        {
            private readonly XotFrameCodec _codec;
            private readonly Leg _leg;

            /// <summary>
            /// Creates the sink over a framing codec.
            /// </summary>
            /// <param name="codec">
            /// The codec that frames and sends packets.
            /// </param>
            /// <param name="leg">
            /// The connection whose channel-number translation is applied to every packet sent.
            /// </param>
            internal PacketSink(XotFrameCodec codec, Leg leg)
            {
                _codec = codec;
                _leg = leg;
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
                    _codec.SendFrame(_leg.TranslateOutbound(packet));
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
