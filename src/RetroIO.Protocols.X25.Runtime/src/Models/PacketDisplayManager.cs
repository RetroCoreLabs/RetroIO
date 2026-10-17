//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Sintran;
namespace RetroIO.Protocols.X25.Runtime.Models
{
    /// <summary>
    /// Manages packet display filtering and collapse functionality
    /// Provides filtered views of packets based on collapse state
    /// </summary>
    public class PacketDisplayManager
    {
        private readonly ProtocolRuntime _runtime;
        private bool _globalCollapseEnabled;
        
        /// <summary>
        /// Gets or sets whether collapse mode is globally enabled
        /// When true, shows only terminal packets and error packets
        /// When false, shows all packets with existing indentation
        /// </summary>
        public bool GlobalCollapseEnabled 
        { 
            get => _globalCollapseEnabled; 
            set => _globalCollapseEnabled = value; 
        }
        
        /// <summary>
        /// Event fired when collapse state changes
        /// </summary>
        public event EventHandler<bool>? CollapseStateChanged;

        /// <summary>
        /// Initialize PacketDisplayManager with default collapsed state
        /// </summary>
        /// <param name="runtime">
        /// Protocol runtime for accessing packet data
        /// </param>
        public PacketDisplayManager(ProtocolRuntime runtime)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _globalCollapseEnabled = true; // Default collapsed as per requirements
        }

        /// <summary>
        /// Get display items based on current collapse state
        /// Uses unified packets if available, falls back to legacy packets
        /// </summary>
        /// <returns>
        /// Filtered list of packet display items
        /// </returns>
        public List<PacketDisplayItem> GetDisplayItems()
        {
            // Try to use unified packets first
            var unifiedItems = _runtime.GetUnifiedPacketsForDisplay();
            if (unifiedItems.Count > 0)
            {
                return GetUnifiedDisplayItems(unifiedItems);
            }
            
            // Fallback to legacy packets
            var allItems = _runtime.GetPacketsForDisplay();
            
            if (!_globalCollapseEnabled)
            {
                // Expanded view - show all packets with existing indentation
                return allItems;
            }
            
            // Collapsed view - filter for terminal and error packets only
            return FilterForCollapsedView(allItems);
        }

        /// <summary>
        /// Get display items for unified packet system
        /// </summary>
        /// <param name="unifiedItems">
        /// Unified packet display items
        /// </param>
        /// <returns>
        /// Filtered list based on collapse state
        /// </returns>
        private List<PacketDisplayItem> GetUnifiedDisplayItems(List<PacketDisplayItem> unifiedItems)
        {
            if (!_globalCollapseEnabled)
            {
                // Expanded view - show all packets
                return unifiedItems;
            }
            
            // Collapsed view - filter for terminal and error packets only
            return FilterUnifiedPacketsForCollapsedView(unifiedItems);
        }

        /// <summary>
        /// Filter unified packets to show only terminal and error packets
        /// </summary>
        /// <param name="allItems">
        /// All unified packet display items
        /// </param>
        /// <returns>
        /// Filtered list for collapsed view
        /// </returns>
        private List<PacketDisplayItem> FilterUnifiedPacketsForCollapsedView(List<PacketDisplayItem> allItems)
        {
            var filteredItems = new List<PacketDisplayItem>();
            
            foreach (var item in allItems)
            {
                if (item.Packet is UnifiedHistoryItem unifiedItem)
                {
                    // Always show error packets (never collapse)
                    if (unifiedItem.HasProcessingError)
                    {
                        filteredItems.Add(item);
                        continue;
                    }
                    
                    // Show only terminal packets for successful chains
                    if (unifiedItem.IsTerminal)
                    {
                        filteredItems.Add(item);
                        continue;
                    }
                    
                    // Skip non-terminal packets in collapsed view
                }
                else
                {
                    // Not a unified packet - include by default to maintain compatibility
                    filteredItems.Add(item);
                }
            }
            
            return filteredItems;
        }

