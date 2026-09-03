using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using RetroIO.Protocols.X25;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Runtime.Models;
using RetroIO.Protocols.X25.Sintran;

namespace RetroIO.Protocols.X25.Tests
{
    [TestFixture]
    public class ConfigurationTests : TestBase
    {
        private string _tempConfigFile;

        [SetUp]
        public override void Setup()
        {
            base.Setup();
            _tempConfigFile = Path.GetTempFileName();
        }

        [TearDown]
        public override void TearDown()
        {
            if (File.Exists(_tempConfigFile))
            {
                File.Delete(_tempConfigFile);
            }
            base.TearDown();
        }

        [Test]
        public void TestDefaultConfiguration_Creation()
        {
            // Act
            var config = RuntimeConfig.CreateDefault();

            // Assert
            Assert.That(config, Is.Not.Null);
            Assert.That(config.Routing.Enabled, Is.True);
            
            // Should have one serial interface (disabled)
            Assert.That(config.Routing.SerialInterfaces.Count, Is.EqualTo(1));
            Assert.That(config.Routing.SerialInterfaces[0].Name, Is.EqualTo("Serial0"));
            Assert.That(config.Routing.SerialInterfaces[0].Enabled, Is.False);
            Assert.That(config.Routing.SerialInterfaces[0].RemoteMachineId, Is.EqualTo("100"));
            
            // Should have routing entries
            Assert.That(config.Routing.RoutingTable.Count, Is.EqualTo(2));
            
            var localTadRoute = config.Routing.RoutingTable.FirstOrDefault(r => r.Type == RouteType.Local);
            Assert.That(localTadRoute, Is.Not.Null);
            Assert.That(localTadRoute.X121AddressPattern, Is.EqualTo("200"));
            Assert.That(localTadRoute.Enabled, Is.True);
        }

        [Test]
        public void TestComprehensiveConfiguration_Creation()
        {
            // Act
            var config = RuntimeConfig.CreateComprehensiveExample();

            // Assert
            Assert.That(config, Is.Not.Null);
            Assert.That(config.Routing.Enabled, Is.True);
            
            // Should have multiple serial interfaces
            Assert.That(config.Routing.SerialInterfaces.Count, Is.EqualTo(2));
            Assert.That(config.Routing.SerialInterfaces[0].Name, Is.EqualTo("Serial0"));
            Assert.That(config.Routing.SerialInterfaces[1].Name, Is.EqualTo("Serial1"));
            
            // Should have XOT interfaces
            Assert.That(config.Routing.XotInterfaces.Count, Is.EqualTo(2));
            Assert.That(config.Routing.XotInterfaces[0].Mode, Is.EqualTo(XotMode.Client));
            Assert.That(config.Routing.XotInterfaces[1].Mode, Is.EqualTo(XotMode.Server));
            
            // Should have comprehensive routing table
            Assert.That(config.Routing.RoutingTable.Count, Is.EqualTo(5));
            
            // Should have known hosts
            Assert.That(config.Routing.KnownHosts.Count, Is.EqualTo(3));
            
            // Should have default gateway
            Assert.That(config.Routing.DefaultGateway, Is.EqualTo("100"));
        }

