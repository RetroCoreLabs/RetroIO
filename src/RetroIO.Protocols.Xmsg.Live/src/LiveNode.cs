using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using RetroIO.Protocols.Xmsg.Hdlc;
using RetroIO.Protocols.Xmsg.Node;   // XmsgNode (legacy path) now lives in the portable RetroIO.Protocols.Xmsg.Node half

namespace RetroIO.Protocols.Xmsg.Live
{
    /// <summary>
    /// Composes a transport, HDLC framing, a <see cref="LapbLayer"/> and an
    /// <see cref="XmsgNode"/> into a receive->decode->respond->encode->send loop.
    /// </summary>
    /// <remarks>
    /// <para><b>Loop</b></para>
    /// The pump, in order:
    ///  - read raw bytes from the <see cref="IByteDuplex"/> transport.
    ///  - split the byte stream on <c>0x7E</c> flags and unstuff each frame.
    ///  - keep only FCS-valid frames and hand them to the LAPB link.
    ///  - the link delivers in-order information fields; each SINTRAN frame is decoded and
    ///    given to the node, whose response (if any) is sent as an I-frame.
    ///  - every LAPB body the link emits is HDLC-encoded and written back to the transport.
    /// The receive frame-splitting is incremental (byte by byte) so it works over a live,
    /// chunked TCP stream as well as a pre-loaded <see cref="InMemoryDuplex"/>.
    /// </remarks>
    public sealed class LiveNode
    {
        /// <summary>
        /// The HDLC flag / frame-delimiter byte.
        /// </summary>
        private const byte Flag = 0x7E;

        private readonly IByteDuplex _transport;
        private readonly LapbLayer _link;
        private readonly XmsgNode _node;

        private readonly List<byte> _frameAccumulator;
        private readonly Queue<byte[]> _pendingWrites;

        private long _ticks;

        /// <summary>
        /// Raised for every FCS-valid LAPB frame received from the transport (raw body,
        /// before the link processes it). Diagnostic hook for logging the live exchange.
        /// </summary>
        public event RawFrameReceivedHandler? OnRawFrameReceived;

        /// <summary>
        /// Initialises a live node over a transport, link and node runtime.
        /// </summary>
        /// <param name="transport">
        /// The raw-byte transport (TCP bridge or in-memory).
        /// </param>
        /// <param name="link">
        /// The LAPB link state machine.
        /// </param>
        /// <param name="node">
        /// The XMSG application-layer node.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when any argument is null.
        /// </exception>
        public LiveNode(IByteDuplex transport, LapbLayer link, XmsgNode node)
        {
            if (transport == null)
            {
                throw new ArgumentNullException(nameof(transport));
            }

            if (link == null)
            {
                throw new ArgumentNullException(nameof(link));
            }

            if (node == null)
            {
                throw new ArgumentNullException(nameof(node));
            }

            _transport = transport;
            _link = link;
            _node = node;
            _frameAccumulator = new List<byte>(512);
            _pendingWrites = new Queue<byte[]>();

            _link.OnTransmit += EnqueueTransmit;
            _link.OnInformation += HandleInformation;
        }

        /// <summary>
        /// Runs the receive/respond pump against a LIVE peer, sending an RR keepalive whenever the
        /// transport has been idle for <paramref name="keepaliveInterval"/>.
        /// </summary>
        /// <param name="cancellationToken">
        /// A token that stops the pump.
        /// </param>
        /// <param name="keepaliveInterval">
        /// The idle interval after which an RR keepalive and a retransmit tick are emitted.
        /// Required, and deliberately so - see the remarks.
        /// </param>
        /// <returns>
        /// A task that completes when the pump stops.
        /// </returns>
        /// <remarks>
        /// <para><b>The interval is not optional, because omitting it breaks the link silently</b></para>
        /// <para>
        /// The pump drives the LAPB tick, so with no interval T1 retransmit and T3 keepalive never
        /// fire. Nothing reports an error - traffic flows while the peer keeps talking, and the
        /// link merely loses the ability to recover a dropped frame or hold an idle line open.
        /// </para>
        /// <para>
        /// There used to be a no-argument overload here that quietly meant "no timers", and the
        /// sibling <c>LapbLayerAdapter</c> had the same defaulted parameter. On 2026-08-08 the
        /// relay was found running two live links with no timers at all, invisible to a 720-test
        /// suite because every test drives the pump by hand. Requiring the interval is what keeps
        /// that from being expressible by accident.
        /// </para>
        /// <para>
        /// A caller that genuinely wants no timers must now say so by name:
        /// <see cref="RunWithoutTimersAsync"/>.
        /// </para>
        /// </remarks>
        public Task RunAsync(CancellationToken cancellationToken, TimeSpan keepaliveInterval)
        {
            return PumpAsync(cancellationToken, keepaliveInterval);
        }

        /// <summary>
        /// Runs the pump with NO timers, for in-memory tests and for the legacy reactive-RR path.
        /// </summary>
        /// <param name="cancellationToken">
        /// A token that stops the pump.
        /// </param>
        /// <returns>
        /// A task that completes when the pump stops.
        /// </returns>
        /// <remarks>
        /// The legacy runner path uses this on purpose: the original validated runner emitted no
        /// periodic keepalive and answered with RR only when spoken to. That is a deliberate choice
        /// to reproduce known-good behaviour, NOT the accidental one described in
        /// <see cref="RunAsync"/> - which is exactly why it now has to be spelled out at the call
        /// site instead of falling out of a default.
        /// </remarks>
        public Task RunWithoutTimersAsync(CancellationToken cancellationToken)
        {
            return PumpAsync(cancellationToken, null);
        }

