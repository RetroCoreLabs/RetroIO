//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using RetroIO.Protocols.X25.PacketLayer;

namespace RetroIO.Protocols.X25.Runtime
{
    /// <summary>
    /// One X.25 packet layer over one connection: the engine, the sink that writes to the
    /// connection, the lock that keeps the single-threaded engine single-threaded, and the wait
    /// that lets a blocking caller sit out a closed window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The desktop runtime used to run its own flow control for XOT (<c>XotFlowControl</c>: a
    /// second P(S)/P(R) keeper beside the old engine's, on the same circuit) and decode packet types
    /// by hand. Since 2026-09-06 (one-stack plan, Phase C1b) each XOT connection and each link
    /// owns one of these instead, and the runtime only routes the engine's events to its modules
    /// and services by local channel.
    /// </para>
    /// <para>
    /// Threading. <see cref="X25Layer"/> is not thread-safe by design. Every call into it -
    /// packets from the transport's reader thread, sends from application threads, ticks from the
    /// timer - goes through <see cref="Sync"/>. Event handlers run inside that lock, so a handler
    /// must not block and must not call back into another endpoint while holding it.
    /// </para>
    /// </remarks>
    public sealed class X25Endpoint : IDisposable
    {
        private readonly Func<byte[], bool> _send;
        private readonly ILogger _logger;
        /// <summary>
        /// What a blocked sender waits on, and what the engine pulses when a window opens, the link
        /// goes down or the endpoint is disposed.
        /// </summary>
        /// <remarks>
        /// A monitor rather than a wait handle, because there is nothing to dispose: a sender can be
        /// inside its wait while the runtime is torn down on another thread, and a handle disposed
        /// under it throws. Lock order is always <c>Sync</c> first, then this - never the reverse.
        /// </remarks>
        private readonly object _windowGate = new object();

        /// <summary>
        /// Set while a window is open; cleared before each attempt to send.
        /// </summary>
        private bool _windowIsOpen;
        private volatile bool _linkUp = true;

        /// <summary>
        /// Creates an endpoint over a connection.
        /// </summary>
        /// <param name="name">
        /// A name for logs (the transport's name).
        /// </param>
        /// <param name="send">
        /// Writes one complete X.25 packet to the connection (the XOT header is the connection's
        /// business) and returns whether it was accepted.
        /// </param>
        /// <param name="config">
        /// The engine's configuration for this connection.
        /// </param>
        /// <param name="logger">
        /// Logger.
        /// </param>
        public X25Endpoint(string name, Func<byte[], bool> send, PacketLayer.X25Config config, ILogger logger)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            _send = send ?? throw new ArgumentNullException(nameof(send));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            Layer = new X25Layer(new Sink(this), config);
            Layer.WindowOpened += OnWindowOpened;
        }

        /// <summary>
        /// The connection's name, for logs.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// The engine. Touch it only inside <see cref="Sync"/>.
        /// </summary>
        public X25Layer Layer { get; }

        /// <summary>
        /// The lock every call into <see cref="Layer"/> must hold.
        /// </summary>
        public object Sync { get; } = new object();

        /// <summary>
        /// True once the runtime has issued the Restart Request that brings a dialled link to a known
        /// state (X.25 4.1). Set under <see cref="Sync"/>.
        /// </summary>
        public bool RestartSent { get; set; }

        /// <summary>
        /// True once <see cref="Dispose"/> has run. Read and written under <see cref="Sync"/>.
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// False once the connection has gone away; sends then fail with <see cref="X25SendResult.LinkDown"/>
        /// without touching the transport.
        /// </summary>
        public bool LinkUp
        {
            get { return _linkUp; }
            set { _linkUp = value; }
        }

        /// <summary>
        /// Feeds one packet from the connection to the engine.
        /// </summary>
        public void Receive(byte[] packet)
        {
            lock (Sync)
            {
                Layer.ProcessPacket(packet);
            }
        }

        /// <summary>
        /// Advances the engine's timers.
        /// </summary>
        public void Tick(long nowMs)
        {
            lock (Sync)
            {
                Layer.Tick(nowMs);
            }
        }

