//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RetroIO.Protocols.X25;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Runtime.Models;
using RetroIO.Protocols.X25.Sintran;

namespace RetroIO.Protocols.X25.Tests
{
    [TestFixture]
    public class SimulationPacketNumberingTests : TestBase
    {
        private ProtocolRuntime _runtime;
        private RuntimeConfig _config;
        private List<UnifiedHistoryItem> _capturedPackets;

        [SetUp]
        public override void Setup()
        {
            base.Setup();
            
            _config = RuntimeConfig.CreateDefault();
            _runtime = new ProtocolRuntime(Logger, _config);
            _capturedPackets = new List<UnifiedHistoryItem>();
            
            // Subscribe to history item events
            _runtime.HistoryItemAdded += OnHistoryItemAdded;
        }
        
        private void OnHistoryItemAdded(object? sender, HistoryItem item)
        {
            if (item is UnifiedHistoryItem unifiedItem)
            {
                _capturedPackets.Add(unifiedItem);
                Logger.LogInformation($"Captured packet: ID={unifiedItem.Id}, GlobalPacketId={unifiedItem.PacketInfo.GlobalPacketId}");
            }
        }

        [Test]
        public void SimulateFourIdenticalFrames_ShouldCreateSequentialPacketIds()
        {
            // Arrange
            var frameData = new byte[] { 0x01, 0x3F, 0x00, 0x64 };
            const int frameCount = 4;
            
            // Act - Simulate 4 identical frames
            for (int i = 0; i < frameCount; i++)
            {
                _runtime.SimulateLapbFrame(frameData, frameData.Length);
                Logger.LogInformation($"Simulated frame {i + 1}");
            }
            
            // Wait a bit for events to process
            Thread.Sleep(100);
            
            // Assert - Check packet count
            Logger.LogInformation($"Total captured packets: {_capturedPackets.Count}");
            foreach (var packet in _capturedPackets)
            {
                Logger.LogInformation($"Packet ID: {packet.Id}, GlobalPacketId: {packet.PacketInfo.GlobalPacketId}, Direction: {packet.PacketInfo.Direction}, HighestLayer: {packet.PacketInfo.HighestLayer}");
            }
            
            // Should have exactly 4 packets
            Assert.That(_capturedPackets.Count, Is.EqualTo(frameCount), $"Expected {frameCount} packets, got {_capturedPackets.Count}");
            
            // Check that packet IDs are sequential starting from 1
            var expectedIds = new[] { 1L, 2L, 3L, 4L };
            var actualIds = new long[_capturedPackets.Count];
            for (int i = 0; i < _capturedPackets.Count; i++)
            {
                actualIds[i] = _capturedPackets[i].PacketInfo.GlobalPacketId;
            }
            
            Logger.LogInformation($"Expected IDs: [{string.Join(", ", expectedIds)}]");
            Logger.LogInformation($"Actual IDs: [{string.Join(", ", actualIds)}]");
            
            Assert.That(actualIds, Is.EqualTo(expectedIds), "Packet IDs should be sequential: 1, 2, 3, 4");
        }

