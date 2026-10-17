//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using RetroIO.Protocols.Xmsg.Hdlc;
using RetroIO.Protocols.Xmsg.Node.Seam;   // ILink + seam types now live in the portable RetroIO.Protocols.Xmsg.Node half

namespace RetroIO.Protocols.Xmsg.Live.Seam
{
    /// <summary>
    /// An <see cref="ILink"/> over the proven HDLC/LAPB stack: wraps an <see cref="IByteDuplex"/>
    /// transport and a <see cref="LapbLayer"/>, runs the receive->deframe->FCS->LAPB pump, delivers each
    /// in-order information field UP as <see cref="PayloadReceived"/>, and turns
    /// <see cref="SendData"/> into a LAPB I-frame DOWN.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the seam-shaped sibling of the proven <see cref="LiveNode"/>: it reuses the identical,
    /// live-tested framing (incremental <c>0x7E</c> splitting, <see cref="HdlcDeframer"/>,
    /// <see cref="Fcs16"/> validation, <see cref="HdlcEncoder"/>) but is DECOUPLED from any
    /// application node - the codec/layer above it does the responding via the up-event. The old
    /// <see cref="LiveNode"/> path stays intact until the new path proves live parity (Phase 5).
    /// </para>
    /// <para>
    /// The HDLC/LAPB transport is common to X.25 and XMSG; this adapter carries the payload opaquely
    /// via <see cref="SendData"/> and never classifies it. Which L3 protocol a link carries is the
    /// composition root's concern (see <c>IProtocolDetector</c>), not the link's.
    /// </para>
    /// <para>
    /// Buffer ownership: the <see cref="PayloadReceived"/> payload MAY be a buffer the adapter
    /// reuses; a consumer that retains the bytes beyond the callback MUST copy them within the
    /// callback. (Today the underlying frame array is fresh per receive, so no copy is strictly
    /// required - but consumers must not rely on that.)
    /// </para>
    /// </remarks>
    public sealed class LapbLayerAdapter : ILink
    {
        /// <summary>
        /// The HDLC flag / frame-delimiter byte.
        /// </summary>
        private const byte Flag = 0x7E;

        private readonly string _linkId;
        private readonly IByteDuplex _transport;
        private readonly LapbLayer _link;

        /// <summary>
        /// The shared streaming HDLC decoder for this link's receive direction.
        /// </summary>
        /// <remarks>
        /// Delimiting and transparency only - <c>UseFcs</c> is false and the minimum length is one
        /// - because the FCS decision belongs here, as it did in the de-framer this replaces. One
        /// instance per direction; the type is not thread-safe by design.
        /// </remarks>
        private readonly RetroIO.Link.Hdlc.HdlcDecoder _hdlcDecoder =
            new RetroIO.Link.Hdlc.HdlcDecoder(
                RetroIO.Link.Hdlc.HdlcFramerOptions.Xmsg with
                {
                    UseFcs = false,
                    MinFrameLength = 1,
                });
        private readonly Queue<byte[]> _pendingWrites;

        /// <summary>
        /// How long the closing flush may spend getting queued frames onto a live link.
        /// </summary>
        /// <remarks>
        /// Long enough for a healthy peer to take a handful of frames, short enough that a dead one
        /// does not hold the process open. See <c>FlushFinalAsync</c> for why the pump's own token
        /// cannot be used here.
        /// </remarks>
        private static readonly TimeSpan FinalFlushGrace = TimeSpan.FromSeconds(2);

        // Monotonic millisecond clock feeding the LAPB timers (T1/T3/N2). The LAPB layer never reads a
        // wall clock itself (deterministic by design); this adapter, in the replaceable live half,
        // injects real elapsed milliseconds so the spec timers fire on real time. Stopwatch is
        // monotonic and immune to wall-clock adjustments. Frame bytes never depend on this value
        // (only timer deadlines do), so the in-memory parity tests stay deterministic.
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private LinkStatus _status;
        // True once Stop()/Dispose() has been called. Used to stop the pump's residual LAPB state from
        // resurrecting the link to Active/Starting. We CANNOT use the status enum for this, because the
        // INITIAL status is also Stopped (before Start) - and from there the link legitimately advances.
        private bool _stopRequested;
        private CancellationTokenSource? _cts;
        private Task? _pump;

