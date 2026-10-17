//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
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
    /// Unit tests to validate the GUI relationship display logic
    /// Tests the two-line display functionality without actually running the GUI
    /// </summary>
    [TestFixture]
    public class GuiRelationshipDisplayTests : TestBase
    {
        private List<PacketInfo> _simulatedPacketHistory;

        public class PacketInfo
        {
            public int PacketNumber { get; set; }
            public DateTime Timestamp { get; set; }
            public string Type { get; set; } = "";
            public string Direction { get; set; } = "";
            public ushort Length { get; set; }
            public byte[] Data { get; set; } = Array.Empty<byte>();
            public string Description { get; set; } = "";
            public PacketInfo? RelatedPacket { get; set; }
            public string RelationshipType { get; set; } = "";
        }

        [SetUp]
        public override void Setup()
        {
            base.Setup();
            _simulatedPacketHistory = new List<PacketInfo>();
        }

        [Test]
        public void TX_X25_LAPB_Relationship_Should_Show_Two_Lines_With_X25_Primary()
        {
            // Arrange - Create related X.25 and LAPB packets (TX direction)
            var now = DateTime.Now;
            
            var x25Packet = new PacketInfo
            {
                PacketNumber = 1,
                Timestamp = now,
                Type = "X25",
                Direction = "TX",
                Length = 18,
                Description = "X.25 CallRequest Packet",
                RelationshipType = "EncapsulatedBy"
            };
            
            var lapbPacket = new PacketInfo
            {
                PacketNumber = 2,
                Timestamp = now.AddMilliseconds(10),
                Type = "LAPB", 
                Direction = "TX",
                Length = 20,
                Description = "LAPB Information Frame",
                RelationshipType = "Encapsulates"
            };
            
            // Set bidirectional relationships
            x25Packet.RelatedPacket = lapbPacket;
            lapbPacket.RelatedPacket = x25Packet;
            
            _simulatedPacketHistory.Add(x25Packet);
            _simulatedPacketHistory.Add(lapbPacket);

            // Act - Test display logic for both packets
            var x25Lines = FormatPacketDisplayLines(x25Packet);
            var lapbLines = FormatPacketDisplayLines(lapbPacket);

            // Assert
            Logger.LogInformation("X.25 packet display lines: {Count}", x25Lines.Count);
            for (int i = 0; i < x25Lines.Count; i++)
            {
                Logger.LogInformation("  Line {LineNo}: {Content}", i + 1, x25Lines[i]);
            }
            
            Logger.LogInformation("LAPB packet display lines: {Count}", lapbLines.Count);
            for (int i = 0; i < lapbLines.Count; i++)
            {
                Logger.LogInformation("  Line {LineNo}: {Content}", i + 1, lapbLines[i]);
            }

            // X.25 packet (TX) should be primary and show two lines
            Assert.That(x25Lines.Count, Is.EqualTo(2), "X.25 TX packet should show two-line display (primary + secondary)");
            Assert.That(x25Lines[0], Contains.Substring("X.25 CallRequest"), "First line should be X.25 packet (primary)");
            Assert.That(x25Lines[1], Contains.Substring("==>"), "Second line should be indented LAPB frame (secondary)");
            Assert.That(x25Lines[1], Contains.Substring("LAPB Information"), "Second line should contain LAPB description");

            // LAPB packet should be filtered out (no lines) because it's the secondary
            Assert.That(lapbLines.Count, Is.EqualTo(0), "LAPB TX packet should be filtered out (secondary in relationship)");
        }

        [Test]
        public void RX_X25_LAPB_Relationship_Should_Show_Two_Lines_With_LAPB_Primary()
        {
            // Arrange - Create related X.25 and LAPB packets (RX direction)
            var now = DateTime.Now;
            
            var lapbPacket = new PacketInfo
            {
                PacketNumber = 1,
                Timestamp = now,
                Type = "LAPB",
                Direction = "RX",
                Length = 20,
                Description = "LAPB Information Frame",
                RelationshipType = "Encapsulates"
            };
            
            var x25Packet = new PacketInfo
            {
                PacketNumber = 2,
                Timestamp = now.AddMilliseconds(5),
                Type = "X25",
                Direction = "RX",
                Length = 18,
                Description = "X.25 Data Packet", 
                RelationshipType = "EncapsulatedBy"
            };
            
            // Set bidirectional relationships
            lapbPacket.RelatedPacket = x25Packet;
            x25Packet.RelatedPacket = lapbPacket;

            // Act - Test display logic
            var lapbLines = FormatPacketDisplayLines(lapbPacket);
            var x25Lines = FormatPacketDisplayLines(x25Packet);

            // Assert
            Logger.LogInformation("LAPB packet display lines: {Count}", lapbLines.Count);
            Logger.LogInformation("X.25 packet display lines: {Count}", x25Lines.Count);

            // LAPB packet (RX) should be primary and show two lines
            Assert.That(lapbLines.Count, Is.EqualTo(2), "LAPB RX packet should show two-line display (primary + secondary)");
            Assert.That(lapbLines[0], Contains.Substring("LAPB Information"), "First line should be LAPB frame (primary)");
            Assert.That(lapbLines[1], Contains.Substring("==>"), "Second line should be indented X.25 packet (secondary)");
            Assert.That(lapbLines[1], Contains.Substring("X.25 Data"), "Second line should contain X.25 description");

            // X.25 packet should be filtered out (secondary in relationship)
            Assert.That(x25Lines.Count, Is.EqualTo(0), "X.25 RX packet should be filtered out (secondary in relationship)");
        }

        [Test]
        public void Unrelated_Packets_Should_Show_Single_Line()
        {
            // Arrange - Create unrelated packets
            var x25Packet = new PacketInfo
            {
                PacketNumber = 1,
                Timestamp = DateTime.Now,
                Type = "X25",
                Direction = "TX",
                Length = 18,
                Description = "X.25 CallRequest Packet",
                RelatedPacket = null,
                RelationshipType = ""
            };

            // Act
            var lines = FormatPacketDisplayLines(x25Packet);

            // Assert
            Assert.That(lines.Count, Is.EqualTo(1), "Unrelated packet should show single line");
            Assert.That(lines[0], Contains.Substring("X.25 CallRequest"), "Line should contain packet description");
            Assert.That(lines[0], Does.Not.Contain("==>"), "Line should not be indented (not a secondary line)");
        }

        // Simulate the GUI display logic methods
        private List<string> FormatPacketDisplayLines(PacketInfo packet)
        {
            var lines = new List<string>();
            
            // Check if we have a relationship and if this packet should be the parent
            if (packet.RelatedPacket != null && ShouldShowAsParent(packet))
            {
                // Show primary line first (the "important" layer)
                var primaryPacket = GetPrimaryPacket(packet);
                var secondaryPacket = GetSecondaryPacket(packet);
                
                lines.Add(FormatPacketDisplayLine(primaryPacket));
                lines.Add($"   ==>  {FormatRelatedPacketLine(secondaryPacket)}");
            }
            else if (packet.RelatedPacket == null)
            {
                // Single line for unrelated packets
                lines.Add(FormatPacketDisplayLine(packet));
            }
            // Skip packets that are already shown as secondary lines
            
            return lines;
        }
        
        private bool ShouldShowAsParent(PacketInfo packet)
        {
            if (packet.RelatedPacket == null) return false;
            
            // For TX: X.25 is primary (business logic), LAPB is secondary (transport)
            // For RX: LAPB is primary (received), X.25 is secondary (extracted content)
            if (packet.Direction == "TX")
            {
                return packet.Type == "X25";
            }
            else
            {
                return packet.Type == "LAPB";
            }
        }
        
        private PacketInfo GetPrimaryPacket(PacketInfo packet)
        {
            if (ShouldShowAsParent(packet))
                return packet;
            else
                return packet.RelatedPacket!;
        }
        
        private PacketInfo GetSecondaryPacket(PacketInfo packet)
        {
            if (ShouldShowAsParent(packet))
                return packet.RelatedPacket!;
            else
                return packet;
        }
        
        private string FormatPacketDisplayLine(PacketInfo packet)
        {
            return $"[{packet.Timestamp:HH:mm:ss.fff}] {packet.Direction,-3} {packet.Type,-6} {packet.Description}";
        }
        
        private string FormatRelatedPacketLine(PacketInfo packet)
        {
            return $"[{packet.Timestamp:HH:mm:ss.fff}] {packet.Direction,-3} {packet.Type,-6} {packet.Description}";
        }
    }
}