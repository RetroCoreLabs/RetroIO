//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RetroIO.Link.Hdlc;
using RetroIO.Protocols.Xmsg.Hdlc;
using RetroIO.Protocols.Xmsg.Live;

namespace RetroIO.Protocols.X25.Runtime.Transport
{
    /// <summary>
    /// The data link under the X.25 packet layer on one serial line: HDLC framing and the frame
    /// check sequence, then LAPB, then X.25 packets in and out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bytes from the line go into <see cref="Feed"/>. Each whole frame is checked and, if valid,
    /// handed to a <see cref="LapbLayer"/> speaking the ITU-T X.25 dialect. The information field of
    /// each in-order I frame comes out of <see cref="PacketReceived"/> as one X.25 packet.
    /// <see cref="SendPacket"/> puts one X.25 packet into an I frame, and the frames the LAPB layer
    /// produces go to the write function given at construction.
    /// </para>
    /// <para>
    /// This class does not decode or answer packets. The packet layer (the X.25 engine the runtime
    /// keeps for the line) does that, so a Call Request is seen once, by that engine, and is
    /// answered once, through <see cref="SendPacket"/>.
    /// </para>
    /// <para>
    /// Frames that X.25 clause 2.3.5.3 calls invalid are dropped without any action: a frame with a
    /// frame check sequence error (d), too short (b), aborted (f), or with an address other than A
    /// or B (e). The first three are done by the HDLC decoder, the address test is done here.
    /// </para>
    /// <para>
    /// Locking. The LAPB layer is not thread safe and calls its handlers from inside its own methods.
    /// Everything here that touches it holds one lock, and anything that leaves this class (a write to
    /// the line, a packet for the packet layer, a link state change) is queued under that lock and
    /// sent after it is released. A peer that answers inside the write call, as a loopback does,
    /// therefore reaches <see cref="Feed"/> with the lock free instead of running the LAPB layer
    /// inside itself.
    /// </para>
    /// </remarks>
    public sealed class SerialLapbLink : IDisposable
    {
        /// <summary>
        /// LAPB address A, 11000000 in transmission order (X.25 2.4.2).
        /// </summary>
        private const byte AddressA = LapbLayer.AddressItuA;

        /// <summary>
        /// LAPB address B, 10000000 in transmission order (X.25 2.4.2).
        /// </summary>
        private const byte AddressB = LapbLayer.AddressItuB;

        private readonly ILogger _logger;
        private readonly string _name;
        private readonly SerialX25LinkOptions _options;
        private readonly Func<byte[], bool> _write;
        private readonly Func<long> _clock;
        private readonly LapbLayer _lapb;
        private readonly HdlcDecoder _decoder;

        /// <summary>
        /// The framing policy: octet transparency with every control octet escaped on the way out.
        /// X.25 2.2.2 and 2.3.5.3 describe the control escape octet for start/stop lines, and this
        /// is the same policy the serial links of <c>RetroIO.Link</c> use.
        /// </summary>
        private static readonly HdlcFramerOptions Framing = HdlcFramerOptions.X25;

        private readonly object _gate = new object();
        private readonly Queue<byte[]> _outbox = new Queue<byte[]>();
        private readonly Queue<byte[]> _inbox = new Queue<byte[]>();
        private readonly Queue<bool> _stateEvents = new Queue<bool>();
        private bool _flushing;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private bool _lastUp;
        private bool _disposed;
        private long _invalidAddressFrames;

        /// <summary>
        /// Creates the data link. It does nothing until <see cref="Start"/> or <see cref="Feed"/>.
        /// </summary>
        /// <param name="logger">
        /// The logger.
        /// </param>
        /// <param name="name">
        /// A name for log lines.
        /// </param>
        /// <param name="options">
        /// The role and the LAPB values.
        /// </param>
        /// <param name="write">
        /// Writes bytes to the line and returns whether they were taken. Called with no lock held.
        /// </param>
        /// <param name="clock">
        /// A clock in milliseconds that only ever goes up, for the LAPB timers. Null uses a
        /// stopwatch started at construction. Tests pass their own so they can move time.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="logger"/>, <paramref name="name"/>, <paramref name="options"/>
        /// or <paramref name="write"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when the role or the link set-up choice in <paramref name="options"/> is not set.
        /// </exception>
        public SerialLapbLink(ILogger logger, string name, SerialX25LinkOptions options, Func<byte[], bool> write, Func<long>? clock = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _name = name ?? throw new ArgumentNullException(nameof(name));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            if (!options.IsComplete)
            {
                throw new ArgumentException("Role and InitiateLinkSetup must both be set; neither has a default.", nameof(options));
            }
            _write = write ?? throw new ArgumentNullException(nameof(write));
            _clock = clock ?? (() => _stopwatch.ElapsedMilliseconds);

            // The node number is only written into the information field of the ND dialect's frames.
            // The ITU dialect has no information field on SABM, UA, DISC, DM or the supervisory
            // frames (X.25 2.3.4), so it is not used and is left at zero.
            _lapb = new LapbLayer(0, options.ToLapbOptions());
            _lapb.OnTransmit += OnLapbTransmit;
            _lapb.OnInformation += OnLapbInformation;
            _lapb.OnLinkFailure += OnLapbLinkFailure;

            // The decoder drops frames with a bad FCS, runts and aborts, as clause 2.3.5.3 requires
            // (DeliverCorruptFrames is off by default).
            _decoder = new HdlcDecoder(Framing);
        }

