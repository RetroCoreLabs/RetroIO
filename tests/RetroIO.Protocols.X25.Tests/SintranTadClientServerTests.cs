using NUnit.Framework;
using Microsoft.Extensions.Logging;
using RetroIO.Protocols.X25;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Protocols.X25.Protocols;
using RetroIO.Protocols.X25.Sintran;
using System;

namespace RetroIO.Protocols.X25.Tests
{
using X25 = RetroIO.Protocols.X25.DeleteMe.X25;   // the old engine; goes with DeleteMe (plan C3)

    [TestFixture]
    public class SintranTadClientServerTests
    {
        private ILogger _logger;
        private ProtocolRuntime _runtime;
        private SintranTadClient _client = null!;

        [SetUp]
        public void Setup()
        {
            // Create logger
            var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Debug);
            });
            _logger = loggerFactory.CreateLogger<SintranTadClientServerTests>();

            // Create runtime with default configuration
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
            _client?.Dispose();
            _runtime?.Dispose();
        }

        [Test]
        public void SintranTadClient_Connect_ToLocalServer_Succeeds()
        {
            // Arrange
            _logger.LogInformation("=== TEST: SINTRAN TAD Client Connect ===");
            _client = new SintranTadClient(_logger, _runtime, "TestClient");

            bool connectionStateChanged = false;
            TadSessionState finalState = TadSessionState.Created;

            _client.ConnectionStateChanged += (sender, e) =>
            {
                _logger.LogInformation("Connection state changed: {State} - {Message}", e.State, e.Message);
                connectionStateChanged = true;
                finalState = e.State;
            };

            // Act
            _logger.LogInformation("Attempting to connect to address '100'");
            bool callPlaced = _client.Connect("100");

            // Assert
            Assert.That(callPlaced, Is.True, "Call should be placed successfully");
            _logger.LogInformation("Call placed: {CallPlaced}", callPlaced);

            // Wait for connection state change (async)
            System.Threading.Thread.Sleep(500);

            if (!connectionStateChanged)
            {
                _logger.LogWarning("No connection state change received");
            }

            _logger.LogInformation("Final state: {State}", finalState);
        }

        [Test]
        public void SintranTadClient_SendData_ToLocalServer_ReceivesEcho()
        {
            // Arrange
            _logger.LogInformation("=== TEST: SINTRAN TAD Client Send/Receive ===");
            _client = new SintranTadClient(_logger, _runtime, "TestClient");

            bool dataReceived = false;
            string receivedData = "";

            _client.ConnectionStateChanged += (sender, e) =>
            {
                _logger.LogInformation("Connection state: {State} - {Message}", e.State, e.Message);
            };

            _client.DataReceived += (sender, e) =>
            {
                _logger.LogInformation("Data received: '{Data}'", e.Data);
                dataReceived = true;
                receivedData = e.Data;
            };

            // Act - Connect
            _logger.LogInformation("Connecting to address '100'");
            bool connected = _client.Connect("100");
            Assert.That(connected, Is.True, "Connection should succeed");

            // Wait for connection
            System.Threading.Thread.Sleep(1000);

            // Act - Send data
            _logger.LogInformation("Sending test data: 'Hello'");
            _client.SendData("Hello");

            // Wait for echo
            System.Threading.Thread.Sleep(500);

            // Assert
            if (dataReceived)
            {
                _logger.LogInformation("Received echo: '{Data}'", receivedData);
                // Echo service returns "Echo: {text}\r\nAnalysis: ...\r\nTAD> " format
                Assert.That(receivedData, Does.Contain("Hello"), "Should receive echo containing sent data");
            }
            else
            {
                _logger.LogWarning("No data received (connection may not be established)");
                Assert.Fail("Expected to receive echo data");
            }
        }

        [Test]
        public void SintranTadClient_Disconnect_CleansUpResources()
        {
            // Arrange
            _logger.LogInformation("=== TEST: SINTRAN TAD Client Disconnect ===");
            _client = new SintranTadClient(_logger, _runtime, "TestClient");

            bool disconnected = false;
            _client.ConnectionStateChanged += (sender, e) =>
            {
                _logger.LogInformation("Connection state: {State} - {Message}", e.State, e.Message);
                if (e.State == TadSessionState.Closed)
                {
                    disconnected = true;
                }
            };

            // Act - Connect
            _logger.LogInformation("Connecting to address '100'");
            _client.Connect("100");
            System.Threading.Thread.Sleep(500);

            // Act - Disconnect
            _logger.LogInformation("Disconnecting");
            _client.Disconnect();
            System.Threading.Thread.Sleep(500);

            // Assert
            Assert.That(_client.IsConnected, Is.False, "Client should not be connected after disconnect");
            Assert.That(_client.ConnectionState, Is.EqualTo(TadSessionState.Closed), "State should be Closed");

            if (disconnected)
            {
                _logger.LogInformation("Disconnect event received");
            }
            else
            {
                _logger.LogWarning("No disconnect event received");
            }
        }

        [Test]
        public void SintranPvcServer_AcceptsConnection_AssignsTerminalId()
        {
            // Arrange
            _logger.LogInformation("=== TEST: SINTRAN PVC Server Terminal Assignment ===");
            var pvcServer = _runtime.GetSintranPvcServer();

            // Get initial stats
            var initialStats = pvcServer.GetStats();
            _logger.LogInformation("Initial stats: {Available}/{Total} terminals available",
                initialStats.AvailableTerminals, initialStats.TotalTerminals);

            // Act - Create client and connect
            _client = new SintranTadClient(_logger, _runtime, "TestClient");
            _client.Connect("100");
            System.Threading.Thread.Sleep(500);

            // Get stats after connection
            var afterStats = pvcServer.GetStats();
            _logger.LogInformation("After connection: {Available}/{Total} terminals available",
                afterStats.AvailableTerminals, afterStats.TotalTerminals);

            // Assert
            Assert.That(afterStats.AvailableTerminals, Is.LessThan(initialStats.AvailableTerminals),
                "Available terminals should decrease after connection");
        }

        [Test]
        public void SintranPvcServer_MultipleConnections_AssignsUniqueTerminalIds()
        {
            // Arrange
            _logger.LogInformation("=== TEST: Multiple SINTRAN TAD Connections ===");
            var pvcServer = _runtime.GetSintranPvcServer();

            var client1 = new SintranTadClient(_logger, _runtime, "Client1");
            var client2 = new SintranTadClient(_logger, _runtime, "Client2");

            // Act - Connect both clients
            _logger.LogInformation("Connecting Client 1");
            client1.Connect("100");
            System.Threading.Thread.Sleep(300);

            _logger.LogInformation("Connecting Client 2");
            client2.Connect("100");
            System.Threading.Thread.Sleep(300);

            var stats = pvcServer.GetStats();
            _logger.LogInformation("After 2 connections: {Available}/{Total} terminals, {Handlers} handlers",
                stats.AvailableTerminals, stats.TotalTerminals, stats.TotalHandlers);

            // Assert
            Assert.That(stats.TotalHandlers, Is.GreaterThanOrEqualTo(2), "Should have at least 2 handlers");
            Assert.That(stats.AvailableTerminals, Is.LessThanOrEqualTo(6), "Should have used at least 2 terminal IDs");

            // Cleanup
            client1.Disconnect();
            client2.Disconnect();
            client1.Dispose();
            client2.Dispose();
        }

        [Test]
        public void SintranTadHandler_EchoesData_SendsRFI()
        {
            // Arrange
            _logger.LogInformation("=== TEST: SINTRAN TAD Handler Echo and RFI ===");
            var handler = new SintranTadHandler(_logger);
            handler.Initialize();

            int responsesReceived = 0;
            byte[] lastResponse = Array.Empty<byte>();

            handler.ResponseReady += (sender, e) =>
            {
                responsesReceived++;
                lastResponse = e.Data;
                _logger.LogInformation("Response #{Count} received: {Bytes} bytes",
                    responsesReceived, e.Data.Length);
            };

            // Simulate channel establishment
            handler.OnChannelEstablished(5);

            // Act - Send binary data message
            var testMessage = SintranTadMessage.CreateBinaryData(
                System.Text.Encoding.ASCII.GetBytes("Test")
            );
            var messageBytes = testMessage.Build();

            _logger.LogInformation("Sending BDAT message with 'Test'");
            handler.HandleData(messageBytes);

            // Allow async processing
            System.Threading.Thread.Sleep(100);

            // Assert
            _logger.LogInformation("Total responses received: {Count}", responsesReceived);
            Assert.That(responsesReceived, Is.GreaterThanOrEqualTo(2),
                "Should receive at least 2 responses (echo + RFI)");

            // Cleanup
            handler.Dispose();
        }
    }
}
