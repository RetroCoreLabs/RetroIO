//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Protocols.X25.Runtime.Models;
using RetroIO.Protocols.X25.Sintran;

namespace RetroIO.Protocols.X25.Tests
{
    [TestFixture]
    public class RoutingValidationTests : TestBase
    {
        [Test]
        public void TestRoutePatternMatching_SerialInterface()
        {
            // Arrange
            var config = new RuntimeConfig();
            config.X25.LocalMachineId = "200";

            config.Routing.SerialInterfaces.Add(new SerialInterfaceConfig
            {
                Name = "Serial0",
                Enabled = false, // Disabled for unit test
                PortName = "COM99",
                RemoteMachineId = "100"
            });

            config.Routing.RoutingTable.Add(new X25RouteEntry
            {
                Name = "SerialRoute",
                X121AddressPattern = "100.*", // Match 100, 1000, 1001, etc.
                Type = RouteType.Interface,
                Destination = "Serial0",
                Priority = 100,
                Enabled = false // Disabled for unit test
            });

            var runtime = new ProtocolRuntime(Logger, config);

            // Test pattern matching logic
            var route = runtime.Config.Routing.RoutingTable.FirstOrDefault(r => r.Name == "SerialRoute");
            Assert.That(route, Is.Not.Null);

            // Act & Assert - Test address pattern matching
            var pattern = new Regex(route.X121AddressPattern);
            
            // Should match
            Assert.That(pattern.IsMatch("100"), Is.True, "Should match exact address");
            Assert.That(pattern.IsMatch("1000"), Is.True, "Should match with additional digits");
            Assert.That(pattern.IsMatch("10012345"), Is.True, "Should match longer addresses");
            
            // Should not match
            Assert.That(pattern.IsMatch("101"), Is.False, "Should not match different prefix");
            Assert.That(pattern.IsMatch("99"), Is.False, "Should not match shorter number");
            Assert.That(pattern.IsMatch("200"), Is.False, "Should not match different address");
        }

        [Test] 
        public void TestRoutePatternMatching_XotInterface()
        {
            // Arrange
            var config = new RuntimeConfig();
            config.X25.LocalMachineId = "200";

            config.Routing.XotInterfaces.Add(new XotInterfaceConfig
            {
                Name = "XOT0",
                Enabled = false, // Disabled for unit test
                Mode = XotMode.Client,
                RemoteIPAddress = "127.0.0.1",
                RemoteMachineId = "300"
            });

            config.Routing.RoutingTable.Add(new X25RouteEntry
            {
                Name = "XotRoute",
                X121AddressPattern = "^30[0-9]$", // Match exactly 300-309
                Type = RouteType.Interface,
                Destination = "XOT0",
                Priority = 100,
                Enabled = false // Disabled for unit test
            });

            var runtime = new ProtocolRuntime(Logger, config);

            // Test pattern matching logic
            var route = runtime.Config.Routing.RoutingTable.FirstOrDefault(r => r.Name == "XotRoute");
            Assert.That(route, Is.Not.Null);

            // Act & Assert - Test address pattern matching
            var pattern = new Regex(route.X121AddressPattern);
            
            // Should match
            Assert.That(pattern.IsMatch("300"), Is.True, "Should match 300");
            Assert.That(pattern.IsMatch("301"), Is.True, "Should match 301");
            Assert.That(pattern.IsMatch("309"), Is.True, "Should match 309");
            
            // Should not match
            Assert.That(pattern.IsMatch("310"), Is.False, "Should not match 310");
            Assert.That(pattern.IsMatch("299"), Is.False, "Should not match 299");
            Assert.That(pattern.IsMatch("3000"), Is.False, "Should not match longer address");
        }

