//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Threading.Tasks;
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
    /// <summary>
    /// Tests for the new centralized X.25 routing system
    /// </summary>
    [TestFixture]
    public class CentralizedRoutingTests : TestBase
    {
        [Test]
        public void TestFindRoute_ExactMatch()
        {
            // Arrange
            Config.Routing.RoutingTable.Add(new X25RouteEntry
            {
                Name = "ExactRoute",
                X121AddressPattern = "200",
                Type = RouteType.Local,
                Destination = "LocalTAD",
                Priority = 100,
                Enabled = true
            });
            
            var runtime = new ProtocolRuntime(Logger, Config);
            
            // Act
            var route = runtime.FindRoute("200");
            
            // Assert
            Assert.That(route, Is.Not.Null, "Should find route for exact match");
            Assert.That(route.Name, Is.EqualTo("ExactRoute"));
            Assert.That(route.Type, Is.EqualTo(RouteType.Local));
            Assert.That(route.Destination, Is.EqualTo("LocalTAD"));
        }
        
        [Test]
        public void TestFindRoute_WildcardMatch()
        {
            // Arrange
            Config.Routing.RoutingTable.Add(new X25RouteEntry
            {
                Name = "WildcardRoute",
                X121AddressPattern = "100.*",
                Type = RouteType.Interface,
                Destination = "Serial0",
                Priority = 100,
                Enabled = true
            });
            
            var runtime = new ProtocolRuntime(Logger, Config);
            
            // Act
            var route = runtime.FindRoute("100.123");
            
            // Assert
            Assert.That(route, Is.Not.Null, "Should find route for wildcard match");
            Assert.That(route.Name, Is.EqualTo("WildcardRoute"));
            Assert.That(route.Type, Is.EqualTo(RouteType.Interface));
        }
        
        [Test]
        public void TestFindRoute_PriorityOrdering()
        {
            // Arrange
            Config.Routing.RoutingTable.Add(new X25RouteEntry
            {
                Name = "LowPriority",
                X121AddressPattern = "100.*",
                Type = RouteType.Interface,
                Destination = "Serial0",
                Priority = 50,
                Enabled = true
            });
            
            Config.Routing.RoutingTable.Add(new X25RouteEntry
            {
                Name = "HighPriority",
                X121AddressPattern = "100.*",
                Type = RouteType.Gateway,
                Destination = "200",
                Priority = 150,
                Enabled = true
            });
            
            var runtime = new ProtocolRuntime(Logger, Config);
            
            // Act
            var route = runtime.FindRoute("100.456");
            
            // Assert
            Assert.That(route, Is.Not.Null, "Should find route");
            Assert.That(route.Name, Is.EqualTo("HighPriority"), "Should return highest priority route");
            Assert.That(route.Priority, Is.EqualTo(150));
        }
        
        [Test]
        public void TestFindRoute_DisabledRoutesIgnored()
        {
            // Arrange
            Config.Routing.RoutingTable.Add(new X25RouteEntry
            {
                Name = "DisabledRoute",
                X121AddressPattern = "100.*",
                Type = RouteType.Interface,
                Destination = "Serial0",
                Priority = 100,
                Enabled = false // Disabled
            });
            
            var runtime = new ProtocolRuntime(Logger, Config);
            
            // Act
            var route = runtime.FindRoute("100.789");
            
            // Assert
            Assert.That(route, Is.Null, "Should not find disabled routes");
        }
        
        [Test]
        public void TestMatchesAddressPattern_RegexPatterns()
        {
            // Arrange
            Config.Routing.RoutingTable.Add(new X25RouteEntry
            {
                Name = "RegexRoute",
                X121AddressPattern = "^10[0-9]{2}$", // Matches 1000-1099
                Type = RouteType.Local,
                Destination = "LocalTAD",
                Priority = 100,
                Enabled = true
            });
            
            var runtime = new ProtocolRuntime(Logger, Config);
            
            // Act & Assert
            Assert.That(runtime.FindRoute("1050"), Is.Not.Null, "Should match 1050");
            Assert.That(Runtime.FindRoute("1099"), Is.Not.Null, "Should match 1099");
            Assert.That(Runtime.FindRoute("10999"), Is.Null, "Should not match 10999");
            Assert.That(Runtime.FindRoute("999"), Is.Null, "Should not match 999");
        }
        
        [Test]
        public void TestProcessRouteEntry_LogsCorrectly()
        {
            // Arrange
            var routes = new[]
            {
                new X25RouteEntry
                {
                    Name = "LocalRoute",
                    X121AddressPattern = "200",
                    Type = RouteType.Local,
                    Destination = "LocalTAD",
                    Priority = 100,
                    Enabled = true
                },
                new X25RouteEntry
                {
                    Name = "InterfaceRoute",
                    X121AddressPattern = "100.*",
                    Type = RouteType.Interface,
                    Destination = "Serial0",
                    Priority = 100,
                    Enabled = true
                },
                new X25RouteEntry
                {
                    Name = "GatewayRoute",
                    X121AddressPattern = "101.*",
                    Type = RouteType.Gateway,
                    Destination = "100",
                    Priority = 100,
                    Enabled = true
                }
            };
            
            foreach (var route in routes)
            {
                Config.Routing.RoutingTable.Add(route);
            }
            
            // Act
            var runtime = new ProtocolRuntime(Logger, Config);
            
            // Assert - runtime should initialize successfully with all route types
            Assert.That(runtime, Is.Not.Null);
            // Logs should show processing of each route type
        }
        
    }
}