        /// <summary>
        /// Gets the current monotonic clock value in milliseconds, injected into the LAPB timers.
        /// </summary>
        private long CurrentMillis
        {
            get { return _clock.ElapsedMilliseconds; }
        }

        /// <inheritdoc />
        public event LinkPayloadReceived? PayloadReceived;

        /// <inheritdoc />
        public event LinkStatusChanged? StatusChanged;

        /// <summary>
        /// Diagnostic up-event: every FCS-valid LAPB frame received from the wire (raw body,
        /// before the LAPB state machine processes it). Lets the runner log SABM/UA/RR/I traffic
        /// from the peer - including a bare-link SABM storm when the peer's XMSG has died.
        /// </summary>
        /// <param name="linkId">
        /// The link the frame arrived on (sender-first).
        /// </param>
        /// <param name="frameBytes">
        /// The unstuffed, FCS-valid LAPB frame (addr, control, info, FCS).
        /// </param>
        public delegate void LinkRawFrameReceived(string linkId, byte[] frameBytes);

        /// <summary>
        /// Occurs for every FCS-valid LAPB frame received (diagnostic).
        /// </summary>
        public event LinkRawFrameReceived? RawFrameReceived;

        /// <summary>
        /// A periodic callback fired on the adapter's own loop thread once per pump iteration (after each
        /// read or idle tick, before the pending writes are flushed).
        /// </summary>
        /// <remarks>
        /// Runs on the SAME single thread as inbound-frame processing, so a handler may safely send frames
        /// down the codec / touch node state with no locking. Used to drive time-based work that has no
        /// inbound trigger - notably releasing the second chunk of a terminal-output pair once the verified
        /// ~46 ms intra-pair gap has elapsed (TAD-Message-Formats.md 22.16).
        /// </remarks>
        public delegate void LinkLoopTick();

        /// <summary>
        /// Occurs once per pump iteration on the adapter's loop thread, for time-based sending that has no
        /// inbound-frame trigger.
        /// </summary>
        public event LinkLoopTick? LoopTick;

        /// <summary>
        /// A callback fired once, on the loop thread, when the pump has decided to stop.
        /// </summary>
        public delegate void LinkStopping();

        /// <summary>
        /// Occurs once when the pump is shutting down, BEFORE the closing flush, so a handler can still
        /// queue frames that will reach the wire.
        /// </summary>
        /// <remarks>
        /// <para><b>Why a server needs this</b></para>
        /// <para>
        /// A session is live state on BOTH sides. On 2026-08-17 our runner was stopped with three TAD
        /// sessions open and D100's XMSG died with a fatal internal inconsistency, holding half a
        /// session each; recovery was an emulator restart. Saying goodbye needs a moment when the
        /// pump is known to be ending but can still transmit, and that moment did not exist.
        /// </para>
        /// <para>
        /// Runs on the SAME thread as inbound processing and <see cref="LoopTick"/>, so a handler may
        /// send without locking. Anything it queues is flushed by the closing flush, which uses its
        /// own bounded token rather than the cancelled one - see <c>FlushFinalAsync</c>, without
        /// which this event would be pointless because the frames could never be written.
        /// </para>
        /// <para>
        /// It cannot help a forced kill, which runs none of our code. The operating rule stands
        /// alongside it: get the users out before stopping a server the ND is still talking to.
        /// </para>
        /// </remarks>
        public event LinkStopping? Stopping;

        /// <summary>
        /// Initialises the adapter over a transport and LAPB link.
        /// </summary>
        /// <param name="linkId">
        /// The link identity stamped on every up-event.
        /// </param>
        /// <param name="transport">
        /// The raw-byte transport (TCP bridge or in-memory).
        /// </param>
        /// <param name="link">
        /// The LAPB link state machine.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when any reference argument is null.
        /// </exception>
        public LapbLayerAdapter(string linkId, IByteDuplex transport, LapbLayer link)
        {
            _linkId = linkId ?? throw new ArgumentNullException(nameof(linkId));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _link = link ?? throw new ArgumentNullException(nameof(link));

            _pendingWrites = new Queue<byte[]>();
            _status = LinkStatus.Stopped;

            _link.OnTransmit += EnqueueTransmit;
            _link.OnInformation += DeliverPayload;
            _link.OnLinkFailure += ReportLinkFailure;
        }

