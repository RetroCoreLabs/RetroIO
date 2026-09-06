using System;

using RetroIO.Link.Models;
namespace RetroIO.Protocols.X25.Models
{
using X25 = RetroIO.Protocols.X25.DeleteMe.X25;   // TODO C3: X25.PacketType must move to a live type before DeleteMe goes

    /// <summary>
    /// Snapshot of runtime statistics
    /// </summary>
    public class StatisticsSnapshot
    {
        /// <summary>
        /// Timestamp when snapshot was taken
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Runtime uptime
        /// </summary>
        public TimeSpan Uptime { get; set; }

        /// <summary>
        /// HDLC statistics
        /// </summary>
        public HdlcStatistics HDLC { get; set; } = new();

        /// <summary>
        /// LAPB statistics
        /// </summary>
        public LapbStatistics LAPB { get; set; } = new();

        /// <summary>
        /// X.25 statistics
        /// </summary>
        public X25Statistics X25 { get; set; } = new();

        /// <summary>
        /// XOT (X.25 Over TCP) statistics
        /// </summary>
        public XOTStatistics XOT { get; set; } = new();

        /// <summary>
        /// Runtime statistics
        /// </summary>
        public RuntimeStatistics Runtime { get; set; } = new();

        /// <summary>
        /// Create statistics snapshot
        /// </summary>
        public StatisticsSnapshot()
        {
        }

        /// <summary>
        /// Get summary string
        /// </summary>
        public string GetSummary()
        {
            return $"Runtime Stats: Uptime={Uptime} HDLC={HDLC.GetSummary()} LAPB={LAPB.GetSummary()} X25={X25.GetSummary()} XOT={XOT.GetSummary()}";
        }

        /// <summary>
        /// Create a deep copy of this snapshot
        /// </summary>
        public StatisticsSnapshot Clone()
        {
            return new StatisticsSnapshot
            {
                Timestamp = this.Timestamp,
                Uptime = this.Uptime,
                HDLC = new HdlcStatistics
                {
                    FramesReceived = this.HDLC.FramesReceived,
                    FramesTransmitted = this.HDLC.FramesTransmitted,
                    FramesValidFcs = this.HDLC.FramesValidFcs,
                    FramesInvalidFcs = this.HDLC.FramesInvalidFcs,
                    BytesReceived = this.HDLC.BytesReceived,
                    BytesTransmitted = this.HDLC.BytesTransmitted,
                    BadFramesReceived = this.HDLC.BadFramesReceived,
                    BadFramesTransmitted = this.HDLC.BadFramesTransmitted
                },
                LAPB = new LapbStatistics
                {
                    FramesReceived = this.LAPB.FramesReceived,
                    FramesTransmitted = this.LAPB.FramesTransmitted,
                    IFramesReceived = this.LAPB.IFramesReceived,
                    IFramesTransmitted = this.LAPB.IFramesTransmitted,
                    SFramesReceived = this.LAPB.SFramesReceived,
                    SFramesTransmitted = this.LAPB.SFramesTransmitted,
                    UFramesReceived = this.LAPB.UFramesReceived,
                    UFramesTransmitted = this.LAPB.UFramesTransmitted,
                    Retransmissions = this.LAPB.Retransmissions,
                    Timeouts = this.LAPB.Timeouts,
                    CurrentState = this.LAPB.CurrentState,
                    BadFramesReceived = this.LAPB.BadFramesReceived,
                    BadFramesTransmitted = this.LAPB.BadFramesTransmitted
                },
                X25 = new X25Statistics
                {
                    PacketsReceived = this.X25.PacketsReceived,
                    PacketsTransmitted = this.X25.PacketsTransmitted,
                    DataPacketsReceived = this.X25.DataPacketsReceived,
                    DataPacketsTransmitted = this.X25.DataPacketsTransmitted,
                    CallRequestsReceived = this.X25.CallRequestsReceived,
                    CallRequestsTransmitted = this.X25.CallRequestsTransmitted,
                    ActiveVirtualCircuits = this.X25.ActiveVirtualCircuits,
                    TotalVirtualCircuits = this.X25.TotalVirtualCircuits,
                    CurrentState = this.X25.CurrentState,
                    BadPacketsReceived = this.X25.BadPacketsReceived,
                    BadPacketsTransmitted = this.X25.BadPacketsTransmitted
                },
                XOT = new XOTStatistics
                {
                    PacketsReceived = this.XOT.PacketsReceived,
                    PacketsTransmitted = this.XOT.PacketsTransmitted,
                    BytesReceived = this.XOT.BytesReceived,
                    BytesTransmitted = this.XOT.BytesTransmitted,
                    TcpConnected = this.XOT.TcpConnected,
                    BadPacketsReceived = this.XOT.BadPacketsReceived,
                    BadPacketsTransmitted = this.XOT.BadPacketsTransmitted
                },
                Runtime = new RuntimeStatistics
                {
                    TotalHistoryItems = this.Runtime.TotalHistoryItems,
                    HistoryCapacity = this.Runtime.HistoryCapacity,
                    ActiveTimers = this.Runtime.ActiveTimers,
                    RegisteredModules = this.Runtime.RegisteredModules,
                    ActiveLcnBindings = this.Runtime.ActiveLcnBindings
                }
            };
        }
    }