        /// <summary>
        /// The pump itself. Private so the no-timer case is reachable only through the explicitly
        /// named <see cref="RunWithoutTimersAsync"/>.
        /// </summary>
        /// <param name="cancellationToken">
        /// A token that stops the pump.
        /// </param>
        /// <param name="keepaliveInterval">
        /// The keepalive interval, or null to run without timers.
        /// </param>
        /// <returns>
        /// A task that completes when the pump stops.
        /// </returns>
        private async Task PumpAsync(CancellationToken cancellationToken, TimeSpan? keepaliveInterval)
        {
            byte[] buffer = new byte[512];

            // Flush anything the link queued before the first read (for example a SABM from
            // an initiating Connect() call made by the caller).
            await FlushPendingAsync(cancellationToken);

            if (keepaliveInterval == null)
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    int n = await _transport.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    if (n <= 0)
                    {
                        break;
                    }

                    for (int i = 0; i < n; i++)
                    {
                        FeedByte(buffer[i]);
                    }

                    await FlushPendingAsync(cancellationToken);
                }

                await FlushPendingAsync(cancellationToken);
                return;
            }

            // Keepalive loop: race the pending read against an idle timer. On idle, emit a
            // retransmit tick and an RR keepalive so the peer keeps the link in RUN.
            Task<int>? pendingRead = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                pendingRead ??= _transport.ReadAsync(buffer, 0, buffer.Length, cancellationToken);

                Task completed = await Task.WhenAny(
                    pendingRead, Task.Delay(keepaliveInterval.Value, cancellationToken));

                if (completed == pendingRead)
                {
                    int n = await pendingRead;
                    pendingRead = null;
                    if (n <= 0)
                    {
                        break;
                    }

                    for (int i = 0; i < n; i++)
                    {
                        FeedByte(buffer[i]);
                    }

                    _ticks++;
                    _link.Tick(_ticks);
                }
                else if (!cancellationToken.IsCancellationRequested)
                {
                    _ticks++;
                    _link.Tick(_ticks);
                    _link.SendKeepalive();
                }

                await FlushPendingAsync(cancellationToken);
            }

            await FlushPendingAsync(cancellationToken);
        }

        /// <summary>
        /// Feeds one received byte into the incremental HDLC frame splitter.
        /// </summary>
        /// <param name="value">
        /// The received byte.
        /// </param>
        private void FeedByte(byte value)
        {
            if (value == Flag)
            {
                if (_frameAccumulator.Count > 0)
                {
                    ProcessAccumulatedFrame();
                    _frameAccumulator.Clear();
                }

                // A run of flags (interframe fill) collapses; nothing to do on empty accum.
                return;
            }

            _frameAccumulator.Add(value);
        }

        /// <summary>
        /// Unstuffs and FCS-checks the bytes accumulated between two flags, then dispatches
        /// a valid frame to the LAPB link.
        /// </summary>
        private void ProcessAccumulatedFrame()
        {
            // Reuse the verified de-framer by wrapping the stuffed content back in flags.
            byte[] wrapped = new byte[_frameAccumulator.Count + 2];
            wrapped[0] = Flag;
            for (int i = 0; i < _frameAccumulator.Count; i++)
            {
                wrapped[i + 1] = _frameAccumulator[i];
            }

            wrapped[wrapped.Length - 1] = Flag;

            IReadOnlyList<byte[]> frames = HdlcDeframer.SplitFrames(wrapped);
            for (int i = 0; i < frames.Count; i++)
            {
                byte[] frameBytes = frames[i];
                if (!Fcs16.IsValid(frameBytes))
                {
                    continue;
                }

                OnRawFrameReceived?.Invoke(frameBytes);
                LapbFrame frame = new LapbFrame(default, frameBytes);
                _link.OnFrameReceived(frame, _ticks);
            }
        }

        /// <summary>
        /// Decodes a delivered SINTRAN information field and sends the node's response.
        /// </summary>
        /// <param name="info">
        /// The information field of an in-order I-frame.
        /// </param>
        private void HandleInformation(ReadOnlyMemory<byte> info)
        {
            ReadOnlySpan<byte> span = info.Span;
            if (span.Length < SintranHeader.Size)
            {
                return;
            }

            // Only SINTRAN frames (marker 0x21, marker2 0x13/0x12) are XMSG traffic.
            if (span[0] != SintranHeader.Marker1Value)
            {
                return;
            }

            XmsgFrame decoded = XmsgFrame.Parse(span);
            // Multi-frame path: a single incoming frame may need several responses (e.g. a TAD
            // connect needs secure-ACK + connect-accept + greeting).
            IReadOnlyList<XmsgFrame> responses = _node.HandleFrames(decoded);
            for (int i = 0; i < responses.Count; i++)
            {
                _ticks++;
                _link.SendInformation(responses[i].ToArray(), _ticks);
            }
        }

        /// <summary>
        /// HDLC-encodes a LAPB body the link wants to transmit and queues it for writing.
        /// </summary>
        /// <param name="lapbBody">
        /// The LAPB frame body (without FCS).
        /// </param>
        private void EnqueueTransmit(byte[] lapbBody)
        {
            _pendingWrites.Enqueue(HdlcEncoder.Encode(lapbBody));
        }

        /// <summary>
        /// Writes every queued outbound frame to the transport in order.
        /// </summary>
        /// <param name="cancellationToken">
        /// A token that cancels the writes.
        /// </param>
        /// <returns>
        /// A task that completes when the queue is empty.
        /// </returns>
        private async Task FlushPendingAsync(CancellationToken cancellationToken)
        {
            while (_pendingWrites.Count > 0)
            {
                byte[] frame = _pendingWrites.Dequeue();
                await _transport.WriteAsync(frame, cancellationToken);
            }
        }
    }
}