        /// <summary>
        /// Filter packets to show only terminal packets and error packets
        /// </summary>
        /// <param name="allItems">
        /// All available packet display items
        /// </param>
        /// <returns>
        /// Filtered list showing only relevant packets for collapsed view
        /// </returns>
        public List<PacketDisplayItem> FilterForCollapsedView(List<PacketDisplayItem> allItems)
        {
            var filteredItems = new List<PacketDisplayItem>();
            
            foreach (var item in allItems)
            {
                var relationship = _runtime.GetPacketRelationship(item.Packet.Id);
                if (relationship == null)
                {
                    // No relationship info - include by default
                    filteredItems.Add(item);
                    continue;
                }
                
                // Always show error packets (never collapse)
                if (relationship.HasProcessingError)
                {
                    filteredItems.Add(item);
                    continue;
                }
                
                // Show only terminal packets for successful chains
                if (relationship.IsTerminal)
                {
                    filteredItems.Add(item);
                    continue;
                }
                
                // Skip non-terminal packets in collapsed view
            }
            
            return filteredItems;
        }

        /// <summary>
        /// Toggle the global collapse state
        /// </summary>
        /// <returns>
        /// New collapse state after toggle
        /// </returns>
        public bool ToggleGlobalCollapse()
        {
            _globalCollapseEnabled = !_globalCollapseEnabled;
            
            // Fire event to notify listeners of state change
            CollapseStateChanged?.Invoke(this, _globalCollapseEnabled);
            
            return _globalCollapseEnabled;
        }
        
        /// <summary>
        /// Set the global collapse state explicitly
        /// </summary>
        /// <param name="collapsed">
        /// True to enable collapse mode, false to disable
        /// </param>
        public void SetGlobalCollapse(bool collapsed)
        {
            if (_globalCollapseEnabled != collapsed)
            {
                _globalCollapseEnabled = collapsed;
                CollapseStateChanged?.Invoke(this, _globalCollapseEnabled);
            }
        }

        /// <summary>
        /// Get statistics about the current display state
        /// Works with both unified and legacy packet systems
        /// </summary>
        /// <returns>
        /// Display statistics
        /// </returns>
        public PacketDisplayStatistics GetDisplayStatistics()
        {
            // Try unified packets first
            var unifiedItems = _runtime.GetUnifiedPacketsForDisplay();
            if (unifiedItems.Count > 0)
            {
                return GetUnifiedDisplayStatistics(unifiedItems);
            }
            
            // Fallback to legacy packets
            var allItems = _runtime.GetPacketsForDisplay();
            var displayedItems = GetDisplayItems();
            
            var stats = new PacketDisplayStatistics
            {
                TotalPackets = allItems.Count,
                DisplayedPackets = displayedItems.Count,
                HiddenPackets = allItems.Count - displayedItems.Count,
                CollapseEnabled = _globalCollapseEnabled
            };
            
            // Count error packets and terminal packets
            foreach (var item in allItems)
            {
                var relationship = _runtime.GetPacketRelationship(item.Packet.Id);
                if (relationship?.HasProcessingError == true)
                {
                    stats.ErrorPackets++;
                }
                if (relationship?.IsTerminal == true)
                {
                    stats.TerminalPackets++;
                }
            }
            
            return stats;
        }

        /// <summary>
        /// Get statistics for unified packet system
        /// </summary>
        /// <param name="unifiedItems">
        /// Unified packet display items
        /// </param>
        /// <returns>
        /// Display statistics
        /// </returns>
        private PacketDisplayStatistics GetUnifiedDisplayStatistics(List<PacketDisplayItem> unifiedItems)
        {
            var displayedItems = GetUnifiedDisplayItems(unifiedItems);
            
            var stats = new PacketDisplayStatistics
            {
                TotalPackets = unifiedItems.Count,
                DisplayedPackets = displayedItems.Count,
                HiddenPackets = unifiedItems.Count - displayedItems.Count,
                CollapseEnabled = _globalCollapseEnabled
            };
            
            // Count error packets and terminal packets from unified items
            foreach (var item in unifiedItems)
            {
                if (item.Packet is UnifiedHistoryItem unifiedItem)
                {
                    if (unifiedItem.HasProcessingError)
                    {
                        stats.ErrorPackets++;
                    }
                    if (unifiedItem.IsTerminal)
                    {
                        stats.TerminalPackets++;
                    }
                }
            }
            
            return stats;
        }

