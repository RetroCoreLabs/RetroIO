//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Sintran;
namespace RetroIO.Protocols.X25.Runtime.Models
{
    /// <summary>
    /// Simple display item - just the packet and reference to runtime for relationship lookup
    /// GUI uses the original dictionary via runtime.GetPacketRelationship()
    /// </summary>
    public class PacketDisplayItem
    {
        /// <summary>
        /// The actual history item (LAPB, X25, etc.)
        /// </summary>
        public HistoryItem Packet { get; set; }

        /// <summary>
        /// Reference to runtime for relationship lookup - GUI can call runtime.GetPacketRelationship()
        /// </summary>
        public ProtocolRuntime Runtime { get; set; }

        /// <summary>
        /// Creates a display item for a packet.
        /// </summary>
        /// <param name="packet">
        /// The history item to show.
        /// </param>
        /// <param name="runtime">
        /// The runtime used to look up packet relationships.
        /// </param>
        /// <exception cref="System.ArgumentNullException">
        /// Thrown when <paramref name="packet"/> or <paramref name="runtime"/> is null.
        /// </exception>
        public PacketDisplayItem(HistoryItem packet, ProtocolRuntime runtime)
        {
            Packet = packet ?? throw new ArgumentNullException(nameof(packet));
            Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        }

        /// <summary>
        /// Get display text with proper indentation using original dictionary
        /// </summary>
        public string GetDisplayText()
        {
            return GetDisplayText(null);
        }
        
        /// <summary>
        /// Get display text with enhanced visual indicators based on collapse state
        /// </summary>
        /// <param name="displayManager">
        /// Display manager for collapse context (null for basic display)
        /// </param>
        /// <returns>
        /// Formatted display text with visual indicators
        /// </returns>
        public string GetDisplayText(PacketDisplayManager? displayManager)
        {
            // Use original dictionary to get relationship info
            var relationship = Runtime.GetPacketRelationship(Packet.Id);
            
            var indent = "";
            var packetNumber = $"#{Packet.Id} ";
            
            if (relationship != null && !relationship.IsPrimary)
            {
                // Derived packet - add indentation, hide packet number
                indent = "    "; // Simple 4-space indentation
                packetNumber = "";
            }
            
            var baseText = $"{indent}{packetNumber}[{Packet.Direction}] {Packet.Layer} - {Packet.GetSummary()}";
            
            // Add visual indicators if display manager is provided
            if (displayManager != null && relationship != null)
            {
                var indicator = GetVisualIndicator(relationship, displayManager);
                if (!string.IsNullOrEmpty(indicator))
                {
                    baseText += $" {indicator}";
                }
            }
            
            return baseText;
        }
        
        /// <summary>
        /// Get visual indicator for packet based on relationship and collapse state
        /// </summary>
        /// <param name="relationship">
        /// Packet relationship info
        /// </param>
        /// <param name="displayManager">
        /// Display manager for collapse context
        /// </param>
        /// <returns>
        /// Visual indicator string or empty if none needed
        /// </returns>
        private string GetVisualIndicator(PacketRelationshipInfo relationship, PacketDisplayManager displayManager)
        {
            // Always show error indicator for error packets
            if (relationship.HasProcessingError)
            {
                return $"[ERROR: {relationship.ErrorReason}]";
            }
            
            // No collapse indicators if collapse is disabled
            if (!displayManager.GlobalCollapseEnabled)
            {
                return "";
            }
            
            // Check if this packet has derived packets (children)
            bool hasChildren = relationship.GeneratedIds.Count > 0;
            
            if (relationship.IsTerminal && hasChildren)
            {
                // Terminal packet with hidden derived packets (collapsed state)
                return "[▼]";
            }
            else if (relationship.IsPrimary && hasChildren && !relationship.IsTerminal)
            {
                // Primary packet that could be collapsed (expanded state)
                return "[▲]";
            }
            
            return "";
        }

        /// <summary>
        /// Check if this is a primary packet using original dictionary
        /// </summary>
        public bool IsPrimary()
        {
            var relationship = Runtime.GetPacketRelationship(Packet.Id);
            return relationship?.IsPrimary ?? true; // Default to primary if no relationship info
        }

        /// <summary>
        /// Check if this packet should be indented (is a derived packet)
        /// </summary>
        public bool IsIndented
        {
            get
            {
                var relationship = Runtime.GetPacketRelationship(Packet.Id);
                return relationship != null && !relationship.IsPrimary;
            }
        }

        /// <summary>
        /// Get primary packet ID using original dictionary
        /// </summary>
        public long? GetPrimaryPacketId()
        {
            var relationship = Runtime.GetPacketRelationship(Packet.Id);
            return relationship?.DerivedFromId;
        }

        /// <summary>
        /// Returns the display text of the packet, the same as GetDisplayText with no display manager.
        /// </summary>
        /// <returns>
        /// The display text.
        /// </returns>
        public override string ToString()
        {
            return GetDisplayText();
        }
    }
}