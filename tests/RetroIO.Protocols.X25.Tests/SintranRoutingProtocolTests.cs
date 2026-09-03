using NUnit.Framework;
using NUnit.Framework.Legacy;
using RetroIO.Protocols.X25.Protocols;
using RetroIO.Protocols.X25.Sintran;
using System;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Unit tests for SINTRAN Routing Protocol (0x21 0x13)
    /// Based on real traffic analysis from HDLC dumps
    /// </summary>
    [TestFixture]
    public class SintranRoutingProtocolTests
    {
        [Test]
        public void TestIsRoutingProtocolFrame_ValidFrame_ReturnsTrue()
        {
            // Arrange: Real routing info request from traffic dump (WITH LAPB framing)
            byte[] frame = {
                0x09, 0x00, 0x21, 0x13, 0x00, 0x19, 0x00, 0x00,
                0x66, 0x00, 0x64, 0xFF, 0xFF, 0x00, 0x01, 0xDE, 0x08
            };

            // Act - Use method for frames WITH LAPB (markers at offset 2-3)
            bool isRouting = SintranBinaryProtocol.IsRoutingProtocolFrameWithLapb(frame);

            // Assert
            ClassicAssert.IsTrue(isRouting, "Should recognize valid routing protocol frame");
        }

        [Test]
        public void TestIsRoutingProtocolFrame_TooShort_ReturnsFalse()
        {
            // Arrange: Frame too short (only 8 bytes, need 17 for full LAPB+routing)
            byte[] frame = { 0x09, 0x00, 0x21, 0x13, 0x00, 0x19, 0x00, 0x66 };

            // Act - Use method for frames WITH LAPB
            bool isRouting = SintranBinaryProtocol.IsRoutingProtocolFrameWithLapb(frame);

            // Assert
            ClassicAssert.IsFalse(isRouting, "Should reject too-short frame");
        }

        [Test]
        public void TestIsRoutingProtocolFrame_WrongMarkers_ReturnsFalse()
        {
            // Arrange: Frame with wrong markers (0x20 0x14 instead of 0x21 0x13)
            byte[] frame = {
                0x09, 0x00, 0x20, 0x14, 0x00, 0x19, 0x00, 0x00,
                0x66, 0x00, 0x64, 0xFF, 0xFF, 0x00, 0x01, 0xDE, 0x08
            };

            // Act - Use method for frames WITH LAPB
            bool isRouting = SintranBinaryProtocol.IsRoutingProtocolFrameWithLapb(frame);

            // Assert
            ClassicAssert.IsFalse(isRouting, "Should reject frame with wrong markers");
        }

        [Test]
        public void TestParseRoutingFrame_RoutingInfoRequest_ParsedCorrectly()
        {
            // Arrange: Real routing info request from Node 100 to Node 102
            // From traffic analysis: 09 00 21 13 00 19 00 00 66 00 64 FF FF 00 01 DE 08
            byte[] frame = {
                0x09,       // [0] LAPB address
                0x00,       // [1] LAPB control: N(S)=0, N(R)=0
                0x21, 0x13, // [2-3] Routing protocol markers
                0x00,       // [4] Packet type
                0x19, 0x00, // [5-6] Length: 25 bytes (little-endian)
                0x00,       // [7] Separator
                0x66,       // [8] Destination: Node 102
                0x00,       // [9] Separator
                0x64,       // [10] Source: Node 100
                0xFF, 0xFF, // [11-12] Broadcast flag
                0x00, 0x01, // [13-14] Protocol version
                0xDE,       // [15] Protocol ID: Routing
                0x08        // [16] Command: Request
            };

            // Act
            var parsed = SintranBinaryProtocol.ParseRoutingFrame(frame);

            // Assert
            Assert.That(parsed.LapbAddress, Is.EqualTo(0x09), "LAPB address");
            Assert.That(parsed.LapbControl, Is.EqualTo(0x00), "LAPB control");
            Assert.That(parsed.NS, Is.EqualTo(0), "N(S) should be 0");
            Assert.That(parsed.NR, Is.EqualTo(0), "N(R) should be 0");
            Assert.That(parsed.Marker1, Is.EqualTo(0x21), "Marker 1");
            Assert.That(parsed.Marker2, Is.EqualTo(0x13), "Marker 2");
            Assert.That(parsed.Length, Is.EqualTo(25), "Payload length");
            Assert.That(parsed.DestinationNode, Is.EqualTo(0x66), "Destination node 102");
            Assert.That(parsed.SourceNode, Is.EqualTo(0x64), "Source node 100");
            Assert.That(parsed.ProtocolId, Is.EqualTo(0xDE), "Protocol ID: Routing");
            Assert.That(parsed.CommandCode, Is.EqualTo(0x08), "Command code: Request");
            ClassicAssert.IsTrue(parsed.IsBroadcast, "Should have broadcast flag");
            ClassicAssert.IsTrue(parsed.IsRoutingRequest, "Should be routing request");
            ClassicAssert.IsTrue(parsed.IsValid, "Should be valid routing frame");
            ClassicAssert.IsTrue(parsed.IsRoutingProtocol, "Should be routing protocol");
        }

        [Test]
        public void TestParseRoutingFrame_RoutingInfoResponse_ParsedCorrectly()
        {
            // Arrange: Real routing info response from Node 102 to Node 100
            // From traffic analysis: 09 20 21 13 00 13 00 00 64 00 66 FF FF 00 01 DE 0E
            byte[] frame = {
                0x09,       // [0] LAPB address
                0x20,       // [1] LAPB control: N(S)=1, N(R)=0
                0x21, 0x13, // [2-3] Routing protocol markers
                0x00,       // [4] Packet type
                0x13, 0x00, // [5-6] Length: 19 bytes (little-endian)
                0x00,       // [7] Separator
                0x64,       // [8] Destination: Node 100 (reversed)
                0x00,       // [9] Separator
                0x66,       // [10] Source: Node 102 (reversed)
                0xFF, 0xFF, // [11-12] Broadcast flag
                0x00, 0x01, // [13-14] Protocol version
                0xDE,       // [15] Protocol ID: Routing
                0x0E        // [16] Command: Response
            };

            // Act
            var parsed = SintranBinaryProtocol.ParseRoutingFrame(frame);

            // Assert
            // Note: 0x20 = 0010 0000 → N(S)=000=0 (bits 1-3), N(R)=001=1 (bits 5-7)
            Assert.That(parsed.LapbControl, Is.EqualTo(0x20), "LAPB control");
            Assert.That(parsed.NS, Is.EqualTo(0), "N(S) should be 0");
            Assert.That(parsed.NR, Is.EqualTo(1), "N(R) should be 1");
            Assert.That(parsed.Length, Is.EqualTo(19), "Payload length (shorter than request)");
            Assert.That(parsed.DestinationNode, Is.EqualTo(0x64), "Destination node 100 (reversed)");
            Assert.That(parsed.SourceNode, Is.EqualTo(0x66), "Source node 102 (reversed)");
            Assert.That(parsed.CommandCode, Is.EqualTo(0x0E), "Command code: Response");
            ClassicAssert.IsFalse(parsed.IsRoutingRequest, "Should not be request");
            ClassicAssert.IsTrue(parsed.IsRoutingResponse, "Should be routing response");
        }

        [Test]
        public void TestParseRoutingFrame_LapbSequenceNumbers_ExtractedCorrectly()
        {
            // Arrange: Frame with N(S)=3, N(R)=2
            // LAPB control byte format: N(R) in bits 5-7, N(S) in bits 1-3
            // N(R)=2 (010), N(S)=3 (011) → 01000110 = 0x46
            byte[] frame = {
                0x09, 0x46, 0x21, 0x13, 0x00, 0x13, 0x00, 0x00,
                0x64, 0x00, 0x66, 0xFF, 0xFF, 0x00, 0x01, 0xDE, 0x0E
            };

            // Act
            var parsed = SintranBinaryProtocol.ParseRoutingFrame(frame);

            // Assert
            Assert.That(parsed.NS, Is.EqualTo(3), "N(S) should be 3");
            Assert.That(parsed.NR, Is.EqualTo(2), "N(R) should be 2");
        }

        [Test]
        public void TestCreateRoutingInfoResponse_ValidParameters_CreatesCorrectFrame()
        {
            // Arrange
            byte localNode = 0x64;  // Node 100
            byte remoteNode = 0x66; // Node 102
            byte ns = 1;
            byte nr = 0;

            // Act
            var frame = SintranBinaryProtocol.CreateRoutingInfoResponse(
                localNode, remoteNode, ns, nr);

            // Assert
            // Note: N(S)=1 (bits 1-3), N(R)=0 (bits 5-7) → 0000 0010 = 0x02
            ClassicAssert.IsNotNull(frame, "Frame should not be null");
            Assert.That(frame.Length, Is.EqualTo(17), "Frame should be 17 bytes");
            Assert.That(frame[0], Is.EqualTo(0x09), "LAPB address");
            Assert.That(frame[1], Is.EqualTo(0x02), "LAPB control (N(S)=1, N(R)=0)");
            Assert.That(frame[2], Is.EqualTo(0x21), "Marker 1");
            Assert.That(frame[3], Is.EqualTo(0x13), "Marker 2");
            Assert.That(frame[4], Is.EqualTo(0x00), "Packet type");
            Assert.That(frame[5], Is.EqualTo(0x13), "Length low byte (19)");
            Assert.That(frame[6], Is.EqualTo(0x00), "Length high byte");
            Assert.That(frame[8], Is.EqualTo(remoteNode), "Destination node (remote)");
            Assert.That(frame[10], Is.EqualTo(localNode), "Source node (local)");
            Assert.That(frame[15], Is.EqualTo(0xDE), "Protocol ID: Routing");
            Assert.That(frame[16], Is.EqualTo(0x0E), "Command: Response");
        }

        [Test]
        public void TestCreateRoutingInfoRequest_ValidParameters_CreatesCorrectFrame()
        {
            // Arrange
            byte localNode = 0x64;  // Node 100
            byte remoteNode = 0x66; // Node 102
            byte ns = 0;
            byte nr = 0;

            // Act
            var frame = SintranBinaryProtocol.CreateRoutingInfoRequest(
                localNode, remoteNode, ns, nr);

            // Assert
            ClassicAssert.IsNotNull(frame, "Frame should not be null");
            Assert.That(frame.Length, Is.EqualTo(17), "Frame should be 17 bytes");
            Assert.That(frame[0], Is.EqualTo(0x09), "LAPB address");
            Assert.That(frame[1], Is.EqualTo(0x00), "LAPB control (N(S)=0, N(R)=0)");
            Assert.That(frame[2], Is.EqualTo(0x21), "Marker 1");
            Assert.That(frame[3], Is.EqualTo(0x13), "Marker 2");
            Assert.That(frame[5], Is.EqualTo(0x19), "Length low byte (25)");
            Assert.That(frame[6], Is.EqualTo(0x00), "Length high byte");
            Assert.That(frame[8], Is.EqualTo(remoteNode), "Destination node (remote)");
            Assert.That(frame[10], Is.EqualTo(localNode), "Source node (local)");
            Assert.That(frame[15], Is.EqualTo(0xDE), "Protocol ID: Routing");
            Assert.That(frame[16], Is.EqualTo(0x08), "Command: Request");
        }

        [Test]
        public void TestParseRoutingFrame_TadProtocol_IdentifiedCorrectly()
        {
            // Arrange: Frame with TAD protocol ID (0xDD)
            byte[] frame = {
                0x09, 0x66, 0x21, 0x13, 0x00, 0x0E, 0x00, 0x00,
                0x64, 0x00, 0x66, 0x00, 0x09, 0x01, 0x08, 0xDD, 0x03
            };

            // Act
            var parsed = SintranBinaryProtocol.ParseRoutingFrame(frame);

            // Assert
            Assert.That(parsed.ProtocolId, Is.EqualTo(0xDD), "Protocol ID should be TAD (0xDD)");
            ClassicAssert.IsTrue(parsed.IsTadProtocol, "Should identify TAD protocol");
            ClassicAssert.IsFalse(parsed.IsPadProtocol, "Should not be PAD protocol");
            ClassicAssert.IsFalse(parsed.IsRoutingProtocol, "Should not be routing protocol");
            Assert.That(parsed.ProtocolName, Is.EqualTo("TAD"), "Protocol name should be TAD");
        }

        [Test]
        public void TestParseRoutingFrame_PadProtocol_IdentifiedCorrectly()
        {
            // Arrange: Frame with PAD protocol ID (0xDA)
            byte[] frame = {
                0x09, 0x44, 0x21, 0x13, 0x00, 0x0E, 0x00, 0x00,
                0x66, 0x00, 0x64, 0x00, 0x04, 0x04, 0x00, 0xDA, 0x10
            };

            // Act
            var parsed = SintranBinaryProtocol.ParseRoutingFrame(frame);

            // Assert
            Assert.That(parsed.ProtocolId, Is.EqualTo(0xDA), "Protocol ID should be PAD (0xDA)");
            ClassicAssert.IsTrue(parsed.IsPadProtocol, "Should identify PAD protocol");
            ClassicAssert.IsFalse(parsed.IsTadProtocol, "Should not be TAD protocol");
            ClassicAssert.IsFalse(parsed.IsRoutingProtocol, "Should not be routing protocol");
            Assert.That(parsed.ProtocolName, Is.EqualTo("PAD"), "Protocol name should be PAD");
        }

        [Test]
        public void TestRoutingFrameToString_RoutingRequest_FormatsCorrectly()
        {
            // Arrange
            byte[] frame = {
                0x09, 0x00, 0x21, 0x13, 0x00, 0x19, 0x00, 0x00,
                0x66, 0x00, 0x64, 0xFF, 0xFF, 0x00, 0x01, 0xDE, 0x08
            };

            // Act
            var parsed = SintranBinaryProtocol.ParseRoutingFrame(frame);
            string result = parsed.ToString();

            // Assert
            Assert.That(result, Does.Contain("ROUTING REQ"), "Should indicate routing request");
            Assert.That(result, Does.Contain("100→102"), "Should show correct direction");
            Assert.That(result, Does.Contain("N(S)=0"), "Should show N(S)");
            Assert.That(result, Does.Contain("N(R)=0"), "Should show N(R)");
        }

        [Test]
        public void TestRoutingFrameToString_RoutingResponse_FormatsCorrectly()
        {
            // Arrange
            byte[] frame = {
                0x09, 0x20, 0x21, 0x13, 0x00, 0x13, 0x00, 0x00,
                0x64, 0x00, 0x66, 0xFF, 0xFF, 0x00, 0x01, 0xDE, 0x0E
            };

            // Act
            var parsed = SintranBinaryProtocol.ParseRoutingFrame(frame);
            string result = parsed.ToString();

            // Assert
            Assert.That(result, Does.Contain("ROUTING RESP"), "Should indicate routing response");
            Assert.That(result, Does.Contain("102→100"), "Should show correct direction");
        }

        [Test]
        public void TestRoundTrip_RequestAndResponse_SequenceNumbersCorrect()
        {
            // Arrange: Simulate request → response sequence
            byte localNode = 100;
            byte remoteNode = 102;

            // Act: Create request
            var requestFrame = SintranBinaryProtocol.CreateRoutingInfoRequest(
                localNode, remoteNode, ns: 0, nr: 0);
            var parsedRequest = SintranBinaryProtocol.ParseRoutingFrame(requestFrame);

            // Create response (increment sequence numbers)
            var responseFrame = SintranBinaryProtocol.CreateRoutingInfoResponse(
                remoteNode, localNode, ns: 1, nr: 0);
            var parsedResponse = SintranBinaryProtocol.ParseRoutingFrame(responseFrame);

            // Assert
            Assert.That(parsedRequest.NS, Is.EqualTo(0), "Request N(S) should be 0");
            Assert.That(parsedRequest.NR, Is.EqualTo(0), "Request N(R) should be 0");
            Assert.That(parsedResponse.NS, Is.EqualTo(1), "Response N(S) should be 1");
            Assert.That(parsedResponse.NR, Is.EqualTo(0), "Response N(R) should be 0");

            // Nodes should be reversed in response
            Assert.That(parsedResponse.DestinationNode, Is.EqualTo(parsedRequest.SourceNode),
                "Response destination should match request source");
            Assert.That(parsedResponse.SourceNode, Is.EqualTo(parsedRequest.DestinationNode),
                "Response source should match request destination");
        }

        [Test]
        public void TestParseRoutingFrame_InvalidFrame_ThrowsException()
        {
            // Arrange: Frame without 0x21 0x13 markers
            byte[] frame = {
                0x09, 0x00, 0x99, 0x99, 0x00, 0x19, 0x00, 0x00,
                0x66, 0x00, 0x64, 0xFF, 0xFF, 0x00, 0x01, 0xDE, 0x08
            };

            // Act & Assert
            var ex = ClassicAssert.Throws<ArgumentException>(() =>
                SintranBinaryProtocol.ParseRoutingFrame(frame));
            ClassicAssert.IsNotNull(ex, "Should throw exception for invalid routing frame");
        }

        [Test]
        public void TestConstants_ProtocolIds_HaveCorrectValues()
        {
            // Assert: Verify protocol ID constants match real traffic
            Assert.That(SintranBinaryProtocol.PROTOCOL_ID_PAD, Is.EqualTo(0xDA),
                "PAD protocol ID should be 0xDA (218)");
            Assert.That(SintranBinaryProtocol.PROTOCOL_ID_TAD, Is.EqualTo(0xDD),
                "TAD protocol ID should be 0xDD (221)");
            Assert.That(SintranBinaryProtocol.PROTOCOL_ID_ROUTING, Is.EqualTo(0xDE),
                "Routing protocol ID should be 0xDE (222)");
        }

        [Test]
        public void TestConstants_RoutingCommands_HaveCorrectValues()
        {
            // Assert: Verify routing command constants match real traffic
            Assert.That(SintranBinaryProtocol.ROUTING_CMD_REQUEST, Is.EqualTo(0x08),
                "Routing request command should be 0x08");
            Assert.That(SintranBinaryProtocol.ROUTING_CMD_RESPONSE, Is.EqualTo(0x0E),
                "Routing response command should be 0x0E");
        }
    }
}
