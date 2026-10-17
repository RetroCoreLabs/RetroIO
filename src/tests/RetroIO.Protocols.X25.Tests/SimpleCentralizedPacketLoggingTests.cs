//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Modules;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Protocols.X25.Runtime.Models;
using RetroIO.Protocols.X25.Sintran;
using Xunit;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Simple unit tests for centralized packet logging functionality
    /// Tests the RouteX25EventToBoundModule method and packet history integration
    /// </summary>
    public class SimpleCentralizedPacketLoggingTests : IDisposable
    {
        private readonly ILogger<ProtocolRuntime> _logger;
        private readonly RuntimeConfig _config;
        private readonly ProtocolRuntime _runtime;
        private readonly List<HistoryItem> _capturedHistoryItems;

        public SimpleCentralizedPacketLoggingTests()
        {
            _logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<ProtocolRuntime>();
            _config = new RuntimeConfig 
            { 
                HistoryCapacity = 100 
            };
            _config.X25.LocalMachineId = "200";
            
            _capturedHistoryItems = new List<HistoryItem>();
            
            // Create runtime and subscribe to history events
            _runtime = new ProtocolRuntime(_logger, _config);
            _runtime.HistoryItemAdded += (sender, item) => _capturedHistoryItems.Add(item);
        }

        public void Dispose()
        {
            _runtime?.Dispose();
        }

        [Fact]
        public void RouteX25EventToBoundModule_WithReceiveDirection_ShouldLogPacketToHistory()
        {
            // Arrange
            const ushort testLcn = 1;
            var padClient = new PadClient(_logger, _runtime);
            _runtime.BindLcnToModule(testLcn, padClient);

            var testPacket = new X25Packet
            {
                LCN = testLcn,
                PacketTypeId = X25PacketTypeIdentifier.CallAccepted,
                Data = new byte[0],
                FullPacket = new byte[] { 0x10, 0x00, 0x01, 0x0F },
                Length = 4
            };

            var eventArgs = new X25PacketEventArgs
            {
                Packet = testPacket,
                Direction = PacketDirection.Receive,
                Timestamp = DateTime.UtcNow
            };

            // Act - Fire X25Layer.PacketReceived event to trigger proper history creation
            _runtime.PacketHistory.FirePacketReceived(eventArgs);

            // Assert
            Assert.True(_capturedHistoryItems.Count >= 1, "Should have at least one history item");
            
            var historyItem = _capturedHistoryItems.OfType<X25HistoryItem>().FirstOrDefault();
            Assert.NotNull(historyItem);
            Assert.Equal(Direction.RX, historyItem.Direction);
            Assert.Equal(X25PacketType.CallAccepted, historyItem.Packet.PacketType);
            Assert.Equal(testLcn, historyItem.Packet.LCN);
        }

        [Fact]
        public void RouteX25EventToBoundModule_WithTransmitDirection_ShouldLogPacketToHistory()
        {
            // Arrange
            const ushort testLcn = 2;
            var padClient = new PadClient(_logger, _runtime);
            _runtime.BindLcnToModule(testLcn, padClient);

            var testPacket = new X25Packet
            {
                LCN = testLcn,
                PacketTypeId = X25PacketTypeIdentifier.CallRequest,
                Data = new byte[0],
                FullPacket = new byte[] { 0x10, 0x00, 0x02, 0x0B },
                Length = 4
            };

            var eventArgs = new X25PacketEventArgs
            {
                Packet = testPacket,
                Direction = PacketDirection.Transmit,
                Timestamp = DateTime.UtcNow
            };

            // Act - Fire X25Layer.PacketTransmitted event to trigger proper history creation
            _runtime.PacketHistory.FirePacketTransmitted(eventArgs);

            // Assert
            Assert.True(_capturedHistoryItems.Count >= 1, "Should have at least one history item");
            
            var historyItem = _capturedHistoryItems.OfType<X25HistoryItem>().FirstOrDefault();
            Assert.NotNull(historyItem);
            Assert.Equal(Direction.TX, historyItem.Direction);
            Assert.Equal(X25PacketType.CallRequest, historyItem.Packet.PacketType);
            Assert.Equal(testLcn, historyItem.Packet.LCN);
        }

        [Fact]
        public void UnifiedRouting_LocalCall_ShouldGeneratePacketHistory()
        {
            // Arrange
            var padClient = new PadClient(_logger, _runtime);
            var initialHistoryCount = _capturedHistoryItems.Count;
            
            // Act - Place call to local TAD service
            var success = _runtime.PlaceX25CallWithModule("200", "100", null, padClient);
            
            
            // Assert
            Assert.True(success, "Local call should succeed");
            
            // Verify packet history was generated
            var newHistoryCount = _capturedHistoryItems.Count;
            Assert.True(newHistoryCount > initialHistoryCount, 
                $"Should have more history items after routing (was {initialHistoryCount}, now {newHistoryCount})");
            
            // Verify we have X.25 level packets
            var x25HistoryItems = _capturedHistoryItems.OfType<X25HistoryItem>().ToList();
            Assert.True(x25HistoryItems.Any(), "Should have X.25 packets in history");
            
            // Look for Call Accepted packet from TAD service
            var callAcceptedPacket = x25HistoryItems.FirstOrDefault(item => 
                item.Packet.PacketType == X25PacketType.CallAccepted);
            Assert.NotNull(callAcceptedPacket);
            Assert.Equal(Direction.RX, callAcceptedPacket.Direction);
        }

        [Fact]
        public void CentralizedLogging_PacketsHaveUniqueFrameIds()
        {
            // Arrange
            const ushort testLcn = 4;
            var padClient = new PadClient(_logger, _runtime);
            _runtime.BindLcnToModule(testLcn, padClient);

            var initialCount = _capturedHistoryItems.Count;

            // Act - Send multiple packets
            for (int i = 0; i < 3; i++)
            {
                var testPacket = new X25Packet
                {
                    LCN = testLcn,
                    PacketTypeId = X25PacketTypeIdentifier.Data,
                    Data = new byte[] { (byte)i },
                    FullPacket = new byte[] { 0x10, 0x00, (byte)testLcn, 0x00, (byte)i },
                    Length = 5
                };

                var eventArgs = new X25PacketEventArgs
                {
                    Packet = testPacket,
                    Direction = PacketDirection.Receive,
                    Timestamp = DateTime.UtcNow
                };

                // Fire PacketReceived event to trigger proper history creation
                _runtime.PacketHistory.FirePacketReceived(eventArgs);
            }

            // Assert
            var newItemsCount = _capturedHistoryItems.Count - initialCount;
            Assert.True(newItemsCount >= 3, $"Should have added 3 or more new history items, but only added {newItemsCount} (total: {_capturedHistoryItems.Count}, initial: {initialCount})");
            
            // Look at only the new items added by this test
            var newItems = _capturedHistoryItems.Skip(initialCount).ToList();
            var frameIds = newItems.Select(item => item.Id).ToArray();
            
            // All frame IDs should be unique
            Assert.Equal(newItems.Count, frameIds.Distinct().Count());
            
            // Frame IDs should be sequential (increasing)
            var sortedIds = frameIds.OrderBy(id => id).ToArray();
            for (int i = 1; i < sortedIds.Length; i++)
            {
                Assert.True(sortedIds[i] > sortedIds[i - 1], 
                    $"Frame ID {sortedIds[i]} should be greater than previous {sortedIds[i - 1]}");
            }
        }

        [Theory]
        [InlineData(PacketDirection.Receive, Direction.RX)]
        [InlineData(PacketDirection.Transmit, Direction.TX)]
        public void CentralizedLogging_DirectionMapping_ShouldBeCorrect(
            PacketDirection packetDirection, Direction expectedHistoryDirection)
        {
            // Arrange
            const ushort testLcn = 5;
            var padClient = new PadClient(_logger, _runtime);
            _runtime.BindLcnToModule(testLcn, padClient);

            var testPacket = new X25Packet
            {
                LCN = testLcn,
                PacketTypeId = X25PacketTypeIdentifier.Data,
                Data = new byte[0],
                FullPacket = new byte[] { 0x10, 0x00, (byte)testLcn, 0x00 },
                Length = 4
            };

            var eventArgs = new X25PacketEventArgs
            {
                Packet = testPacket,
                Direction = packetDirection,
                Timestamp = DateTime.UtcNow
            };

            // Act - Fire appropriate X25Layer event based on direction
            if (packetDirection == PacketDirection.Receive)
            {
                _runtime.PacketHistory.FirePacketReceived(eventArgs);
            }
            else
            {
                _runtime.PacketHistory.FirePacketTransmitted(eventArgs);
            }

            // Assert
            Assert.True(_capturedHistoryItems.Count >= 1, "Should have history items");
            
            var historyItem = _capturedHistoryItems.OfType<X25HistoryItem>().FirstOrDefault();
            Assert.NotNull(historyItem);
            Assert.Equal(expectedHistoryDirection, historyItem.Direction);
        }
    }
}