        /// <summary>
        /// Reports a LAPB link failure that was NOT re-established, and takes the link down.
        /// </summary>
        /// <param name="reason">
        /// The description supplied by <see cref="LapbLayer.OnLinkFailure"/>.
        /// </param>
        /// <remarks>
        /// <para><b>Why this is loud</b></para>
        /// This is the exact spot where the runner used to go quiet: N2 exhausted, the layer reset
        /// the link with a SABM, and the only sign anything had happened was a wall of identical
        /// <c>SABM state=SabmSent</c> lines until the transfer timed out minutes later. Twice on
        /// 2026-08-21 that SABM also killed D100's XMSG gateway outright (fatal code 27 within
        /// 200 ms).
        /// <para>
        /// Now the link stops and SAYS the number that matters - how many frames were left
        /// unacknowledged - so a stalled window is distinguishable from a peer that never answered.
        /// </para>
        /// </remarks>
        private void ReportLinkFailure(string reason)
        {
            Console.WriteLine("[link] " + _linkId + " " + reason);
            SetStatus(LinkStatus.Stopped, reason);
        }

        /// <inheritdoc />
        public string Name
        {
            get { return _linkId; }
        }

        /// <inheritdoc />
        public LinkStatus Status
        {
            get { return _status; }
        }

        /// <summary>
        /// Gets the task representing the running receive pump (null until <see cref="Start"/>), so a
        /// test can await pump completion when the transport reaches end of stream.
        /// </summary>
        public Task? Completion
        {
            get { return _pump; }
        }

        /// <inheritdoc />
        public bool Start()
        {
            // Live start: initiate LAPB establishment, then pump forever with an RR keepalive so the
            // peer keeps the link in RUN. Deterministic tests use Initiate()+RunAsync directly.
            _cts = new CancellationTokenSource();
            SetStatus(LinkStatus.Starting, "start requested");
            Initiate();
            _pump = RunAsync(_cts.Token, TimeSpan.FromSeconds(1));
            return true;
        }

        /// <inheritdoc />
        public void Stop()
        {
            // Idempotent: a second Stop (or a Stop after Dispose) is a no-op.
            if (_status == LinkStatus.Stopped || _status == LinkStatus.Stopping)
            {
                return;
            }

            _stopRequested = true;
            SetStatus(LinkStatus.Stopping, "stop requested");
            _cts?.Cancel();
            SetStatus(LinkStatus.Stopped, "stopped");
        }

        /// <inheritdoc />
        public void Dispose()
        {
            // Dispose implies Stop. We only CANCEL the pump (cooperative) and never await it here, so
            // Dispose can never hang on the receive pump. The token source is then released.
            Stop();
            _cts?.Dispose();
        }

        /// <summary>
        /// Initiates LAPB establishment (sends our SABM); the pump flushes it on its next cycle.
        /// </summary>
        public void Initiate()
        {
            _link.Connect(CurrentMillis);
        }

        /// <summary>
        /// Runs the receive/respond pump against a LIVE peer until end of stream or cancellation.
        /// </summary>
        /// <param name="cancellationToken">
        /// A token that stops the pump.
        /// </param>
        /// <param name="keepaliveInterval">
        /// The idle interval after which an RR keepalive and a retransmit tick fire. Required, and
        /// deliberately so - see the remarks.
        /// </param>
        /// <returns>
        /// A task that completes when the pump stops.
        /// </returns>
        /// <remarks>
        /// <para><b>The interval is not optional, because omitting it breaks the link silently</b></para>
        /// <para>
        /// The pump is what drives <see cref="LapbLayer.Tick"/>, so with no interval the LAPB T1
        /// retransmit and T3 keepalive timers NEVER FIRE. Nothing reports an error: frames still
        /// flow while the peer keeps talking, and the link simply cannot recover a lost frame or
        /// hold an idle line open. It also silences <see cref="LoopTick"/> except when a frame
        /// happens to arrive, so any work scheduled there stops happening.
        /// </para>
        /// <para>
        /// This used to be an optional parameter defaulting to null. On 2026-08-08 the relay path
        /// was found running BOTH its links that way - no timers at all on two live HDLC lines -
        /// and a 720-test suite never noticed, because every test drives the pump by hand. It was
        /// found only by reading the adapter while chasing an unrelated fault. Making the parameter
        /// required is what stops that recurring: a caller now has to say which behaviour it wants.
        /// </para>
        /// <para>
        /// For an in-memory test with no clock, call <see cref="RunWithoutTimersAsync"/> instead.
        /// Its name is the point - "no timers" should be a visible decision at the call site.
        /// </para>
        /// </remarks>
        public Task RunAsync(CancellationToken cancellationToken, TimeSpan keepaliveInterval)
        {
            return PumpAsync(cancellationToken, keepaliveInterval);
        }

