//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.PacketLayer;

namespace RetroIO.Protocols.X25.Transport
{
    /// <summary>
    /// One call this node places on its own XOT connection (RFC 1613 section 5: "A separate TCP connection MUST be
    /// used for each X.25 virtual circuit"), with an X.25 packet layer of its own on that connection acting as the DTE.
    /// <see cref="Place"/> sends the Call Request and waits for the answer, so the caller learns what really happened:
    /// accepted, cleared with the cause and diagnostic the peer gave, or no answer within T21.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The packet layer (<see cref="X25Layer"/>) builds the Call Request (X.25 5.2.1, addresses in BCD), runs time-limit
    /// T21 and, when it expires, sends the Clear Request X.25 Annex D Table D.2 names for it ("To transmit a clear
    /// request", T21 = 200 s, started when the DTE issues a call request, state p2, normally terminated when the DTE
    /// leaves p2 "e.g. the call connected or clear indication is received"). The layer's timers are driven by a one
    /// second clock here. The time-limit used is <see cref="X25Config.CallRequestTimeoutMs"/>; X.25 Annex D note 1 lets
    /// a DTE use a shorter one.
    /// </para>
    /// <para>
    /// Made 2026-11-18 for <see cref="X25Router"/>. Until then the router hand-built a Call Request with the
    /// addresses in ASCII instead of BCD, sent it, and reported the call as placed without waiting for any answer
    /// ("TODO: Wait for call accepted/cleared response. For now, assume success").
    /// </para>
    /// </remarks>
    public sealed class XotOutgoingCall : IDisposable
    {
        private const int ClockPeriodMs = 1000;

        private readonly ILogger _logger;
        private readonly XotClientTransport _transport;
        private readonly XotClientCircuit _circuit;
        private readonly X25Layer _layer;
        private readonly object _sync = new object();
        private readonly ManualResetEventSlim _answered = new ManualResetEventSlim(false);
        private readonly Timer _clock;

        // Data and end-of-call events found while the layer runs under _sync are raised after _sync is released, so a
        // handler that sends on this call or takes a lock of its own (the runtime's routing lock) cannot deadlock against
        // the connection's read thread.
        private readonly List<Action> _pending = new List<Action>();

        private ushort _lci;
        private X25CallOutcome? _outcome;
        private bool _connected;
        private bool _ended;
        private bool _disposed;

        /// <summary>
        /// Creates the call on a connection opened for it. Nothing is sent until <see cref="Place"/>.
        /// </summary>
        /// <param name="logger">
        /// Diagnostics logger.
        /// </param>
        /// <param name="transport">
        /// The XOT client transport that opened the connection and sends on it.
        /// </param>
        /// <param name="circuit">
        /// The connection, from <see cref="XotClientTransport.ConnectToXotGateway"/>, used by this call only.
        /// </param>
        /// <param name="config">
        /// The packet layer settings. <see cref="X25Config.IsDte"/> is forced to true (this end places the call), and the
        /// connection carries one circuit, so one channel is enough.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when an argument is null.
        /// </exception>
        public XotOutgoingCall(ILogger logger, XotClientTransport transport, XotClientCircuit circuit, X25Config config)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _circuit = circuit ?? throw new ArgumentNullException(nameof(circuit));
            ArgumentNullException.ThrowIfNull(config);

            X25Config own = new X25Config
            {
                LocalAddress = config.LocalAddress,
                IsDte = true,
                LciBase = config.LciBase,
                LciCount = 1,
                DefaultPacketSize = config.DefaultPacketSize,
                DefaultWindowSize = config.DefaultWindowSize,
                CallRequestTimeoutMs = config.CallRequestTimeoutMs,
                IncomingCallTimeoutMs = config.IncomingCallTimeoutMs,
                ResetRequestTimeoutMs = config.ResetRequestTimeoutMs,
                ClearRequestTimeoutMs = config.ClearRequestTimeoutMs,
                RestartTimeoutMs = config.RestartTimeoutMs,
                MaxRetransmissions = config.MaxRetransmissions,
                Modulo = config.Modulo,
            };

            _layer = new X25Layer(new CircuitSink(transport, circuit), own);
            _layer.CallConnected += OnConnected;
            _layer.Cleared += OnCleared;
            _layer.DataReceived += OnData;

            _transport.DataReceived += OnTransportData;
            _circuit.ConnectionClosed += OnConnectionClosed;
            _clock = new Timer(OnClock, null, ClockPeriodMs, ClockPeriodMs);
        }

