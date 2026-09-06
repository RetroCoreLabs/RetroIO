using NUnit.Framework;
using Microsoft.Extensions.Logging;
using RetroIO.Protocols.X25;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Protocols.X25.Protocols;
using RetroIO.Protocols.X25.Sintran;
using System;
using System.Threading;

namespace RetroIO.Protocols.X25.Tests
{
using X25 = RetroIO.Protocols.X25.DeleteMe.X25;   // the old engine; goes with DeleteMe (plan C3)

    [TestFixture]
    public class SintranTadConnectionDebugTest
    {
        private ILogger _logger;
        private ProtocolRuntime _runtime;

        [SetUp]
        public void Setup()
        {
            var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Trace); // Use Trace for maximum verbosity
            });
            _logger = loggerFactory.CreateLogger<SintranTadConnectionDebugTest>();

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
        public void Debug_ConnectToAddress100_ShowsRoutingDecisions()
        {
            _logger.LogInformation("=== DEBUG: Testing connection to address 100 ===");

            // Check if service is registered
            var services = _runtime.GetProtocolServiceRegistry();
            _logger.LogInformation("Registered services: {Count}", services.GetRegisteredServices().Length);

            foreach (var service in services.GetRegisteredServices())
            {
                _logger.LogInformation("  Service: {Name}, Addresses: [{Addresses}]",
                    service.ServiceName, string.Join(", ", service.LocalAddresses));
            }

            // Check if PVC server is available
            var pvcServer = _runtime.GetSintranPvcServer();
            var pvcStats = pvcServer.GetStats();
            _logger.LogInformation("PVC Server: {AvailableTerminals}/{TotalTerminals} terminals available",
                pvcStats.AvailableTerminals, pvcStats.TotalTerminals);

            // Try to create client
            _logger.LogInformation("Creating SINTRAN TAD client");
            var client = new SintranTadClient(_logger, _runtime, "DebugClient");

            bool connectionStateChanged = false;
            string lastConnectionState = "";

            client.ConnectionStateChanged += (sender, e) =>
            {
                _logger.LogInformation(">>> CONNECTION STATE CHANGE: {State} - {Message}", e.State, e.Message);
                connectionStateChanged = true;
                lastConnectionState = $"{e.State}: {e.Message}";
            };

            client.ErrorOccurred += (sender, e) =>
            {
                _logger.LogError(">>> CLIENT ERROR: {Message}", e.Message);
            };

            // Try to connect
            _logger.LogInformation("Attempting to connect to address '100'");
            bool callPlaced = client.Connect("100");
            _logger.LogInformation("Connect() returned: {CallPlaced}", callPlaced);

            // Wait for connection
            Thread.Sleep(2000);

            // Check PVC stats again
            var pvcStatsAfter = pvcServer.GetStats();
            _logger.LogInformation("PVC Server after connection: {AvailableTerminals}/{TotalTerminals} terminals, {Handlers} handlers",
                pvcStatsAfter.AvailableTerminals, pvcStatsAfter.TotalTerminals, pvcStatsAfter.TotalHandlers);

            // Log final state
            _logger.LogInformation("Connection state changed: {Changed}", connectionStateChanged);
            _logger.LogInformation("Last connection state: {State}", lastConnectionState);
            _logger.LogInformation("Client connected: {IsConnected}", client.IsConnected);
            _logger.LogInformation("Client state: {State}", client.ConnectionState);

            // Get active handlers
            var handlers = pvcServer.GetActiveHandlers();
            _logger.LogInformation("Active PVC handlers: {Count}", handlers.Count);
            foreach (var kvp in handlers)
            {
                _logger.LogInformation("  LCN {LCN}: {Protocol} handler", kvp.Key, kvp.Value.Protocol);
            }

            // Assert
            Assert.That(callPlaced, Is.True, "Call should be placed");

            // Cleanup
            client.Dispose();
        }

        [Test]
        public void Debug_CheckRoutingForAddress100()
        {
            _logger.LogInformation("=== DEBUG: Checking routing for address 100 ===");

            // Get routing layer
            var routingLayer = _runtime.RoutingLayer;
            _logger.LogInformation("Routing layer available: {Available}", routingLayer != null);

            // Get protocol service registry
            var serviceRegistry = _runtime.GetProtocolServiceRegistry();
            var services = serviceRegistry.GetRegisteredServices();
            _logger.LogInformation("Total registered services: {Count}", services.Length);

            foreach (var service in services)
            {
                _logger.LogInformation("Service: {Name}", service.ServiceName);
                _logger.LogInformation("  Priority: {Priority}", service.Priority);
                _logger.LogInformation("  Addresses: [{Addresses}]", string.Join(", ", service.LocalAddresses));

                // Check if this service handles address "100"
                bool handlesAddress = false;
                foreach (var addr in service.LocalAddresses)
                {
                    if (addr == "100")
                    {
                        handlesAddress = true;
                        break;
                    }
                }
                _logger.LogInformation("  Handles address 100: {Handles}", handlesAddress);

                // Check if it can handle empty CUD
                bool canHandleEmptyCud = service.CanHandleCUD(Array.Empty<byte>());
                _logger.LogInformation("  Can handle empty CUD: {CanHandle}", canHandleEmptyCud);
            }

            // Try to find service for address "100"
            var foundService = serviceRegistry.FindServiceForCall("100", Array.Empty<byte>());
            if (foundService != null)
            {
                _logger.LogInformation("Found service for address 100: {ServiceName}", foundService.ServiceName);
            }
            else
            {
                _logger.LogWarning("NO SERVICE FOUND for address 100!");
            }
        }
    }
}
