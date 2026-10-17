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
    /// Unified history item that extends HistoryItem with unified packet tracking
    /// Uses a single packet ID that accumulates layer information as packet traverses protocol stack
    /// </summary>
    public class UnifiedHistoryItem : HistoryItem
    {
        /// <summary>
        /// Unified packet information that evolves as packet moves through layers
        /// </summary>
        public UnifiedPacketInfo PacketInfo { get; set; }
        
        /// <summary>
        /// When this history item was last updated
        /// </summary>
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
        
        /// <summary>
        /// Create a new unified history item with packet information
        /// </summary>
        /// <param name="packetInfo">
        /// The unified packet information
        /// </param>
        public UnifiedHistoryItem(UnifiedPacketInfo packetInfo) 
            : base(packetInfo.HighestLayer, packetInfo.Direction, Array.Empty<byte>(), packetInfo.GetSummary())
        {
            PacketInfo = packetInfo ?? throw new ArgumentNullException(nameof(packetInfo));
            Id = packetInfo.GlobalPacketId;
            Timestamp = packetInfo.Created;
            LastUpdated = DateTime.UtcNow;
        }
        
        /// <summary>
        /// Update this history item with new layer information
        /// This is called as the packet moves through different protocol layers
        /// </summary>
        /// <param name="layerType">
        /// The layer being updated
        /// </param>
        public void UpdateLayer(LayerType layerType)
        {
            // Update highest layer reached
            if (layerType > PacketInfo.HighestLayer)
            {
                PacketInfo.HighestLayer = layerType;
            }
            
            // Add to layers visited if not already present
            if (!PacketInfo.LayersVisited.Contains(layerType))
            {
                PacketInfo.LayersVisited.Add(layerType);
            }
            
            // Update base HistoryItem properties to reflect new layer
            Layer = PacketInfo.HighestLayer;
            Description = PacketInfo.GetSummary();
            
            LastUpdated = DateTime.UtcNow;
        }
        
        /// <summary>
        /// Mark this packet as having a processing error
        /// </summary>
        /// <param name="errorReason">
        /// Reason for the error
        /// </param>
        /// <param name="errorLayer">
        /// Layer where error occurred
        /// </param>
        public void MarkError(string errorReason, LayerType errorLayer)
        {
            PacketInfo.HasProcessingError = true;
            PacketInfo.ErrorReason = errorReason;
            PacketInfo.ErrorLayer = errorLayer;
            
            // Update base HistoryItem description to reflect error
            Description = PacketInfo.GetSummary();
            
            LastUpdated = DateTime.UtcNow;
        }
        
        /// <summary>
        /// Get the display summary for this packet (for the packet list view)
        /// </summary>
        /// <returns>
        /// Summary string for display in list
        /// </returns>
        public string GetDisplaySummary()
        {
            return PacketInfo.GetSummary();
        }
        
        /// <summary>
        /// Get detailed information for this packet (for the F2 detail window)
        /// </summary>
        /// <returns>
        /// Detailed multi-line information showing all layers
        /// </returns>
        public string GetDetailedDisplay()
        {
            return PacketInfo.GetDetailedInfo();
        }
        
        /// <summary>
        /// Whether this packet is terminal (for collapse functionality)
        /// </summary>
        public bool IsTerminal => PacketInfo.IsTerminal;
        
        /// <summary>
        /// Whether this packet has a processing error (never collapse error packets)
        /// </summary>
        public bool HasProcessingError => PacketInfo.HasProcessingError;
        
        /// <summary>
        /// Override GetSummary to use UnifiedPacketInfo summary
        /// </summary>
        /// <returns>
        /// Summary from unified packet info
        /// </returns>
        public override string GetSummary()
        {
            return PacketInfo.GetSummary();
        }
    }
}