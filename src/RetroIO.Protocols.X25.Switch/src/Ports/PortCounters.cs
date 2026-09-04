using System.Threading;

namespace RetroIO.Protocols.X25.Switch.Ports
{
    /// <summary>
    /// Per-port lifetime counters, safe to read from other threads.
    /// </summary>
    /// <remarks>
    /// Counters are maintained with interlocked operations because a port's I/O runs on its own thread while
    /// management surfaces read the totals concurrently.
    /// </remarks>
    public sealed class PortCounters
    {
        private long _callsIn;
        private long _callsOut;
        private long _callsCleared;
        private long _packetsIn;
        private long _packetsOut;

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
        /// Number of packets received on this port.
        /// </summary>
        public uint PacketsIn => (uint)Interlocked.Read(ref _packetsIn);

        /// <summary>
        /// Number of packets sent from this port.
        /// </summary>
        public uint PacketsOut => (uint)Interlocked.Read(ref _packetsOut);

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
        /// Increments the packets-received counter.
        /// </summary>
        public void IncrementPacketsIn()
        {
            Interlocked.Increment(ref _packetsIn);
        }

        /// <summary>
        /// Increments the packets-sent counter.
        /// </summary>
        public void IncrementPacketsOut()
        {
            Interlocked.Increment(ref _packetsOut);
        }
    }
}