        [Test]
        public void TestConfiguration_JsonSerialization()
        {
            // Arrange
            var originalConfig = RuntimeConfig.CreateComprehensiveExample();
            originalConfig.X25.LocalMachineId = "200";

            // Act - Serialize to JSON
            var json = JsonSerializer.Serialize(originalConfig, new JsonSerializerOptions 
            { 
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            
            // Write to file
            File.WriteAllText(_tempConfigFile, json);

            // Assert - File should exist and contain expected content
            Assert.That(File.Exists(_tempConfigFile), Is.True);
            var fileContent = File.ReadAllText(_tempConfigFile);
            Assert.That(fileContent, Contains.Substring("\"localMachineId\": \"200\""));
            Assert.That(fileContent, Contains.Substring("\"serialInterfaces\""));
            Assert.That(fileContent, Contains.Substring("\"xotInterfaces\""));
            Assert.That(fileContent, Contains.Substring("\"routingTable\""));
        }

        [Test]
        public void TestConfiguration_JsonDeserialization()
        {
            // Arrange
            var originalConfig = RuntimeConfig.CreateComprehensiveExample();
            originalConfig.X25.LocalMachineId = "200";
            
            var json = JsonSerializer.Serialize(originalConfig, new JsonSerializerOptions 
            { 
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            File.WriteAllText(_tempConfigFile, json);

            // Act - Deserialize from JSON
            var loadedJson = File.ReadAllText(_tempConfigFile);
            var loadedConfig = JsonSerializer.Deserialize<RuntimeConfig>(loadedJson, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            // Assert
            Assert.That(loadedConfig, Is.Not.Null);
            Assert.That(loadedConfig.X25.LocalMachineId, Is.EqualTo("200"));
            Assert.That(loadedConfig.Routing.SerialInterfaces.Count, Is.EqualTo(originalConfig.Routing.SerialInterfaces.Count));
            Assert.That(loadedConfig.Routing.XotInterfaces.Count, Is.EqualTo(originalConfig.Routing.XotInterfaces.Count));
            Assert.That(loadedConfig.Routing.RoutingTable.Count, Is.EqualTo(originalConfig.Routing.RoutingTable.Count));
            Assert.That(loadedConfig.Routing.DefaultGateway, Is.EqualTo(originalConfig.Routing.DefaultGateway));
        }

        [Test]
        public void TestSerialRouting_Configuration()
        {
            // Arrange
            var config = new RuntimeConfig();
            config.X25.LocalMachineId = "200";

            // Add serial interface
            config.Routing.SerialInterfaces.Add(new SerialInterfaceConfig
            {
                Name = "Serial0",
                Enabled = true,
                PortName = "COM1",
                BaudRate = 9600,
                RemoteMachineId = "100",
                Description = "Test serial connection"
            });

            // Add routing for serial interface
            config.Routing.RoutingTable.Add(new X25RouteEntry
            {
                Name = "SerialRoute",
                X121AddressPattern = "100.*",
                Type = RouteType.Interface,
                Destination = "Serial0",
                Priority = 100,
                Enabled = true,
                Description = "Route to serial interface"
            });

            // Act - Create runtime with configuration
            var runtime = new ProtocolRuntime(Logger, config);

            // Assert
            Assert.That(runtime, Is.Not.Null);
            Assert.That(runtime.Config.Routing.SerialInterfaces.Count, Is.EqualTo(1));
            
            var serialInterface = runtime.Config.Routing.SerialInterfaces[0];
            Assert.That(serialInterface.Name, Is.EqualTo("Serial0"));
            Assert.That(serialInterface.RemoteMachineId, Is.EqualTo("100"));
            Assert.That(serialInterface.Enabled, Is.True);
            
            var route = runtime.Config.Routing.RoutingTable.FirstOrDefault(r => r.Type == RouteType.Interface);
            Assert.That(route, Is.Not.Null);
            Assert.That(route.Destination, Is.EqualTo("Serial0"));
        }

        [Test]
        public void TestXotRouting_Configuration()
        {
            // Arrange
            var config = new RuntimeConfig();
            config.X25.LocalMachineId = "200";

            // Add XOT client interface
            config.Routing.XotInterfaces.Add(new XotInterfaceConfig
            {
                Name = "XOT0",
                Enabled = true,
                Mode = XotMode.Client,
                RemoteIPAddress = "192.168.1.100",
                RemotePort = 1998,
                RemoteMachineId = "300",
                Description = "XOT client connection"
            });

            // Add routing for XOT interface  
            config.Routing.RoutingTable.Add(new X25RouteEntry
            {
                Name = "XotRoute",
                X121AddressPattern = "300.*",
                Type = RouteType.Interface,
                Destination = "XOT0", 
                Priority = 100,
                Enabled = true,
                Description = "Route to XOT interface"
            });

            // Act - Create runtime with configuration
            var runtime = new ProtocolRuntime(Logger, config);

            // Assert
            Assert.That(runtime, Is.Not.Null);
            Assert.That(runtime.Config.Routing.XotInterfaces.Count, Is.EqualTo(1));
            
            var xotInterface = runtime.Config.Routing.XotInterfaces[0];
            Assert.That(xotInterface.Name, Is.EqualTo("XOT0"));
            Assert.That(xotInterface.Mode, Is.EqualTo(XotMode.Client));
            Assert.That(xotInterface.RemoteIPAddress, Is.EqualTo("192.168.1.100"));
            Assert.That(xotInterface.RemoteMachineId, Is.EqualTo("300"));
            
            var route = runtime.Config.Routing.RoutingTable.FirstOrDefault(r => r.Destination == "XOT0");
            Assert.That(route, Is.Not.Null);
            Assert.That(route.Type, Is.EqualTo(RouteType.Interface));
        }

        [Test]
        public void TestLocalRouting_Configuration()
        {
            // Arrange
            var config = new RuntimeConfig();
            config.X25.LocalMachineId = "200";

            // Add local TAD route
            config.Routing.RoutingTable.Add(new X25RouteEntry
            {
                Name = "LocalTAD",
                X121AddressPattern = "200",
                Type = RouteType.Local,
                Destination = "LocalTAD",
                Priority = 200,
                Enabled = true,
                Description = "Route to built-in TAD server"
            });

            // Act - Create runtime with configuration
            var runtime = new ProtocolRuntime(Logger, config);

            // Assert
            Assert.That(runtime, Is.Not.Null);
            
            var localRoute = runtime.Config.Routing.RoutingTable.FirstOrDefault(r => r.Type == RouteType.Local);
            Assert.That(localRoute, Is.Not.Null);
            Assert.That(localRoute.X121AddressPattern, Is.EqualTo("200"));
            Assert.That(localRoute.Destination, Is.EqualTo("LocalTAD"));
            Assert.That(localRoute.Enabled, Is.True);
        }

        [Test]
        public void TestGatewayRouting_Configuration()
        {
            // Arrange
            var config = new RuntimeConfig();
            config.X25.LocalMachineId = "200";

            // Add known host entry for gateway
            config.Routing.KnownHosts.Add(new KnownHostEntry
            {
                MachineId = "100",
                Reachability = HostReachability.Direct,
                Via = "Serial0",
                Name = "TestGateway",
                Description = "Gateway machine 100"
            });

            // Add gateway route
            config.Routing.RoutingTable.Add(new X25RouteEntry
            {
                Name = "GatewayRoute",
                X121AddressPattern = "101.*",
                Type = RouteType.Gateway,
                Destination = "100", // Route through gateway machine
                Priority = 50,
                Enabled = true,
                Description = "Route through gateway 100"
            });

            // Set default gateway
            config.Routing.DefaultGateway = "100";

            // Act - Create runtime with configuration
            var runtime = new ProtocolRuntime(Logger, config);

            // Assert
            Assert.That(runtime, Is.Not.Null);
            Assert.That(runtime.Config.Routing.DefaultGateway, Is.EqualTo("100"));
            
            var gatewayHost = runtime.Config.Routing.KnownHosts.FirstOrDefault(h => h.MachineId == "100");
            Assert.That(gatewayHost, Is.Not.Null);
            Assert.That(gatewayHost.Reachability, Is.EqualTo(HostReachability.Direct));
            Assert.That(gatewayHost.Name, Is.EqualTo("TestGateway"));
            
            var gatewayRoute = runtime.Config.Routing.RoutingTable.FirstOrDefault(r => r.Type == RouteType.Gateway);
            Assert.That(gatewayRoute, Is.Not.Null);
            Assert.That(gatewayRoute.Destination, Is.EqualTo("100"));
        }

        [Test]
        public void TestConfiguration_RequiredSettings_Validation()
        {
            // Arrange - Configuration without LocalMachineId
            var config = new RuntimeConfig();
            config.X25.LocalMachineId = null!; // Explicitly set to null

            // Act & Assert - Should throw when LocalMachineId is missing
            var caughtException = Assert.Throws<ArgumentException>(() => new ProtocolRuntime(Logger, config));
            Assert.That(caughtException, Is.Not.Null);
            Assert.That(caughtException.Message, Contains.Substring("LocalMachineId"));
        }

        [Test]
        public void TestConfiguration_MultipleRouteTypes_Integration()
        {
            // Arrange - Create configuration with all route types
            var config = new RuntimeConfig();
            config.X25.LocalMachineId = "200";

            // Serial interface
            config.Routing.SerialInterfaces.Add(new SerialInterfaceConfig
            {
                Name = "Serial0",
                Enabled = false, // Disabled for testing
                PortName = "COM99",
                RemoteMachineId = "100"
            });

            // XOT interface
            config.Routing.XotInterfaces.Add(new XotInterfaceConfig
            {
                Name = "XOT0", 
                Enabled = false, // Disabled for testing
                Mode = XotMode.Client,
                RemoteIPAddress = "127.0.0.1",
                RemoteMachineId = "300"
            });

            // Multiple route types
            config.Routing.RoutingTable.AddRange(new[]
            {
                new X25RouteEntry
                {
                    Name = "SerialRoute",
                    X121AddressPattern = "100.*",
                    Type = RouteType.Interface,
                    Destination = "Serial0",
                    Priority = 100,
                    Enabled = false // Disabled for testing
                },
                new X25RouteEntry
                {
                    Name = "XotRoute", 
                    X121AddressPattern = "300.*",
                    Type = RouteType.Interface,
                    Destination = "XOT0",
                    Priority = 100,
                    Enabled = false // Disabled for testing
                },
                new X25RouteEntry
                {
                    Name = "LocalTAD",
                    X121AddressPattern = "200",
                    Type = RouteType.Local,
                    Destination = "LocalTAD",
                    Priority = 200,
                    Enabled = true
                },
                new X25RouteEntry
                {
                    Name = "GatewayRoute",
                    X121AddressPattern = "101.*", 
                    Type = RouteType.Gateway,
                    Destination = "100",
                    Priority = 50,
                    Enabled = false // Disabled for testing
                }
            });

            // Act - Create runtime
            var runtime = new ProtocolRuntime(Logger, config);

            // Assert - Verify all route types are configured
            Assert.That(runtime, Is.Not.Null);
            Assert.That(runtime.Config.Routing.RoutingTable.Count, Is.EqualTo(4));
            
            var routeTypes = runtime.Config.Routing.RoutingTable.Select(r => r.Type).ToList();
            Assert.That(routeTypes, Contains.Item(RouteType.Interface));
            Assert.That(routeTypes, Contains.Item(RouteType.Local));
            Assert.That(routeTypes, Contains.Item(RouteType.Gateway));
            
            // Verify enabled routes
            var enabledRoutes = runtime.Config.Routing.RoutingTable.Where(r => r.Enabled).ToList();
            Assert.That(enabledRoutes.Count, Is.EqualTo(1)); // Only local TAD enabled
            Assert.That(enabledRoutes[0].Type, Is.EqualTo(RouteType.Local));
        }

        [Test]
        public void TestConfiguration_RoundTrip_JsonPersistence()
        {
            // Arrange - Create comprehensive configuration
            var originalConfig = RuntimeConfig.CreateComprehensiveExample();
            originalConfig.X25.LocalMachineId = "200";
            
            // Enable some interfaces for testing
            originalConfig.Routing.SerialInterfaces[0].Enabled = true;
            originalConfig.Routing.XotInterfaces[0].Enabled = true;
            
            // Enable some routes
            var networkARoute = originalConfig.Routing.RoutingTable.FirstOrDefault(r => r.Name == "Network_A_Route");
            if (networkARoute != null) networkARoute.Enabled = true;

            // Act - Save to JSON file
            var json = JsonSerializer.Serialize(originalConfig, new JsonSerializerOptions 
            { 
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            File.WriteAllText(_tempConfigFile, json);

            // Load from JSON file
            var loadedJson = File.ReadAllText(_tempConfigFile);
            var loadedConfig = JsonSerializer.Deserialize<RuntimeConfig>(loadedJson, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            // Create runtime with loaded configuration
            var runtime = new ProtocolRuntime(Logger, loadedConfig!);

            // Assert - Verify round-trip integrity
            Assert.That(runtime.Config.X25.LocalMachineId, Is.EqualTo("200"));
            Assert.That(runtime.Config.Routing.SerialInterfaces[0].Enabled, Is.True);
            Assert.That(runtime.Config.Routing.XotInterfaces[0].Enabled, Is.True);
            Assert.That(runtime.Config.Routing.RoutingTable.Any(r => r.Name == "Network_A_Route" && r.Enabled), Is.True);
        }
    }
}