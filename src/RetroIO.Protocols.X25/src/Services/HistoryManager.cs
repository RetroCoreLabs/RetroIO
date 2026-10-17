//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using RetroIO.Protocols.X25.Diagnostics;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;

namespace RetroIO.Protocols.X25.Services
{
    /// <summary>
    /// Manages history buffer for protocol packets with relationship tracking.
    /// Extracted from ProtocolRuntime to reduce God class complexity.
    /// </summary>
    public class HistoryManager
    {
        private readonly ILogger _logger;

        // History ring buffer
        private readonly Queue<HistoryItem> _historyBuffer = new();
        private readonly object _historyLock = new();
        private int _historyCapacity = 100;

        // Unified packet tracking system - single ID per packet with layer enrichment
        private readonly Dictionary<long, UnifiedHistoryItem> _unifiedPackets = new();
        private readonly object _unifiedPacketsLock = new();

        // Track most recent X.25 items for linking with LAPB encapsulation
        private readonly Dictionary<long, X25HistoryItem> _recentX25Items = new();
        private readonly Dictionary<string, LapbHistoryItem> _recentLapbFrames = new();
        private readonly HashSet<string> _pendingLapbFrameKeys = new();
        private readonly Dictionary<string, X25HistoryItem> _pendingX25LinksToLapb = new();
        private readonly object _x25LinkingLock = new();

        // Simple relationship tracking
        private LapbHistoryItem? _lastLapbIFrame = null;

        // Shared frame counter
        private long _sharedFrameId = 0;
        private readonly object _frameIdLock = new();

        /// <summary>
        /// Fired when a history item is added
        /// </summary>
        public event EventHandler<HistoryItem>? HistoryItemAdded;

        /// <summary>
        /// Fired when display should be updated
        /// </summary>
        public event Action<long, bool>? DisplayUpdateRequested;

