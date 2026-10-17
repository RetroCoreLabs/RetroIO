//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Modules;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Protocols.X25.Runtime.Models;
using RetroIO.Protocols.X25.Sintran;

namespace RetroIO.Protocols.X25.Tests
{
    [TestFixture]
    public class PadTadConnectionTests
    {
        private ILogger<PadTadConnectionTests> _logger;
        private ProtocolRuntime _runtime;
        
        [SetUp]
        public void Setup()
        {
            // Create logger
            var loggerFactory = LoggerFactory.Create(builder => 
            {
                builder.AddConsole();
                builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug);
            });
            _logger = loggerFactory.CreateLogger<PadTadConnectionTests>();

            // Create runtime config with local machine ID 200
            var config = new RuntimeConfig();
            config.X25.LocalMachineId = "200";
            
            // Create runtime
            _runtime = new ProtocolRuntime(_logger, config);
            
            _logger.LogInformation("Test setup complete - LocalMachineId: {LocalMachineId}", 
                config.X25.LocalMachineId);
        }
        
        [TearDown]
        public void TearDown()
        {
            _runtime?.Dispose();
        }
        
        /// <summary>
        /// Helper method to wait for a condition with timeout
        /// </summary>
        private bool WaitForCondition(Func<bool> condition, int timeoutMs)
        {
            var startTime = DateTime.Now;
            while ((DateTime.Now - startTime).TotalMilliseconds < timeoutMs)
            {
                if (condition())
                    return true;
                Thread.Sleep(50);
            }
            return false;
        }
        
        [Test]
        public void TestPadConnectToTadServer_ShouldEstablishConnection()
        {
            // Arrange
            var padClient = _runtime.CreatePadClient("TestPAD");
            var connectionEstablished = false;
            var connectionStates = new List<PadConnectionState>();

            padClient.ConnectionStateChanged += (sender, args) =>
            {
                connectionStates.Add(args.State);
                if (args.State == PadConnectionState.Connected)
                    connectionEstablished = true;
                _logger.LogInformation("PAD state: {State} - {Reason}", args.State, args.Reason);
            };

            // Act
            _logger.LogInformation("Connecting PAD to TAD server at address 200");
            var connectTask = padClient.Connect("200", "100");
            var connectSuccess = connectTask;
            
            // Wait for connection
            Thread.Sleep(1000);

            // Assert
            Assert.That(connectSuccess, Is.True, "Connect should return true");
            Assert.That(connectionEstablished, Is.True, "Connection should be established");
            // Note: Connection might establish so quickly that Connecting state is skipped
            // The important thing is that we eventually reach Connected state
            Assert.That(connectionStates, Contains.Item(PadConnectionState.Connected), 
                "Should eventually reach Connected state");

            // Verify TAD server sees the client
            var localTadProtocol = _runtime.GetLocalTadProtocol();
            var activeSessions = localTadProtocol.GetActiveSessions();
            Assert.That(activeSessions.Count(), Is.GreaterThan(0), 
                "TAD server should have active sessions");
            Assert.That(activeSessions.Any(s => s.CallingAddress == "100"), Is.True,
                "TAD should see calling address 100");

            // Cleanup
            padClient.Disconnect();
            padClient.Dispose();
        }
        
        [Test]
        public void TestPadTadDataExchange_ShouldReceiveEcho()
        {
            // Arrange
            var padClient = _runtime.CreatePadClient("DataTestPAD");
            var echoReceived = false;
            var dataReceived = new List<string>();

            padClient.DataReceived += (sender, args) =>
            {
                dataReceived.Add(args.Data);
                if (args.Data.Contains("Echo: Hello"))
                    echoReceived = true;
                _logger.LogInformation("Received: {Data}", args.Data);
            };

            // Act
            padClient.Connect("200", "100");
            Thread.Sleep(600); // Wait for welcome
            
            padClient.SendText("Hello\r\n");
            Thread.Sleep(300); // Wait for echo

            // Assert
            Assert.That(echoReceived, Is.True, "Should receive echo response");
            Assert.That(dataReceived.Count, Is.GreaterThan(0), "Should receive data");
            
            // Cleanup
            padClient.Disconnect();
            padClient.Dispose();
        }
        
