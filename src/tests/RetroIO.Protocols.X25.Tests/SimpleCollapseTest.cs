using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RetroIO.Protocols.X25;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Runtime.Models;
using RetroIO.Protocols.X25.Sintran;
using Xunit.Abstractions;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Simple test to diagnose collapse functionality issues
    /// </summary>
    [TestFixture]
    public class SimpleCollapseTest : TestBase
    {
        private ProtocolRuntime _runtime = null!;
        private PacketDisplayManager _displayManager = null!;
        private ILogger _logger = null!;

        [SetUp]
        public new void Setup()
        {
            var config = RuntimeConfig.CreateDefault();
            _logger = new TestLogger(new TestOutputHelper(), "SimpleCollapseTest");
            _runtime = new ProtocolRuntime(_logger, config);
            _displayManager = new PacketDisplayManager(_runtime);
        }

        [TearDown]
        public void Cleanup()
        {
            _runtime?.Dispose();
        }

        [Test]
        public void DiagnoseCollapseIssue()
        {
            // Create some history items directly using runtime's existing history
            _logger.LogInformation("=== DIAGNOSING COLLAPSE ISSUE ===");

            // Get all packets from runtime (if any)
            var allPackets = _runtime.GetPacketsForDisplay();
            _logger.LogInformation("Runtime has {Count} packets in history", allPackets.Count);

            // Test display manager basic functionality
            _logger.LogInformation("Display manager default collapsed state: {Collapsed}", _displayManager.GlobalCollapseEnabled);

            // Test with some mock data if no real packets exist
            if (allPackets.Count == 0)
            {
                _logger.LogInformation("No packets in runtime, testing with mock data");
                
                // Create mock history items by manually creating them
                var mockItems = new List<PacketDisplayItem>();
                
                // Since we can't easily create HistoryItem objects, let's test the display manager methods directly
                var stats = _displayManager.GetDisplayStatistics();
                _logger.LogInformation("Display stats - Total:{Total} Displayed:{Displayed} Hidden:{Hidden} ErrorPackets:{ErrorPackets} TerminalPackets:{TerminalPackets}", 
                    stats.TotalPackets, stats.DisplayedPackets, stats.HiddenPackets, stats.ErrorPackets, stats.TerminalPackets);
            }
            else
            {
                // Test with actual packets
                _logger.LogInformation("Testing with {Count} actual packets", allPackets.Count);
                
                // Test collapsed vs expanded
                _displayManager.SetGlobalCollapse(true);
                var collapsedItems = _displayManager.GetDisplayItems();
                _logger.LogInformation("Collapsed view: {Count} items", collapsedItems.Count);
                
                _displayManager.SetGlobalCollapse(false);
                var expandedItems = _displayManager.GetDisplayItems();
                _logger.LogInformation("Expanded view: {Count} items", expandedItems.Count);
                
                // Log details of each item
                _logger.LogInformation("=== COLLAPSED VIEW ITEMS ===");
                foreach (var item in collapsedItems)
                {
                    var relationship = _runtime.GetPacketRelationship(item.Packet.Id);
                    _logger.LogInformation("ID:{Id} Layer:{Layer} Direction:{Direction} IsTerminal:{IsTerminal} HasError:{HasError}", 
                        item.Packet.Id, item.Packet.Layer, item.Packet.Direction, 
                        relationship?.IsTerminal, relationship?.HasProcessingError);
                }
                
                _logger.LogInformation("=== EXPANDED VIEW ITEMS ===");
                foreach (var item in expandedItems)
                {
                    var relationship = _runtime.GetPacketRelationship(item.Packet.Id);
                    _logger.LogInformation("ID:{Id} Layer:{Layer} Direction:{Direction} IsTerminal:{IsTerminal} HasError:{HasError}", 
                        item.Packet.Id, item.Packet.Layer, item.Packet.Direction, 
                        relationship?.IsTerminal, relationship?.HasProcessingError);
                }

                // Check if they're the same (the bug!)
                if (collapsedItems.Count == expandedItems.Count)
                {
                    _logger.LogWarning("BUG DETECTED: Collapsed and expanded views show the same number of items!");
                    
                    // Check if all packet IDs are the same
                    // Build ID lists manually and sort them
                    var collapsedIds = new List<long>();
                    foreach (var item in collapsedItems)
                    {
                        collapsedIds.Add(item.Packet.Id);
                    }
                    // Sort collapsedIds manually
                    for (int i = 0; i < collapsedIds.Count - 1; i++)
                    {
                        for (int j = i + 1; j < collapsedIds.Count; j++)
                        {
                            if (collapsedIds[i] > collapsedIds[j])
                            {
                                var temp = collapsedIds[i];
                                collapsedIds[i] = collapsedIds[j];
                                collapsedIds[j] = temp;
                            }
                        }
                    }
                    
                    var expandedIds = new List<long>();
                    foreach (var item in expandedItems)
                    {
                        expandedIds.Add(item.Packet.Id);
                    }
                    // Sort expandedIds manually
                    for (int i = 0; i < expandedIds.Count - 1; i++)
                    {
                        for (int j = i + 1; j < expandedIds.Count; j++)
                        {
                            if (expandedIds[i] > expandedIds[j])
                            {
                                var temp = expandedIds[i];
                                expandedIds[i] = expandedIds[j];
                                expandedIds[j] = temp;
                            }
                        }
                    }
                    
                    // Check if lists are equal manually
                    bool listsEqual = collapsedIds.Count == expandedIds.Count;
                    if (listsEqual)
                    {
                        for (int i = 0; i < collapsedIds.Count; i++)
                        {
                            if (collapsedIds[i] != expandedIds[i])
                            {
                                listsEqual = false;
                                break;
                            }
                        }
                    }
                    
                    if (listsEqual)
                    {
                        _logger.LogError("CONFIRMED BUG: Same packet IDs in both views");
                        
                        // Analyze why - are all packets terminal?
                        var allTerminal = true;
                        var allPrimary = true;
                        foreach (var item in expandedItems)
                        {
                            var relationship = _runtime.GetPacketRelationship(item.Packet.Id);
                            if (relationship != null)
                            {
                                if (!relationship.IsTerminal) allTerminal = false;
                                if (!relationship.IsPrimary) allPrimary = false;
                            }
                        }
                        
                        _logger.LogInformation("All packets terminal: {AllTerminal}, All packets primary: {AllPrimary}", allTerminal, allPrimary);
                        
                        if (allTerminal && allPrimary)
                        {
                            _logger.LogInformation("ROOT CAUSE: All packets are both terminal AND primary - no collapse relationships exist");
                        }
                    }
                }
            }

            // Test toggle functionality
            var initialState = _displayManager.GlobalCollapseEnabled;
            var toggledState = _displayManager.ToggleGlobalCollapse();
            _logger.LogInformation("Toggle test: {Initial} -> {Toggled}", initialState, toggledState);
            
            Assert.That(toggledState, Is.Not.EqualTo(initialState), "Toggle should change state");
        }

        [Test]
        public void TestPacketRelationshipStorage()
        {
            // Test if packet relationships are being stored correctly
            _logger.LogInformation("=== TESTING PACKET RELATIONSHIP STORAGE ===");
            
            // Try to manually add some relationships using reflection to access private method
            var method = _runtime.GetType().GetMethod("AddPacketWithChainTracking", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            if (method != null)
            {
                _logger.LogInformation("Found AddPacketWithChainTracking method, testing...");
                
                // Add a primary packet
                method.Invoke(_runtime, new object?[] { 1L, "HDLC", Direction.RX, null, null });
                
                // Add a derived packet  
                method.Invoke(_runtime, new object?[] { 2L, "LAPB", Direction.RX, 1L, null });
                
                // Add an error packet
                method.Invoke(_runtime, new object?[] { 3L, "HDLC", Direction.RX, null, "FCS_Error" });
                
                // Check relationships
                var rel1 = _runtime.GetPacketRelationship(1);
                var rel2 = _runtime.GetPacketRelationship(2);
                var rel3 = _runtime.GetPacketRelationship(3);
                
                _logger.LogInformation("Packet 1: IsPrimary:{IsPrimary} IsTerminal:{IsTerminal} HasError:{HasError}", 
                    rel1?.IsPrimary, rel1?.IsTerminal, rel1?.HasProcessingError);
                _logger.LogInformation("Packet 2: IsPrimary:{IsPrimary} IsTerminal:{IsTerminal} HasError:{HasError} DerivedFrom:{DerivedFrom}", 
                    rel2?.IsPrimary, rel2?.IsTerminal, rel2?.HasProcessingError, rel2?.DerivedFromId);
                _logger.LogInformation("Packet 3: IsPrimary:{IsPrimary} IsTerminal:{IsTerminal} HasError:{HasError} ErrorReason:{ErrorReason}", 
                    rel3?.IsPrimary, rel3?.IsTerminal, rel3?.HasProcessingError, rel3?.ErrorReason);
                
                // Verify expected relationships
                Assert.That(rel1, Is.Not.Null, "Packet 1 should have relationship");
                Assert.That(rel2, Is.Not.Null, "Packet 2 should have relationship");
                Assert.That(rel3, Is.Not.Null, "Packet 3 should have relationship");
                
                if (rel1 != null && rel2 != null)
                {
                    Assert.That(rel1.IsPrimary, Is.True, "Packet 1 should be primary");
                    Assert.That(rel2.IsPrimary, Is.False, "Packet 2 should not be primary");
                    Assert.That(rel1.IsTerminal, Is.False, "Packet 1 should not be terminal (has children)");
                    Assert.That(rel2.IsTerminal, Is.True, "Packet 2 should be terminal");
                    Assert.That(rel2.DerivedFromId, Is.EqualTo(1), "Packet 2 should be derived from packet 1");
                }
                
                if (rel3 != null)
                {
                    Assert.That(rel3.HasProcessingError, Is.True, "Packet 3 should have error");
                    Assert.That(rel3.ErrorReason, Is.EqualTo("FCS_Error"), "Packet 3 should have correct error reason");
                }
            }
            else
            {
                _logger.LogError("Could not find AddPacketWithChainTracking method");
                Assert.Fail("Required method not found");
            }
        }
    }
}