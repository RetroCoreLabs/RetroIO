//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Link.Models;
namespace RetroIO.Protocols.X25.Models
{
    /// <summary>
    /// Business layer iterator that handles two-line display logic for packet history.
    /// Yields primary packets normally and secondary packets with indentation for related items.
    /// </summary>
    public class PacketDisplayIterator
    {
        /// <summary>
        /// Generate display packets from history items with two-line display logic
        /// </summary>
        public static IEnumerable<DisplayPacket> GetDisplayPackets(IList<HistoryItem> historyItems)
        {
            if (historyItems == null)
                yield break;
                
            var sharedPacketIdCounter = 1;
            var processedItems = new HashSet<HistoryItem>();
            
            for (int i = 0; i < historyItems.Count; i++)
            {
                var currentItem = historyItems[i];
                
                // Skip if already processed as part of a relationship
                if (processedItems.Contains(currentItem))
                    continue;
                    
                // Check if this item has a relationship
                if (currentItem.RelatedItem != null && !processedItems.Contains(currentItem.RelatedItem))
                {
                    // Handle two-line display for related items
                    var primaryItem = currentItem.IsMainLine ? currentItem : currentItem.RelatedItem;
                    var secondaryItem = currentItem.IsMainLine ? currentItem.RelatedItem : currentItem;
                    
                    var sharedId = sharedPacketIdCounter++;
                    
                    // Yield primary line (not indented)
                    yield return new DisplayPacket(primaryItem, sharedId, false);
                    
                    // Yield secondary line (indented with "==>")
                    yield return new DisplayPacket(secondaryItem, sharedId, true);
                    
                    // Mark both items as processed
                    processedItems.Add(currentItem);
                    processedItems.Add(currentItem.RelatedItem);
                }
                else
                {
                    // Single item without relationship
                    yield return new DisplayPacket(currentItem, sharedPacketIdCounter++, false);
                    processedItems.Add(currentItem);
                }
            }
        }
    }
}