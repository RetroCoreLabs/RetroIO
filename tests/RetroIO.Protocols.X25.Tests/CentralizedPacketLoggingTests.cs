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
using System.Collections.Generic;
using System.Linq;
using System;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Unit tests for centralized packet logging functionality
    /// Tests the RouteX25EventToBoundModule method and packet history integration
    /// </summary>
    public class CentralizedPacketLoggingTests : IDisposable
    {
        private readonly ILogger<ProtocolRuntime> _logger;
        private readonly RuntimeConfig _config;
        private readonly ProtocolRuntime _runtime;
        private readonly List<HistoryItem> _capturedHistoryItems;

        public CentralizedPacketLoggingTests()
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

            // Act
            // Use appropriate Fire method based on packet direction
if (eventArgs.Direction == PacketDirection.Receive)
{
    _runtime.X25Layer.FirePacketReceived(eventArgs);
}
else
{
    _runtime.X25Layer.FirePacketTransmitted(eventArgs);
}

            // Assert
            Assert.Single(_capturedHistoryItems);
            var historyItem = _capturedHistoryItems.First() as X25HistoryItem;
            
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

            // Act
            // Use appropriate Fire method based on packet direction
if (eventArgs.Direction == PacketDirection.Receive)
{
    _runtime.X25Layer.FirePacketReceived(eventArgs);
}
else
{
    _runtime.X25Layer.FirePacketTransmitted(eventArgs);
}

            // Assert
            Assert.Single(_capturedHistoryItems);
            var historyItem = _capturedHistoryItems.First() as X25HistoryItem;
            
            Assert.NotNull(historyItem);
            Assert.Equal(Direction.TX, historyItem.Direction);
            Assert.Equal(X25PacketType.CallRequest, historyItem.Packet.PacketType);
            Assert.Equal(testLcn, historyItem.Packet.LCN);
        }

        [Fact]
        public void RouteX25EventToBoundModule_MultiplePackets_ShouldLogAllPacketsToHistory()
        {
            // Arrange
            const ushort testLcn = 3;
            var padClient = new PadClient(_logger, _runtime);
            _runtime.BindLcnToModule(testLcn, padClient);

            var packets = new[]
            {
                (X25PacketTypeIdentifier.CallRequest, PacketDirection.Transmit, Direction.TX, X25PacketType.CallRequest),
                (X25PacketTypeIdentifier.CallAccepted, PacketDirection.Receive, Direction.RX, X25PacketType.CallAccepted),
                (X25PacketTypeIdentifier.Data, PacketDirection.Transmit, Direction.TX, X25PacketType.Data),
                (X25PacketTypeIdentifier.Data, PacketDirection.Receive, Direction.RX, X25PacketType.Data)
            };

            // Act
            foreach (var (packetTypeId, direction, expectedHistoryDirection, expectedPacketType) in packets)
            {
                var testPacket = new X25Packet
                {
                    LCN = testLcn,
                    PacketTypeId = packetTypeId,
                    Data = new byte[0],
                    FullPacket = new byte[] { 0x10, 0x00, (byte)testLcn, 0x00 },
                    Length = 4
                };

                var eventArgs = new X25PacketEventArgs
                {
                    Packet = testPacket,
                    Direction = direction,
                    Timestamp = DateTime.UtcNow
                };

                // Use appropriate Fire method based on packet direction
if (eventArgs.Direction == PacketDirection.Receive)
{
    _runtime.X25Layer.FirePacketReceived(eventArgs);
}
else
{
    _runtime.X25Layer.FirePacketTransmitted(eventArgs);
}
            }

            // Assert
            Assert.Equal(4, _capturedHistoryItems.Count);
            
            for (int i = 0; i < packets.Length; i++)
            {
                var historyItem = _capturedHistoryItems[i] as X25HistoryItem;
                var (_, _, expectedDirection, expectedPacketType) = packets[i];
                
                Assert.NotNull(historyItem);
                Assert.Equal(expectedDirection, historyItem.Direction);
                Assert.Equal(expectedPacketType, historyItem.Packet.PacketType);
                Assert.Equal(testLcn, historyItem.Packet.LCN);
            }
        }

        [Fact]
        public void RouteX25EventToBoundModule_WithoutBoundModule_ShouldStillLogPacketToHistory()
        {
            // Arrange
            const ushort testLcn = 99; // No module bound to this LCN

            var testPacket = new X25Packet
            {
                LCN = testLcn,
                PacketTypeId = X25PacketTypeIdentifier.Data,
                Data = new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F }, // "Hello"
                FullPacket = new byte[] { 0x10, 0x00, (byte)testLcn, 0x00, 0x48, 0x65, 0x6C, 0x6C, 0x6F },
                Length = 9
            };

            var eventArgs = new X25PacketEventArgs
            {
                Packet = testPacket,
                Direction = PacketDirection.Receive,
                Timestamp = DateTime.UtcNow
            };

            // Act
            // Use appropriate Fire method based on packet direction
if (eventArgs.Direction == PacketDirection.Receive)
{
    _runtime.X25Layer.FirePacketReceived(eventArgs);
}
else
{
    _runtime.X25Layer.FirePacketTransmitted(eventArgs);
}

            // Assert - Packet should still be logged even if no module is bound
            Assert.Single(_capturedHistoryItems);
            var historyItem = _capturedHistoryItems.First() as X25HistoryItem;
            
            Assert.NotNull(historyItem);
            Assert.Equal(Direction.RX, historyItem.Direction);
            Assert.Equal(X25PacketType.Data, historyItem.Packet.PacketType);
            Assert.Equal(testLcn, historyItem.Packet.LCN);
            Assert.Equal(5, historyItem.Packet.Data.Length);
        }

        [Fact]
        public void RouteX25EventToBoundModule_PacketsShouldHaveUniqueFrameIds()
        {
            // Arrange
            const ushort testLcn = 4;
            var padClient = new PadClient(_logger, _runtime);
            _runtime.BindLcnToModule(testLcn, padClient);

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

                // Use appropriate Fire method based on packet direction
if (eventArgs.Direction == PacketDirection.Receive)
{
    _runtime.X25Layer.FirePacketReceived(eventArgs);
}
else
{
    _runtime.X25Layer.FirePacketTransmitted(eventArgs);
}
            }

            // Assert
            Assert.Equal(3, _capturedHistoryItems.Count);
            
            var frameIds = _capturedHistoryItems.Select(item => item.Id).ToArray();
            
            // All frame IDs should be unique
            Assert.Equal(3, frameIds.Distinct().Count());
            
            // Frame IDs should be sequential (increasing)
            for (int i = 1; i < frameIds.Length; i++)
            {
                Assert.True(frameIds[i] > frameIds[i - 1], "Frame IDs should be sequential");
            }
        }

        [Theory]
        [InlineData(PacketDirection.Receive, Direction.RX)]
        [InlineData(PacketDirection.Transmit, Direction.TX)]
        public void RouteX25EventToBoundModule_DirectionMapping_ShouldBeCorrect(
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

            // Act
            // Use appropriate Fire method based on packet direction
if (eventArgs.Direction == PacketDirection.Receive)
{
    _runtime.X25Layer.FirePacketReceived(eventArgs);
}
else
{
    _runtime.X25Layer.FirePacketTransmitted(eventArgs);
}

            // Assert
            Assert.Single(_capturedHistoryItems);
            var historyItem = _capturedHistoryItems.First() as X25HistoryItem;
            
            Assert.NotNull(historyItem);
            Assert.Equal(expectedHistoryDirection, historyItem.Direction);
        }
    }
}