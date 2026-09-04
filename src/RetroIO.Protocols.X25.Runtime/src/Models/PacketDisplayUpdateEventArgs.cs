using System;
using System.Collections.Generic;

using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Sintran;
namespace RetroIO.Protocols.X25.Runtime.Models
{
    /// <summary>
    /// Event args for GUI packet display updates
    /// Supports real-time updates and relationship visibility
    /// </summary>
    public class PacketDisplayUpdateEventArgs : EventArgs
    {
        /// <summary>
        /// ID of the newly added packet
        /// </summary>
        public long NewPacketId { get; set; }

        /// <summary>
        /// True if the new packet is a derived packet that should appear as Line 2
        /// GUI can use this to scroll to keep relationships visible
        /// </summary>
        public bool IsNewDerivedPacket { get; set; }

        /// <summary>
        /// Complete list of all packets formatted for display with indentation
        /// GUI can replace entire display or update incrementally
        /// </summary>
        public List<PacketDisplayItem> AllDisplayItems { get; set; } = new List<PacketDisplayItem>();
        
        /// <summary>
        /// True if the new packet is a terminal packet (last in successful chain)
        /// Used for collapse functionality - terminal packets are shown in collapsed view
        /// </summary>
        public bool IsTerminalPacket { get; set; }
        
        /// <summary>
        /// True if the new packet has processing errors
        /// Error packets are always visible regardless of collapse state
        /// </summary>
        public bool HasError { get; set; }
        
        /// <summary>
        /// Error reason if HasError is true
        /// </summary>
        public string? ErrorReason { get; set; }

        /// <summary>
        /// Find the display item for the new packet
        /// </summary>
        public PacketDisplayItem? GetNewPacketDisplayItem()
        {
            return AllDisplayItems.FirstOrDefault(item => item.Packet.Id == NewPacketId);
        }

        /// <summary>
        /// Get the primary packet if this is a derived packet update
        /// </summary>
        public PacketDisplayItem? GetPrimaryPacketForNewDerived()
        {
            if (!IsNewDerivedPacket) return null;
            
            var newItem = GetNewPacketDisplayItem();
            var primaryId = newItem?.GetPrimaryPacketId();
            if (primaryId == null) return null;
            
            return AllDisplayItems.FirstOrDefault(item => item.Packet.Id == primaryId);
        }
    }
}