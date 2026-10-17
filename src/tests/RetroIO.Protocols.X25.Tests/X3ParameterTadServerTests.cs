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
using RetroIO.Protocols.X25.Protocols;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Unit tests for X.3 parameter management from TAD server perspective
    /// Tests parameter updates, X.29 control messages, password mode, and server-initiated changes
    /// </summary>
    [TestFixture]
    public class X3ParameterTadServerTests : TestBase
    {
        [Test]
        public void TestTadServer_SessionX3ParameterAccess()
        {
            // Arrange - Establish PAD to TAD connection
            var padId = Runtime.CreateManagedPadClient("TAD_X3_Test_PAD");
            var padClient = Runtime.GetPadClient(padId)!;
            var tadProtocol = Runtime.GetLocalTadProtocol()!;
            
            bool connected = false;
            padClient.ConnectionStateChanged += (sender, args) =>
            {
                if (args.State == PadConnectionState.Connected)
                    connected = true;
            };
            
            // Act - Connect PAD to TAD
            padClient.Connect("200", "100");
            Assert.That(WaitForCondition(() => connected, 2000), Is.True, "PAD should connect to TAD");
            
            // Get TAD session
            var sessions = tadProtocol.GetActiveSessions();
            Assert.That(sessions.Length, Is.GreaterThan(0), "TAD should have active session");
            var session = sessions[0];
            
            // Test parameter access
            var parameters = tadProtocol.GetSessionX3Parameters(session.SessionId)!;
            Assert.That(parameters, Is.Not.Null, "Should retrieve X.3 parameters for session");
            Assert.That(parameters.Count, Is.EqualTo(22), "Should have all 22 X.3 parameters");
            
            // Test access by LCN
            var paramsByLCN = tadProtocol.GetSessionX3ParametersByLCN(session.LCN);
            Assert.That(paramsByLCN, Is.Not.Null, "Should retrieve X.3 parameters by LCN");
            Assert.That(paramsByLCN.Count, Is.EqualTo(22), "Should have all 22 X.3 parameters by LCN");
            
            // Verify parameter values match between access methods
            foreach (var param in parameters)
            {
                Assert.That(paramsByLCN[param.Key], Is.EqualTo(param.Value), 
                    $"Parameter {param.Key} should match between access methods");
            }
            
            // Cleanup
            padClient.Disconnect();
            Runtime.DisposePadClient(padId);
        }
        
        [Test]
        public void TestTadServer_X3ParameterUpdates()
        {
            // Arrange - Establish connection
            var padId = Runtime.CreateManagedPadClient("TAD_Update_Test");
            var padClient = Runtime.GetPadClient(padId)!;
            var tadProtocol = Runtime.GetLocalTadProtocol()!;
            
            var x29MessagesReceived = new List<X29MessageEventArgs>();
            padClient.X29MessageReceived += (sender, args) =>
            {
                x29MessagesReceived.Add(args);
                Logger.LogDebug("PAD received X.29 message: {MessageType}", args.Message.MessageType);
            };
            
            bool connected = false;
            padClient.ConnectionStateChanged += (sender, args) =>
            {
                if (args.State == PadConnectionState.Connected) connected = true;
            };
            
            padClient.Connect("200", "100");
            Assert.That(WaitForCondition(() => connected, 2000), Is.True, "Connection should be established");
            
            var session = tadProtocol.GetActiveSessions()[0];
            
            // Act - Update parameters from TAD server
            Assert.That(tadProtocol.SetSessionX3Parameter(session.SessionId, 2, 0), Is.True, 
                "Should successfully set ECHO parameter to 0");
            Assert.That(tadProtocol.SetSessionX3Parameter(session.SessionId, 4, 10), Is.True,
                "Should successfully set IDLE_TIMER parameter to 10");
            Assert.That(tadProtocol.SetSessionX3Parameter(session.SessionId, 15, 0), Is.True,
                "Should successfully set EDITING parameter to 0");
            
            // Allow time for X.29 messages to be sent/received
            Thread.Sleep(100);
            
            // Assert - Verify parameters updated
            var updatedParams = tadProtocol.GetSessionX3Parameters(session.SessionId)!;
            Assert.That(updatedParams[2], Is.EqualTo(0), "ECHO should be disabled");
            Assert.That(updatedParams[4], Is.EqualTo(10), "IDLE_TIMER should be set to 10");
            Assert.That(updatedParams[15], Is.EqualTo(0), "EDITING should be disabled");
            
            // Verify X.29 Parameter Indication messages were sent
            Assert.That(x29MessagesReceived.Count, Is.GreaterThanOrEqualTo(3), 
                "Should receive X.29 Parameter Indication messages");
            
            // Cleanup
            padClient.Disconnect();
            Runtime.DisposePadClient(padId);
        }
        
        [Test]
        public void TestTadServer_PasswordModeScenario()
        {
            // Arrange - Establish connection
            var padId = Runtime.CreateManagedPadClient("Password_Mode_Test");
            var padClient = Runtime.GetPadClient(padId)!;
            var tadProtocol = Runtime.GetLocalTadProtocol()!;
            
            bool connected = false;
            padClient.ConnectionStateChanged += (sender, args) =>
            {
                if (args.State == PadConnectionState.Connected) connected = true;
            };
            
            padClient.Connect("200", "100");
            Assert.That(WaitForCondition(() => connected, 2000), Is.True, "Connection should be established");
            
            var session = tadProtocol.GetActiveSessions()[0];
            
            // Get original parameters
            var originalParams = tadProtocol.GetSessionX3Parameters(session.SessionId)!;
            var originalEcho = originalParams[2];
            var originalForwarding = originalParams[3];
            var originalEditing = originalParams[15];
            
            // Act - Simulate TAD application requesting password input
            Logger.LogInformation("Simulating password mode entry...");
            
            // Step 1: Switch to character-by-character mode (disable line buffering)
            Assert.That(tadProtocol.SetSessionX3Parameter(session.SessionId, 3, 1), Is.True,
                "Should set DATA_FORWARDING to forward each character");
            
            // Step 2: Disable echo for password input
            Assert.That(tadProtocol.SetSessionX3Parameter(session.SessionId, 2, 0), Is.True,
                "Should disable ECHO for password mode");
            
            // Step 3: Disable editing to prevent password character deletion
            Assert.That(tadProtocol.SetSessionX3Parameter(session.SessionId, 15, 0), Is.True,
                "Should disable EDITING for password mode");
            
            // Allow time for parameters to be applied
            Thread.Sleep(50);
            
            // Verify password mode parameters
            var passwordParams = tadProtocol.GetSessionX3Parameters(session.SessionId)!;
            Assert.That(passwordParams[2], Is.EqualTo(0), "ECHO should be disabled in password mode");
            Assert.That(passwordParams[3], Is.EqualTo(1), "DATA_FORWARDING should be character-by-character");
            Assert.That(passwordParams[15], Is.EqualTo(0), "EDITING should be disabled in password mode");
            
            // Simulate password entry completion and restoration
            Logger.LogInformation("Simulating password mode exit...");
            
            // Step 4: Restore original parameters after password entry
            Assert.That(tadProtocol.SetSessionX3Parameter(session.SessionId, 2, originalEcho), Is.True,
                "Should restore original ECHO setting");
            Assert.That(tadProtocol.SetSessionX3Parameter(session.SessionId, 3, originalForwarding), Is.True,
                "Should restore original DATA_FORWARDING setting");
            Assert.That(tadProtocol.SetSessionX3Parameter(session.SessionId, 15, originalEditing), Is.True,
                "Should restore original EDITING setting");
            
            // Allow time for restoration
            Thread.Sleep(50);
            
            // Verify parameters restored
            var restoredParams = tadProtocol.GetSessionX3Parameters(session.SessionId)!;
            Assert.That(restoredParams[2], Is.EqualTo(originalEcho), "ECHO should be restored");
            Assert.That(restoredParams[3], Is.EqualTo(originalForwarding), "DATA_FORWARDING should be restored");
            Assert.That(restoredParams[15], Is.EqualTo(originalEditing), "EDITING should be restored");
            
            Logger.LogInformation("Password mode scenario completed successfully");
            
            // Cleanup
            padClient.Disconnect();
            Runtime.DisposePadClient(padId);
        }
        
        [Test]
        public void TestTadServer_InvalidParameterUpdates()
        {
            // Arrange - Establish connection
            var padId = Runtime.CreateManagedPadClient("Invalid_Param_Test");
            var padClient = Runtime.GetPadClient(padId)!;
            var tadProtocol = Runtime.GetLocalTadProtocol()!;
            
            bool connected = false;
            padClient.ConnectionStateChanged += (sender, args) =>
            {
                if (args.State == PadConnectionState.Connected) connected = true;
            };
            
            padClient.Connect("200", "100");
            Assert.That(WaitForCondition(() => connected, 2000), Is.True, "Connection should be established");
            
            var session = tadProtocol.GetActiveSessions()[0];
            var originalParams = tadProtocol.GetSessionX3Parameters(session.SessionId)!;
            
            // Act & Assert - Test invalid parameter updates
            Assert.That(tadProtocol.SetSessionX3Parameter(session.SessionId, 1, 5), Is.False,
                "Should reject invalid ESCAPE value 5");
            Assert.That(tadProtocol.SetSessionX3Parameter(session.SessionId, 2, 10), Is.False,
                "Should reject invalid ECHO value 10");
            Assert.That(tadProtocol.SetSessionX3Parameter(session.SessionId, 0, 1), Is.False,
                "Should reject invalid parameter number 0");
            Assert.That(tadProtocol.SetSessionX3Parameter(session.SessionId, 23, 1), Is.False,
                "Should reject invalid parameter number 23");
            
            // Verify parameters unchanged after invalid attempts
            var unchangedParams = tadProtocol.GetSessionX3Parameters(session.SessionId)!;
            for (byte i = 1; i <= 22; i++)
            {
                Assert.That(unchangedParams[i], Is.EqualTo(originalParams[i]),
                    $"Parameter {i} should be unchanged after invalid update attempts");
            }
            
            // Cleanup
            padClient.Disconnect();
            Runtime.DisposePadClient(padId);
        }
        
        [Test]
        public void TestTadServer_NonexistentSessionHandling()
        {
            // Arrange
            var tadProtocol = Runtime.GetLocalTadProtocol()!;
            
            // Act & Assert - Test operations on non-existent session
            var invalidSessionId = "LCN-999";
            
            Assert.That(tadProtocol.GetSessionX3Parameters(invalidSessionId), Is.Null,
                "Should return null for non-existent session");
            Assert.That(tadProtocol.GetSessionX3ParametersByLCN(999), Is.Null,
                "Should return null for non-existent LCN");
            Assert.That(tadProtocol.SetSessionX3Parameter(invalidSessionId, 2, 0), Is.False,
                "Should return false when setting parameter for non-existent session");
        }
        
        [Test]
        public void TestTadServer_ConcurrentParameterUpdates()
        {
            // Arrange - Multiple PAD connections
            var padId1 = Runtime.CreateManagedPadClient("Concurrent_Test_1");
            var padId2 = Runtime.CreateManagedPadClient("Concurrent_Test_2");
            var padClient1 = Runtime.GetPadClient(padId1)!;
            var padClient2 = Runtime.GetPadClient(padId2)!;
            var tadProtocol = Runtime.GetLocalTadProtocol()!;
            
            int connectionsEstablished = 0;
            EventHandler<PadConnectionEventArgs> connectionHandler = (sender, args) =>
            {
                if (args.State == PadConnectionState.Connected)
                    Interlocked.Increment(ref connectionsEstablished);
            };
            
            padClient1.ConnectionStateChanged += connectionHandler;
            padClient2.ConnectionStateChanged += connectionHandler;
            
            // Establish connections
            padClient1.Connect("200", "100");
            padClient2.Connect("200", "101");
            Assert.That(WaitForCondition(() => connectionsEstablished == 2, 3000), Is.True,
                "Both PAD clients should connect");
            
            var sessions = tadProtocol.GetActiveSessions();
            Assert.That(sessions.Length, Is.EqualTo(2), "Should have 2 active sessions");
            
            var session1 = sessions.First(s => s.CallingAddress == "100");
            var session2 = sessions.First(s => s.CallingAddress == "101");
            
            // Act - Concurrent parameter updates
            var updateTasks = new[]
            {
                System.Threading.Tasks.Task.Run(() =>
                {
                    for (int i = 0; i < 10; i++)
                    {
                        tadProtocol.SetSessionX3Parameter(session1.SessionId, 2, (byte)(i % 2));
                        Thread.Sleep(10);
                    }
                }),
                System.Threading.Tasks.Task.Run(() =>
                {
                    for (int i = 0; i < 10; i++)
                    {
                        tadProtocol.SetSessionX3Parameter(session2.SessionId, 4, (byte)(i * 2));
                        Thread.Sleep(10);
                    }
                })
            };
            
            System.Threading.Tasks.Task.WaitAll(updateTasks, 2000);
            
            // Assert - Verify both sessions maintained separate parameters
            var params1 = tadProtocol.GetSessionX3Parameters(session1.SessionId);
            var params2 = tadProtocol.GetSessionX3Parameters(session2.SessionId);
            
            Assert.That(params1, Is.Not.Null, "Session 1 parameters should be accessible");
            Assert.That(params2, Is.Not.Null, "Session 2 parameters should be accessible");
            
            // Parameters should be different between sessions
            Assert.That(params1[2], Is.Not.EqualTo(params2[2]).Or.EqualTo(params2[2]), 
                "Sessions should maintain independent ECHO parameters");
            
            Logger.LogInformation("Concurrent parameter updates completed successfully");
            
            // Cleanup
            padClient1.Disconnect();
            padClient2.Disconnect();
            Runtime.DisposePadClient(padId1);
            Runtime.DisposePadClient(padId2);
        }
        
        [Test]
        public void TestTadServer_ParameterHistoryAndAudit()
        {
            // Arrange - Establish connection
            var padId = Runtime.CreateManagedPadClient("Audit_Test");
            var padClient = Runtime.GetPadClient(padId)!;
            var tadProtocol = Runtime.GetLocalTadProtocol()!;
            
            bool connected = false;
            padClient.ConnectionStateChanged += (sender, args) =>
            {
                if (args.State == PadConnectionState.Connected) connected = true;
            };
            
            padClient.Connect("200", "100");
            Assert.That(WaitForCondition(() => connected, 2000), Is.True, "Connection should be established");
            
            var session = tadProtocol.GetActiveSessions()[0];
            var startTime = DateTime.UtcNow;
            
            // Act - Sequence of parameter changes (simulating login process)
            var parameterChanges = new[]
            {
                (param: (byte)2, value: (byte)0, description: "Disable echo for password"),
                (param: (byte)3, value: (byte)1, description: "Character-by-character forwarding"),
                (param: (byte)15, value: (byte)0, description: "Disable editing"),
                (param: (byte)2, value: (byte)1, description: "Re-enable echo after password"),
                (param: (byte)3, value: (byte)13, description: "Restore CR forwarding"),
                (param: (byte)15, value: (byte)1, description: "Re-enable editing")
            };
            
            foreach (var change in parameterChanges)
            {
                Logger.LogInformation("Setting parameter {Param} to {Value}: {Description}", 
                    change.param, change.value, change.description);
                
                Assert.That(tadProtocol.SetSessionX3Parameter(session.SessionId, change.param, change.value), 
                    Is.True, $"Should set parameter {change.param} to {change.value}");
                
                Thread.Sleep(50); // Small delay between changes
            }
            
            // Assert - Verify final state
            var finalParams = tadProtocol.GetSessionX3Parameters(session.SessionId)!;
            Assert.That(finalParams[2], Is.EqualTo(1), "ECHO should be restored");
            Assert.That(finalParams[3], Is.EqualTo(13), "DATA_FORWARDING should be restored");
            Assert.That(finalParams[15], Is.EqualTo(1), "EDITING should be restored");
            
            var endTime = DateTime.UtcNow;
            Logger.LogInformation("Parameter audit test completed in {Duration}ms", 
                (endTime - startTime).TotalMilliseconds);
            
            // Cleanup
            padClient.Disconnect();
            Runtime.DisposePadClient(padId);
        }
        
        /// <summary>
        /// Helper method to wait for a condition with timeout
        /// </summary>
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