        [Test]
        public void TestRoutePatternMatching_LocalTAD()
        {
            // Arrange
            var config = new RuntimeConfig();
            config.X25.LocalMachineId = "200";

            config.Routing.RoutingTable.Add(new X25RouteEntry
            {
                Name = "LocalTAD",
                X121AddressPattern = "200", // Exact match for local TAD
                Type = RouteType.Local,
                Destination = "LocalTAD",
                Priority = 200,
                Enabled = true
            });

            var runtime = new ProtocolRuntime(Logger, config);

            // Test pattern matching logic
            var route = runtime.Config.Routing.RoutingTable.FirstOrDefault(r => r.Name == "LocalTAD");
            Assert.That(route, Is.Not.Null);

            // Act & Assert - Test address pattern matching
            var pattern = new Regex($"^{route.X121AddressPattern}$"); // Exact match
            
            // Should match
            Assert.That(pattern.IsMatch("200"), Is.True, "Should match exact local machine ID");
            
            // Should not match
            Assert.That(pattern.IsMatch("2000"), Is.False, "Should not match longer address");
            Assert.That(pattern.IsMatch("20"), Is.False, "Should not match shorter address");
            Assert.That(pattern.IsMatch("201"), Is.False, "Should not match similar address");
        }

        [Test]
        public void TestRoutePriority_OrderingLogic()
        {
            // Arrange
            var config = new RuntimeConfig();
            config.X25.LocalMachineId = "200";

            // Add multiple routes with different priorities
            config.Routing.RoutingTable.AddRange(new[]
            {
                new X25RouteEntry
                {
                    Name = "LowPriority",
                    X121AddressPattern = "100.*",
                    Type = RouteType.Interface,
                    Destination = "Serial0",
                    Priority = 50, // Lower priority
                    Enabled = true
                },
                new X25RouteEntry
                {
                    Name = "HighPriority",
                    X121AddressPattern = "100.*", // Same pattern
                    Type = RouteType.Interface, 
                    Destination = "XOT0",
                    Priority = 150, // Higher priority
                    Enabled = true
                },
                new X25RouteEntry
                {
                    Name = "LocalTAD",
                    X121AddressPattern = "200",
                    Type = RouteType.Local,
                    Destination = "LocalTAD",
                    Priority = 200, // Highest priority
                    Enabled = true
                }
            });

            var runtime = new ProtocolRuntime(Logger, config);

            // Act - Sort routes by priority (descending)
            var sortedRoutes = runtime.Config.Routing.RoutingTable
                .Where(r => r.Enabled)
                .OrderByDescending(r => r.Priority)
                .ToList();

            // Assert - Verify priority ordering
            Assert.That(sortedRoutes.Count, Is.EqualTo(3));
            Assert.That(sortedRoutes[0].Priority, Is.EqualTo(200)); // LocalTAD first
            Assert.That(sortedRoutes[0].Name, Is.EqualTo("LocalTAD"));
            Assert.That(sortedRoutes[1].Priority, Is.EqualTo(150)); // HighPriority second
            Assert.That(sortedRoutes[1].Name, Is.EqualTo("HighPriority"));
            Assert.That(sortedRoutes[2].Priority, Is.EqualTo(50)); // LowPriority last
            Assert.That(sortedRoutes[2].Name, Is.EqualTo("LowPriority"));
        }

        [Test]
        public void TestGatewayRouting_KnownHostLookup()
        {
            // Arrange
            var config = new RuntimeConfig();
            config.X25.LocalMachineId = "200";

            // Define gateway machine
            config.Routing.KnownHosts.Add(new KnownHostEntry
            {
                MachineId = "100",
                Reachability = HostReachability.Direct,
                Via = "Serial0", 
                Name = "TestHost100"
            });

            // Define indirect host
            config.Routing.KnownHosts.Add(new KnownHostEntry
            {
                MachineId = "101",
                Reachability = HostReachability.Gateway,
                Via = "100" // Reachable through gateway 100
            });

            // Gateway route
            config.Routing.RoutingTable.Add(new X25RouteEntry
            {
                Name = "GatewayRoute",
                X121AddressPattern = "101.*",
                Type = RouteType.Gateway,
                Destination = "100", // Route through machine 100
                Priority = 50,
                Enabled = true
            });

            var runtime = new ProtocolRuntime(Logger, config);

            // Act - Verify gateway configuration
            var gatewayHost = runtime.Config.Routing.KnownHosts
                .FirstOrDefault(h => h.MachineId == "100");
            var indirectHost = runtime.Config.Routing.KnownHosts
                .FirstOrDefault(h => h.MachineId == "101");
            var gatewayRoute = runtime.Config.Routing.RoutingTable
                .FirstOrDefault(r => r.Type == RouteType.Gateway);

            // Assert - Verify gateway routing setup
            Assert.That(gatewayHost, Is.Not.Null);
            Assert.That(gatewayHost.Reachability, Is.EqualTo(HostReachability.Direct));
            Assert.That(gatewayHost.Via, Is.EqualTo("Serial0"));
            Assert.That(gatewayHost.Name, Is.EqualTo("TestHost100"));

            Assert.That(indirectHost, Is.Not.Null);
            Assert.That(indirectHost.Reachability, Is.EqualTo(HostReachability.Gateway));
            Assert.That(indirectHost.Via, Is.EqualTo("100"));

            Assert.That(gatewayRoute, Is.Not.Null);
            Assert.That(gatewayRoute.Destination, Is.EqualTo("100"));
        }

