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
using NUnit.Framework;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Modules;
using RetroIO.Protocols.X25.Protocols;
using RetroIO.Protocols.X25.Runtime.Models;
using RetroIO.Protocols.X25.Sintran;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Simple test to debug data forwarding issues
    /// </summary>
    [TestFixture]
    public class SimpleDataForwardingTest : TestBase
    {
        [Test]
        public void TestDataForwardingAfterConnection()
        {
            // Arrange - Create PAD client and connect
            var padId = Runtime.CreateManagedPadClient("DataForward_Test");
            var padClient = Runtime.GetPadClient(padId);
            
            var receivedData = new List<string>();
            padClient!.DataReceived += (sender, args) => receivedData.Add(args.Data);
            
            bool connected = false;
            padClient.ConnectionStateChanged += (sender, args) =>
            {
                Logger.LogInformation("PAD connection state: {State} - {Reason}", args.State, args.Reason);
                if (args.State == PadConnectionState.Connected)
                    connected = true;
            };
            
            // Act - Connect to TAD server
            Logger.LogInformation("Starting CALL 200 connection test...");
            padClient.Connect("200", "100");
            Assert.That(WaitForCondition(() => connected, 2000), Is.True, "PAD should connect to TAD");
            
            // Clear any welcome messages
            receivedData.Clear();
            
            Logger.LogInformation("Connection established. Testing character forwarding...");
            
            // Test 1: Send individual characters (should not forward yet)
            Logger.LogInformation("Sending individual characters 'h', 'e', 'l', 'p'");
            padClient.OnKey('h');
            padClient.OnKey('e');
            padClient.OnKey('l');
            padClient.OnKey('p');
            
            Thread.Sleep(100); // Wait for any immediate responses
            Logger.LogInformation("After individual chars - received {Count} messages", receivedData.Count);
            
            // Test 2: Send carriage return (should trigger forwarding)
            Logger.LogInformation("Sending carriage return to trigger forwarding");
            padClient.OnKey('\r');
            
            Thread.Sleep(200); // Wait for forwarding and response
            Logger.LogInformation("After CR - received {Count} messages", receivedData.Count);
            
            // Log all received data
            for (int i = 0; i < receivedData.Count; i++)
            {
                Logger.LogInformation("Received data [{Index}]: '{Data}'", i, receivedData[i]);
            }
            
            // Assert - We should have received echo from TAD server
            Assert.That(receivedData.Count, Is.GreaterThan(0), "Should receive response from TAD server");
            
            // Cleanup
            padClient.Disconnect();
            Runtime.DisposePadClient(padId);
        }
        
        private new bool WaitForCondition(Func<bool> condition, int timeoutMs)
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
    }
}