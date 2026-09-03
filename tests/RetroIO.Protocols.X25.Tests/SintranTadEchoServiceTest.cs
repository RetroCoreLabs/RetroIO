using NUnit.Framework;
using Microsoft.Extensions.Logging;
using RetroIO.Protocols.X25;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Protocols.X25.Protocols;
using RetroIO.Protocols.X25.Sintran;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Runtime.Models;
using System;
using System.Threading;
using System.Collections.Generic;

namespace RetroIO.Protocols.X25.Tests
{
using X25 = RetroIO.Protocols.X25.Protocols.X25;

    [TestFixture]
    public class SintranTadEchoServiceTest
    {
        private ILogger _logger;
        private ProtocolRuntime _runtime;

        [SetUp]
        public void Setup()
        {
            var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Debug);
            });
            _logger = loggerFactory.CreateLogger<SintranTadEchoServiceTest>();

            var config = new RuntimeConfig
            {
                X25 = new X25Config
                {
                    LocalMachineId = "200"
                }
            };

            _runtime = new ProtocolRuntime(_logger, config);
        }

        [TearDown]
        public void TearDown()
        {
            _runtime?.Dispose();
        }

        [Test]
        public void Test_ClientSendsData_ServerReceivesAndEchoes()
        {
            _logger.LogInformation("=== TEST: Client sends data to server, expects echo ===");

            // Create client
            var client = new SintranTadClient(_logger, _runtime, "TestClient");

            // Track client events
            string lastDataReceived = "";
            var dataReceivedEvents = new List<string>();

            client.ConnectionStateChanged += (sender, e) =>
            {
                _logger.LogInformation(">>> CLIENT STATE CHANGE: {State} - {Message}", e.State, e.Message);
            };

            client.DataReceived += (sender, e) =>
            {
                _logger.LogInformation(">>> CLIENT DATA RECEIVED: '{Data}'", e.Data);
                lastDataReceived = e.Data;
                dataReceivedEvents.Add(e.Data);
            };

            // Connect to address "100"
            _logger.LogInformation("Connecting client to address '100'");
            bool callPlaced = client.Connect("100");
            Assert.That(callPlaced, Is.True, "Call should be placed");

            // Wait for connection to establish
            Thread.Sleep(500);

            // Check server state
            var pvcServer = _runtime.GetSintranPvcServer();
            var handlers = pvcServer.GetActiveHandlers();
            _logger.LogInformation("Active handlers: {Count}", handlers.Count);
            Assert.That(handlers.Count, Is.EqualTo(1), "Should have 1 active handler");

            var handler = handlers[1]; // LCN 1
            var stats = handler.GetStats();
            _logger.LogInformation("Handler stats: RX={RX}, TX={TX}, Status={Status}",
                stats.MessagesReceived, stats.MessagesSent, stats.Status);

            // Verify handshake completed
            Assert.That(stats.MessagesSent, Is.GreaterThan(0), "Server should have sent handshake messages");
            Assert.That(stats.Status, Does.Contain("Active"), "Handler should be active");

            _logger.LogInformation("=== Handshake complete, now sending data from client ===");

            // Send data from client
            string testMessage = "Hello SINTRAN TAD!";
            _logger.LogInformation("Client sending: '{Message}'", testMessage);
            client.SendData(testMessage);

            // Wait for server to process and echo back
            Thread.Sleep(1000);

            // Check server stats after sending data
            stats = handler.GetStats();
            _logger.LogInformation("Handler stats after send: RX={RX}, TX={TX}",
                stats.MessagesReceived, stats.MessagesSent);

            // ASSERTIONS
            Assert.That(stats.MessagesReceived, Is.GreaterThan(0),
                "❌ PROBLEM: Server should have received the client's message (RX should be > 0)");

            Assert.That(dataReceivedEvents.Count, Is.GreaterThan(0),
                "Client should have received echo response");

            Assert.That(lastDataReceived, Does.Contain("Echo:"),
                "Echo response should contain 'Echo:' prefix");

            Assert.That(lastDataReceived, Does.Contain(testMessage),
                "Echo response should contain original message");

            _logger.LogInformation("✅ TEST PASSED: Echo service working correctly");

            // Cleanup
            client.Dispose();
        }

        [Test]
        public void Test_ClientSendsHelpCommand_ServerRespondsWithHelp()
        {
            _logger.LogInformation("=== TEST: Client sends HELP command ===");

            var client = new SintranTadClient(_logger, _runtime, "HelpTestClient");
            var dataReceivedEvents = new List<string>();

            client.DataReceived += (sender, e) =>
            {
                _logger.LogInformation(">>> CLIENT DATA RECEIVED: '{Data}'", e.Data);
                dataReceivedEvents.Add(e.Data);
            };

            // Connect
            client.Connect("100");
            Thread.Sleep(500);

            // Send HELP command
            _logger.LogInformation("Sending HELP command");
            client.SendData("HELP");
            Thread.Sleep(1000);

            // Check server received the command
            var pvcServer = _runtime.GetSintranPvcServer();
            var handlers = pvcServer.GetActiveHandlers();
            var stats = handlers[1].GetStats();

            _logger.LogInformation("Handler stats: RX={RX}, TX={TX}", stats.MessagesReceived, stats.MessagesSent);

            Assert.That(stats.MessagesReceived, Is.GreaterThan(0),
                "Server should have received HELP command");

            Assert.That(dataReceivedEvents.Count, Is.GreaterThan(0),
                "Client should have received help response");

            // Check that help response contains expected content
            var helpResponse = string.Join("", dataReceivedEvents);
            Assert.That(helpResponse, Does.Contain("HELP"), "Response should contain HELP command info");
            Assert.That(helpResponse, Does.Contain("STATUS"), "Response should contain STATUS command info");
            Assert.That(helpResponse, Does.Contain("QUIT"), "Response should contain QUIT command info");

            _logger.LogInformation("✅ TEST PASSED: HELP command works");

            client.Dispose();
        }

        [Test]
        public void Test_MultipleClients_EachGetSeparateEcho()
        {
            _logger.LogInformation("=== TEST: Multiple clients get separate echo responses ===");

            var client1 = new SintranTadClient(_logger, _runtime, "Client1");
            var client2 = new SintranTadClient(_logger, _runtime, "Client2");

            var client1Data = new List<string>();
            var client2Data = new List<string>();

            client1.DataReceived += (sender, e) => client1Data.Add(e.Data);
            client2.DataReceived += (sender, e) => client2Data.Add(e.Data);

            // Connect both clients
            client1.Connect("100");
            Thread.Sleep(300);
            client2.Connect("100");
            Thread.Sleep(300);

            // Check both handlers exist
            var handlers = _runtime.GetSintranPvcServer().GetActiveHandlers();
            Assert.That(handlers.Count, Is.EqualTo(2), "Should have 2 active handlers");

            // Send different messages
            _logger.LogInformation("Client1 sending message");
            client1.SendData("Message from Client 1");
            Thread.Sleep(500);

            _logger.LogInformation("Client2 sending message");
            client2.SendData("Message from Client 2");
            Thread.Sleep(500);

            // Verify each client got their own echo
            var client1Response = string.Join("", client1Data);
            var client2Response = string.Join("", client2Data);

            _logger.LogInformation("Client1 received: {Data}", client1Response.Substring(0, Math.Min(100, client1Response.Length)));
            _logger.LogInformation("Client2 received: {Data}", client2Response.Substring(0, Math.Min(100, client2Response.Length)));

            Assert.That(client1Response, Does.Contain("Client 1"), "Client1 should receive its own echo");
            Assert.That(client2Response, Does.Contain("Client 2"), "Client2 should receive its own echo");

            Assert.That(client1Response, Does.Not.Contain("Client 2"), "Client1 should not receive Client2's echo");
            Assert.That(client2Response, Does.Not.Contain("Client 1"), "Client2 should not receive Client1's echo");

            _logger.LogInformation("✅ TEST PASSED: Multiple clients work independently");

            client1.Dispose();
            client2.Dispose();
        }
    }
}
