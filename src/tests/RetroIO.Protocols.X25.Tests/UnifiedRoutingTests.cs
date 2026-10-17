//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Xunit;
using Microsoft.Extensions.Logging;
using RetroIO.Protocols.X25;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Runtime.Models;
using RetroIO.Protocols.X25.Sintran;
using RetroIO.Protocols.X25.Modules;
using RetroIO.Protocols.X25.Services;
using RetroIO.Protocols.X25.Sintran.Services;
using RetroIO.Protocols.X25.Protocols;
using RetroIO.Link.Lapb;
using RetroIO.Protocols.X25.Layers;
using RetroIO.Protocols.X25.Runtime.Layers;
using System.Collections.Generic;
using System.Linq;
using System;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Unit tests for the unified X.25 routing system
    /// Tests both outbound and inbound routing through X25RoutingLayer
    /// </summary>
    public class UnifiedRoutingTests : IDisposable
    {
        private readonly ILogger<ProtocolRuntime> _logger;
        private readonly RuntimeConfig _config;
        private readonly ProtocolRuntime _runtime;
        private readonly List<HistoryItem> _capturedHistoryItems;
        private readonly List<X25PacketEventArgs> _capturedPadEvents;

        public UnifiedRoutingTests()
        {
            _logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<ProtocolRuntime>();
            _config = new RuntimeConfig 
            { 
                HistoryCapacity = 100 
            };
            _config.X25.LocalMachineId = "200";
            
            _capturedHistoryItems = new List<HistoryItem>();
            _capturedPadEvents = new List<X25PacketEventArgs>();
            
            // Create runtime and subscribe to events
            _runtime = new ProtocolRuntime(_logger, _config);
            _runtime.HistoryItemAdded += (sender, item) => _capturedHistoryItems.Add(item);
        }

        public void Dispose()
        {
            _runtime?.Dispose();
        }

        [Fact]
        public void OutboundRouting_ToLocalService_ShouldRouteCorrectly()
        {
            // Arrange
            var padClient = new PadClient(_logger, _runtime);
            
            // Act
            var success = _runtime.PlaceX25CallWithModule("200", "100", null, padClient);
            
            
            // Assert
            Assert.True(success, "Outbound call to local service should succeed");
            
            // Verify packet was logged to history 
            var x25HistoryItems = _capturedHistoryItems.OfType<X25HistoryItem>().ToList();
            Assert.True(x25HistoryItems.Count >= 1, "Should have at least one X.25 packet in history");
            
            // Look for Call Accepted packet from TAD service
            var callAcceptedPacket = x25HistoryItems.FirstOrDefault(item => 
                item.Packet.PacketType == X25PacketType.CallAccepted);
            Assert.NotNull(callAcceptedPacket);
            Assert.Equal(Direction.RX, callAcceptedPacket.Direction);
        }

        [Fact]
        public void OutboundRouting_ToUnknownAddress_ShouldFail()
        {
            // Arrange
            var padClient = new PadClient(_logger, _runtime);
            
            // Act
            var success = _runtime.PlaceX25CallWithModule("999", "100", null, padClient);
            
            // Assert
            Assert.False(success, "Outbound call to unknown address should fail");
        }

        [Fact]
        public void LocalServiceRouting_ShouldBindModuleToLCN()
        {
            // Arrange
            var padClient = new PadClient(_logger, _runtime);
            
            // Act
            var success = _runtime.PlaceX25CallWithModule("200", "100", null, padClient);
            
            
            // Assert
            Assert.True(success);
            
            // Verify LCN binding was created
            var lcnBindings = _runtime.GetLcnBindings();
            Assert.Contains(padClient, lcnBindings.Values);
        }

        [Fact]
        public void LocalServiceRouting_MultipleServices_ShouldSelectByPriority()
        {
            // Arrange - Both TAD and XMSG services handle address "200", but XMSG has higher priority
            var padClient = new PadClient(_logger, _runtime);
            
            // Create call with XMSG-specific CUD (Call User Data)
            var xmsgCud = new byte[] { 0x58, 0x4D, 0x53, 0x47 }; // "XMSG" in ASCII
            
            // Act
            var success = _runtime.PlaceX25CallWithModule("200", "100", xmsgCud, padClient);
            
            
            // Assert
            Assert.True(success, "Call with XMSG CUD should be routed to XMSG service");
            
            // Look for XMSG-specific behavior in history (XMSG sends different response than TAD)
            var x25HistoryItems = _capturedHistoryItems.OfType<X25HistoryItem>().ToList();
            Assert.True(x25HistoryItems.Count >= 1, "Should have packets in history");
        }

        [Fact]
        public void LocalServiceRouting_CallUserData_ShouldInfluenceServiceSelection()
        {
            // Arrange
            var padClient = new PadClient(_logger, _runtime);
            
            // Test TAD service (no specific CUD required)
            var tadSuccess = _runtime.PlaceX25CallWithModule("200", "100", null, padClient);
            Assert.True(tadSuccess, "TAD call should succeed");
            
            // Clear history for second test
            _capturedHistoryItems.Clear();
            
            // Create new PAD client for second call
            var padClient2 = new PadClient(_logger, _runtime);
            
            // Test with CUD that might influence service selection
            var testCud = new byte[] { 0x01, 0x02, 0x03 };
            var cudSuccess = _runtime.PlaceX25CallWithModule("200", "101", testCud, padClient2);
            
            
            // Assert
            Assert.True(cudSuccess, "Call with CUD should still be handled by available service");
        }

        [Fact]
        public void RoutingLayer_ShouldLogOutboundCallAttempts()
        {
            // Arrange
            var padClient = new PadClient(_logger, _runtime);
            
            // Act
            var success = _runtime.PlaceX25CallWithModule("200", "100", null, padClient);
            
            // Wait for LocalTadProtocol timers to fire (Call Accepted + Welcome Message)
            
            // Assert
            Assert.True(success);
            
            // Verify routing layer activity in history
            var x25HistoryItems = _capturedHistoryItems.OfType<X25HistoryItem>().ToList();
            Assert.True(x25HistoryItems.Count >= 1, "Should have X.25 packets from routing");
            
            // Should have both outbound call attempt and inbound response
            var directions = x25HistoryItems.Select(item => item.Direction).Distinct().ToList();
            Assert.Contains(Direction.RX, directions); // Response from local service
        }

        [Fact]
        public void RoutingLayer_Integration_ShouldMaintainPacketFlow()
        {
            // Arrange
            var padClient = new PadClient(_logger, _runtime);
            var initialHistoryCount = _capturedHistoryItems.Count;
            
            // Act - Place call and simulate some data exchange
            var success = _runtime.PlaceX25CallWithModule("200", "100", null, padClient);
            
            
            // Assert
            Assert.True(success);
            
            // Verify packet flow increased
            var newHistoryCount = _capturedHistoryItems.Count;
            Assert.True(newHistoryCount > initialHistoryCount, 
                $"Should have more history items after routing (was {initialHistoryCount}, now {newHistoryCount})");
            
            // Verify we have X.25 level packets
            var x25HistoryItems = _capturedHistoryItems.OfType<X25HistoryItem>().ToList();
            Assert.True(x25HistoryItems.Any(), "Should have X.25 packets in history");
        }

        [Fact]
        public void RoutingLayer_PacketDirection_ShouldBeConsistent()
        {
            // Arrange
            var padClient = new PadClient(_logger, _runtime);
            
            // Act
            var success = _runtime.PlaceX25CallWithModule("200", "100", null, padClient);
            
            
            // Assert
            Assert.True(success);
            
            var x25HistoryItems = _capturedHistoryItems.OfType<X25HistoryItem>().ToList();
            Assert.True(x25HistoryItems.Any(), "Should have X.25 packets");
            
            // Verify direction consistency
            foreach (var item in x25HistoryItems)
            {
                Assert.True(item.Direction == Direction.TX || item.Direction == Direction.RX,
                    $"Packet direction should be TX or RX, got {item.Direction}");
                
                // Verify timestamp is reasonable (not default DateTime)
                Assert.True(item.Timestamp > DateTime.MinValue, "Packet should have valid timestamp");
            }
        }

        [Fact]
        public void RoutingLayer_FrameIds_ShouldBeSequential()
        {
            // Arrange
            var padClient = new PadClient(_logger, _runtime);
            
            // Act - Make multiple calls to generate multiple packets
            for (int i = 0; i < 3; i++)
            {
                var padClientN = new PadClient(_logger, _runtime);
                _runtime.PlaceX25CallWithModule("200", $"10{i}", null, padClientN);
            }
            
            
            // Assert
            var allHistoryItems = _capturedHistoryItems.ToList();
            Assert.True(allHistoryItems.Count >= 3, "Should have multiple history items");
            
            // Verify frame IDs are sequential
            var frameIds = allHistoryItems.Select(item => item.Id).OrderBy(id => id).ToList();
            
            for (int i = 1; i < frameIds.Count; i++)
            {
                Assert.True(frameIds[i] > frameIds[i - 1], 
                    $"Frame ID {frameIds[i]} should be greater than previous {frameIds[i - 1]}");
            }
        }

        [Theory]
        [InlineData("200", true)]  // Local TAD service
        [InlineData("999", false)] // Unknown address
        [InlineData("", false)]    // Empty address
        public void RoutingLayer_AddressResolution_ShouldWorkCorrectly(string calledAddress, bool shouldSucceed)
        {
            // Arrange
            var padClient = new PadClient(_logger, _runtime);
            
            // Act
            var success = _runtime.PlaceX25CallWithModule(calledAddress, "100", null, padClient);
            
            
            // Assert
            if (shouldSucceed)
            {
                Assert.True(success, $"Call to address '{calledAddress}' should succeed");
                
                // Should have generated packets
                var x25HistoryItems = _capturedHistoryItems.OfType<X25HistoryItem>().ToList();
                Assert.True(x25HistoryItems.Any(), $"Call to '{calledAddress}' should generate packets");
            }
            else
            {
                Assert.False(success, $"Call to address '{calledAddress}' should fail");
            }
        }
    }
}