    /// <summary>
    /// HDLC layer statistics
    /// </summary>
    public class HdlcStatistics
    {
        /// <summary>
        /// Total frames received
        /// </summary>
        public long FramesReceived { get; set; }

        /// <summary>
        /// Total frames transmitted
        /// </summary>
        public long FramesTransmitted { get; set; }

        /// <summary>
        /// Frames with valid FCS
        /// </summary>
        public long FramesValidFcs { get; set; }

        /// <summary>
        /// Frames with invalid FCS
        /// </summary>
        public long FramesInvalidFcs { get; set; }

        /// <summary>
        /// Total bytes received
        /// </summary>
        public long BytesReceived { get; set; }

        /// <summary>
        /// Total bytes transmitted
        /// </summary>
        public long BytesTransmitted { get; set; }

        /// <summary>
        /// Bad/erroneous frames received
        /// </summary>
        public long BadFramesReceived { get; set; }

        /// <summary>
        /// Bad/erroneous frames transmitted
        /// </summary>
        public long BadFramesTransmitted { get; set; }

        /// <summary>
        /// Get summary string
        /// </summary>
        public string GetSummary()
        {
            return $"RX:{FramesReceived}({BytesReceived}) TX:{FramesTransmitted}({BytesTransmitted}) Valid:{FramesValidFcs} Invalid:{FramesInvalidFcs} BadRX:{BadFramesReceived} BadTX:{BadFramesTransmitted}";
        }
    }

    /// <summary>
    /// LAPB layer statistics
    /// </summary>
    public class LapbStatistics
    {
        /// <summary>
        /// Total frames received
        /// </summary>
        public long FramesReceived { get; set; }

        /// <summary>
        /// Total frames transmitted
        /// </summary>
        public long FramesTransmitted { get; set; }

        /// <summary>
        /// I-frames received
        /// </summary>
        public long IFramesReceived { get; set; }

        /// <summary>
        /// I-frames transmitted
        /// </summary>
        public long IFramesTransmitted { get; set; }

        /// <summary>
        /// S-frames received
        /// </summary>
        public long SFramesReceived { get; set; }

        /// <summary>
        /// S-frames transmitted
        /// </summary>
        public long SFramesTransmitted { get; set; }

        /// <summary>
        /// U-frames received
        /// </summary>
        public long UFramesReceived { get; set; }

        /// <summary>
        /// U-frames transmitted
        /// </summary>
        public long UFramesTransmitted { get; set; }

        /// <summary>
        /// Retransmissions
        /// </summary>
        public long Retransmissions { get; set; }

        /// <summary>
        /// Timeouts
        /// </summary>
        public long Timeouts { get; set; }

        /// <summary>
        /// Current state
        /// </summary>
        public string CurrentState { get; set; } = "Unknown";

        /// <summary>
        /// Bad/erroneous frames received
        /// </summary>
        public long BadFramesReceived { get; set; }

        /// <summary>
        /// Bad/erroneous frames transmitted
        /// </summary>
        public long BadFramesTransmitted { get; set; }

        /// <summary>
        /// Get summary string
        /// </summary>
        public string GetSummary()
        {
            return $"RX:{FramesReceived}(I:{IFramesReceived} S:{SFramesReceived} U:{UFramesReceived}) TX:{FramesTransmitted}(I:{IFramesTransmitted} S:{SFramesTransmitted} U:{UFramesTransmitted}) Retx:{Retransmissions} Timeouts:{Timeouts} State:{CurrentState} BadRX:{BadFramesReceived} BadTX:{BadFramesTransmitted}";
        }
    }