        /// <summary>
        /// Runs the pump with NO timers, for deterministic in-memory tests that drive both ends
        /// themselves.
        /// </summary>
        /// <param name="cancellationToken">
        /// A token that stops the pump.
        /// </param>
        /// <returns>
        /// A task that completes when the pump stops.
        /// </returns>
        /// <remarks>
        /// NOT for a live link: without timers LAPB cannot retransmit or keep an idle line open.
        /// See <see cref="RunAsync"/> for what that costs and how it was missed for months.
        /// </remarks>
        public Task RunWithoutTimersAsync(CancellationToken cancellationToken)
        {
            return PumpAsync(cancellationToken, null);
        }

        /// <summary>
        /// The pump itself. Private so the null case can only be reached through the explicitly
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

            // Flush the SABM queued by Initiate() before the first read.
            await FlushPendingAsync(cancellationToken);
            RaiseStatusIfChanged();

            if (keepaliveInterval == null)
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    int n = await _transport.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    if (n <= 0)
                    {
                        break;
                    }

                    FeedBytes(buffer, n);

                    await FlushPendingAsync(cancellationToken);
                    RaiseStatusIfChanged();
                }

                RaiseStopping();
                await FlushFinalAsync();
                RaiseStatusIfChanged();
                return;
            }

            // Keepalive loop: race the pending read against an idle timer.
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

                    FeedBytes(buffer, n);

                    // Drive the LAPB timers from the real monotonic clock after processing inbound data.
                    _link.Tick(CurrentMillis);
                }
                else if (!cancellationToken.IsCancellationRequested)
                {
                    // Idle tick: advance the LAPB timers ONLY. Tick() itself retransmits on T1 and emits
                    // the conformant RR keepalive poll when T3 expires, so we must NOT also send an
                    // unconditional RR here - that flooded the link with one RR per idle second. The tick
                    // interval is just the timer resolution; the keepalive cadence is the T3 period.
                    _link.Tick(CurrentMillis);
                }

                // Time-based sending with no inbound trigger (e.g. releasing the second chunk of a
                // terminal-output pair once the ~46 ms intra-pair gap has elapsed). Runs on THIS loop
                // thread, so a handler may send down the codec without locking. See 22.16.
                LoopTick?.Invoke();

                await FlushPendingAsync(cancellationToken);
                RaiseStatusIfChanged();
            }

            RaiseStopping();
            await FlushFinalAsync();
            RaiseStatusIfChanged();
        }

        /// <summary>
        /// Fires <see cref="Stopping"/> once, letting handlers queue their goodbyes.
        /// </summary>
        /// <remarks>
        /// A throwing handler must not replace a clean stop with a stack trace, and must not stop the
        /// closing flush from running - whatever earlier handlers queued still deserves to go out.
        /// </remarks>
        private void RaiseStopping()
        {
            try
            {
                Stopping?.Invoke();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[link] {_linkId} a Stopping handler threw, continuing shutdown: {ex.Message}");
            }
        }

        /// <summary>
        /// Flushes whatever is still queued on the way out, ignoring the cancellation that stopped us.
        /// </summary>
        /// <returns>
        /// A task that completes when the queue is empty, the transport refuses, or the grace elapses.
        /// </returns>
        /// <remarks>
        /// <para><b>Why this cannot use the pump's own token</b></para>
        /// <para>
        /// Both loops above end because cancellation was requested, and the flush that followed used
        /// that same token. <see cref="System.Net.Sockets.NetworkStream"/> returns a CANCELLED task
        /// without writing when the token is already cancelled, so the closing flush discarded every
        /// queued frame and threw - and the throw looked like an ordinary "time window elapsed" stop
        /// because the caller catches <see cref="OperationCanceledException"/>. Anything queued as we
        /// shut down therefore never reached the wire.
        /// </para>
        /// <para>
        /// MEASURED 2026-08-17 by the consequence rather than the code: our runner was stopped with
        /// three TAD sessions open, and D100's XMSG died with a fatal internal inconsistency because
        /// it was left holding half a session. Sending the goodbye was impossible while this stood.
        /// </para>
        /// <para>
        /// The grace is bounded so a dead peer cannot hang the exit: if the far end has gone, the
        /// write will not complete and we leave anyway. Failures are swallowed deliberately - this
        /// runs on the way out, and a shutdown that throws is worse than one that gave up flushing.
        /// </para>
        /// </remarks>
        private async Task FlushFinalAsync()
        {
            if (_pendingWrites.Count == 0)
            {
                return;
            }

            using (CancellationTokenSource grace = new CancellationTokenSource(FinalFlushGrace))
            {
                try
                {
                    await FlushPendingAsync(grace.Token);
                }
                catch (OperationCanceledException)
                {
                    // The peer did not take it within the grace. Nothing further to try.
                }
                catch (System.IO.IOException)
                {
                    // The link is already gone - expected when the peer dropped first.
                }
                catch (ObjectDisposedException)
                {
                    // The transport was disposed underneath us; also a normal shutdown race.
                }
            }
        }

        /// <inheritdoc />
        public bool SendData(ReadOnlySpan<byte> payload)
        {
            // Contract: a not-Active link (or an empty payload) is a logged false, NEVER a throw.
            if (_status != LinkStatus.Active)
            {
                Console.WriteLine($"[link] {_linkId} SendData refused: link not Active (status {_status})");
                return false;
            }

            if (payload.Length == 0)
            {
                Console.WriteLine($"[link] {_linkId} SendData refused: empty payload");
                return false;
            }

            // Emits the I-frame via LapbLayer.OnTransmit -> EnqueueTransmit; the pump flushes it.
            // SendInformation cannot retain the span (compiler-enforced) and copies the bytes into the
            // LAPB body it enqueues, so the caller's buffer is free the moment this returns.
            _link.SendInformation(payload, CurrentMillis);
            return true;
        }

        /// <summary>
        /// Feeds received bytes into the shared streaming HDLC decoder and dispatches every
        /// complete, FCS-valid frame to the LAPB link.
        /// </summary>
        /// <param name="buffer">
        /// The receive buffer.
        /// </param>
        /// <param name="count">
        /// How many bytes of it are valid.
        /// </param>
        /// <remarks>
        /// <para>
        /// Until 2026-09-06 this was a hand-rolled byte-at-a-time flag splitter - a near-identical
        /// copy of the one in <c>LiveNode</c> - that accumulated into a <c>List{byte}</c> and
        /// then wrapped the result back up in flags to hand to a whole-buffer de-framer, costing an
        /// extra copy of every frame.
        /// </para>
        /// <para>
        /// It existed only because that de-framer could not hold a partial frame across reads. The
        /// shared decoder can, so both copies go away.
        /// </para>
        /// </remarks>
        private void FeedBytes(byte[] buffer, int count)
        {
            ReadOnlySpan<byte> data = new ReadOnlySpan<byte>(buffer, 0, count);

            int offset = 0;
            while (offset < data.Length)
            {
                RetroIO.Link.Hdlc.HdlcDecodeStatus status =
                    _hdlcDecoder.Decode(data.Slice(offset), out int consumed);
                offset += consumed;

                if (status == RetroIO.Link.Hdlc.HdlcDecodeStatus.NeedMoreData)
                {
                    break;
                }

                if (status != RetroIO.Link.Hdlc.HdlcDecodeStatus.FrameComplete)
                {
                    continue;
                }

                byte[] frameBytes = _hdlcDecoder.Frame.ToArray();
                if (!Fcs16.IsValid(frameBytes))
                {
                    continue;
                }

                // Diagnostic: surface every FCS-valid received frame (SABM/UA/RR/I) before the LAPB
                // state machine consumes it, so the runner can see the peer's raw traffic.
                RawFrameReceived?.Invoke(_linkId, frameBytes);

                LapbFrame frame = LapbFrame.FromCapture(frameBytes);
                _link.OnFrameReceived(frame, CurrentMillis);

                // Refresh coarse status right after each frame so a SABM/UA that brings the LAPB link
                // to Connected flips us to Active BEFORE the very next frame (the peer's first I-frame)
                // delivers its payload - a send issued from that PayloadReceived callback then sees an
                // Active link. Waiting until the end of the read chunk would race that.
                RaiseStatusIfChanged();
            }
        }

        /// <summary>
        /// Raises the up-event for an in-order information field delivered by the LAPB link.
        /// </summary>
        private void DeliverPayload(ReadOnlyMemory<byte> info)
        {
            // The ILink contract hands the payload up as byte[] + length. The frame's info field is
            // already backed by its own array, so surface that array directly (zero-copy) when it is a
            // whole-array segment; only copy on the unusual sliced case. Consumers copy if they retain.
            if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(info, out ArraySegment<byte> seg)
                && seg.Array != null && seg.Offset == 0 && seg.Array.Length == seg.Count)
            {
                PayloadReceived?.Invoke(this, seg.Array, seg.Count);
            }
            else
            {
                byte[] copy = info.ToArray();
                PayloadReceived?.Invoke(this, copy, copy.Length);
            }
        }

        /// <summary>
        /// HDLC-encodes a LAPB body the link wants to transmit and queues it for writing.
        /// </summary>
        private void EnqueueTransmit(byte[] lapbBody)
        {
            _pendingWrites.Enqueue(HdlcEncoder.Encode(lapbBody));
        }

        /// <summary>
        /// Writes every queued outbound frame to the transport, in order.
        /// </summary>
        private async Task FlushPendingAsync(CancellationToken cancellationToken)
        {
            while (_pendingWrites.Count > 0)
            {
                byte[] frame = _pendingWrites.Dequeue();
                await _transport.WriteAsync(frame, cancellationToken);
            }
        }

        /// <summary>
        /// Maps the LAPB state to the coarse <see cref="LinkStatus"/> and raises StatusChanged on a
        /// transition, so the layer above learns the pipe became usable / unusable. LAPB
        /// <c>Connected</c> maps to <see cref="LinkStatus.Active"/>; anything else while the pump runs
        /// is <see cref="LinkStatus.Starting"/> (establishing or re-establishing).
        /// </summary>
        private void RaiseStatusIfChanged()
        {
            // Once Stop()/Dispose() has been requested, a residual LAPB state left in the pump must not
            // resurrect the link to Active/Starting. (We check the flag, not the status enum, because
            // the initial pre-Start status is also Stopped - and from there we DO advance.)
            if (_stopRequested)
            {
                return;
            }

            // IsUp, NOT State == Connected. The spec's state table carries "notify up" as an action
            // separate from the transition, and the two differ in exactly one place that matters:
            // entering CONNECTED by answering the PEER's SABM while our own SABM is still
            // unacknowledged is written with NO notify up. Mapping the state enum straight to
            // Active told the layer above it could send while only half the handshake was done.
            //
            // MEASURED 2026-08-19: on a run where the peer flushed a queued backlog, that gap was
            // 180 ms wide, an I-frame went out inside it, a later SABM reset V(S), and we ended up
            // sending FRMR to the peer's own answer. See LapbLayer.IsUp.
            bool up = _link.IsUp;
            LinkStatus mapped = up ? LinkStatus.Active : LinkStatus.Starting;
            SetStatus(mapped, up ? "LAPB connected" : "LAPB establishing");
        }

        /// <summary>
        /// Sets the coarse status and raises <see cref="StatusChanged"/> only on an actual transition,
        /// carrying the previous status, the new status, and a reason (sender-first: this link).
        /// </summary>
        /// <param name="next">
        /// The new status.
        /// </param>
        /// <param name="reason">
        /// A short human-readable reason for the transition (for logs).
        /// </param>
        private void SetStatus(LinkStatus next, string reason)
        {
            if (next != _status)
            {
                LinkStatus previous = _status;
                _status = next;
                StatusChanged?.Invoke(this, previous, next, reason);
            }
        }
    }
}
