using System;

using RetroIO.Link.Models;
namespace RetroIO.Protocols.X25.Models
{
    /// <summary>
    /// HDLC-specific history item
    /// </summary>
    public class HdlcHistoryItem : HistoryItem
    {
        /// <summary>
        /// FCS validation result
        /// </summary>
        public bool FcsValid { get; set; }

        /// <summary>
        /// Frame ID (shared counter)
        /// </summary>
        public long FrameId { get; set; }

        /// <summary>
        /// Create HDLC history item
        /// </summary>
        public HdlcHistoryItem(Direction direction, byte[] payload, bool fcsValid, long frameId, string? description = null)
            : base(LayerType.HDLC, direction, payload, description ?? $"HDLC Frame FCS={fcsValid}")
        {
            FcsValid = fcsValid;
            FrameId = frameId;
        }

        /// <summary>
        /// Get summary string
        /// </summary>
        public override string GetSummary()
        {
            return $"HDLC[{Direction}] FrameId={FrameId} FCS={FcsValid} Len={Length}";
        }
    }
}
