//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using Xunit;
using RetroIO.Protocols.X25;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Runtime.Models;
using RetroIO.Protocols.X25.Sintran;
using RetroIO.Protocols.X25.Modules;
using RetroIO.Protocols.X25.Protocols;
using RetroIO.Protocols.X25.Services;
using RetroIO.Protocols.X25.Sintran.Services;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Test to debug PAD to TAD data flow - messages sent from PAD client should reach TAD server
    /// </summary>
    public class PadToTadDataFlowTest
    {
        private readonly ILogger _logger;
        private readonly ProtocolRuntime _runtime;
        private readonly LocalTadProtocol _localTadProtocol;
        private readonly PadClient _padClient;
        private readonly List<X25PacketEventArgs> _receivedPackets;
        private readonly List<string> _tadReceivedMessages;
        
        public PadToTadDataFlowTest()
        {
            _logger = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug)).CreateLogger<PadToTadDataFlowTest>();
            
            // Create runtime with default config
            var config = RuntimeConfig.CreateDefault();
            config.X25.LocalMachineId = "200";
            config.Routing.Enabled = true;
            _runtime = new ProtocolRuntime(_logger, config);
            
            // Get the built-in LocalTadProtocol from ProtocolRuntime (instead of creating a separate instance)
            _localTadProtocol = _runtime.GetLocalTadProtocol();
            
            // Create PAD client
            _padClient = _runtime.CreatePadClient("TestPAD");
            
            // Track packets and messages
            _receivedPackets = new List<X25PacketEventArgs>();
            _tadReceivedMessages = new List<string>();
            
            // Subscribe to events
            _localTadProtocol.PacketGenerated += OnTadPacketGenerated;
            _padClient.DataReceived += OnPadDataReceived;
        }
        
        private void OnTadPacketGenerated(object? sender, X25PacketEventArgs e)
        {
            _logger.LogDebug("[TEST] TAD generated packet: LCN={LCN}, PTI=0x{PTI:X2}", e.Packet.LCN, e.Packet.PTI);
            _receivedPackets.Add(e);
        }
        
        private void OnPadDataReceived(object? sender, PadDataEventArgs e)
        {
            _logger.LogDebug("[TEST] PAD received data: '{Text}'", e.Data);
            _tadReceivedMessages.Add(e.Data);
        }
        
        [Fact]
        public void TestPadToTadDataFlow_ShouldReceiveMessages()
        {
            // Arrange - establish connection
            _logger.LogInformation("[TEST] ========== Starting PAD to TAD Data Flow Test ==========");
            
            // Step 1: PAD client connects to address 200
            _logger.LogInformation("[TEST] Step 1: PAD client connecting to 200");
            bool connected = _padClient.Connect("200");
            Assert.True(connected, "PAD client should connect successfully");
            
            // Wait for connection to establish
            Thread.Sleep(100);
            
            // Verify connection established
            Assert.Equal(1, _localTadProtocol.GetActiveSessionsCount());
            
            _logger.LogInformation("[TEST] Step 1 Complete: Connection established, TAD sessions={Sessions}", 
                                 _localTadProtocol.GetActiveSessionsCount());
            
            // Step 2: Clear any welcome messages
            _receivedPackets.Clear();
            _tadReceivedMessages.Clear();
            
            // Step 3: PAD client sends a test message
            string testMessage = "status";
            _logger.LogInformation("[TEST] Step 3: PAD client sending message: '{Message}'", testMessage);
            
            bool sentSuccessfully = _padClient.SendText(testMessage + "\r\n");
            Assert.True(sentSuccessfully, "PAD client should send message successfully");
            
            // Wait for message processing
            Thread.Sleep(200);
            
            // Step 4: Debug - check what happened
            _logger.LogInformation("[TEST] Step 4: Debugging message flow");
            _logger.LogInformation("[TEST] - TAD Protocol active sessions: {Count}", _localTadProtocol.GetActiveSessionsCount());
            _logger.LogInformation("[TEST] - Received packets from TAD: {Count}", _receivedPackets.Count);
            _logger.LogInformation("[TEST] - Messages received by PAD: {Count}", _tadReceivedMessages.Count);
            
            // Check protocol runtime routing
            var runtimeStats = _runtime.GetStatistics();
            _logger.LogInformation("[TEST] - Runtime stats: X25 TX={TX}, RX={RX}", 
                                 runtimeStats.X25.DataPacketsTransmitted, 
                                 runtimeStats.X25.DataPacketsReceived);
            
            // Check LCN bindings
            var lcnBindingsProperty = typeof(ProtocolRuntime).GetField("_lcnBindings", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (lcnBindingsProperty?.GetValue(_runtime) is Dictionary<ushort, IProtocolModule> lcnBindings)
            {
                _logger.LogInformation("[TEST] - LCN bindings count: {Count}", lcnBindings.Count);
                foreach (var binding in lcnBindings)
                {
                    _logger.LogInformation("[TEST] - LCN {LCN} -> {Module}", binding.Key, binding.Value.GetType().Name);
                }
            }
            
            // Assert - PAD message should have triggered a TAD response
            Assert.True(_receivedPackets.Count > 0, 
                       $"TAD should have generated response packets. Received: {_receivedPackets.Count}");
            
            // The TAD should respond to "status" command with status information
            Assert.True(_tadReceivedMessages.Count > 0, 
                       $"PAD should have received response messages. Received: {_tadReceivedMessages.Count}");
            
            // Check if response contains expected status information
            string combinedResponse = string.Join("", _tadReceivedMessages);
            Assert.Contains("Connection Status", combinedResponse);
            
            _logger.LogInformation("[TEST] ========== Test Completed Successfully ==========");
        }
        
        [Fact]
        public void TestDirectTadProtocolDataHandling()
        {
            // Test LocalTadProtocol directly to isolate the issue
            _logger.LogInformation("[TEST] ========== Testing Direct TAD Protocol Data Handling ==========");
            
            // Arrange
            ushort lcn = 1;
            string calledAddress = "200";
            string callingAddress = "100";
            byte[]? userData = null;
            
            // Step 1: Establish TAD session
            bool handled = _localTadProtocol.HandleOutgoingCall(lcn, calledAddress, callingAddress, userData);
            Assert.True(handled, "TAD should handle call to address 200");
            
            Thread.Sleep(100); // Allow welcome message to be sent
            
            // Clear packets from welcome
            _receivedPackets.Clear();
            
            // Step 2: Send data directly to TAD
            string testData = "status";
            byte[] dataBytes = Encoding.ASCII.GetBytes(testData + "\r\n");
            
            _logger.LogInformation("[TEST] Sending data directly to TAD: '{Data}'", testData);
            _localTadProtocol.HandleIncomingData(lcn, dataBytes);
            
            // Wait for processing
            Thread.Sleep(100);
            
            // Assert - TAD should have responded
            Assert.True(_receivedPackets.Count > 0, 
                       $"TAD should have generated response to status command. Received: {_receivedPackets.Count}");
            
            // Check all response packets for expected content
            // (first packets may be echo bytes, command response comes after)
            var allResponseText = new StringBuilder();
            foreach (var pkt in _receivedPackets)
            {
                string text = Encoding.ASCII.GetString(pkt.Packet.Data ?? Array.Empty<byte>());
                allResponseText.Append(text);
            }
            string fullResponse = allResponseText.ToString();
            _logger.LogInformation("[TEST] TAD full response: '{Response}'", fullResponse.Replace("\r\n", "\\r\\n"));
            Assert.Contains("Connection Status", fullResponse);
            
            _logger.LogInformation("[TEST] ========== Direct TAD Test Completed ==========");
        }
    }
}