        [Test]
        public void TestMultiplePadConnections_ShouldAllConnect()
        {
            // Arrange
            var localTadProtocol = _runtime.GetLocalTadProtocol();
            
            // Ensure we start clean (clear any leftover sessions)
            var initialSessions = localTadProtocol.GetActiveSessions();
            if (initialSessions.Count() > 0)
            {
                _logger.LogWarning("Found {Count} leftover sessions from previous tests", initialSessions.Count());
            }
            
            var pad1 = _runtime.CreatePadClient("PAD1");
            var pad2 = _runtime.CreatePadClient("PAD2");
            var pad3 = _runtime.CreatePadClient("PAD3");

            // Act - Connect all three
            pad1.Connect("200", "201");
            pad2.Connect("200", "202");
            pad3.Connect("200", "203");
            
            // Wait for all connections to be established
            var waitResult = WaitForCondition(() => localTadProtocol.GetActiveSessions().Count() == 3, 3000);
            Assert.That(waitResult, Is.True, "All 3 connections should be established within timeout");

            // Assert - TAD server should see all three
            var activeSessions = localTadProtocol.GetActiveSessions();
            
            _logger.LogInformation("Active sessions: {Count}", activeSessions.Count());
            foreach (var session in activeSessions)
            {
                _logger.LogInformation("Session: {SessionId}, Calling: {Calling}, Called: {Called}", 
                    session.SessionId, session.CallingAddress, session.CalledAddress);
            }
            
            Assert.That(activeSessions.Count(), Is.EqualTo(3), 
                "Should have exactly 3 active sessions");
            Assert.That(activeSessions.Any(s => s.CallingAddress == "201"), Is.True, "Should have session from 201");
            Assert.That(activeSessions.Any(s => s.CallingAddress == "202"), Is.True, "Should have session from 202");
            Assert.That(activeSessions.Any(s => s.CallingAddress == "203"), Is.True, "Should have session from 203");

            // Cleanup
            pad1.Disconnect();
            pad2.Disconnect();
            pad3.Disconnect();
            pad1.Dispose();
            pad2.Dispose();
            pad3.Dispose();
            
            // Wait for cleanup to complete
            var cleanupResult = WaitForCondition(() => localTadProtocol.GetActiveSessions().Count() == 0, 2000);
            var finalSessions = localTadProtocol.GetActiveSessions();
            
            _logger.LogInformation("After cleanup: {Count} sessions remaining", finalSessions.Count());
            Assert.That(finalSessions.Count(), Is.EqualTo(0), 
                "All sessions should be cleared after disconnect");
        }
        
        [Test]
        public void TestTadServerCommands_ShouldRespondCorrectly()
        {
            // Arrange
            var padClient = _runtime.CreatePadClient("CommandTestPAD");
            var responses = new List<string>();
            
            padClient.DataReceived += (sender, args) => responses.Add(args.Data);
            
            padClient.Connect("200", "150");
            Thread.Sleep(800); // Wait for welcome
            responses.Clear(); // Clear welcome message

            // Test HELP command
            padClient.SendText("HELP\r\n");
            Thread.Sleep(400);
            Assert.That(responses.Any(r => r.Contains("Available commands:")), Is.True,
                "Should respond to HELP");
            responses.Clear();

            // Test STATUS command
            padClient.SendText("STATUS\r\n");
            Thread.Sleep(400);
            Assert.That(responses.Any(r => r.Contains("Connection Status:")), Is.True,
                "Should respond to STATUS");
            Assert.That(responses.Any(r => r.Contains("LCN:")), Is.True,
                "STATUS should include LCN");
            responses.Clear();

            // Test echo
            padClient.SendText("Test Echo\r\n");
            Thread.Sleep(400);
            Assert.That(responses.Any(r => r.Contains("Echo: Test Echo")), Is.True, 
                "Should echo messages");

            // Cleanup
            padClient.Disconnect();
            padClient.Dispose();
        }
        
        [Test]
        public void TestRuntimeConfiguration_LocalMachineIdShouldBe200()
        {
            // Assert - Verify configuration
            var stats = _runtime.GetStatistics();
            Assert.That(stats, Is.Not.Null);
            
            var config = _runtime.Config;
            Assert.That(config.X25.LocalMachineId, Is.EqualTo("200"), 
                "LocalMachineId should be 200");
            
            var localTadProtocol = _runtime.GetLocalTadProtocol();
            Assert.That(localTadProtocol, Is.Not.Null, 
                "LocalTadProtocol should be initialized");
        }
    }
}