        /// <summary>
        /// Sends a data packet, waiting up to <paramref name="timeoutMs"/> for the window to open
        /// or the peer's busy condition to clear when the engine refuses for those reasons.
        /// </summary>
        /// <returns>
        /// True when the packet left. False when the circuit is not in data transfer, the data is
        /// longer than the negotiated packet size, the link is down, or the wait timed out - a busy
        /// peer is a pause, not an error, and the caller decides what a timeout means.
        /// </returns>
        /// <remarks>
        /// This keeps the behaviour the PAD's <c>SendText</c> had with the old flow control: it
        /// blocked for up to two seconds on a closed window and then reported failure. The wait is
        /// on the engine's <see cref="X25LayerBase.WindowOpened"/>, outside the lock, so packets
        /// from the transport keep flowing while a sender waits.
        /// </remarks>
        public bool SendBlocking(ushort lci, byte[] data, int timeoutMs, bool qBit = false, bool mBit = false, bool dBit = false)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (true)
            {
                bool refusedForFlowControl;
                lock (Sync)
                {
                    // A send can be in flight on another thread while the runtime is being torn
                    // down: the old engine's bridge queues one on the thread pool. Checked inside
                    // the lock that Dispose also takes, so the wait handle cannot go away between
                    // this test and the Reset below.
                    if (_disposed)
                    {
                        return false;
                    }

                    lock (_windowGate)
                    {
                        _windowIsOpen = false;
                    }

                    if (Layer.SendData(lci, data, qBit, mBit, dBit))
                    {
                        return true;
                    }

                    refusedForFlowControl = WouldFitLater(lci, data.Length);
                }

                if (!refusedForFlowControl || !_linkUp)
                {
                    return false;
                }

                long remaining = deadline - Environment.TickCount64;
                if (remaining <= 0 || !WaitForWindow((int)remaining))
                {
                    _logger.LogWarning("X25Endpoint[{Name}]: send window timeout on LCI {Lci}", Name, lci);
                    return false;
                }
            }
        }

        /// <summary>
        /// True when a refused send was refused only for flow control - the circuit is in data
        /// transfer, the data fits the packet size, and the window is shut or the peer is busy - so
        /// waiting can help.
        /// </summary>
        private bool WouldFitLater(ushort lci, int length)
        {
            if (!Layer.Circuits.TryGetValue(lci, out X25Vc? vc))
            {
                return false;
            }

            if (vc.State != X25VcState.DataTransfer && vc.State != X25VcState.PvcReady)
            {
                return false;
            }

            if (length > vc.PacketSizeOut)
            {
                return false;
            }

            return vc.PeerBusy || vc.PendingSegments.Count > 0 || ((vc.SendSeq - vc.AckedSeq) & 0x07) >= vc.WindowSizeOut;
        }

        private void OnWindowOpened(X25Layer layer, ushort lci)
        {
            OpenWindowGate();
        }

        /// <summary>
        /// Wakes every sender waiting on the gate and leaves it open.
        /// </summary>
        private void OpenWindowGate()
        {
            lock (_windowGate)
            {
                _windowIsOpen = true;
                Monitor.PulseAll(_windowGate);
            }
        }

        /// <summary>
        /// Waits for the gate to open.
        /// </summary>
        /// <param name="timeoutMs">
        /// How long to wait.
        /// </param>
        /// <returns>
        /// True when the gate opened, false on timeout.
        /// </returns>
        private bool WaitForWindow(int timeoutMs)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            lock (_windowGate)
            {
                while (!_windowIsOpen)
                {
                    int remaining = (int)(deadline - Environment.TickCount64);
                    if (remaining <= 0 || !Monitor.Wait(_windowGate, remaining))
                    {
                        return _windowIsOpen;
                    }
                }

                return true;
            }
        }

        /// <summary>
        /// Called when the connection is gone: no more sends, and the engine's timers will clear
        /// what was open.
        /// </summary>
        public void MarkLinkDown()
        {
            _linkUp = false;
            OpenWindowGate();
        }

        /// <summary>
        /// Disposes the endpoint: marks it disposed and the link down, and wakes every sender that
        /// is waiting for a window so it gives up instead of waiting out its timeout.
        /// </summary>
        /// <remarks>
        /// A second call does nothing. The engine itself is not disposed or detached here.
        /// </remarks>
        public void Dispose()
        {
            lock (Sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            _linkUp = false;

            // Wake anyone already waiting: they see _disposed on the next pass round the loop and
            // give up rather than waiting out the timeout.
            OpenWindowGate();
        }

        private sealed class Sink : IX25PacketSink
        {
            private readonly X25Endpoint _owner;

            public Sink(X25Endpoint owner)
            {
                _owner = owner;
            }

            public X25SendResult SendPacket(ReadOnlySpan<byte> packet)
            {
                if (!_owner._linkUp)
                {
                    return X25SendResult.LinkDown;
                }

                try
                {
                    return _owner._send(packet.ToArray()) ? X25SendResult.Sent : X25SendResult.Refused;
                }
                catch (Exception ex)
                {
                    _owner._logger.LogWarning(ex, "X25Endpoint[{Name}]: transport send failed", _owner.Name);
                    _owner._linkUp = false;
                    return X25SendResult.LinkDown;
                }
            }
        }
    }
}