        /// <summary>
        /// Raised for every data packet that arrives once the call is up: the channel, the data, its length and the
        /// Q and M bits. Raised on the connection's read thread.
        /// </summary>
        public event Action<XotOutgoingCall, byte[], int, bool, bool>? DataReceived;

        /// <summary>
        /// Raised once when an accepted call ends (cleared by either end, or the connection closed): the cause and
        /// the diagnostic.
        /// </summary>
        public event Action<XotOutgoingCall, byte, byte>? CallEnded;

        /// <summary>
        /// The packet layer that carries the call.
        /// </summary>
        /// <remarks>
        /// Added 2026-11-21 for the runtime's call join, which drives the call the way it drives a call on one of its own
        /// lines: it subscribes to the layer's events before <see cref="Place"/> (so nothing that arrives right after
        /// the Call Connected is missed) and calls the layer's methods. Every call on the layer must hold
        /// <see cref="SyncRoot"/>, as this class does; the layer raises its events while that lock is held, on the
        /// connection's read thread or the clock's thread.
        /// </remarks>
        public X25Layer Layer => _layer;

        /// <summary>
        /// The lock every use of <see cref="Layer"/> holds.
        /// </summary>
        public object SyncRoot => _sync;

        /// <summary>
        /// The connection the call is on.
        /// </summary>
        public XotClientCircuit Circuit => _circuit;

        /// <summary>
        /// The logical channel the call was placed on (0 before <see cref="Place"/>).
        /// </summary>
        public ushort Lci => _lci;

        /// <summary>
        /// Sends the Call Request and waits for the answer: Call Connected, Clear Indication, the connection
        /// closing, or T21 running out (then this end sends the Clear Request, X.25 Table D.2).
        /// </summary>
        /// <param name="request">
        /// The call.
        /// </param>
        /// <returns>
        /// The outcome. For <see cref="X25CallOutcomeKind.Accepted"/>, <see cref="X25CallOutcome.Call"/> is this object,
        /// which then carries the call; for every other outcome the connection is closed.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="request"/> is null.
        /// </exception>
        public X25CallOutcome Place(X25CallRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            lock (_sync)
            {
                byte[]? cud = request.CallUserData.Length == 0 ? null : request.CallUserData;
                _lci = request.Facilities.Length == 0
                    ? _layer.PlaceCall(request.CalledAddress, request.CallingAddress, cud)
                    : _layer.PlaceCall(request.CalledAddress, request.CallingAddress, cud, request.Facilities);
                if (_lci == 0)
                {
                    _outcome = X25CallOutcome.NotPlaced("the packet layer did not send the Call Request (no free channel, or the connection did not take it)");
                    _answered.Set();
                }
            }

            // T21 bounds the wait: the layer clears the call when it expires and that ends this wait through OnCleared.
            // The connection closing ends it through OnConnectionClosed. Nothing else is waited for.
            _answered.Wait();

            X25CallOutcome outcome;
            lock (_sync)
            {
                outcome = _outcome!;
            }

            if (outcome.Kind != X25CallOutcomeKind.Accepted)
            {
                Dispose();
            }

            return outcome;
        }

        /// <summary>
        /// Sends data on the accepted call.
        /// </summary>
        /// <param name="data">
        /// The data.
        /// </param>
        /// <param name="qBit">
        /// The Q bit.
        /// </param>
        /// <param name="mBit">
        /// The M bit.
        /// </param>
        /// <returns>
        /// False when the call is not up, or the window is shut.
        /// </returns>
        public bool Send(byte[] data, bool qBit = false, bool mBit = false)
        {
            lock (_sync)
            {
                return _connected && !_ended && _layer.SendData(_lci, data, qBit, mBit);
            }
        }

        /// <summary>
        /// Clears the accepted call from this end (the connection is closed once the Clear Confirmation crosses it).
        /// </summary>
        /// <param name="cause">
        /// The clearing cause.
        /// </param>
        /// <param name="diagnostic">
        /// The diagnostic.
        /// </param>
        /// <returns>
        /// False when the call is not up.
        /// </returns>
        public bool Clear(byte cause, byte diagnostic)
        {
            lock (_sync)
            {
                return _connected && !_ended && _layer.ClearCall(_lci, cause, diagnostic);
            }
        }

        /// <summary>
        /// Resets the call (X.25 (10/96) 4.4.3): a Reset Request goes out and the call stays.
        /// </summary>
        /// <param name="cause">Resetting cause.</param>
        /// <param name="diagnostic">Diagnostic code.</param>
        /// <returns>False when the call is not up or the packet layer would not send it.</returns>
        public bool Reset(byte cause, byte diagnostic)
        {
            lock (_sync)
            {
                return _connected && !_ended && _layer.ResetCall(_lci, cause, diagnostic);
            }
        }