        [Test]
        public void TestDefaultGateway_FallbackRouting()
        {
            // Arrange
            var config = new RuntimeConfig();
            config.X25.LocalMachineId = "200";

            // Set default gateway
            config.Routing.DefaultGateway = "100";

            // Define default gateway host
            config.Routing.KnownHosts.Add(new KnownHostEntry
            {
                MachineId = "100",
                Reachability = HostReachability.Direct,
                Via = "Serial0",
                Description = "Default gateway for unknown addresses"
            });

            var runtime = new ProtocolRuntime(Logger, config);

            // Act & Assert - Verify default gateway configuration
            Assert.That(runtime.Config.Routing.DefaultGateway, Is.EqualTo("100"));
            
            var defaultGatewayHost = runtime.Config.Routing.KnownHosts
                .FirstOrDefault(h => h.MachineId == runtime.Config.Routing.DefaultGateway);
            Assert.That(defaultGatewayHost, Is.Not.Null);
            Assert.That(defaultGatewayHost.Reachability, Is.EqualTo(HostReachability.Direct));
        }

        [Test]
        public void TestRouteValidation_DuplicatePatterns()
        {
            // Arrange
            var config = new RuntimeConfig();
            config.X25.LocalMachineId = "200";

            // Add routes with duplicate patterns but different priorities
            config.Routing.RoutingTable.AddRange(new[]
            {
                new X25RouteEntry
                {
                    Name = "Route1",
                    X121AddressPattern = "100.*",
                    Type = RouteType.Interface,
                    Destination = "Serial0",
                    Priority = 100,
                    Enabled = true
                },
                new X25RouteEntry
                {
                    Name = "Route2",
                    X121AddressPattern = "100.*", // Duplicate pattern
                    Type = RouteType.Interface,
                    Destination = "XOT0",
                    Priority = 150, // Higher priority
                    Enabled = true
                }
            });

            var runtime = new ProtocolRuntime(Logger, config);

            // Act - Find routes matching pattern
            var matchingRoutes = runtime.Config.Routing.RoutingTable
                .Where(r => r.X121AddressPattern == "100.*" && r.Enabled)
                .OrderByDescending(r => r.Priority)
                .ToList();

            // Assert - Should handle duplicate patterns with priority
            Assert.That(matchingRoutes.Count, Is.EqualTo(2));
            Assert.That(matchingRoutes[0].Priority, Is.GreaterThan(matchingRoutes[1].Priority));
            Assert.That(matchingRoutes[0].Name, Is.EqualTo("Route2")); // Higher priority first
            Assert.That(matchingRoutes[1].Name, Is.EqualTo("Route1")); // Lower priority second
        }

