//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using RetroIO.Link.Models;
using RetroIO.Protocols.Xmsg.Hdlc;
using RetroIO.Protocols.Xmsg.Live;

namespace RetroIO.Link.Lapb
{
    /// <summary>
    /// A LAPB station for a link: the event-driven surface the links here have always used, running
    /// on the shared engine in <c>RetroIO.Lapb</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the same shape the HDLC merge used: keep the surface the callers depend on, move the
    /// protocol underneath it. <c>LapbInstance</c>, which this replaces, was a second LAPB
    /// implementation - it retransmitted into a peer that had just sent RNR (X.25 2.4.5.7 forbids
    /// it), and it ran its T1 on a thread-pool timer with no lock, so a retransmission could rewrite
    /// V(S) while the receive thread was inside frame handling. Both are pinned by
    /// <c>LapbInstancePinTests</c>.
    /// </para>
    /// <para>
    /// The engine is single-threaded by design and this class is what makes that safe here: every
    /// entry point - frames from the transport's reader thread, sends from application threads, and
    /// the tick timer - goes through <see cref="_sync"/>. Events are raised inside that lock, so a
    /// handler must not block or call back into the same station.
    /// </para>
    /// <para>
    /// The engine speaks ITU-T X.25 clause 2 here (<see cref="LapbVariant.Itu"/>), because these
    /// links carry X.25. The ND dialect is what the XMSG side selects.
    /// </para>
    /// </remarks>
    public sealed class LapbStation : IDisposable
    {
        private const int TickIntervalMs = 100;

        /// <summary>
        /// N1 for a link that carries X.25: the largest information field this station accepts.
        /// </summary>
        /// <remarks>
        /// N1 is a link parameter, not a constant of the protocol (X.25 2.3.2), and the engine's own
        /// default is the ND network's 622 - far too small here. X.25 Table 6-2 allows a data field
        /// of up to 4096 octets, and a data packet's header is at most 4 octets (a general format
        /// identifier and channel number, then a one- or two-octet packet type identifier when
        /// extended sequence numbering is in use), so 4100 is the largest X.25 packet that can arrive.
        /// Anything longer is answered with FRMR, reason Y (2.3.4.10).
        /// </remarks>
        public const int X25InformationLength = 4100;

        private readonly ILogger _logger;
        private readonly string _linkName;
        private readonly LapbLayer _lapb;
        private readonly object _sync = new object();
        private readonly LapbStatistics _statistics = new LapbStatistics();
        private readonly Timer _ticker;

        private LapbState _state = LapbState.Disconnected;
        private bool _disposed;

        /// <summary>
        /// Creates a station over a link.
        /// </summary>
        /// <param name="logger">
        /// Logger.
        /// </param>
        /// <param name="linkName">
        /// The link's name, for logs.
        /// </param>
        /// <param name="windowSize">
        /// The send window, k.
        /// </param>
        /// <param name="maxRetries">
        /// N2, the retry limit.
        /// </param>
        /// <param name="t1TimeoutMs">
        /// T1, the retransmission timer.
        /// </param>
        /// <param name="t2TimeoutMs">
        /// Unused: the shared engine acknowledges as it receives rather than running a T2 delay.
        /// Kept so the four links did not have to change their constructor calls.
        /// </param>
        /// <param name="t3TimeoutMs">
        /// T3, the idle keepalive; zero disables it.
        /// </param>
        /// <param name="isPrimary">
        /// True when this station is the DTE, which decides the ITU address pair (X.25 2.4.2).
        /// </param>
        public LapbStation(
            ILogger logger,
            string linkName,
            byte windowSize = 7,
            byte maxRetries = 10,
            int t1TimeoutMs = 3000,
            int t2TimeoutMs = 1500,
            int t3TimeoutMs = 0,
            bool isPrimary = true)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _linkName = linkName ?? throw new ArgumentNullException(nameof(linkName));

            LapbOptions options = new LapbOptions(
                t1: t1TimeoutMs,
                t3: t3TimeoutMs > 0 ? t3TimeoutMs : int.MaxValue,
                n2: maxRetries,
                windowSize: windowSize,
                reestablishOnLinkFailure: true,
                variant: LapbVariant.Itu,
                isDte: isPrimary,
                maxInformationLength: X25InformationLength);

            _lapb = new LapbLayer(0, options);
            _lapb.OnTransmit += OnEngineTransmit;
            _lapb.OnInformation += OnEngineInformation;
            _lapb.OnLinkFailure += OnEngineLinkFailure;

            _statistics.WindowSize = windowSize;
            _ticker = new Timer(OnTick, null, TickIntervalMs, TickIntervalMs);

            _logger.LogInformation("LAPB[{Link}]: station created - K={K}, N2={N2}, T1={T1}ms, T3={T3}ms, DTE={Dte}",
                linkName, windowSize, maxRetries, t1TimeoutMs, t3TimeoutMs, isPrimary);
        }