        /// <summary>
        /// Raised with one X.25 packet for each in-order I frame. Raised with no lock held.
        /// </summary>
        public event EventHandler<byte[]>? PacketReceived;

        /// <summary>
        /// Raised with true when the data link becomes usable and with false when it stops being
        /// usable. Raised with no lock held.
        /// </summary>
        public event EventHandler<bool>? LinkStateChanged;

        /// <summary>
        /// True when the data link is set up in both directions and packets can be sent.
        /// </summary>
        public bool IsUp
        {
            get
            {
                lock (_gate)
                {
                    return _lapb.IsUp;
                }
            }
        }

        /// <summary>
        /// The role this side plays.
        /// </summary>
        public SerialX25LinkRole Role
        {
            get { return _options.Role.GetValueOrDefault(); }
        }

        /// <summary>
        /// How many frames were dropped because their address was neither A nor B
        /// (X.25 2.3.5.3 e).
        /// </summary>
        public long InvalidAddressFrames
        {
            get { return System.Threading.Interlocked.Read(ref _invalidAddressFrames); }
        }

        /// <summary>
        /// The HDLC decoder counters: good frames, FCS errors, runts, aborts.
        /// </summary>
        public HdlcStatistics HdlcStatistics
        {
            get
            {
                lock (_gate)
                {
                    return _decoder.Statistics;
                }
            }
        }

        /// <summary>
        /// Starts the link. When the options say to initiate, a SABM with P=1 goes out now
        /// (X.25 2.4.4.1); otherwise nothing is sent and the peer's SABM is awaited.
        /// </summary>
        public void Start()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                if (_options.InitiateLinkSetup == true)
                {
                    _lapb.Connect(_clock());
                }

                NoteStateChange();
            }

