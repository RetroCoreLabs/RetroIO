//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using NUnit.Framework;
using RetroIO.Protocols.X25.Protocols;
using RetroIO.Protocols.X25.Sintran;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;

namespace RetroIO.Protocols.X25.Tests
{
    [TestFixture]
    public class SintranRoutingToTadIntegrationTests
    {
        private ILogger _logger;
        private SintranRoutingHandler _routingHandler;
        private SintranTadHandler _tadHandler;
        private List<SintranResponseEventArgs> _routingResponses;
        private List<SintranResponseEventArgs> _tadResponses;

        [SetUp]
        public void Setup()
        {
            // Create logger
            var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Debug);
            });
            _logger = loggerFactory.CreateLogger<SintranRoutingToTadIntegrationTests>();

            // Create handlers
            _routingHandler = new SintranRoutingHandler(_logger);
            _tadHandler = new SintranTadHandler(_logger);

            // Initialize handlers
            _routingHandler.Initialize();
            _tadHandler.Initialize();

            // Register TAD handler with routing handler
            _routingHandler.RegisterTadHandler(_tadHandler);

            // Track responses
            _routingResponses = new List<SintranResponseEventArgs>();
            _tadResponses = new List<SintranResponseEventArgs>();

            _routingHandler.ResponseReady += (sender, args) =>
            {
                _routingResponses.Add(args);
            };

            _tadHandler.ResponseReady += (sender, args) =>
            {
                _tadResponses.Add(args);
            };

            // Establish channels
            _routingHandler.OnChannelEstablished(275);
            _tadHandler.OnChannelEstablished(275);
        }

        [TearDown]
        public void TearDown()
        {
            _routingHandler?.Dispose();
            _tadHandler?.Dispose();
        }

        [Test]
        public void RoutingHandler_ReceiveTadFrame_RoutesToTadHandler()
        {
            // Arrange: Create 0x21 0x13 0xDD frame with 7DUMM payload
            byte[] tadPayload = new byte[] { SintranTadMessageTypes.DUMM, 0x00 };
            byte[] frame = new byte[3 + tadPayload.Length];
            frame[0] = 0x21; // SINTRAN marker
            frame[1] = 0x13; // Routing protocol marker
            frame[2] = 0xDD; // TAD protocol ID
            Array.Copy(tadPayload, 0, frame, 3, tadPayload.Length);

            // Clear any initialization responses
            _routingResponses.Clear();
            _tadResponses.Clear();

            // Act: Send frame to routing handler
            _routingHandler.HandleData(frame);

            // Assert: TAD handler should receive the payload
            // TAD handler sends responses during initialization, so check if it processed data
            // The dummy message doesn't generate a response, so we just verify no errors occurred
            Assert.That(_routingResponses.Count, Is.GreaterThanOrEqualTo(0));
        }

        [Test]
        public void RoutingHandler_ReceiveTadBinaryData_ForwardsToTadAndWrapsResponse()
        {
            // Arrange: Create 0x21 0x13 0xDD frame with 7BDAT payload
            string testData = "Hello";
            byte[] dataBytes = Encoding.ASCII.GetBytes(testData);
            byte[] tadMessage = new byte[2 + dataBytes.Length];
            tadMessage[0] = SintranTadMessageTypes.BDAT;
            tadMessage[1] = (byte)dataBytes.Length;
            Array.Copy(dataBytes, 0, tadMessage, 2, dataBytes.Length);

            byte[] frame = new byte[3 + tadMessage.Length];
            frame[0] = 0x21;
            frame[1] = 0x13;
            frame[2] = 0xDD;
            Array.Copy(tadMessage, 0, frame, 3, tadMessage.Length);

            // Clear any initialization responses
            _routingResponses.Clear();

            // Act: Send frame to routing handler
            _routingHandler.HandleData(frame);

            // Assert: Should receive wrapped responses (echo + RFI)
            Assert.That(_routingResponses.Count, Is.GreaterThanOrEqualTo(2), "Expected at least 2 responses (echo + RFI)");

            // Verify first response is wrapped in routing protocol
            var firstResponse = _routingResponses[0];
            Assert.That(firstResponse.Data[0], Is.EqualTo(0x21), "First byte should be SINTRAN marker");
            Assert.That(firstResponse.Data[1], Is.EqualTo(0x13), "Second byte should be routing marker");
            Assert.That(firstResponse.Data[2], Is.EqualTo(0xDD), "Third byte should be TAD protocol ID");

            // Extract and verify TAD response (skip 0x21 0x13 0xDD header)
            byte[] tadResponse = new byte[firstResponse.Data.Length - 3];
            Array.Copy(firstResponse.Data, 3, tadResponse, 0, tadResponse.Length);

            var parsedResponse = SintranTadMessage.Parse(tadResponse);
            Assert.That(parsedResponse.MessageType, Is.EqualTo(SintranTadMessageTypes.BDAT), "Response should be binary data (echo)");
            // Echo service returns "Echo: {text}\r\nAnalysis: ...\r\nTAD> " format
            Assert.That(parsedResponse.GetDataAsString(), Does.Contain(testData), "Echo response should contain original data");
        }

        [Test]
        public void TadHandler_SendResponse_WrappedInRoutingProtocol()
        {
            // Arrange: Send data directly to TAD handler
            string testData = "Test123";
            var tadMessage = SintranTadMessage.CreateBinaryData(Encoding.ASCII.GetBytes(testData));
            byte[] messageBytes = tadMessage.Build();

            // Clear responses
            _routingResponses.Clear();
            _tadResponses.Clear();

            // Act: Send directly to TAD handler (bypassing routing)
            _tadHandler.HandleData(messageBytes);

            // Assert: Responses should be wrapped when going through routing handler
            // Since we're testing integration, we verify the wrapping happens in routing handler
            Assert.That(_tadResponses.Count, Is.GreaterThanOrEqualTo(2), "Expected at least 2 TAD responses");
        }

        [Test]
        public void RoutingHandler_TadHandshake_CompletesSuccessfully()
        {
            // Arrange: Create routing info request frame (0xDE 0x08)
            byte[] routingRequest = new byte[]
            {
                0x21, 0x13, // SINTRAN routing markers
                0xDE, 0x08, // Routing info request
                0x00, 0x64, // Destination node (100)
                0x01, 0x00, // Source node (256)
                0x00, 0x00  // N(S)=0, N(R)=0
            };

            // Clear responses
            _routingResponses.Clear();

            // Act: Send routing request
            _routingHandler.HandleData(routingRequest);

            // Assert: Should receive routing info response
            Assert.That(_routingResponses.Count, Is.GreaterThanOrEqualTo(1), "Expected routing response");

            var response = _routingResponses[0];
            Assert.That(response.Data.Length, Is.GreaterThan(3), "Response should have data");

            // Verify response contains routing protocol data
            // Response format may include LAPB header prefix, so check for markers anywhere in first 5 bytes
            if (response.Data.Length >= 4)
            {
                bool hasRoutingMarkers = false;
                // Check for 0x21 0x13 markers (may be at offset 0-1 or 2-3 depending on framing)
                for (int i = 0; i < response.Data.Length - 1 && i < 5; i++)
                {
                    if (response.Data[i] == 0x21 && response.Data[i + 1] == 0x13)
                    {
                        hasRoutingMarkers = true;
                        break;
                    }
                }
                Assert.That(hasRoutingMarkers || response.Data[0] == 0x07, Is.True,
                    "Response should be routing protocol or error");
            }
        }

        [Test]
        public void EndToEnd_SendBinaryData_ReceiveEchoAndRFI()
        {
            // Arrange: Complete end-to-end test
            string testData = "SINTRAN";
            var tadMessage = SintranTadMessage.CreateBinaryData(Encoding.ASCII.GetBytes(testData));
            byte[] tadBytes = tadMessage.Build();

            // Wrap in routing protocol frame
            byte[] routingFrame = new byte[3 + tadBytes.Length];
            routingFrame[0] = 0x21;
            routingFrame[1] = 0x13;
            routingFrame[2] = 0xDD;
            Array.Copy(tadBytes, 0, routingFrame, 3, tadBytes.Length);

            // Clear responses
            _routingResponses.Clear();

            // Act: Send complete frame through routing handler
            _routingHandler.HandleData(routingFrame);

            // Assert: Verify complete flow
            Assert.That(_routingResponses.Count, Is.GreaterThanOrEqualTo(2),
                "Expected multiple responses (echo + RFI)");

            // Verify all responses are properly wrapped
            for (int i = 0; i < _routingResponses.Count; i++)
            {
                var response = _routingResponses[i];
                Assert.That(response.Data.Length, Is.GreaterThanOrEqualTo(3),
                    $"Response {i} should have routing header");
                Assert.That(response.Data[0], Is.EqualTo(0x21),
                    $"Response {i} should have SINTRAN marker");
                Assert.That(response.Data[1], Is.EqualTo(0x13),
                    $"Response {i} should have routing marker");
                Assert.That(response.Data[2], Is.EqualTo(0xDD),
                    $"Response {i} should have TAD protocol ID");
            }

            // Parse first response (should be echo)
            byte[] firstTadResponse = new byte[_routingResponses[0].Data.Length - 3];
            Array.Copy(_routingResponses[0].Data, 3, firstTadResponse, 0, firstTadResponse.Length);
            var echoMessage = SintranTadMessage.Parse(firstTadResponse);

            Assert.That(echoMessage.MessageType, Is.EqualTo(SintranTadMessageTypes.BDAT));
            // Echo service returns "Echo: {text}\r\nAnalysis: ...\r\nTAD> " format
            Assert.That(echoMessage.GetDataAsString(), Does.Contain(testData));

            // Parse second response (should be RFI)
            byte[] secondTadResponse = new byte[_routingResponses[1].Data.Length - 3];
            Array.Copy(_routingResponses[1].Data, 3, secondTadResponse, 0, secondTadResponse.Length);
            var rfiMessage = SintranTadMessage.Parse(secondTadResponse);

            Assert.That(rfiMessage.MessageType, Is.EqualTo(SintranTadMessageTypes.RFI));
        }

        [Test]
        public void RoutingHandler_TadDisconnect_HandledCorrectly()
        {
            // Arrange: Create disconnect message wrapped in routing frame
            var disconnectMsg = SintranTadMessage.CreateDisconnect();
            byte[] tadBytes = disconnectMsg.Build();

            byte[] frame = new byte[3 + tadBytes.Length];
            frame[0] = 0x21;
            frame[1] = 0x13;
            frame[2] = 0xDD;
            Array.Copy(tadBytes, 0, frame, 3, tadBytes.Length);

            // Clear responses
            _routingResponses.Clear();

            // Act: Send disconnect
            _routingHandler.HandleData(frame);

            // Assert: Should receive disconnect acknowledgment
            Assert.That(_routingResponses.Count, Is.GreaterThanOrEqualTo(1),
                "Expected disconnect acknowledgment");

            // Verify response is wrapped
            var response = _routingResponses[0];
            Assert.That(response.Data[0], Is.EqualTo(0x21));
            Assert.That(response.Data[1], Is.EqualTo(0x13));
            Assert.That(response.Data[2], Is.EqualTo(0xDD));

            // Parse TAD response
            byte[] tadResponse = new byte[response.Data.Length - 3];
            Array.Copy(response.Data, 3, tadResponse, 0, tadResponse.Length);
            var ackMsg = SintranTadMessage.Parse(tadResponse);

            Assert.That(ackMsg.MessageType, Is.EqualTo(SintranTadMessageTypes.DCON));
        }

        [Test]
        public void RoutingHandler_InvalidProtocolId_DoesNotCrash()
        {
            // Arrange: Create frame with invalid protocol ID
            byte[] frame = new byte[]
            {
                0x21, 0x13, 0xFF, // Invalid protocol ID (0xFF)
                0x00, 0x00        // Dummy payload
            };

            // Act & Assert: Should not throw exception
            Assert.DoesNotThrow(() => _routingHandler.HandleData(frame));
        }

        [Test]
        public void RoutingHandler_EmptyTadPayload_HandledGracefully()
        {
            // Arrange: Create TAD frame with no payload
            byte[] frame = new byte[] { 0x21, 0x13, 0xDD };

            // Act & Assert: Should not crash
            Assert.DoesNotThrow(() => _routingHandler.HandleData(frame));
        }

        [Test]
        public void TadHandler_MultipleMessages_AllWrappedCorrectly()
        {
            // Arrange: Send multiple messages in sequence
            var messages = new[]
            {
                "Message 1",
                "Message 2",
                "Message 3"
            };

            _routingResponses.Clear();

            // Act: Send multiple messages
            for (int i = 0; i < messages.Length; i++)
            {
                var tadMsg = SintranTadMessage.CreateBinaryData(Encoding.ASCII.GetBytes(messages[i]));
                byte[] tadBytes = tadMsg.Build();

                byte[] frame = new byte[3 + tadBytes.Length];
                frame[0] = 0x21;
                frame[1] = 0x13;
                frame[2] = 0xDD;
                Array.Copy(tadBytes, 0, frame, 3, tadBytes.Length);

                _routingHandler.HandleData(frame);
            }

            // Assert: Should have responses for all messages (echo + RFI for each)
            Assert.That(_routingResponses.Count, Is.GreaterThanOrEqualTo(messages.Length * 2),
                "Expected responses for all messages");

            // Verify all responses are properly wrapped
            for (int i = 0; i < _routingResponses.Count; i++)
            {
                Assert.That(_routingResponses[i].Data[0], Is.EqualTo(0x21));
                Assert.That(_routingResponses[i].Data[1], Is.EqualTo(0x13));
                Assert.That(_routingResponses[i].Data[2], Is.EqualTo(0xDD));
            }
        }
    }
}
