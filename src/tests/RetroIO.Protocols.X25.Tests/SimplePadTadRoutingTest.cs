//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using Xunit;
using RetroIO.Protocols.X25;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Runtime.Models;
using RetroIO.Protocols.X25.Sintran;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Simple test to verify PAD to TAD data routing works
    /// </summary>
    public class SimplePadTadRoutingTest
    {
        private readonly ILogger _logger;

        public SimplePadTadRoutingTest()
        {
            _logger = LoggerFactory.Create(builder => 
                builder.AddConsole().SetMinimumLevel(LogLevel.Debug)
            ).CreateLogger<SimplePadTadRoutingTest>();
        }

        [Fact]
        public void TestPadTadDataRouting_ShouldReceiveResponse()
        {
            // Arrange
            var config = RuntimeConfig.CreateDefault();
            config.X25.LocalMachineId = "200";
            config.Routing.Enabled = true;
            
            var runtime = new ProtocolRuntime(_logger, config);
            
            // Create PAD client
            var padClient = runtime.CreatePadClient("TestPAD");
            
            // Track responses
            string? receivedResponse = null;
            padClient.DataReceived += (sender, e) => {
                receivedResponse = e.Data;
                _logger.LogInformation("[TEST] PAD received: '{Text}'", receivedResponse);
            };
            
            // Act - connect and send message
            _logger.LogInformation("[TEST] Connecting PAD to 200");
            bool connected = padClient.Connect("200");
            Assert.True(connected, "PAD should connect to TAD");
            
            // Wait for connection establishment
            Thread.Sleep(200);
            
            // Clear welcome message
            receivedResponse = null;
            
            // Send test message
            _logger.LogInformation("[TEST] Sending 'status' command");
            bool sent = padClient.SendText("status\r\n");
            Assert.True(sent, "PAD should send text successfully");
            
            // Wait for response
            Thread.Sleep(300);
            
            // Assert
            Assert.NotNull(receivedResponse);
            Assert.Contains("Connection Status", receivedResponse);
            
            _logger.LogInformation("[TEST] Success - received status response");
        }
    }
}