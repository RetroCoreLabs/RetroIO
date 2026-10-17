//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Link.Models;
namespace RetroIO.Protocols.X25.Models
{
    /// <summary>
    /// Represents a packet prepared for display in GUI, including two-line display logic
    /// </summary>
    public class DisplayPacket
    {
        /// <summary>
        /// Shared packet ID for related items (same for primary and secondary lines)
        /// </summary>
        public int SharedPacketId { get; set; }
        
        /// <summary>
        /// True if this line should be indented (secondary line with "==>" prefix)
        /// </summary>
        public bool IsIndented { get; set; }
        
        /// <summary>
        /// Original history item containing all protocol data
        /// </summary>
        public HistoryItem HistoryItem { get; set; }
        
        /// <summary>
        /// Timestamp from history item
        /// </summary>
        public DateTime Timestamp => HistoryItem.Timestamp;
        
        /// <summary>
        /// Direction from history item
        /// </summary>
        public Direction Direction => HistoryItem.Direction;
        
        /// <summary>
        /// Layer type from history item
        /// </summary>
        public LayerType Layer => HistoryItem.Layer;
        
        /// <summary>
        /// Description from history item
        /// </summary>
        public string Description => HistoryItem.Description;
        
        /// <summary>
        /// Data length from history item
        /// </summary>
        public int Length => HistoryItem.Length;
        
        /// <summary>
        /// Create display packet
        /// </summary>
        public DisplayPacket(HistoryItem historyItem, int sharedPacketId, bool isIndented)
        {
            HistoryItem = historyItem ?? throw new ArgumentNullException(nameof(historyItem));
            SharedPacketId = sharedPacketId;
            IsIndented = isIndented;
        }
    }
}