        public HistoryManager(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        #region Public Properties

        /// <summary>
        /// Get current history capacity
        /// </summary>
        public int HistoryCapacity => _historyCapacity;

        /// <summary>
        /// Get current history count
        /// </summary>
        public int HistoryCount
        {
            get
            {
                lock (_historyLock)
                {
                    return _historyBuffer.Count;
                }
            }
        }

        /// <summary>
        /// Get/set the last LAPB I-frame for relationship tracking
        /// </summary>
        public LapbHistoryItem? LastLapbIFrame
        {
            get => _lastLapbIFrame;
            set => _lastLapbIFrame = value;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Set history capacity
        /// </summary>
        public void SetHistoryCapacity(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentException("History capacity must be positive", nameof(capacity));

            lock (_historyLock)
            {
                _historyCapacity = capacity;

                // Trim history if it exceeds new capacity
                while (_historyBuffer.Count > _historyCapacity)
                {
                    _historyBuffer.Dequeue();
                }

                _logger.LogInformation("[History] Set history capacity to {Capacity}", capacity);
            }
        }

        /// <summary>
        /// Get current history items
        /// </summary>
        public IReadOnlyList<HistoryItem> GetHistory()
        {
            lock (_historyLock)
            {
                var historyList = new List<HistoryItem>(_historyBuffer);
                return historyList.AsReadOnly();
            }
        }

        /// <summary>
        /// Export history to JSON file
        /// </summary>
        public void ExportHistoryAsJson(string path)
        {
            try
            {
                var history = GetHistory();
                var json = System.Text.Json.JsonSerializer.Serialize(history, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                });
                System.IO.File.WriteAllText(path, json);
                _logger.LogInformation("[History] Exported {Count} history items to {Path}", history.Count, path);
            }
            catch (Exception ex)
            {
                _logger.LogError("[History] Failed to export history to {Path}: {Message}", path, ex.Message);
            }
        }

        /// <summary>
        /// Add item to history ring buffer
        /// </summary>
        public void AddToHistory(HistoryItem item)
        {
            AddToHistory(item, null, RelationshipType.None);
        }

        /// <summary>
        /// Add item to history with relationship
        /// </summary>
        public void AddToHistory(HistoryItem item, HistoryItem? relatedItem, RelationshipType relationshipType)
        {
            if (item == null)
                return;

            lock (_historyLock)
            {
                // Set unique ID for the history item
                if (item is UnifiedHistoryItem unifiedItem)
                {
                    item.Id = unifiedItem.PacketInfo.GlobalPacketId;
                }
                else
                {
                    item.Id = GetNextFrameId();
                }

                // Set relationship if provided
                if (relatedItem != null)
                {
                    item.RelatedItem = relatedItem;
                    item.RelationshipType = relationshipType;

                    // Set reverse relationship
                    if (relationshipType == RelationshipType.Encapsulates)
                    {
                        relatedItem.RelatedItem = item;
                        relatedItem.RelationshipType = RelationshipType.EncapsulatedBy;
                    }
                    else if (relationshipType == RelationshipType.EncapsulatedBy)
                    {
                        relatedItem.RelatedItem = item;
                        relatedItem.RelationshipType = RelationshipType.Encapsulates;
                    }

                    // Apply two-line display business logic
                    ApplyTwoLineDisplayLogic(item, relatedItem);
                }

                // Add to ring buffer
                _historyBuffer.Enqueue(item);

                // Remove oldest items if capacity exceeded
                while (_historyBuffer.Count > _historyCapacity)
                {
                    _historyBuffer.Dequeue();
                }

                // Track X.25 items for potential linking with LAPB
                if (item is X25HistoryItem x25Item)
                {
                    TrackX25Item(x25Item);
                }

                // Fire event
                HistoryItemAdded?.Invoke(this, item);

                // Trigger GUI display update
                var relationship = GetPacketRelationship(item.Id);
                bool isNewDerivedPacket = relationship?.IsPrimary == false;
                DisplayUpdateRequested?.Invoke(item.Id, isNewDerivedPacket);

                // Log history item added
                _logger.LogHistoryItemAdded(item);
            }
        }

        /// <summary>
        /// Get next shared frame ID
        /// </summary>
        public long GetNextFrameId()
        {
            lock (_frameIdLock)
            {
                return ++_sharedFrameId;
            }
        }

        #endregion

        #region Relationship Tracking

        /// <summary>
        /// Track X.25 item for potential LAPB linking
        /// </summary>
        private void TrackX25Item(X25HistoryItem x25Item)
        {
            lock (_x25LinkingLock)
            {
                _recentX25Items[x25Item.Id] = x25Item;

                // Clean up old entries (keep last 10)
                while (_recentX25Items.Count > 10)
                {
                    long oldestKey = long.MaxValue;
                    foreach (var key in _recentX25Items.Keys)
                    {
                        if (key < oldestKey)
                            oldestKey = key;
                    }
                    if (oldestKey != long.MaxValue)
                        _recentX25Items.Remove(oldestKey);
                }
            }
        }

        /// <summary>
        /// Store LAPB frame for relationship linking
        /// </summary>
        public void StoreLapbFrameForLinking(string frameKey, LapbHistoryItem lapbItem)
        {
            lock (_x25LinkingLock)
            {
                _recentLapbFrames[frameKey] = lapbItem;

                // Clean up old entries (keep last 10)
                while (_recentLapbFrames.Count > 10)
                {
                    string? oldestKey = null;
                    foreach (var key in _recentLapbFrames.Keys)
                    {
                        oldestKey = key;
                        break;
                    }
                    if (oldestKey != null)
                        _recentLapbFrames.Remove(oldestKey);
                }
            }
        }

        /// <summary>
        /// Get recent LAPB frame by key
        /// </summary>
        public LapbHistoryItem? GetRecentLapbFrame(string frameKey)
        {
            lock (_x25LinkingLock)
            {
                if (_recentLapbFrames.TryGetValue(frameKey, out var lapbItem))
                    return lapbItem;
                return null;
            }
        }

        /// <summary>
        /// Pre-store LAPB frame key for relationship linking
        /// </summary>
        public void PreStoreLapbFrameKey(string frameKey)
        {
            lock (_x25LinkingLock)
            {
                _pendingLapbFrameKeys.Add(frameKey);
                _logger.LogDebug("PRE-STORED LAPB frame KEY for X.25 relationship linking, key: {Key}",
                    frameKey.Length > 16 ? frameKey.Substring(0, 16) + "..." : frameKey);

                // Clean up old entries (keep last 10)
                while (_pendingLapbFrameKeys.Count > 10)
                {
                    string? oldest = null;
                    foreach (var key in _pendingLapbFrameKeys)
                    {
                        oldest = key;
                        break;
                    }
                    if (oldest != null)
                        _pendingLapbFrameKeys.Remove(oldest);
                }
            }
        }

        /// <summary>
        /// Check if LAPB frame key is pending
        /// </summary>
        public bool IsPendingLapbFrameKey(string frameKey)
        {
            lock (_x25LinkingLock)
            {
                return _pendingLapbFrameKeys.Contains(frameKey);
            }
        }

        /// <summary>
        /// Store X.25 item pending LAPB link
        /// </summary>
        public void StorePendingX25Link(string frameKey, X25HistoryItem x25Item)
        {
            lock (_x25LinkingLock)
            {
                _pendingX25LinksToLapb[frameKey] = x25Item;
            }
        }

        /// <summary>
        /// Get and remove pending X.25 link
        /// </summary>
        public X25HistoryItem? GetAndRemovePendingX25Link(string frameKey)
        {
            lock (_x25LinkingLock)
            {
                if (_pendingX25LinksToLapb.TryGetValue(frameKey, out var x25Item))
                {
                    _pendingX25LinksToLapb.Remove(frameKey);
                    _pendingLapbFrameKeys.Remove(frameKey);
                    return x25Item;
                }
                return null;
            }
        }

        /// <summary>
        /// Get packet relationship information.
        /// Note: IsPrimary and DerivedFromId are defaults - full relationship tracking
        /// is maintained by ProtocolRuntime's PacketRelationshipInfo dictionary.
        /// </summary>
        public PacketRelationship? GetPacketRelationship(long packetId)
        {
            lock (_unifiedPacketsLock)
            {
                if (_unifiedPackets.TryGetValue(packetId, out var unifiedItem))
                {
                    return new PacketRelationship
                    {
                        PacketId = packetId,
                        IsPrimary = true, // Default - full tracking in ProtocolRuntime
                        IsTerminal = unifiedItem.PacketInfo.IsTerminal,
                        DerivedFromId = null // Default - full tracking in ProtocolRuntime
                    };
                }
            }
            return null;
        }

        /// <summary>
        /// Register unified packet
        /// </summary>
        public void RegisterUnifiedPacket(long packetId, UnifiedHistoryItem item)
        {
            lock (_unifiedPacketsLock)
            {
                _unifiedPackets[packetId] = item;
            }
        }

        /// <summary>
        /// Get unified packet by ID
        /// </summary>
        public UnifiedHistoryItem? GetUnifiedPacket(long packetId)
        {
            lock (_unifiedPacketsLock)
            {
                if (_unifiedPackets.TryGetValue(packetId, out var item))
                    return item;
                return null;
            }
        }

        #endregion

        #region Two-Line Display Logic

        /// <summary>
        /// Apply two-line display business logic to related history items
        /// </summary>
        private void ApplyTwoLineDisplayLogic(HistoryItem currentItem, HistoryItem relatedItem)
        {
            // Determine which item is primary (shown first) and which is secondary (indented)
            // Based on direction and layer hierarchy

            HistoryItem primaryItem;
            HistoryItem secondaryItem;

            // For RX: Lower layer (LAPB) is primary, higher layer (X.25) is secondary
            // For TX: Higher layer (X.25) is primary, lower layer (LAPB) is secondary
            if (currentItem.Direction == Direction.RX)
            {
                // RX: LAPB received first, then X.25 extracted
                if (currentItem is LapbHistoryItem)
                {
                    primaryItem = currentItem;
                    secondaryItem = relatedItem;
                }
                else
                {
                    primaryItem = relatedItem;
                    secondaryItem = currentItem;
                }
            }
            else
            {
                // TX: X.25 sent first, then encapsulated in LAPB
                if (currentItem is X25HistoryItem)
                {
                    primaryItem = currentItem;
                    secondaryItem = relatedItem;
                }
                else
                {
                    primaryItem = relatedItem;
                    secondaryItem = currentItem;
                }
            }

            // Set display flags
            primaryItem.IsMainLine = true;
            primaryItem.IsSecondaryLine = false;

            secondaryItem.IsMainLine = false;
            secondaryItem.IsSecondaryLine = true;
        }

        #endregion

        #region Iteration Support

        /// <summary>
        /// Iterate over history buffer (for GUI display)
        /// </summary>
        public void IterateHistory(Action<HistoryItem> action)
        {
            lock (_historyLock)
            {
                foreach (var packet in _historyBuffer)
                {
                    action(packet);
                }
            }
        }

        #endregion
    }

    /// <summary>
    /// Packet relationship information for GUI display
    /// </summary>
    public class PacketRelationship
    {
        public long PacketId { get; set; }
        public bool IsPrimary { get; set; }
        public bool IsTerminal { get; set; }
        public long? DerivedFromId { get; set; }
    }
}