    /// <summary>
    /// X.25 layer statistics
    /// </summary>
    public class X25Statistics
    {
        /// <summary>
        /// Total packets received
        /// </summary>
        public long PacketsReceived { get; set; }

        /// <summary>
        /// Total packets transmitted
        /// </summary>
        public long PacketsTransmitted { get; set; }

        /// <summary>
        /// Data packets received
        /// </summary>
        public long DataPacketsReceived { get; set; }

        /// <summary>
        /// Data packets transmitted
        /// </summary>
        public long DataPacketsTransmitted { get; set; }

        /// <summary>
        /// Call requests received
        /// </summary>
        public long CallRequestsReceived { get; set; }

        /// <summary>
        /// Call requests transmitted
        /// </summary>
        public long CallRequestsTransmitted { get; set; }

        /// <summary>
        /// Active virtual circuits
        /// </summary>
        public int ActiveVirtualCircuits { get; set; }

        /// <summary>
        /// Total virtual circuits created
        /// </summary>
        public long TotalVirtualCircuits { get; set; }

        /// <summary>
        /// Current state
        /// </summary>
        public string CurrentState { get; set; } = "Unknown";

        /// <summary>
        /// Bad/erroneous packets received
        /// </summary>
        public long BadPacketsReceived { get; set; }

        /// <summary>
        /// Bad/erroneous packets transmitted
        /// </summary>
        public long BadPacketsTransmitted { get; set; }

        /// <summary>
        /// Get summary string
        /// </summary>
        public string GetSummary()
        {
            return $"RX:{PacketsReceived}(Data:{DataPacketsReceived} Call:{CallRequestsReceived}) TX:{PacketsTransmitted}(Data:{DataPacketsTransmitted} Call:{CallRequestsTransmitted}) ActiveVC:{ActiveVirtualCircuits} TotalVC:{TotalVirtualCircuits} State:{CurrentState} BadRX:{BadPacketsReceived} BadTX:{BadPacketsTransmitted}";
        }
    }

    /// <summary>
    /// XOT (X.25 Over TCP) layer statistics
    /// </summary>
    public class XOTStatistics
    {
        /// <summary>
        /// Total packets received
        /// </summary>
        public long PacketsReceived { get; set; }

        /// <summary>
        /// Total packets transmitted
        /// </summary>
        public long PacketsTransmitted { get; set; }

        /// <summary>
        /// Total bytes received
        /// </summary>
        public long BytesReceived { get; set; }

        /// <summary>
        /// Total bytes transmitted
        /// </summary>
        public long BytesTransmitted { get; set; }

        /// <summary>
        /// TCP connection status
        /// </summary>
        public bool TcpConnected { get; set; }

        /// <summary>
        /// Bad/erroneous packets received
        /// </summary>
        public long BadPacketsReceived { get; set; }

        /// <summary>
        /// Bad/erroneous packets transmitted
        /// </summary>
        public long BadPacketsTransmitted { get; set; }

        /// <summary>
        /// Get summary string
        /// </summary>
        public string GetSummary()
        {
            return $"RX:{PacketsReceived}({BytesReceived}) TX:{PacketsTransmitted}({BytesTransmitted}) TCP:{TcpConnected} BadRX:{BadPacketsReceived} BadTX:{BadPacketsTransmitted}";
        }
    }

    /// <summary>
    /// Runtime statistics
    /// </summary>
    public class RuntimeStatistics
    {
        /// <summary>
        /// Total history items
        /// </summary>
        public long TotalHistoryItems { get; set; }

        /// <summary>
        /// Current history capacity
        /// </summary>
        public int HistoryCapacity { get; set; }

        /// <summary>
        /// Active timers
        /// </summary>
        public int ActiveTimers { get; set; }

        /// <summary>
        /// Registered modules
        /// </summary>
        public int RegisteredModules { get; set; }

        /// <summary>
        /// Active LCN bindings
        /// </summary>
        public int ActiveLcnBindings { get; set; }

        /// <summary>
        /// Get summary string
        /// </summary>
        public string GetSummary()
        {
            return $"History:{TotalHistoryItems}/{HistoryCapacity} Timers:{ActiveTimers} Modules:{RegisteredModules} Bindings:{ActiveLcnBindings}";
        }
    }
}