        /// <summary>
        /// The link's name.
        /// </summary>
        public string LinkName => _linkName;

        /// <summary>
        /// The station's state, in the link layer's own vocabulary.
        /// </summary>
        public LapbState State
        {
            get { lock (_sync) { return _state; } }
        }

        /// <summary>
        /// V(S), the send sequence variable.
        /// </summary>
        public byte VS
        {
            get { lock (_sync) { return (byte)_lapb.SendVariable; } }
        }

        /// <summary>
        /// V(R), the receive sequence variable.
        /// </summary>
        public byte VR
        {
            get { lock (_sync) { return (byte)_lapb.ReceiveVariable; } }
        }

        /// <summary>
        /// V(A), the last acknowledged sequence number.
        /// </summary>
        public byte VA
        {
            get { lock (_sync) { return (byte)_lapb.AcknowledgeVariable; } }
        }

        /// <summary>
        /// Raised for every frame the station puts on the wire.
        /// </summary>
        public event EventHandler<LapbFrameEventArgs>? FrameToTransmit;

        /// <summary>
        /// Raised for every frame handed to the station.
        /// </summary>
        public event EventHandler<LapbFrameEventArgs>? FrameReceived;

        /// <summary>
        /// Raised with the information field of each accepted I-frame.
        /// </summary>
        public event EventHandler<byte[]>? DataReceived;

        /// <summary>
        /// Raised when the station's state changes.
        /// </summary>
        public event EventHandler<LapbStateChangedEventArgs>? StateChanged;

        /// <summary>
        /// Raised when the link fails.
        /// </summary>
        public event EventHandler<string>? ErrorOccurred;

        /// <summary>
        /// Starts link set-up: sends SABM and waits for the UA.
        /// </summary>
        public void Connect()
        {
            lock (_sync)
            {
                _lapb.Connect(Now);
                SyncState();
            }
        }

        /// <summary>
        /// Starts link tear-down: sends DISC.
        /// </summary>
        public void Disconnect()
        {
            lock (_sync)
            {
                _lapb.Disconnect(Now);
                SyncState();
            }
        }

        /// <summary>
        /// Sends an information frame.
        /// </summary>
        /// <param name="data">
        /// The information field.
        /// </param>
        /// <param name="pollFinal">
        /// Ignored: the engine decides the P bit from the state it is in (X.25 2.4.5).
        /// </param>
        /// <returns>
        /// True when the frame was queued or sent; false when the station is not connected.
        /// </returns>
        public bool SendData(ReadOnlySpan<byte> data, bool pollFinal = false)
        {
            lock (_sync)
            {
                if (_state != LapbState.DataTransfer)
                {
                    _logger.LogWarning("LAPB[{Link}]: cannot send - not connected", _linkName);
                    return false;
                }

                _lapb.SendInformation(data, Now);
                SyncState();
                return true;
            }
        }

        /// <summary>
        /// Puts a frame on the wire as given, without the state machine seeing it. Used by the links
        /// to emit a frame they built themselves.
        /// </summary>
        public void SendFrame(byte[] frame, int length)
        {
            if (frame == null || length < 2)
            {
                _logger.LogWarning("LAPB[{Link}]: invalid frame - too short", _linkName);
                return;
            }

            byte[] copy = new byte[length];
            Array.Copy(frame, 0, copy, 0, length);
            RaiseTransmit(copy);
        }

        /// <summary>
        /// Hands a received frame to the station.
        /// </summary>
        public void ReceiveFrame(byte[] frameData, int length)
        {
            if (frameData == null || length < 2)
            {
                _logger.LogWarning("LAPB[{Link}]: invalid frame - too short", _linkName);
                return;
            }

            byte[] bytes = frameData.Length == length ? frameData : frameData.AsSpan(0, length).ToArray();
            LapbFrame frame;
            try
            {
                frame = new LapbFrame(bytes);
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning("LAPB[{Link}]: undecodable frame discarded: {Reason}", _linkName, ex.Message);
                return;
            }

            lock (_sync)
            {
                CountFrame(frame.Control, received: true);
                FrameReceived?.Invoke(this, Describe(bytes, length, FrameDirection.Receive));

                _lapb.OnFrameReceived(frame, Now);
                SyncState();
            }
        }

        /// <summary>
        /// The station's counters and sequence variables.
        /// </summary>
        public LapbStatistics GetStatistics()
        {
            lock (_sync)
            {
                _statistics.VS = (byte)_lapb.SendVariable;
                _statistics.VR = (byte)_lapb.ReceiveVariable;
                _statistics.VA = (byte)_lapb.AcknowledgeVariable;
                _statistics.UnackedFrameCount = _lapb.Outstanding;
                return _statistics;
            }
        }