        /// <summary>
        /// DEBUGGING: Get detailed relationship information for all packets
        /// This method is for debugging collapse issues
        /// </summary>
        public string GetDebugInfo()
        {
            var allItems = _runtime.GetPacketsForDisplay();
            var collapsedItems = FilterForCollapsedView(allItems);
            
            var debug = $"=== PACKET DISPLAY DEBUG INFO ===\n";
            debug += $"Collapse Mode: {(_globalCollapseEnabled ? "COLLAPSED" : "EXPANDED")}\n";
            debug += $"Total Packets: {allItems.Count}\n";
            debug += $"Displayed in Collapsed: {collapsedItems.Count}\n";
            debug += $"Would Hide: {allItems.Count - collapsedItems.Count}\n\n";
            
            debug += "=== ALL PACKETS ===\n";
            foreach (var item in allItems)
            {
                var relationship = _runtime.GetPacketRelationship(item.Packet.Id);
                var status = "UNKNOWN";
                
                if (relationship != null)
                {
                    var flags = new List<string>();
                    if (relationship.IsPrimary) flags.Add("PRIMARY");
                    if (relationship.IsTerminal) flags.Add("TERMINAL");
                    if (relationship.HasProcessingError) flags.Add($"ERROR:{relationship.ErrorReason}");
                    if (relationship.DerivedFromId.HasValue) flags.Add($"FROM:{relationship.DerivedFromId}");
                    status = string.Join("|", flags);
                }
                
                string shown;
                if (_globalCollapseEnabled)
                {
                    bool foundInCollapsed = false;
                    for (int i = 0; i < collapsedItems.Count; i++)
                    {
                        if (collapsedItems[i].Packet.Id == item.Packet.Id)
                        {
                            foundInCollapsed = true;
                            break;
                        }
                    }
                    shown = foundInCollapsed ? "SHOWN" : "HIDDEN";
                }
                else
                {
                    shown = "SHOWN";
                }
                    
                debug += $"  ID:{item.Packet.Id} {item.Packet.Layer} {item.Packet.Direction} [{status}] -> {shown}\n";
            }
            
            // Check if all packets are terminal (the likely issue)
            int terminalCount = 0;
            for (int i = 0; i < allItems.Count; i++)
            {
                var rel = _runtime.GetPacketRelationship(allItems[i].Packet.Id);
                if (rel?.IsTerminal == true)
                {
                    terminalCount++;
                }
            }
            
            debug += $"\n=== ANALYSIS ===\n";
            debug += $"Terminal packets: {terminalCount}/{allItems.Count}\n";
            
            if (terminalCount == allItems.Count && allItems.Count > 0)
            {
                debug += "**ISSUE FOUND**: All packets are terminal! No collapse filtering will occur.\n";
                debug += "This happens when packets are independent (no derivation chains).\n";
            }
            
            return debug;
        }

        /// <summary>
        /// TESTING: Create test chain relationships to demonstrate collapse functionality
        /// This method creates artificial relationships to test the collapse feature
        /// </summary>
        public void CreateTestCollapseChain()
        {
            var allItems = _runtime.GetPacketsForDisplay();
            if (allItems.Count < 2) return;

            // Take the first two packets and create a chain relationship for testing
            var primaryPacket = allItems[0];
            var derivedPacket = allItems[1];

            // Use reflection to access the private AddPacketWithChainTracking method
            var method = _runtime.GetType().GetMethod("AddPacketWithChainTracking", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            if (method != null)
            {
                // Re-create the relationships with a chain
                method.Invoke(_runtime, new object?[] { primaryPacket.Packet.Id, primaryPacket.Packet.Layer.ToString(), primaryPacket.Packet.Direction, null, null });
                method.Invoke(_runtime, new object?[] { derivedPacket.Packet.Id, derivedPacket.Packet.Layer.ToString(), derivedPacket.Packet.Direction, primaryPacket.Packet.Id, null });
            }
        }
    }

    /// <summary>
    /// Statistics about packet display state
    /// </summary>
    public class PacketDisplayStatistics
    {
        /// <summary>
        /// Total number of packets in history
        /// </summary>
        public int TotalPackets { get; set; }
        
        /// <summary>
        /// Number of packets currently displayed
        /// </summary>
        public int DisplayedPackets { get; set; }
        
        /// <summary>
        /// Number of packets hidden by collapse
        /// </summary>
        public int HiddenPackets { get; set; }
        
        /// <summary>
        /// Number of error packets (always shown)
        /// </summary>
        public int ErrorPackets { get; set; }
        
        /// <summary>
        /// Number of terminal packets
        /// </summary>
        public int TerminalPackets { get; set; }
        
        /// <summary>
        /// Whether collapse mode is currently enabled
        /// </summary>
        public bool CollapseEnabled { get; set; }
    }
}