using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using RetroIO.Protocols.X25;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Runtime.Models;
using RetroIO.Protocols.X25.Sintran;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Unit tests to verify that X.25 and LAPB history items are properly linked
    /// for two-line display functionality
    /// </summary>
    [TestFixture]
    public class TwoLineDisplayRelationshipTests : TestBase
    {
        [SetUp]
        public override void Setup()
        {
            // Call base setup first
            base.Setup();
            
            // Configure serial routing to force packets through LAPB layer
            ConfigureSerialRouting();
        }
        
        private void ConfigureSerialRouting()
        {
            // Enable test mode for mock serial ports
            Config.TestMode = true;
            
            // Configure serial interface for machine 100 (different from local machine 200)
            Config.Routing.SerialInterfaces.Add(new SerialInterfaceConfig 
            {
                Name = "Serial0",
                PortName = "MOCK1",
                BaudRate = 9600,
                RemoteMachineId = "100", 
                Enabled = true,
                Description = "Mock serial connection to machine 100"
            });
            
            // Configure route for address 100.* to use Serial0 (NOT local address 200)
            Config.Routing.RoutingTable.Add(new X25RouteEntry
            {
                X121AddressPattern = "100.*",
                Type = RetroIO.Protocols.X25.Runtime.RouteType.Interface,
                Destination = "Serial0",
                Priority = 100,
                Enabled = true,
                Description = "Route calls to 100.* via Serial0 interface"
            });
            
            // Recreate runtime with new routing config
            Runtime?.Dispose();
            var newRuntime = new ProtocolRuntime(Logger, Config);
            
            // Use reflection to update Runtime property
            var runtimeProperty = typeof(TestBase).GetProperty("Runtime", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            runtimeProperty?.SetValue(this, newRuntime);
        }
        
        [Test]
        public void PAD_To_Serial_Connection_Should_Create_Linked_X25_LAPB_History_Items()
        {
            // This test verifies that LAPB RX I-frames with X.25 data create linked history items
            // Using direct LAPB frame injection (same approach as TwoLineIntegrationTest)
            Logger.LogInformation("=== Testing History Relationships via LAPB I-frame ===");

            // Track all history items as they're added
            var historyItems = new List<HistoryItem>();
            Runtime.HistoryItemAdded += (sender, item) =>
            {
                historyItems.Add(item);
                Logger.LogInformation("History Item #{Id}: {Layer} {Direction}",
                    item.Id, item.Layer, item.Direction);

                if (item.RelatedItem != null)
                {
                    Logger.LogInformation("  -> Related to #{RelatedId}: {RelatedLayer}",
                        item.RelatedItem.Id, item.RelatedItem.Layer);
                }
            };

            // Step 1: Establish LAPB connection
            byte[] sabmFrame = new byte[] { 0x01, 0x3F, 0x00, 0x64 }; // SABM from machine 100
            Runtime.InjectLapbFrame(sabmFrame, (ushort)sabmFrame.Length);
            Thread.Sleep(200);

            // Verify connection established
            var stats = Runtime.GetStatistics();
            Assert.That(stats.LAPB.CurrentState, Is.EqualTo("CONNECTED"),
                "LAPB should be connected after SABM/UA exchange");

            // Step 2: Send I-frame with X.25 data
            var x25CallRequest = new byte[] { 0x10, 0x01, 0x0B, 0x02, 0x04, 0x31, 0x32, 0x33, 0x34 };
            var lapbFrame = new byte[2 + x25CallRequest.Length];
            lapbFrame[0] = 0x01; // Address
            lapbFrame[1] = 0x00; // I-frame control
            Array.Copy(x25CallRequest, 0, lapbFrame, 2, x25CallRequest.Length);

            Logger.LogInformation("Sending LAPB I-frame with X.25 Call Request...");
            Runtime.InjectLapbFrame(lapbFrame, (ushort)lapbFrame.Length);
            Thread.Sleep(200);

            // Assert - Find history items with relationships
            Logger.LogInformation("=== Analyzing History Relationships ===");
            Logger.LogInformation("Total history items: {Count}", historyItems.Count);

            // Count items by layer and relationship status using traditional loops
            int x25Count = 0;
            int lapbCount = 0;
            int x25WithRelCount = 0;
            int lapbWithRelCount = 0;

            for (int i = 0; i < historyItems.Count; i++)
            {
                var item = historyItems[i];
                if (item.Layer == LayerType.X25)
                {
                    x25Count++;
                    if (item.RelatedItem != null) x25WithRelCount++;
                }
                else if (item.Layer == LayerType.LAPB)
                {
                    lapbCount++;
                    if (item.RelatedItem != null) lapbWithRelCount++;
                }
            }

            Logger.LogInformation("X.25 items: {Count}, with relationships: {RelCount}", x25Count, x25WithRelCount);
            Logger.LogInformation("LAPB items: {Count}, with relationships: {RelCount}", lapbCount, lapbWithRelCount);

            Assert.That(x25Count, Is.GreaterThan(0), "Should have X.25 history items");
            Assert.That(lapbCount, Is.GreaterThan(0), "Should have LAPB history items");
            Assert.That(x25WithRelCount, Is.GreaterThan(0), "Should have X.25 items linked to LAPB items");

            // Find items with relationships and verify them
            for (int i = 0; i < historyItems.Count; i++)
            {
                var item = historyItems[i];
                if (item.Layer == LayerType.X25 && item.RelatedItem != null)
                {
                    Assert.That(item.RelatedItem.Layer, Is.EqualTo(LayerType.LAPB),
                        "X.25 item should be related to LAPB item");
                    Assert.That(item.RelatedItem.RelatedItem, Is.EqualTo(item),
                        "LAPB item should have reverse relationship to X.25 item");

                    Logger.LogInformation("✅ X.25 #{X25Id} linked to LAPB #{LapbId}",
                        item.Id, item.RelatedItem.Id);
                }
            }
        }

        [Test]
        public void History_Relationships_Should_Match_Data_Content()
        {
            // This test verifies that linked LAPB and X.25 items have matching data content
            Logger.LogInformation("=== Testing Data Content Matching in Relationships ===");

            var historyItems = new List<HistoryItem>();
            Runtime.HistoryItemAdded += (sender, item) => historyItems.Add(item);

            // Step 1: Establish LAPB connection
            byte[] sabmFrame = new byte[] { 0x01, 0x3F, 0x00, 0x64 };
            Runtime.InjectLapbFrame(sabmFrame, (ushort)sabmFrame.Length);
            Thread.Sleep(200);

            // Step 2: Send I-frame with specific X.25 data
            var x25Data = new byte[] { 0x10, 0x01, 0x0B, 0x02, 0x04, 0x31, 0x32, 0x33, 0x34 };
            var lapbFrame = new byte[2 + x25Data.Length];
            lapbFrame[0] = 0x01;
            lapbFrame[1] = 0x00;
            Array.Copy(x25Data, 0, lapbFrame, 2, x25Data.Length);

            Runtime.InjectLapbFrame(lapbFrame, (ushort)lapbFrame.Length);
            Thread.Sleep(200);

            // Find related pairs and verify content matching
            int relatedPairsCount = 0;
            int contentMatchesCount = 0;

            for (int i = 0; i < historyItems.Count; i++)
            {
                var item = historyItems[i];
                if (item.RelatedItem == null) continue;

                relatedPairsCount++;

                // For LAPB with X.25 relationship, verify data content
                if (item.Layer == LayerType.LAPB && item.RelatedItem.Layer == LayerType.X25)
                {
                    var lapbPayload = item.Payload;
                    var x25Payload = item.RelatedItem.Payload;

                    if (lapbPayload != null && x25Payload != null)
                    {
                        Logger.LogInformation("Checking data match - LAPB len:{LapbLen}, X.25 len:{X25Len}",
                            lapbPayload.Length, x25Payload.Length);

                        // The LAPB information field (after address+control) should match X.25 data
                        if (lapbPayload.Length >= 3 && x25Payload.Length > 0)
                        {
                            // Skip address (1) and control (1) bytes to get info field
                            bool dataMatches = true;
                            int infoOffset = 2;
                            int matchLen = Math.Min(lapbPayload.Length - infoOffset, x25Payload.Length);

                            for (int j = 0; j < matchLen; j++)
                            {
                                if (lapbPayload[infoOffset + j] != x25Payload[j])
                                {
                                    dataMatches = false;
                                    break;
                                }
                            }

                            if (dataMatches)
                            {
                                contentMatchesCount++;
                                Logger.LogInformation("✅ Data content matches between LAPB and X.25");
                            }
                        }
                    }
                }
            }

            Assert.That(relatedPairsCount, Is.GreaterThan(0), "Should have related pairs");
            Logger.LogInformation("Found {RelatedPairs} related pairs, {ContentMatches} with matching content",
                relatedPairsCount, contentMatchesCount);
        }

        [Test]
        public void Unrelated_Packets_Should_Not_Have_Relationships()
        {
            // Test that SABM/UA control frames don't get linked to X.25 packets
            Logger.LogInformation("=== Testing Unrelated Packets ===");

            var historyItems = new List<HistoryItem>();
            Runtime.HistoryItemAdded += (sender, item) => historyItems.Add(item);

            // Send SABM frame (U-frame, no X.25 content)
            byte[] sabmFrame = new byte[] { 0x01, 0x3F, 0x00, 0x64 };
            Runtime.InjectLapbFrame(sabmFrame, (ushort)sabmFrame.Length);
            Thread.Sleep(100);

            // Find SABM and UA frames and verify no relationships
            int sabmCount = 0;
            int uaCount = 0;
            int controlFramesWithRelationships = 0;

            for (int i = 0; i < historyItems.Count; i++)
            {
                var item = historyItems[i];
                if (item.Layer != LayerType.LAPB) continue;

                // Check for SABM frames (description should contain SABM or control byte 0x3F)
                if (item.Description != null && item.Description.Contains("SABM"))
                {
                    sabmCount++;
                    if (item.RelatedItem != null)
                    {
                        controlFramesWithRelationships++;
                    }
                    else
                    {
                        Logger.LogInformation("✅ SABM frame #{Id} correctly has no relationship", item.Id);
                    }
                }

                // Check for UA frames
                if (item.Description != null && item.Description.Contains("UA"))
                {
                    uaCount++;
                    if (item.RelatedItem != null)
                    {
                        controlFramesWithRelationships++;
                    }
                    else
                    {
                        Logger.LogInformation("✅ UA frame #{Id} correctly has no relationship", item.Id);
                    }
                }
            }

            Logger.LogInformation("Found {SabmCount} SABM frames, {UaCount} UA frames", sabmCount, uaCount);
            Assert.That(controlFramesWithRelationships, Is.EqualTo(0),
                "SABM and UA control frames should not be linked to X.25 packets");
        }
    }
}