        /// <summary>
        /// V(S), V(R) and V(A) together.
        /// </summary>
        public (byte VS, byte VR, byte VA) GetSequenceNumbers()
        {
            lock (_sync)
            {
                return ((byte)_lapb.SendVariable, (byte)_lapb.ReceiveVariable, (byte)_lapb.AcknowledgeVariable);
            }
        }

        /// <summary>
        /// Drops the link to disconnected without telling the peer, discarding anything queued. Used
        /// when the transport under the station has gone away and the peer cannot be reached.
        /// </summary>
        public void Reset()
        {
            lock (_sync)
            {
                _lapb.ForceDisconnected();
                SyncState();
            }

            _logger.LogInformation("LAPB[{Link}]: station reset", _linkName);
        }

        /// <summary>
        /// Stops the station's tick timer.
        /// </summary>
        /// <remarks>
        /// Calling it again does nothing. It does not disconnect the link or tell the peer.
        /// </remarks>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _ticker.Dispose();
        }

        private static long Now => Environment.TickCount64;

        private void OnTick(object? state)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                lock (_sync)
                {
                    _lapb.Tick(Now);
                    SyncState();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LAPB[{Link}]: tick failed", _linkName);
            }
        }

        private void OnEngineTransmit(byte[] body)
        {
            CountFrame(body.Length > 1 ? body[1] : (byte)0, received: false);
            RaiseTransmit(body);
        }

        private void RaiseTransmit(byte[] body)
        {
            FrameToTransmit?.Invoke(this, Describe(body, body.Length, FrameDirection.Transmit));
        }

        /// <summary>
        /// Describes a frame for the listeners that build the packet history and the desktop views.
        /// </summary>
        /// <remarks>
        /// The sequence numbers, the P/F bit and the information field all have to be here: the
        /// history relates an I-frame to the X.25 packet it carried by matching N(S), and a view
        /// that is handed only the address and control shows a link with no traffic on it.
        /// </remarks>
        private static LapbFrameEventArgs Describe(byte[] frame, int length, FrameDirection direction)
        {
            byte control = length > 1 ? frame[1] : (byte)0;
            LapbFrameType type = LapbFrameUtils.DetectFrameType(control);

            LapbFrameEventArgs args = new LapbFrameEventArgs
            {
                FrameData = frame,
                Length = (ushort)length,
                Direction = direction,
                Timestamp = DateTime.UtcNow,
                FrameType = type,
                Address = length > 0 ? frame[0] : (byte)0,
                Control = control,
                PF = (control & 0x10) != 0,
            };

            if (type == LapbFrameType.Information)
            {
                args.NS = LapbFrameUtils.ExtractNS(control);
                args.NR = LapbFrameUtils.ExtractNR(control);
            }
            else if (type == LapbFrameType.Supervisory)
            {
                args.NR = LapbFrameUtils.ExtractNR(control);
            }

            if (length > 2)
            {
                args.Information = LapbFrameUtils.ExtractInformation(frame, length);
            }

            return args;
        }

        private void OnEngineInformation(ReadOnlyMemory<byte> info)
        {
            DataReceived?.Invoke(this, info.ToArray());
        }

        private void OnEngineLinkFailure(string reason)
        {
            _logger.LogError("LAPB[{Link}]: link failure - {Reason}", _linkName, reason);
            ErrorOccurred?.Invoke(this, reason);
        }

        /// <summary>
        /// Maps the engine's state onto the link layer's three-state view and raises
        /// <see cref="StateChanged"/> when it moves. Called after every entry into the engine,
        /// inside the lock.
        /// </summary>
        private void SyncState()
        {
            LapbState mapped = _lapb.State switch
            {
                LapbLayerState.Connected => LapbState.DataTransfer,
                LapbLayerState.SabmSent => LapbState.Connecting,
                LapbLayerState.DiscSent => LapbState.Disconnecting,
                _ => LapbState.Disconnected,
            };

            if (mapped == _state)
            {
                return;
            }

            LapbState previous = _state;
            _state = mapped;
            _logger.LogInformation("LAPB[{Link}]: {Old} -> {New}", _linkName, previous, mapped);
            StateChanged?.Invoke(this, new LapbStateChangedEventArgs
            {
                OldState = previous,
                NewState = mapped,
            });
        }

        private void CountFrame(byte control, bool received)
        {
            LapbFrameType type = LapbFrameUtils.DetectFrameType(control);
            switch (type)
            {
                case LapbFrameType.Information:
                    if (received) { _statistics.IFramesReceived++; } else { _statistics.IFramesSent++; }
                    break;
                case LapbFrameType.Supervisory:
                    if (received) { _statistics.SFramesReceived++; } else { _statistics.SFramesSent++; }
                    break;
                default:
                    if (received) { _statistics.UFramesReceived++; } else { _statistics.UFramesSent++; }
                    break;
            }
        }
    }
}
