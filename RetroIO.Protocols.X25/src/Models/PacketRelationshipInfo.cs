using System;
using System.Collections.Generic;

using RetroIO.Link.Models;
namespace RetroIO.Protocols.X25.Models
{
    /// <summary>
    /// Simple metadata for packet relationships - tracks primary/derived status
    /// </summary>
    public class PacketRelationshipInfo
    {
        /// <summary>
        /// True if this is a primary/original packet (came from external source)
        /// False if this is derived/generated from another packet
        /// </summary>
        public bool IsPrimary { get; set; }

        /// <summary>
        /// If derived, the ID of the packet this was generated from
        /// </summary>
        public long? DerivedFromId { get; set; }

        /// <summary>
        /// List of packet IDs that were generated from this packet
        /// </summary>
        public List<long> GeneratedIds { get; set; } = new List<long>();

        /// <summary>
        /// Layer name for debugging (HDLC, LAPB, X25)
        /// </summary>
        public string Layer { get; set; } = "";

        /// <summary>
        /// Direction (TX/RX)
        /// </summary>
        public Direction Direction { get; set; }

        /// <summary>
        /// Creation timestamp
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        // --- Collapse Functionality Fields ---

        /// <summary>
        /// True if this is the last packet in a successful derivation chain
        /// False if more packets were derived from this packet
        /// Used for collapse functionality to show only terminal packets
        /// </summary>
        public bool IsTerminal { get; set; } = true;

        /// <summary>
        /// True if this packet had processing errors and should never be collapsed
        /// Error packets are always visible for analysis purposes
        /// </summary>
        public bool HasProcessingError { get; set; } = false;

        /// <summary>
        /// Description of processing error if HasProcessingError is true
        /// Examples: "FCS_Error", "LAPB_Parse_Error", "X25_Parse_Error"
        /// </summary>
        public string? ErrorReason { get; set; }

        /// <summary>
        /// ID of the primary (root) packet in this derivation chain
        /// All packets in the same chain share the same ChainRootId
        /// Used for grouping related packets for collapse functionality
        /// </summary>
        public long ChainRootId { get; set; }
    }
}