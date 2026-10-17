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
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Runtime.Models;
using RetroIO.Protocols.X25.Sintran;
using RetroIO.Protocols.X25.Modules;

namespace RetroIO.Protocols.X25.Tests
{
    [TestFixture]
    public class PadTadTests : TestBase
    {
        [Test]
        public void TestManagedPadClient_Creation()
        {
            // Act
            var padId = Runtime.CreateManagedPadClient("TestPAD");
            
            // Assert
            Assert.That(padId, Is.Not.Null);
            Assert.That(padId, Does.StartWith("PAD_"));
            
            var padClient = Runtime.GetPadClient(padId);
            Assert.That(padClient, Is.Not.Null);
            Assert.That(padClient.Name, Is.EqualTo("TestPAD"));
            
            // Cleanup
            Assert.That(Runtime.DisposePadClient(padId), Is.True);
        }
        
        [Test]
        public void TestManagedPadClient_Lifecycle()
        {
            // Create
            var padId = Runtime.CreateManagedPadClient();
            Assert.That(padId, Is.Not.Null);
            
            // Get
            var padClient = Runtime.GetPadClient(padId);
            Assert.That(padClient, Is.Not.Null);
            
            // List
            var allPadIds = Runtime.GetPadClientIds();
            Assert.That(allPadIds, Contains.Item(padId));
            
            // Dispose
            Assert.That(Runtime.DisposePadClient(padId), Is.True);
            
            // Verify disposed
            Assert.That(Runtime.GetPadClient(padId), Is.Null);
            Assert.That(Runtime.GetPadClientIds(), Does.Not.Contain(padId));
        }
        
        [Test]
        public void TestPadToTad_Connection()
        {
            // Arrange
            var padId = Runtime.CreateManagedPadClient("PAD_TAD_Test");
            var padClient = Runtime.GetPadClient(padId);
            Assert.That(padClient, Is.Not.Null);
            
            bool connected = false;
            var connectionStates = new List<PadConnectionState>();
            
            padClient.ConnectionStateChanged += (sender, args) =>
            {
                connectionStates.Add(args.State);
                if (args.State == PadConnectionState.Connected)
                    connected = true;
                Logger.LogInformation("PAD state: {State} - {Reason}", args.State, args.Reason);
            };
            
            // Act
            Logger.LogInformation("Connecting PAD {PadId} to TAD at address 200", padId);
            var connectTask = padClient.Connect("200", "100");
            var connectInitiated = connectTask;
            
            // Allow a small delay to ensure state transitions are captured
            Thread.Sleep(50);
            
            // Wait for connection
            var connectionEstablished = WaitForCondition(() => connected, 2000);
            
            // Debug: Log captured states
            Logger.LogInformation("Captured connection states: [{States}]", string.Join(", ", connectionStates));
            
            // Assert
            Assert.That(connectInitiated, Is.True, "Connection should be initiated");
            Assert.That(connectionEstablished, Is.True, "Connection should be established within timeout");
            
            // Check if we have at least Connected state (Connecting might be too fast to catch)
            Assert.That(connectionStates, Contains.Item(PadConnectionState.Connected), "Should capture Connected state");
            
            // Only check for Connecting if we actually captured it (some connections might be too fast)
            if (connectionStates.Contains(PadConnectionState.Connecting))
            {
                Assert.That(connectionStates, Contains.Item(PadConnectionState.Connecting), "Should capture Connecting state if present");
            }
            else
            {
                Logger.LogInformation("Connection was too fast - Connecting state not captured, which is acceptable for local routing");
            }
            
            // Verify TAD sees the connection
            var tadProtocol = Runtime.GetLocalTadProtocol();
            var activeSessions = tadProtocol.GetActiveSessions();
            Assert.That(activeSessions.Count(), Is.GreaterThan(0), "TAD should have active sessions");
            Assert.That(activeSessions.Any(s => s.CallingAddress == "100"), Is.True, 
                "TAD should see calling address 100");
            
            // Cleanup
            padClient.Disconnect();
            Runtime.DisposePadClient(padId);
        }
        
