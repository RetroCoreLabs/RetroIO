//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Protocols;
using RetroIO.Protocols.X25.Runtime.Models;
using RetroIO.Protocols.X25.Sintran;
using Xunit;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Unit tests for LocalTadProtocol class
    /// Tests the TAD protocol handler independently of the full runtime
    /// </summary>
    public class LocalTadProtocolTests : IDisposable
    {
        private readonly ILogger<LocalTadProtocolTests> _logger;
        private readonly LocalTadProtocol _localTadProtocol;

        public LocalTadProtocolTests()
        {
            var loggerFactory = LoggerFactory.Create(builder => 
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Information);
            });
            _logger = loggerFactory.CreateLogger<LocalTadProtocolTests>();
            _localTadProtocol = new LocalTadProtocol(_logger, "200"); // Test TAD server on machine ID 200
        }

        [Fact]
        public void TestHandleOutgoingCall_TadAddress_ShouldReturnTrue()
        {
            // Arrange
            ushort lcn = 1;
            string calledAddress = "200";
            string callingAddress = "100";
            byte[]? userData = null;
            var eventReceived = false;
            X25PacketEventArgs? receivedEvent = null;

            void NotifyCallback(X25PacketEventArgs eventArgs)
            {
                if (!eventReceived && eventArgs.Packet.PTI == 0x0F) // Only capture Call Accepted
                {
                    eventReceived = true;
                    receivedEvent = eventArgs;
                }
            }

            // Act
            _localTadProtocol.PacketGenerated += (sender, eventArgs) => NotifyCallback(eventArgs);
            var result = _localTadProtocol.HandleOutgoingCall(lcn, calledAddress, callingAddress, userData);

            // Assert
            Assert.True(result, "Should handle call to TAD address 200");
            Assert.Equal(1, _localTadProtocol.GetActiveSessionsCount());

            // Wait for timer to fire
            Thread.Sleep(200);

            Assert.True(eventReceived, "Should receive call accepted event");
            Assert.NotNull(receivedEvent);
            Assert.Equal(lcn, receivedEvent.Packet.LCN);
            Assert.Equal(0x0F, receivedEvent.Packet.PTI); // Call Accepted
        }

        [Fact]
        public void TestHandleOutgoingCall_NonTadAddress_ShouldReturnFalse()
        {
            // Arrange
            ushort lcn = 1;
            string calledAddress = "12345"; // Not TAD address
            string callingAddress = "100";
            byte[]? userData = null;

            void NotifyCallback(X25PacketEventArgs eventArgs) { }

            // Act
            _localTadProtocol.PacketGenerated += (sender, eventArgs) => NotifyCallback(eventArgs);
            var result = _localTadProtocol.HandleOutgoingCall(lcn, calledAddress, callingAddress, userData);

            // Assert
            Assert.False(result, "Should not handle call to non-TAD address");
            Assert.Equal(0, _localTadProtocol.GetActiveSessionsCount());
        }

        [Fact]
        public void TestHandleIncomingData_EchoMessage()
        {
            // Arrange
            ushort lcn = 1;
            string testMessage = "Hello World";
            var responseReceived = false;
            string responseData = "";

            void NotifyCallback(X25PacketEventArgs eventArgs)
            {
                if ((eventArgs.Packet.PTI & 0x01) == 0) // Data packet (bit 0 = 0)
                {
                    responseReceived = true;
                    responseData = System.Text.Encoding.ASCII.GetString(eventArgs.Packet.Data);
                }
            }

            // First establish session
            _localTadProtocol.PacketGenerated += (sender, eventArgs) => NotifyCallback(eventArgs);
            _localTadProtocol.HandleOutgoingCall(lcn, "200", "100", null);

            // Act
            var messageBytes = System.Text.Encoding.ASCII.GetBytes(testMessage + "\r\n");
            _localTadProtocol.HandleIncomingData(lcn, messageBytes);

            // Assert
            Assert.True(responseReceived, "Should receive echo response");
            Assert.Contains($"Echo: {testMessage}", responseData);
            Assert.Contains("TAD>", responseData);
        }

        [Fact]
        public void TestHandleIncomingData_HelpCommand()
        {
            // Arrange
            ushort lcn = 1;
            var helpResponseReceived = false;
            string responseData = "";

            void NotifyCallback(X25PacketEventArgs eventArgs)
            {
                if ((eventArgs.Packet.PTI & 0x01) == 0) // Data packet (bit 0 = 0)
                {
                    var data = System.Text.Encoding.ASCII.GetString(eventArgs.Packet.Data);
                    if (data.Contains("Available commands:"))
                    {
                        helpResponseReceived = true;
                        responseData = data;
                    }
                }
            }

            // First establish session
            _localTadProtocol.PacketGenerated += (sender, eventArgs) => NotifyCallback(eventArgs);
            _localTadProtocol.HandleOutgoingCall(lcn, "200", "100", null);

            // Act
            var helpBytes = System.Text.Encoding.ASCII.GetBytes("HELP\r\n");
            _localTadProtocol.HandleIncomingData(lcn, helpBytes);

            // Assert
            Assert.True(helpResponseReceived, "Should receive help response");
            Assert.Contains("Available commands:", responseData);
            Assert.Contains("HELP - Show this help", responseData);
            Assert.Contains("STATUS - Show connection status", responseData);
            Assert.Contains("QUIT - Disconnect", responseData);
        }

        [Fact]
        public void TestHandleIncomingData_StatusCommand()
        {
            // Arrange
            ushort lcn = 1;
            var statusResponseReceived = false;
            string responseData = "";

            void NotifyCallback(X25PacketEventArgs eventArgs)
            {
                if ((eventArgs.Packet.PTI & 0x01) == 0) // Data packet (bit 0 = 0)
                {
                    var data = System.Text.Encoding.ASCII.GetString(eventArgs.Packet.Data);
                    if (data.Contains("Connection Status:"))
                    {
                        statusResponseReceived = true;
                        responseData = data;
                    }
                }
            }

            // First establish session
            _localTadProtocol.PacketGenerated += (sender, eventArgs) => NotifyCallback(eventArgs);
            _localTadProtocol.HandleOutgoingCall(lcn, "200", "100", null);

            // Act
            var statusBytes = System.Text.Encoding.ASCII.GetBytes("STATUS\r\n");
            _localTadProtocol.HandleIncomingData(lcn, statusBytes);

            // Assert
            Assert.True(statusResponseReceived, "Should receive status response");
            Assert.Contains("Connection Status:", responseData);
            Assert.Contains($"LCN: {lcn}", responseData);
            Assert.Contains("Connected:", responseData);
            Assert.Contains("Echo service active", responseData);
        }

        [Fact]
        public void TestHandleIncomingData_QuitCommand()
        {
            // Arrange
            ushort lcn = 1;
            var goodbyeReceived = false;
            var clearRequestReceived = false;
            string responseData = "";

            void NotifyCallback(X25PacketEventArgs eventArgs)
            {
                if ((eventArgs.Packet.PTI & 0x01) == 0) // Data packet (bit 0 = 0)
                {
                    var data = System.Text.Encoding.ASCII.GetString(eventArgs.Packet.Data);
                    if (data.Contains("Goodbye!"))
                    {
                        goodbyeReceived = true;
                        responseData = data;
                    }
                }
                else if (eventArgs.Packet.PTI == 0x13) // Clear Request
                {
                    clearRequestReceived = true;
                }
            }

            // First establish session
            _localTadProtocol.PacketGenerated += (sender, eventArgs) => NotifyCallback(eventArgs);
            _localTadProtocol.HandleOutgoingCall(lcn, "200", "100", null);

            // Act
            var quitBytes = System.Text.Encoding.ASCII.GetBytes("QUIT\r\n");
            _localTadProtocol.HandleIncomingData(lcn, quitBytes);

            // Wait for timer to fire (500ms delay for clear request)
            Thread.Sleep(700);

            // Assert
            Assert.True(goodbyeReceived, "Should receive goodbye message");
            Assert.Contains("Goodbye!", responseData);
            Assert.True(clearRequestReceived, "Should receive clear request after delay");
            Assert.Equal(0, _localTadProtocol.GetActiveSessionsCount()); // Session should be cleared
        }

        [Fact]
        public void TestHandleCallClear()
        {
            // Arrange
            ushort lcn = 1;
            
            void NotifyCallback(X25PacketEventArgs eventArgs) { }

            // First establish session
            _localTadProtocol.PacketGenerated += (sender, eventArgs) => NotifyCallback(eventArgs);
            _localTadProtocol.HandleOutgoingCall(lcn, "200", "100", null);
            Assert.Equal(1, _localTadProtocol.GetActiveSessionsCount());

            // Act
            _localTadProtocol.HandleCallClear(lcn);

            // Assert
            Assert.Equal(0, _localTadProtocol.GetActiveSessionsCount());
        }

        [Fact]
        public void TestMultipleSessions()
        {
            // Arrange
            var sessions = new List<(ushort lcn, string callingAddress)>
            {
                (1, "100"),
                (2, "101"),
                (3, "102")
            };

            // Act - Create multiple sessions
            foreach (var (lcn, callingAddress) in sessions)
            {
                void NotifyCallback(X25PacketEventArgs eventArgs) { }
                _localTadProtocol.PacketGenerated += (sender, eventArgs) => NotifyCallback(eventArgs);
                var result = _localTadProtocol.HandleOutgoingCall(lcn, "200", callingAddress, null);
                Assert.True(result, $"Should handle call for LCN {lcn}");
            }

            // Assert
            Assert.Equal(3, _localTadProtocol.GetActiveSessionsCount());

            // Clear one session
            _localTadProtocol.HandleCallClear(2);
            Assert.Equal(2, _localTadProtocol.GetActiveSessionsCount());

            // Clear remaining sessions
            _localTadProtocol.HandleCallClear(1);
            _localTadProtocol.HandleCallClear(3);
            Assert.Equal(0, _localTadProtocol.GetActiveSessionsCount());
        }

        [Fact]
        public void TestWelcomeMessageSentAfterCallAccepted()
        {
            // Arrange
            ushort lcn = 1;
            var callAcceptedReceived = false;
            var welcomeMessageReceived = false;
            var eventOrder = new List<string>();

            void NotifyCallback(X25PacketEventArgs eventArgs)
            {
                if (eventArgs.Packet.PTI == 0x0F) // Call Accepted
                {
                    callAcceptedReceived = true;
                    eventOrder.Add("CallAccepted");
                }
                else if ((eventArgs.Packet.PTI & 0x01) == 0) // Data packet (bit 0 = 0) (welcome message)
                {
                    var data = System.Text.Encoding.ASCII.GetString(eventArgs.Packet.Data);
                    if (data.Contains("X.25 TAD Loopback Echo Service"))
                    {
                        welcomeMessageReceived = true;
                        eventOrder.Add("WelcomeMessage");
                    }
                }
            }

            // Act
            _localTadProtocol.PacketGenerated += (sender, eventArgs) => NotifyCallback(eventArgs);
            _localTadProtocol.HandleOutgoingCall(lcn, "200", "100", null);

            // Wait for both timers (100ms + 200ms + processing time)
            Thread.Sleep(500);

            // Assert
            Assert.True(callAcceptedReceived, "Should receive call accepted");
            Assert.True(welcomeMessageReceived, "Should receive welcome message");
            Assert.Equal(2, eventOrder.Count);
            Assert.Equal("CallAccepted", eventOrder[0]);
            Assert.Equal("WelcomeMessage", eventOrder[1]);
        }

        [Fact]
        public void TestCaseInsensitiveCommands()
        {
            // Arrange
            ushort lcn = 1;
            var responses = new List<string>();

            void NotifyCallback(X25PacketEventArgs eventArgs)
            {
                if ((eventArgs.Packet.PTI & 0x01) == 0) // Data packet (bit 0 = 0)
                {
                    var data = System.Text.Encoding.ASCII.GetString(eventArgs.Packet.Data);
                    responses.Add(data);
                }
            }

            // First establish session
            _localTadProtocol.PacketGenerated += (sender, eventArgs) => NotifyCallback(eventArgs);
            _localTadProtocol.HandleOutgoingCall(lcn, "200", "100", null);

            // Act - Test different case variations
            var commands = new[] { "help", "Help", "HELP", "status", "Status", "STATUS" };
            
            foreach (var command in commands)
            {
                var commandBytes = System.Text.Encoding.ASCII.GetBytes(command + "\r\n");
                _localTadProtocol.HandleIncomingData(lcn, commandBytes);
            }

            // Assert
            var helpResponses = responses.Count(r => r.Contains("Available commands:"));
            var statusResponses = responses.Count(r => r.Contains("Connection Status:"));
            
            Assert.Equal(3, helpResponses); // 3 help commands in different cases
            Assert.Equal(3, statusResponses); // 3 status commands in different cases
        }

        public void Dispose()
        {
            // LocalTadProtocol doesn't implement IDisposable, but if it did, we'd dispose it here
        }
    }
}