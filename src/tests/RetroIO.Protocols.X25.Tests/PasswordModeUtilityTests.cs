//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Modules;
using RetroIO.Protocols.X25.Runtime.Models;
using RetroIO.Protocols.X25.Sintran;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Unit tests for password mode utility methods in LocalTadProtocol
    /// Tests the new EnterPasswordMode, ExitPasswordMode, and IsInPasswordMode methods
    /// </summary>
    [TestFixture]
    public class PasswordModeUtilityTests : TestBase
    {
        [Test]
        public void TestPasswordModeUtilities_EnterAndExit()
        {
            // Arrange - Establish PAD to TAD connection
            var padId = Runtime.CreateManagedPadClient("Password_Utility_Test");
            var padClient = Runtime.GetPadClient(padId)!;
            var tadProtocol = Runtime.GetLocalTadProtocol();
            
            bool connected = false;
            padClient.ConnectionStateChanged += (sender, args) =>
            {
                if (args.State == PadConnectionState.Connected)
                    connected = true;
            };
            
            padClient.Connect("200", "100");
            Assert.That(WaitForCondition(() => connected, 2000), Is.True, "Connection should be established");
            
            var session = tadProtocol.GetActiveSessions()[0];
            var sessionId = session.SessionId;
            
            // Verify initial state (not in password mode)
            Assert.That(tadProtocol.IsInPasswordMode(sessionId), Is.False, "Should not be in password mode initially");
            
            // Act - Enter password mode
            Assert.That(tadProtocol.EnterPasswordMode(sessionId), Is.True, "Should successfully enter password mode");
            
            // Assert - Verify password mode parameters
            Assert.That(tadProtocol.IsInPasswordMode(sessionId), Is.True, "Should be in password mode");
            var parameters = tadProtocol.GetSessionX3Parameters(sessionId)!;
            Assert.That(parameters[2], Is.EqualTo(0), "ECHO should be disabled");
            // X.3 3.3: value 1 is alphanumerics only, so a password with a symbol would not forward.
            // 127 is every flag (alphanumerics, CR, and all of columns 0 and 1 and DEL). Space and the
            // symbols in columns 2 to 7 are in no flag, so the idle timer (X.3 3.4, 1 = one twentieth of a
            // second) forwards them.
            Assert.That(parameters[3], Is.EqualTo(127), "DATA_FORWARDING should be every flag (X.3 3.3)");
            Assert.That(parameters[4], Is.EqualTo(1), "IDLE_TIMER should be one twentieth of a second (X.3 3.4)");
            Assert.That(parameters[15], Is.EqualTo(0), "EDITING should be disabled");
            
            // Act - Exit password mode
            Assert.That(tadProtocol.ExitPasswordMode(sessionId), Is.True, "Should successfully exit password mode");
            
            // Assert - Verify parameters restored
            Assert.That(tadProtocol.IsInPasswordMode(sessionId), Is.False, "Should not be in password mode after exit");
            parameters = tadProtocol.GetSessionX3Parameters(sessionId)!;
            // The session starts on the simple standard profile (Table 1/X.28): echo 1, forwarding 126, idle timer 0, editing 0
            Assert.That(parameters[2], Is.EqualTo(1), "ECHO should be restored");
            Assert.That(parameters[3], Is.EqualTo(126), "DATA_FORWARDING should be restored to original value");
            Assert.That(parameters[4], Is.EqualTo(0), "IDLE_TIMER should be restored to original value");
            Assert.That(parameters[15], Is.EqualTo(0), "EDITING should be restored");
            
            // Cleanup
            padClient.Disconnect();
            Runtime.DisposePadClient(padId);
        }
        
        [Test]
        public void TestPasswordModeUtilities_ByLCN()
        {
            // Arrange - Establish connection
            var padId = Runtime.CreateManagedPadClient("Password_LCN_Test");
            var padClient = Runtime.GetPadClient(padId)!;
            var tadProtocol = Runtime.GetLocalTadProtocol();
            
            bool connected = false;
            padClient.ConnectionStateChanged += (sender, args) =>
            {
                if (args.State == PadConnectionState.Connected)
                    connected = true;
            };
            
            padClient.Connect("200", "100");
            Assert.That(WaitForCondition(() => connected, 2000), Is.True, "Connection should be established");
            
            var session = tadProtocol.GetActiveSessions()[0];
            var lcn = session.LCN;
            
            // Test LCN-based methods
            Assert.That(tadProtocol.IsInPasswordModeByLCN(lcn), Is.False, "Should not be in password mode initially");
            
            Assert.That(tadProtocol.EnterPasswordModeByLCN(lcn), Is.True, "Should enter password mode by LCN");
            Assert.That(tadProtocol.IsInPasswordModeByLCN(lcn), Is.True, "Should be in password mode");
            
            Assert.That(tadProtocol.ExitPasswordModeByLCN(lcn), Is.True, "Should exit password mode by LCN");
            Assert.That(tadProtocol.IsInPasswordModeByLCN(lcn), Is.False, "Should not be in password mode after exit");
            
            // Cleanup
            padClient.Disconnect();
            Runtime.DisposePadClient(padId);
        }
        
        [Test]
        public void TestPasswordModeUtilities_NonexistentSession()
        {
            // Arrange
            var tadProtocol = Runtime.GetLocalTadProtocol();
            var invalidSessionId = "LCN-999";
            
            // Act & Assert - Test operations on non-existent session
            Assert.That(tadProtocol.EnterPasswordMode(invalidSessionId), Is.False,
                "Should return false for non-existent session");
            Assert.That(tadProtocol.ExitPasswordMode(invalidSessionId), Is.False,
                "Should return false for non-existent session");
            Assert.That(tadProtocol.IsInPasswordMode(invalidSessionId), Is.False,
                "Should return false for non-existent session");
            
            // Test LCN-based methods
            Assert.That(tadProtocol.EnterPasswordModeByLCN(999), Is.False,
                "Should return false for non-existent LCN");
            Assert.That(tadProtocol.ExitPasswordModeByLCN(999), Is.False,
                "Should return false for non-existent LCN");
            Assert.That(tadProtocol.IsInPasswordModeByLCN(999), Is.False,
                "Should return false for non-existent LCN");
        }
        
        [Test]
        public void TestPasswordModeUtilities_ParameterRestoration()
        {
            // Arrange - Establish connection and set custom parameters
            var padId = Runtime.CreateManagedPadClient("Password_Restore_Test");
            var padClient = Runtime.GetPadClient(padId)!;
            var tadProtocol = Runtime.GetLocalTadProtocol();
            
            bool connected = false;
            padClient.ConnectionStateChanged += (sender, args) =>
            {
                if (args.State == PadConnectionState.Connected)
                    connected = true;
            };
            
            padClient.Connect("200", "100");
            Assert.That(WaitForCondition(() => connected, 2000), Is.True, "Connection should be established");
            
            var session = tadProtocol.GetActiveSessions()[0];
            var sessionId = session.SessionId;
            
            // Set custom parameters before entering password mode
            Assert.That(tadProtocol.SetSessionX3Parameter(sessionId, 2, 1), Is.True, "Should set custom ECHO");
            Assert.That(tadProtocol.SetSessionX3Parameter(sessionId, 3, 10), Is.True, "Should set custom DATA_FORWARDING");
            Assert.That(tadProtocol.SetSessionX3Parameter(sessionId, 4, 7), Is.True, "Should set custom IDLE_TIMER");
            Assert.That(tadProtocol.SetSessionX3Parameter(sessionId, 15, 1), Is.True, "Should set custom EDITING");

            Thread.Sleep(50); // Allow parameters to be applied
            
            // Enter password mode
            Assert.That(tadProtocol.EnterPasswordMode(sessionId), Is.True, "Should enter password mode");
            
            // Verify password mode parameters
            var passwordParams = tadProtocol.GetSessionX3Parameters(sessionId)!;
            Assert.That(passwordParams[2], Is.EqualTo(0), "ECHO should be disabled in password mode");
            Assert.That(passwordParams[3], Is.EqualTo(127), "DATA_FORWARDING should be every flag (X.3 3.3)");
            Assert.That(passwordParams[4], Is.EqualTo(1), "IDLE_TIMER should be one twentieth of a second (X.3 3.4)");
            Assert.That(passwordParams[15], Is.EqualTo(0), "EDITING should be disabled in password mode");
            
            // Exit password mode
            Assert.That(tadProtocol.ExitPasswordMode(sessionId), Is.True, "Should exit password mode");
            
            // Verify original parameters restored
            var restoredParams = tadProtocol.GetSessionX3Parameters(sessionId)!;
            Assert.That(restoredParams[2], Is.EqualTo(1), "ECHO should be restored to custom value");
            Assert.That(restoredParams[3], Is.EqualTo(10), "DATA_FORWARDING should be restored to custom value");
            Assert.That(restoredParams[4], Is.EqualTo(7), "IDLE_TIMER should be restored to custom value");
            Assert.That(restoredParams[15], Is.EqualTo(1), "EDITING should be restored to custom value");
            
            // Cleanup
            padClient.Disconnect();
            Runtime.DisposePadClient(padId);
        }
        
        [Test]
        public void TestPasswordModeUtilities_LoginCommand()
        {
            // Arrange - Establish connection
            var padId = Runtime.CreateManagedPadClient("Password_Login_Test");
            var padClient = Runtime.GetPadClient(padId)!;
            var tadProtocol = Runtime.GetLocalTadProtocol();
            
            var receivedData = new System.Collections.Generic.List<string>();
            padClient.DataReceived += (sender, args) => receivedData.Add(args.Data);
            
            bool connected = false;
            padClient.ConnectionStateChanged += (sender, args) =>
            {
                if (args.State == PadConnectionState.Connected)
                    connected = true;
            };
            
            padClient.Connect("200", "100");
            Assert.That(WaitForCondition(() => connected, 2000), Is.True, "Connection should be established");
            
            // Clear welcome message
            receivedData.Clear();
            
            // Act - Send LOGIN command
            padClient.SendText("LOGIN\r\n");
            
            // Wait for response
            Thread.Sleep(100);
            
            // Assert - Verify LOGIN command response
            Assert.That(receivedData.Count, Is.GreaterThan(0), "Should receive response to LOGIN command");
            var response = string.Join("", receivedData);
            Assert.That(response, Does.Contain("Login Demonstration"), "Should contain login demo text");
            Assert.That(response, Does.Contain("EnterPasswordMode"), "Should mention password mode methods");
            Assert.That(response, Does.Contain("Username:"), "Should show username prompt");
            
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