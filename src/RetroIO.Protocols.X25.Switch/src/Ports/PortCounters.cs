//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Threading;

namespace RetroIO.Protocols.X25.Switch.Ports
{
    /// <summary>
    /// Per-port lifetime counters, safe to read from other threads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Counters are maintained with interlocked operations because a port's I/O runs on its own thread while
    /// management surfaces read the totals concurrently.
    /// </para>
    /// <para>
    /// What each counter means, and who counts it. Every event is counted once, in one place. The C emulator
    /// (x25switch) keeps the same counters with the same meaning, so a monitor shows two switches side by side.
    /// </para>
    /// <list type="bullet">
    /// <item><description><see cref="CallsIn"/>: calls that arrived on this port from outside (an incoming Call
    /// Request). Counted by <c>SwitchCore</c> when the port reports the call. One call across three switches is one call
    /// in on each port it arrives on. A call a port refuses itself (no free channel) never reaches the core, so the
    /// port counts it (in, and cleared).</description></item>
    /// <item><description><see cref="CallsOut"/>: calls this switch placed out of this port to carry a routed call.
    /// Counted by <c>SwitchCore</c> when the port accepts the onward Call Request. A call placed by a client of the
    /// port itself is not counted.</description></item>
    /// <item><description><see cref="CallsCleared"/>: calls that ended on this port: its channel is free again. The
    /// X.25 engine reports this once per call, whoever ended it (a Clear Indication from the far side, or the
    /// Clear Confirmation to a Clear Request the switch sent). Counted by <c>SwitchCore</c> when the port reports it. A
    /// call that went through and ended normally is therefore one call in or out, and one cleared, on each port.</description></item>
    /// <item><description><see cref="PacketsIn"/> and <see cref="PacketsOut"/>: DATA packets only (X.25 user data
    /// packets, whatever their Q, M and D bits). In is counted when a data packet arrives on the port and is
    /// handed to the switch; out is counted when the switch's forwarding has the port send one that really left.
    /// Call set-up and clearing, RR, RNR, reset, interrupt, restart and diagnostic packets are not counted here.
    /// N data packets each way over a call gives N in and N out on every port the call touches.</description></item>
    /// <item><description><see cref="AllPacketsIn"/>, <see cref="AllPacketsOut"/> and <see cref="BytesIn"/>,
    /// <see cref="BytesOut"/>: every X.25 packet of every type as it crosses the wire, and their total size.
    /// Counted by the port itself, because only the port sees the control packets its own X.25 engine answers
    /// by itself. Data packets are included, so <c>AllPackets</c> is always at least <c>Packets</c>.</description></item>
    /// </list>
    /// </remarks>
    public sealed class PortCounters
    {
        private long _callsIn;
        private long _callsOut;
        private long _callsCleared;
        private long _packetsIn;
        private long _packetsOut;
        private long _bytesIn;
        private long _bytesOut;
        private long _allPacketsIn;
        private long _allPacketsOut;

        /// <summary>
        /// Number of calls received on this port.
        /// </summary>
        public uint CallsIn => (uint)Interlocked.Read(ref _callsIn);

        /// <summary>
        /// Number of calls placed out of this port.
        /// </summary>
        public uint CallsOut => (uint)Interlocked.Read(ref _callsOut);

        /// <summary>
        /// Number of calls cleared on this port.
        /// </summary>
        public uint CallsCleared => (uint)Interlocked.Read(ref _callsCleared);

        /// <summary>
        /// Number of data packets received on this port (see the remarks on the class).
        /// </summary>
        public uint PacketsIn => (uint)Interlocked.Read(ref _packetsIn);

        /// <summary>
        /// Number of data packets sent from this port (see the remarks on the class).
        /// </summary>
        public uint PacketsOut => (uint)Interlocked.Read(ref _packetsOut);

        /// <summary>
        /// Number of X.25 packets of every type received on this port, as they crossed the wire.
        /// </summary>
        public uint AllPacketsIn => (uint)Interlocked.Read(ref _allPacketsIn);

        /// <summary>
        /// Number of X.25 packets of every type sent from this port, as they crossed the wire.
        /// </summary>
        public uint AllPacketsOut => (uint)Interlocked.Read(ref _allPacketsOut);

        /// <summary>
        /// Total bytes of X.25 packets received on this port.
        /// </summary>
        /// <remarks>
        /// Counted at the X.25 packet layer, so any XOT or HDLC framing is not included. The C
        /// emulator counts the same way, so the two report identical totals for the same traffic.
        /// </remarks>
        public long BytesIn => Interlocked.Read(ref _bytesIn);

        /// <summary>
        /// Total bytes of X.25 packets sent from this port, counted as <see cref="BytesIn"/> is.
        /// </summary>
        public long BytesOut => Interlocked.Read(ref _bytesOut);

        /// <summary>
        /// Increments the calls-received counter.
        /// </summary>
        public void IncrementCallsIn()
        {
            Interlocked.Increment(ref _callsIn);
        }

        /// <summary>
        /// Increments the calls-placed counter.
        /// </summary>
        public void IncrementCallsOut()
        {
            Interlocked.Increment(ref _callsOut);
        }

        /// <summary>
        /// Increments the calls-cleared counter.
        /// </summary>
        public void IncrementCallsCleared()
        {
            Interlocked.Increment(ref _callsCleared);
        }

        /// <summary>
        /// Increments the data-packets-received counter.
        /// </summary>
        public void IncrementPacketsIn()
        {
            Interlocked.Increment(ref _packetsIn);
        }

        /// <summary>
        /// Increments the data-packets-sent counter.
        /// </summary>
        public void IncrementPacketsOut()
        {
            Interlocked.Increment(ref _packetsOut);
        }

        /// <summary>
        /// Increments the every-packet-received counter.
        /// </summary>
        public void IncrementAllPacketsIn()
        {
            Interlocked.Increment(ref _allPacketsIn);
        }

        /// <summary>
        /// Increments the every-packet-sent counter.
        /// </summary>
        public void IncrementAllPacketsOut()
        {
            Interlocked.Increment(ref _allPacketsOut);
        }

        /// <summary>
        /// Adds the size of a received packet to the bytes-received total.
        /// </summary>
        /// <param name="count">
        /// The packet length in bytes.
        /// </param>
        public void AddBytesIn(int count)
        {
            Interlocked.Add(ref _bytesIn, count);
        }

        /// <summary>
        /// Adds the size of a sent packet to the bytes-sent total.
        /// </summary>
        /// <param name="count">
        /// The packet length in bytes.
        /// </param>
        public void AddBytesOut(int count)
        {
            Interlocked.Add(ref _bytesOut, count);
        }
    }
}
