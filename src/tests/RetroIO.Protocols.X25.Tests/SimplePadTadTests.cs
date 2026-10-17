//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Modules;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Protocols.X25.Runtime.Models;
using RetroIO.Protocols.X25.Sintran;
using Xunit;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Simple tests for PAD/TAD loopback functionality via address "200"
    /// Tests the basic functionality without complex configuration
    /// </summary>
    public class SimplePadTadTests : IDisposable
    {
        private readonly ILogger<SimplePadTadTests> _logger;
        private readonly ProtocolRuntime _runtime;

        public SimplePadTadTests()
        {
            // Create simple logger with debug level
            var loggerFactory = LoggerFactory.Create(builder => 
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Debug);
            });
            _logger = loggerFactory.CreateLogger<SimplePadTadTests>();

            // Create simple runtime config
            var config = new RuntimeConfig();
            
            // Create runtime
            _runtime = new ProtocolRuntime(_logger, config);
        }

        [Fact]
        public void TestBasicPadConnectToTad()
        {
            // Arrange
            var padClient = _runtime.CreatePadClient("TestPAD");
            var connected = false;

            padClient.ConnectionStateChanged += (sender, args) =>
            {
                if (args.State == PadConnectionState.Connected)
                    connected = true;
            };

            // Act
            var success = padClient.Connect("200", "100");

            // Wait for connection to establish
            System.Threading.Thread.Sleep(500);

            // Assert
            Assert.True(success, "Should successfully initiate connection");
            Assert.True(connected, "Should establish connection");
            
            // Cleanup
            padClient.Disconnect();
            padClient.Dispose();
        }

        [Fact]
        public void TestPadReceivesWelcomeFromTad()
        {
            // Arrange
            var padClient = _runtime.CreatePadClient("TestPAD");
            var welcomeReceived = false;

            padClient.DataReceived += (sender, args) =>
            {
                if (args.Data.Contains("X.25 TAD Loopback Echo Service"))
                    welcomeReceived = true;
            };

            // Act
            padClient.Connect("200", "100");
            System.Threading.Thread.Sleep(600); // Wait for timers to complete

            // Assert
            Assert.True(welcomeReceived, "Should receive welcome message");
            
            // Cleanup
            padClient.Disconnect();
            padClient.Dispose();
        }

        [Fact]
        public void TestEchoFunctionality()
        {
            // Arrange
            var padClient = _runtime.CreatePadClient("TestPAD");
            var echoReceived = false;

            padClient.DataReceived += (sender, args) =>
            {
                if (args.Data.Contains("Echo: Hello"))
                    echoReceived = true;
            };

            // Act
            padClient.Connect("200", "100");
            System.Threading.Thread.Sleep(600); // Wait for welcome
            
            padClient.SendText("Hello\r\n");
            System.Threading.Thread.Sleep(300); // Wait for echo

            // Assert
            Assert.True(echoReceived, "Should receive echo response");
            
            // Cleanup
            padClient.Disconnect();
            padClient.Dispose();
        }

        [Fact]
        public void TestHelpCommand()
        {
            // Arrange
            var padClient = _runtime.CreatePadClient("TestPAD");
            var helpReceived = false;

            padClient.DataReceived += (sender, args) =>
            {
                if (args.Data.Contains("Available commands:"))
                    helpReceived = true;
            };

            // Act
            padClient.Connect("200", "100");
            System.Threading.Thread.Sleep(600); // Wait for welcome
            
            padClient.SendText("HELP\r\n");
            System.Threading.Thread.Sleep(300); // Wait for response

            // Assert
            Assert.True(helpReceived, "Should receive help response");
            
            // Cleanup
            padClient.Disconnect();
            padClient.Dispose();
        }

        [Fact]
        public void TestQuitCommand()
        {
            // Arrange
            var padClient = _runtime.CreatePadClient("TestPAD");
            var goodbyeReceived = false;
            var disconnected = false;

            padClient.DataReceived += (sender, args) =>
            {
                if (args.Data.Contains("Goodbye!"))
                    goodbyeReceived = true;
            };

            padClient.ConnectionStateChanged += (sender, args) =>
            {
                if (args.State == PadConnectionState.Disconnected)
                    disconnected = true;
            };

            // Act
            padClient.Connect("200", "100");
            System.Threading.Thread.Sleep(600); // Wait for welcome
            
            padClient.SendText("QUIT\r\n");
            System.Threading.Thread.Sleep(1000); // Wait for goodbye + clear

            // Assert
            Assert.True(goodbyeReceived, "Should receive goodbye message");
            Assert.True(disconnected, "Should disconnect after QUIT");
            
            // Cleanup
            padClient.Dispose();
        }

        [Fact]
        public void TestMultipleClients()
        {
            // Arrange
            var client1 = _runtime.CreatePadClient("PAD1");
            var client2 = _runtime.CreatePadClient("PAD2");
            var client1Echo = false;
            var client2Echo = false;

            client1.DataReceived += (sender, args) =>
            {
                if (args.Data.Contains("Echo: Message1"))
                    client1Echo = true;
            };

            client2.DataReceived += (sender, args) =>
            {
                if (args.Data.Contains("Echo: Message2"))
                    client2Echo = true;
            };

            // Act
            client1.Connect("200", "101");
            client2.Connect("200", "102");
            System.Threading.Thread.Sleep(800); // Wait for both to connect

            client1.SendText("Message1\r\n");
            client2.SendText("Message2\r\n");
            System.Threading.Thread.Sleep(400); // Wait for echoes

            // Assert
            Assert.True(client1Echo, "Client 1 should receive its echo");
            Assert.True(client2Echo, "Client 2 should receive its echo");
            
            // Cleanup
            client1.Disconnect();
            client2.Disconnect();
            client1.Dispose();
            client2.Dispose();
        }

        [Fact]
        public void TestRuntimeInitialization()
        {
            // Act & Assert - should not throw
            var stats = _runtime.GetStatistics();
            Assert.NotNull(stats);
            Assert.NotNull(stats.Runtime);
        }

        [Fact]
        public void TestModuleRegistration()
        {
            // Act
            var padClient = _runtime.CreatePadClient("TestModule");
            var stats = _runtime.GetStatistics();

            // Assert
            Assert.NotNull(padClient);
            Assert.Equal("TestModule", padClient.Name);
            Assert.True(stats.Runtime.RegisteredModules > 0);
            
            // Cleanup
            padClient.Dispose();
        }

        public void Dispose()
        {
            _runtime?.Dispose();
        }
    }
}