        [Test]
        public void TestCompleteRoutingScenario_WithJsonPersistence()
        {
            // Arrange - Create comprehensive routing scenario
            var config = new RuntimeConfig();
            config.X25.LocalMachineId = "200";

            // Serial interface for primary network
            config.Routing.SerialInterfaces.Add(new SerialInterfaceConfig
            {
                Name = "Serial0",
                Enabled = false, // Disabled for testing
                PortName = "COM1",
                BaudRate = 9600,
                RemoteMachineId = "100",
                Description = "Primary X.25 network connection"
            });

            // XOT interface for remote site
            config.Routing.XotInterfaces.Add(new XotInterfaceConfig
            {
                Name = "XOT0",
                Enabled = false, // Disabled for testing
                Mode = XotMode.Client,
                RemoteIPAddress = "192.168.1.100",
                RemotePort = 1998,
                RemoteMachineId = "300",
                Description = "Remote site XOT connection"
            });

            // Comprehensive routing table
            config.Routing.RoutingTable.AddRange(new[]
            {
                // Local TAD server
                new X25RouteEntry
                {
                    Name = "LocalTAD",
                    X121AddressPattern = "200",
                    Type = RouteType.Local,
                    Destination = "LocalTAD",
                    Priority = 200,
                    Enabled = true,
                    Description = "Built-in TAD server"
                },
                // Primary network via serial
                new X25RouteEntry
                {
                    Name = "PrimaryNetwork",
                    X121AddressPattern = "1[0-4][0-9]", // 100-149
                    Type = RouteType.Interface,
                    Destination = "Serial0",
                    Priority = 100,
                    Enabled = false, // Disabled for testing
                    Description = "Primary network addresses"
                },
                // Remote site via XOT
                new X25RouteEntry
                {
                    Name = "RemoteSite",
                    X121AddressPattern = "3[0-9][0-9]", // 300-399
                    Type = RouteType.Interface,
                    Destination = "XOT0", 
                    Priority = 100,
                    Enabled = false, // Disabled for testing
                    Description = "Remote site addresses"
                },
                // Gateway routing
                new X25RouteEntry
                {
                    Name = "GatewayHosts",
                    X121AddressPattern = "15[1-9]", // 151-159
                    Type = RouteType.Gateway,
                    Destination = "100", // Route through machine 100
                    Priority = 75,
                    Enabled = false, // Disabled for testing
                    Description = "Hosts reachable through gateway 100"
                }
            });

            // Known hosts for gateway routing
            config.Routing.KnownHosts.AddRange(new[]
            {
                new KnownHostEntry
                {
                    MachineId = "100",
                    Reachability = HostReachability.Direct,
                    Via = "Serial0",
                    Name = "Gateway100",
                    Description = "Primary gateway machine"
                },
                new KnownHostEntry
                {
                    MachineId = "151",
                    Reachability = HostReachability.Gateway,
                    Via = "100",
                    Description = "Host reachable through gateway 100"
                }
            });

            config.Routing.DefaultGateway = "100";

            // Act - Save configuration to JSON and reload
            var tempFile = Path.GetTempFileName();
            try
            {
                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions 
                { 
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                File.WriteAllText(tempFile, json);

                var loadedJson = File.ReadAllText(tempFile);
                var loadedConfig = JsonSerializer.Deserialize<RuntimeConfig>(loadedJson, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                var runtime = new ProtocolRuntime(Logger, loadedConfig!);

                // Assert - Verify complete routing configuration
                Assert.That(runtime.Config.X25.LocalMachineId, Is.EqualTo("200"));
                Assert.That(runtime.Config.Routing.SerialInterfaces.Count, Is.EqualTo(1));
                Assert.That(runtime.Config.Routing.XotInterfaces.Count, Is.EqualTo(1));
                Assert.That(runtime.Config.Routing.RoutingTable.Count, Is.EqualTo(4));
                Assert.That(runtime.Config.Routing.KnownHosts.Count, Is.EqualTo(2));
                Assert.That(runtime.Config.Routing.DefaultGateway, Is.EqualTo("100"));

                // Verify route types
                var routeTypes = runtime.Config.Routing.RoutingTable.Select(r => r.Type).Distinct().ToList();
                Assert.That(routeTypes, Contains.Item(RouteType.Local));
                Assert.That(routeTypes, Contains.Item(RouteType.Interface));
                Assert.That(routeTypes, Contains.Item(RouteType.Gateway));

                // Verify only local TAD is enabled (others disabled for testing)
                var enabledRoutes = runtime.Config.Routing.RoutingTable.Where(r => r.Enabled).ToList();
                Assert.That(enabledRoutes.Count, Is.EqualTo(1));
                Assert.That(enabledRoutes[0].Type, Is.EqualTo(RouteType.Local));

                Logger.LogInformation("Complete routing scenario validation successful");
            }
            finally
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
        }
    }
}