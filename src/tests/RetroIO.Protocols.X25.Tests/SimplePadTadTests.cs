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

        // How long a test waits for something the loopback service sends. The service answers in
        // milliseconds; the deadline is only there so a broken service fails the test instead of
        // hanging it. Each wait returns as soon as the thing it waits for has happened, so a slow
        // machine (the GitHub runner) gets the time it needs and a fast one loses none.
        private const int Deadline = 10000;

        private const string Welcome = "X.25 TAD Loopback Echo Service";

        [Fact]
        public void TestBasicPadConnectToTad()
        {
            // Arrange
            var padClient = _runtime.CreatePadClient("TestPAD");
            using var connected = new ManualResetEventSlim();

            padClient.ConnectionStateChanged += (sender, args) =>
            {
                if (args.State == PadConnectionState.Connected)
                    connected.Set();
            };

            // Act
            var success = padClient.Connect("200", "100");

            // Assert
            Assert.True(success, "Should successfully initiate connection");
            Assert.True(connected.Wait(Deadline), "Should establish connection");

            // Cleanup
            padClient.Disconnect();
            padClient.Dispose();
        }

        [Fact]
        public void TestPadReceivesWelcomeFromTad()
        {
            // Arrange
            var padClient = _runtime.CreatePadClient("TestPAD");
            using var welcomeReceived = WhenReceived(padClient, Welcome);

            // Act
            padClient.Connect("200", "100");

            // Assert
            Assert.True(welcomeReceived.Wait(Deadline), "Should receive welcome message");

            // Cleanup
            padClient.Disconnect();
            padClient.Dispose();
        }

        [Fact]
        public void TestEchoFunctionality()
        {
            // Arrange
            var padClient = _runtime.CreatePadClient("TestPAD");
            using var welcome = WhenReceived(padClient, Welcome);
            using var echoReceived = WhenReceived(padClient, "Echo: Hello");

            // Act
            padClient.Connect("200", "100");
            Assert.True(welcome.Wait(Deadline), "Should receive welcome message");
            padClient.SendText("Hello\r\n");

            // Assert
            Assert.True(echoReceived.Wait(Deadline), "Should receive echo response");

            // Cleanup
            padClient.Disconnect();
            padClient.Dispose();
        }

        [Fact]
        public void TestHelpCommand()
        {
            // Arrange
            var padClient = _runtime.CreatePadClient("TestPAD");
            using var welcome = WhenReceived(padClient, Welcome);
            using var helpReceived = WhenReceived(padClient, "Available commands:");

            // Act
            padClient.Connect("200", "100");
            Assert.True(welcome.Wait(Deadline), "Should receive welcome message");
            padClient.SendText("HELP\r\n");

            // Assert
            Assert.True(helpReceived.Wait(Deadline), "Should receive help response");

            // Cleanup
            padClient.Disconnect();
            padClient.Dispose();
        }

        [Fact]
        public void TestQuitCommand()
        {
            // Arrange
            var padClient = _runtime.CreatePadClient("TestPAD");
            using var welcome = WhenReceived(padClient, Welcome);
            using var goodbyeReceived = WhenReceived(padClient, "Goodbye!");
            using var disconnected = new ManualResetEventSlim();

            padClient.ConnectionStateChanged += (sender, args) =>
            {
                if (args.State == PadConnectionState.Disconnected)
                    disconnected.Set();
            };

            // Act
            padClient.Connect("200", "100");
            Assert.True(welcome.Wait(Deadline), "Should receive welcome message");
            padClient.SendText("QUIT\r\n");

            // Assert
            Assert.True(goodbyeReceived.Wait(Deadline), "Should receive goodbye message");
            Assert.True(disconnected.Wait(Deadline), "Should disconnect after QUIT");

            // Cleanup
            padClient.Dispose();
        }

        [Fact]
        public void TestMultipleClients()
        {
            // Arrange
            var client1 = _runtime.CreatePadClient("PAD1");
            var client2 = _runtime.CreatePadClient("PAD2");
            using var welcome1 = WhenReceived(client1, Welcome);
            using var welcome2 = WhenReceived(client2, Welcome);
            using var client1Echo = WhenReceived(client1, "Echo: Message1");
            using var client2Echo = WhenReceived(client2, "Echo: Message2");

            // Act
            client1.Connect("200", "101");
            client2.Connect("200", "102");
            Assert.True(welcome1.Wait(Deadline), "Client 1 should receive the welcome message");
            Assert.True(welcome2.Wait(Deadline), "Client 2 should receive the welcome message");

            client1.SendText("Message1\r\n");
            client2.SendText("Message2\r\n");

            // Assert
            Assert.True(client1Echo.Wait(Deadline), "Client 1 should receive its echo");
            Assert.True(client2Echo.Wait(Deadline), "Client 2 should receive its echo");

            // Cleanup
            client1.Disconnect();
            client2.Disconnect();
            client1.Dispose();
            client2.Dispose();
        }

        // Set once the client has received data containing the text.
        private static ManualResetEventSlim WhenReceived(PadClient client, string text)
        {
            var seen = new ManualResetEventSlim();
            client.DataReceived += (sender, args) =>
            {
                if (args.Data.Contains(text))
                    seen.Set();
            };
            return seen;
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