            Flush();
        }

        /// <summary>
        /// Stops the link. A set up link is disconnected with a DISC (X.25 2.4.4.3) and then dropped
        /// without waiting for the answer, because the line is being closed.
        /// </summary>
        public void Stop()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                if (_lapb.State != LapbLayerState.Disconnected)
                {
                    _lapb.Disconnect(_clock());
                }

                // Nothing will answer the DISC once the line is closed.
                _lapb.ForceDisconnected();
                NoteStateChange();
            }

            Flush();
        }

        /// <summary>
        /// Takes bytes read from the line. A read may hold part of a frame or several frames.
        /// </summary>
        /// <param name="data">
        /// The bytes.
        /// </param>
        /// <param name="length">
        /// How many bytes of <paramref name="data"/> to use.
        /// </param>
        public void Feed(byte[] data, int length)
        {
            if (data == null || length <= 0)
            {
                return;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                ReadOnlySpan<byte> input = new ReadOnlySpan<byte>(data, 0, Math.Min(length, data.Length));
                int offset = 0;
                while (offset < input.Length)
                {
                    HdlcDecodeStatus status = _decoder.Decode(input.Slice(offset), out int consumed);
                    offset += consumed;

                    if (status == HdlcDecodeStatus.NeedMoreData)
                    {
                        break;
                    }

                    if (status == HdlcDecodeStatus.FrameComplete)
                    {
                        HandleFrame(_decoder.Frame);
                    }
                    else
                    {
                        // BadFcs is not delivered with these options, so what is left is a runt, an
                        // overlong frame or an abort. All are dropped with no action (2.3.5.3).
                        _logger.LogDebug("SerialLapbLink[{Name}]: frame dropped ({Status})", _name, status);
                    }
                }

                NoteStateChange();
            }

            Flush();
        }

        /// <summary>
        /// Puts one X.25 packet into an I frame.
        /// </summary>
        /// <param name="packet">
        /// The whole X.25 packet.
        /// </param>
        /// <returns>
        /// True when the packet was handed to LAPB. False when the data link is not set up.
        /// </returns>
        public bool SendPacket(byte[] packet)
        {
            if (packet == null || packet.Length == 0)
            {
                return false;
            }

            lock (_gate)
            {
                if (_disposed || !_lapb.IsUp)
                {
                    return false;
                }

                try
                {
                    _lapb.SendInformation(packet, _clock());
                }
                catch (InvalidOperationException ex)
                {
                    // The state changed between the test above and the send; the layer refuses.
                    _logger.LogDebug(ex, "SerialLapbLink[{Name}]: packet refused, link not connected", _name);
                    return false;
                }

                NoteStateChange();
            }

            Flush();
            return true;
        }

        /// <summary>
        /// Moves the LAPB timers (T1, T2, T3) on to the time the clock gives now.
        /// </summary>
        public void Tick()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _lapb.Tick(_clock());
                NoteStateChange();
            }

            Flush();
        }

        /// <summary>
        /// Stops using the link. The decoder buffer is returned.
        /// </summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _decoder.Dispose();
            }
        }

        // ------------------------------------------------------------------ inside the lock

        /// <summary>
        /// Handles one frame the decoder says is whole and has a good FCS: it holds address,
        /// control, information and the two FCS octets.
        /// </summary>
        private void HandleFrame(ReadOnlySpan<byte> frameWithFcs)
        {
            // Strip the two FCS octets; the layer above wants address, control and information.
            int bodyLength = frameWithFcs.Length - 2;
            if (bodyLength < 2)
            {
                return;
            }

            byte address = frameWithFcs[0];

            // X.25 2.3.5.3 e): a frame with an address other than A or B (single link operation)
            // is invalid, and an invalid frame is discarded with no action.
            if (address != AddressA && address != AddressB)
            {
                System.Threading.Interlocked.Increment(ref _invalidAddressFrames);
                _logger.LogDebug("SerialLapbLink[{Name}]: frame with address 0x{Address:X2} dropped (neither A nor B)", _name, address);
                return;
            }

            LapbFrame frame = new LapbFrame(frameWithFcs.Slice(0, bodyLength).ToArray());
            _lapb.OnFrameReceived(frame, _clock());
        }

        private void OnLapbTransmit(byte[] lapbBody)
        {
            // Called from inside the layer, so only queued here. The FCS and the flags are added by
            // the encoder (X.25 2.2.7.4 and 2.2.1).
            _outbox.Enqueue(HdlcEncoder.Encode(lapbBody, Framing));
        }

        private void OnLapbInformation(ReadOnlyMemory<byte> info)
        {
            _inbox.Enqueue(info.ToArray());
        }

        private void OnLapbLinkFailure(string reason)
        {
            _logger.LogWarning("SerialLapbLink[{Name}]: data link failed: {Reason}", _name, reason);
        }

        /// <summary>
        /// Queues a link state event when <see cref="LapbLayer.IsUp"/> changed since the last look.
        /// Called with the lock held after every step that can change it.
        /// </summary>
        private void NoteStateChange()
        {
            bool up = _lapb.IsUp;
            if (up != _lastUp)
            {
                _lastUp = up;
                _stateEvents.Enqueue(up);
            }
        }

        // ------------------------------------------------------------------ outside the lock

        /// <summary>
        /// Sends what the locked code queued: first the link state change (so a packet is never
        /// delivered for a link the packet layer has not been told is up), then the packets, then
        /// the frames. Runs with the lock free.
        /// </summary>
        /// <remarks>
        /// Only one thread flushes at a time, and a call made while a flush is running (from a
        /// handler that answers a packet, or from a loopback peer that answers a write) returns at
        /// once: the running flush finds what it queued on its next turn round the loop. That keeps
        /// the packet layer from being handed packet two while it is still handling packet one, and
        /// keeps frames in the order the LAPB layer produced them.
        /// </remarks>
        private void Flush()
        {
            lock (_gate)
            {
                if (_flushing)
                {
                    return;
                }

                _flushing = true;
            }

            try
            {
                while (true)
                {
                    byte[]? bytes = null;
                    byte[]? packet = null;
                    bool hasState = false;
                    bool state = false;

                    lock (_gate)
                    {
                        if (_stateEvents.Count > 0)
                        {
                            hasState = true;
                            state = _stateEvents.Dequeue();
                        }
                        else if (_inbox.Count > 0)
                        {
                            packet = _inbox.Dequeue();
                        }
                        else if (_outbox.Count > 0)
                        {
                            bytes = _outbox.Dequeue();
                        }
                        else
                        {
                            _flushing = false;
                            return;
                        }
                    }

                    if (hasState)
                    {
                        Raise(LinkStateChanged, state);
                    }
                    else if (packet != null)
                    {
                        Raise(PacketReceived, packet);
                    }
                    else if (bytes != null)
                    {
                        bool taken = false;
                        try
                        {
                            taken = _write(bytes);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "SerialLapbLink[{Name}]: write to the line failed", _name);
                        }

                        if (!taken)
                        {
                            // LAPB recovers a lost frame with T1, so this is a log line, not a stop.
                            _logger.LogWarning("SerialLapbLink[{Name}]: the line did not take a {Length} byte frame", _name, bytes.Length);
                        }
                    }
                }
            }
            catch
            {
                lock (_gate)
                {
                    _flushing = false;
                }

                throw;
            }
        }

        private void Raise<T>(EventHandler<T>? handler, T value)
        {
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(this, value);
            }
            catch (Exception ex)
            {
                // A handler that throws must not stop the read thread of the serial port.
                _logger.LogError(ex, "SerialLapbLink[{Name}]: an event handler threw", _name);
            }
        }
    }
}