        [Test]
        public void SimulatedPackets_ShouldHaveCompleteLayerInfo()
        {
            // Arrange
            var frameData = new byte[] { 0x01, 0x3F, 0x00, 0x64 };
            
            // Act
            _runtime.SimulateLapbFrame(frameData, frameData.Length);
            Thread.Sleep(100);
            
            // Assert
            Assert.That(_capturedPackets.Count, Is.EqualTo(1), "Should have exactly 1 packet");
            
            var packet = _capturedPackets[0];
            var packetInfo = packet.PacketInfo;
            
            // Check packet structure
            Assert.That(packetInfo.Direction, Is.EqualTo(Direction.RX), "Should be RX direction");
            Assert.That(packetInfo.OriginLayer, Is.EqualTo(LayerType.HDLC), "Should originate from HDLC");
            Assert.That(packetInfo.HighestLayer, Is.EqualTo(LayerType.LAPB), "Should reach LAPB layer");
            Assert.That(packetInfo.LayersVisited.Contains(LayerType.HDLC), Is.True, "Should visit HDLC layer");
            Assert.That(packetInfo.LayersVisited.Contains(LayerType.LAPB), Is.True, "Should visit LAPB layer");
            
            // Check HDLC layer info
            Assert.That(packetInfo.HDLC, Is.Not.Null, "HDLC layer info should not be null");
            Assert.That(packetInfo.HDLC.Length, Is.EqualTo(frameData.Length), "HDLC length should match");
            Assert.That(packetInfo.HDLC.FcsValid, Is.True, "HDLC FCS should be valid (simulated)");
            Assert.That(packetInfo.HDLC.FrameData, Is.EqualTo(frameData), "HDLC frame data should match");
            
            // Check LAPB layer info  
            Assert.That(packetInfo.LAPB, Is.Not.Null, "LAPB layer info should not be null");
            Assert.That(packetInfo.LAPB.Address, Is.EqualTo(0x01), "LAPB address should be 0x01");
            Assert.That(packetInfo.LAPB.Control, Is.EqualTo(0x3F), "LAPB control should be 0x3F");
            Assert.That(packetInfo.LAPB.FrameType, Is.EqualTo(LapbFrameType.Unnumbered), "Should be Unnumbered frame");
            // 0x3F = 0011 1111, bit 4 (P/F) = 1
            Assert.That(packetInfo.LAPB.PollFinal, Is.True, "P/F bit is set in control byte 0x3F");
            Assert.That(packetInfo.LAPB.InformationLength, Is.EqualTo(2), "Information length should be 2 (0x00, 0x64)");
            Assert.That(packetInfo.LAPB.Information, Is.EqualTo(new byte[] { 0x00, 0x64 }), "Information should be 0x00, 0x64");
        }

        [Test]
        public void CheckUnderlyingDictionaries_ForDuplicatePackets()
        {
            // Arrange
            var frameData = new byte[] { 0x01, 0x3F, 0x00, 0x64 };
            
            // Act - Simulate 4 frames
            for (int i = 0; i < 4; i++)
            {
                _runtime.SimulateLapbFrame(frameData, frameData.Length);
            }
            Thread.Sleep(100);
            
            // Act - Get unified packets directly from runtime
            var displayItems = _runtime.GetUnifiedPacketsForDisplay();
            
            Logger.LogInformation($"Runtime reports {displayItems.Count} display items");
            foreach (var item in displayItems)
            {
                if (item.Packet is UnifiedHistoryItem unifiedItem)
                {
                    Logger.LogInformation($"Display Item: ID={unifiedItem.Id}, GlobalPacketId={unifiedItem.PacketInfo.GlobalPacketId}");
                }
            }
            
            // Assert - Check both event capture and runtime display
            Assert.That(_capturedPackets.Count, Is.EqualTo(4), "Event capture should have 4 packets");
            Assert.That(displayItems.Count, Is.EqualTo(4), "Runtime display should have 4 packets");
            
            // Check for duplicate IDs in captured packets
            var capturedIds = new List<long>();
            foreach (var packet in _capturedPackets)
            {
                capturedIds.Add(packet.PacketInfo.GlobalPacketId);
            }
            var uniqueCapturedIds = new List<long>();
            foreach (var id in capturedIds)
            {
                if (!uniqueCapturedIds.Contains(id))
                {
                    uniqueCapturedIds.Add(id);
                }
            }
            Assert.That(uniqueCapturedIds.Count, Is.EqualTo(capturedIds.Count), "Captured packets should not have duplicate IDs");
            
            // Check for duplicate IDs in display items
            var displayIds = new List<long>();
            foreach (var item in displayItems)
            {
                if (item.Packet is UnifiedHistoryItem unifiedItem)
                {
                    displayIds.Add(unifiedItem.PacketInfo.GlobalPacketId);
                }
            }
            var uniqueDisplayIds = new List<long>();
            foreach (var id in displayIds)
            {
                if (!uniqueDisplayIds.Contains(id))
                {
                    uniqueDisplayIds.Add(id);
                }
            }
            Assert.That(uniqueDisplayIds.Count, Is.EqualTo(displayIds.Count), "Display items should not have duplicate IDs");
        }

        [TearDown]
        public override void TearDown()
        {
            _runtime?.Dispose();
            base.TearDown();
        }
    }
}