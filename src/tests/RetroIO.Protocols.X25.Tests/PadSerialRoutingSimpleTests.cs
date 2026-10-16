using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Xunit;
using RetroIO.Protocols.X25;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Runtime.Models;
using RetroIO.Protocols.X25.Sintran;
using RetroIO.Protocols.X25.Modules;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Simple tests for PAD client connection to address 100 (non-existent, should fail)
    /// Tests that connection attempts generate X.25 frames and attempts serial routing
    /// </summary>
    public class PadSerialRoutingSimpleTests : IDisposable
    {
        private readonly ILogger<PadSerialRoutingSimpleTests> _logger;
        private readonly ProtocolRuntime _runtime;

        public PadSerialRoutingSimpleTests()
        {
            // Create logger with debug level
            var loggerFactory = LoggerFactory.Create(builder => 
            {
                builder.AddConsole();
                builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug);
            });
            _logger = loggerFactory.CreateLogger<PadSerialRoutingSimpleTests>();

            // Create runtime config 
            var config = new RuntimeConfig();
            config.X25.LocalMachineId = "200"; // We are machine 200
            
            // Enable centralized routing
            config.Routing.Enabled = true;
            
            // Add mock serial interface for testing
            config.Routing.SerialInterfaces.Add(new SerialInterfaceConfig
            {
                Name = "TestSerial",
                Enabled = true,
                PortName = "COM99", // Mock serial port that doesn't exist
                BaudRate = 19200,
                DataBits = 8,
                Parity = System.IO.Ports.Parity.None,
                StopBits = System.IO.Ports.StopBits.One,
                RemoteMachineId = "100",
                Description = "Test serial interface"
            });
            
            // Add routing table entry to route address 100 to the TestSerial interface
            config.Routing.RoutingTable.Add(new X25RouteEntry
            {
                Name = "TestRoute",
                X121AddressPattern = "100",
                Type = RouteType.Interface,
                Destination = "TestSerial",
                Priority = 100,
                Enabled = true,
                Description = "Route address 100 to TestSerial for testing"
            });
            
            // Create runtime
            _runtime = new ProtocolRuntime(_logger, config);
            
            _logger.LogInformation("PadSerialRoutingSimpleTests initialized");
        }

        [Fact]
        public void TestPadToNonExistentAddress_ShouldNotConnect()
        {
            // Arrange
            var padClient = _runtime.CreatePadClient("SerialTestPAD");
            var connectionStates = new System.Collections.Generic.List<PadConnectionState>();
            
            padClient.ConnectionStateChanged += (sender, args) =>
            {
                connectionStates.Add(args.State);
                _logger.LogInformation("PAD connection state: {State} - {Reason}", args.State, args.Reason);
            };

            // Act - Attempt to connect to address 100 (should not succeed)
            _logger.LogInformation("Attempting PAD connection to address 100 (should fail)");
            var connectTask = padClient.Connect("100", "200");
            var connectSuccess = connectTask; // This should return true (connection initiated)
            
            // Wait for potential timeout/failure
            Thread.Sleep(1000);

            // Assert - Connection should fail because serial port doesn't exist
            Assert.False(connectSuccess, "Connect should return false (connection failed due to inactive transport)");
            
            // Should not see connecting state since transport failed immediately
            Assert.DoesNotContain(PadConnectionState.Connecting, connectionStates);
            
            _logger.LogInformation("Connection states: {States}", string.Join(" -> ", connectionStates));

            // Cleanup
            padClient.Disconnect();
            padClient.Dispose();
        }

        [Fact]
        public void TestRuntimeWithSerialConfig_ShouldBeConfigured()
        {
            // Assert - Verify the runtime is configured 
            var stats = _runtime.GetStatistics();
            Assert.NotNull(stats);
            
            // Verify we can create PAD clients
            var padClient = _runtime.CreatePadClient("TestClient");
            Assert.NotNull(padClient);
            Assert.Equal("TestClient", padClient.Name);
            
            padClient.Dispose();
        }

        public void Dispose()
        {
            _runtime?.Dispose();
        }
    }
}