        /// <summary>
        /// Stops the clock and closes the connection.
        /// </summary>
        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            _clock.Dispose();
            _transport.DataReceived -= OnTransportData;
            _circuit.ConnectionClosed -= OnConnectionClosed;
            _transport.CloseCircuit(_circuit);
        }

        private void OnTransportData(object? sender, TransportDataEventArgs e)
        {
            if (!ReferenceEquals(e.Connection, _circuit))
            {
                return;
            }

            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _layer.ProcessPacket(new ReadOnlySpan<byte>(e.Data, 0, e.Length));
            }

            RaisePending();
        }

        private void RaisePending()
        {
            Action[] due;
            lock (_sync)
            {
                if (_pending.Count == 0)
                {
                    return;
                }

                due = _pending.ToArray();
                _pending.Clear();
            }

            for (int i = 0; i < due.Length; i++)
            {
                due[i]();
            }
        }

        private void OnClock(object? state)
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _layer.Tick(Environment.TickCount64);
            }

            RaisePending();
        }

        // Runs with _sync held (inside ProcessPacket).
        private void OnConnected(X25Layer layer, ushort lci)
        {
            if (lci != _lci || _outcome != null)
            {
                return;
            }

            _connected = true;
            _outcome = X25CallOutcome.Accepted(this);
            _logger.LogInformation("XOT call on {Address}:{Port} channel {Lci}: accepted", _circuit.RemoteAddress, _circuit.RemotePort, lci);
            _answered.Set();
        }

        // Runs with _sync held (inside ProcessPacket or Tick).
        private void OnCleared(X25Layer layer, ushort lci, byte cause, byte diagnostic)
        {
            if (lci != _lci)
            {
                return;
            }

            if (_outcome == null)
            {
                // Before the answer. The layer counts a T21 expiry in CallTimeouts and then reports the clear it sent.
                _outcome = layer.CallTimeouts > 0
                    ? X25CallOutcome.TimedOut(cause, diagnostic)
                    : X25CallOutcome.Cleared(cause, diagnostic, "cleared by the network or the called end before it was accepted");
                _logger.LogWarning("XOT call on {Address}:{Port} channel {Lci}: {Outcome}", _circuit.RemoteAddress, _circuit.RemotePort, lci, _outcome);
                _answered.Set();
                return;
            }

            EndOnce(cause, diagnostic);
        }

        // Runs with _sync held (inside ProcessPacket).
        private void OnData(X25Layer layer, ushort lci, byte[] data, int length, bool qBit, bool mBit, bool dBit)
        {
            if (lci == _lci)
            {
                byte[] copy = new byte[length];
                Buffer.BlockCopy(data, 0, copy, 0, length);
                _pending.Add(() => DataReceived?.Invoke(this, copy, length, qBit, mBit));
            }
        }

        private void OnConnectionClosed(object? sender, string remote)
        {
            lock (_sync)
            {
                if (_outcome == null)
                {
                    // RFC 1613 section 5: the connection carries the circuit; with it gone the call cannot go on.
                    _outcome = X25CallOutcome.Cleared(X25Cause.OutOfOrder, X25Diagnostic.None, "the XOT connection closed before the call was answered");
                    _logger.LogWarning("XOT call on {Address}:{Port}: {Outcome}", _circuit.RemoteAddress, _circuit.RemotePort, _outcome);
                    _answered.Set();
                    return;
                }

                if (_connected)
                {
                    EndOnce(X25Cause.OutOfOrder, X25Diagnostic.None);
                }
            }

            RaisePending();
        }

        private void EndOnce(byte cause, byte diagnostic)
        {
            if (_ended || !_connected)
            {
                return;
            }

            _ended = true;
            _pending.Add(() => CallEnded?.Invoke(this, cause, diagnostic));
        }

        /// <summary>
        /// Hands the packets the layer builds to the call's own connection.
        /// </summary>
        private sealed class CircuitSink : IX25PacketSink
        {
            private readonly XotClientTransport _transport;
            private readonly XotClientCircuit _circuit;

            internal CircuitSink(XotClientTransport transport, XotClientCircuit circuit)
            {
                _transport = transport;
                _circuit = circuit;
            }

            public X25SendResult SendPacket(ReadOnlySpan<byte> packet)
            {
                return _transport.SendX25Packet(_circuit, packet.ToArray()) ? X25SendResult.Sent : X25SendResult.LinkDown;
            }
        }
    }
}