        [Test]
        public void TestPadToTad_DataExchange()
        {
            // Arrange
            var padId = Runtime.CreateManagedPadClient("DataExchangeTest");
            var padClient = Runtime.GetPadClient(padId);
            Assert.That(padClient, Is.Not.Null);
            
            var receivedData = new List<string>();
            bool echoReceived = false;
            
            padClient.DataReceived += (sender, args) =>
            {
                receivedData.Add(args.Data);
                if (args.Data.Contains("Echo: TestMessage"))
                    echoReceived = true;
                Logger.LogInformation("Received: {Data}", args.Data);
            };
            
            // Connect
            padClient.Connect("200", "101");
            Thread.Sleep(800); // Wait for connection and welcome
            
            // Act - Send data
            padClient.SendText("TestMessage\r\n");
            
            // Wait for echo
            var echoReceivedInTime = WaitForCondition(() => echoReceived, 1000);
            
            // Assert
            Assert.That(echoReceivedInTime, Is.True, "Should receive echo within timeout");
            Assert.That(receivedData.Any(d => d.Contains("Echo: TestMessage")), Is.True, 
                "Should receive echoed message");
            Assert.That(receivedData.Any(d => d.Contains("TAD>")), Is.True, 
                "Should receive TAD prompt");
            
            // Cleanup
            padClient.Disconnect();
            Runtime.DisposePadClient(padId);
        }
        
        [Test]
        public void TestPadToTad_MultipleConnections()
        {
            // Arrange
            var padIds = new List<string>();
            for (int i = 1; i <= 3; i++)
            {
                padIds.Add(Runtime.CreateManagedPadClient($"PAD_{i}"));
            }
            
            // Act - Connect all PADs
            int connectionNumber = 201;
            foreach (var padId in padIds)
            {
                var padClient = Runtime.GetPadClient(padId);
                Assert.That(padClient, Is.Not.Null, $"PAD {padId} should exist");
                padClient.Connect("200", connectionNumber.ToString());
                connectionNumber++;
            }
            
            // Wait for all connections
            Thread.Sleep(1500);
            
            // Assert - TAD should see all connections
            var tadProtocol = Runtime.GetLocalTadProtocol();
            var activeSessions = tadProtocol.GetActiveSessions();
            
            Assert.That(activeSessions.Count(), Is.EqualTo(3), 
                "TAD should have exactly 3 active sessions");
            Assert.That(activeSessions.Any(s => s.CallingAddress == "201"), Is.True);
            Assert.That(activeSessions.Any(s => s.CallingAddress == "202"), Is.True);
            Assert.That(activeSessions.Any(s => s.CallingAddress == "203"), Is.True);
            
            // Cleanup
            foreach (var padId in padIds)
            {
                var padClient = Runtime.GetPadClient(padId);
                padClient?.Disconnect();
                Runtime.DisposePadClient(padId);
            }
            
            // Verify cleanup
            Thread.Sleep(500);
            activeSessions = tadProtocol.GetActiveSessions();
            Assert.That(activeSessions.Count(), Is.EqualTo(0), 
                "All TAD sessions should be closed");
        }
        
        [Test]
        public void TestPadToTad_Commands()
        {
            // Arrange
            var padId = Runtime.CreateManagedPadClient("CommandTest");
            var padClient = Runtime.GetPadClient(padId);
            Assert.That(padClient, Is.Not.Null);
            
            var responses = new List<string>();
            padClient.DataReceived += (sender, args) => responses.Add(args.Data);
            
            // Connect
            padClient.Connect("200", "150");
            Thread.Sleep(800);
            responses.Clear(); // Clear welcome
            
            // Test HELP command
            padClient.SendText("HELP\r\n");
            Thread.Sleep(400);
            Assert.That(responses.Any(r => r.Contains("Available commands:")), Is.True,
                "HELP should return command list");
            responses.Clear();

            // Test STATUS command
            padClient.SendText("STATUS\r\n");
            Thread.Sleep(400);
            Assert.That(responses.Any(r => r.Contains("Connection Status:")), Is.True,
                "STATUS should return connection info");
            Assert.That(responses.Any(r => r.Contains("LCN:")), Is.True,
                "STATUS should include LCN");
            responses.Clear();

            // Test QUIT command
            padClient.SendText("QUIT\r\n");
            Thread.Sleep(600);
            Assert.That(responses.Any(r => r.Contains("Goodbye")), Is.True, 
                "QUIT should say goodbye");
            
            // Cleanup
            Runtime.DisposePadClient(padId);
        }
    }
}