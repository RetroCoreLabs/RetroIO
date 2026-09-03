using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RetroIO.Protocols.X25;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Protocols.X25.Modules;

namespace RetroIO.Protocols.X25.Tests
{
    [TestFixture]
    public class JsonConfigTest
    {
        private ILogger _logger = null!;

        [SetUp]
        public void SetUp()
        {
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            _logger = loggerFactory.CreateLogger<JsonConfigTest>();
        }

        [Test]
        public void TestFullJsonConfigurationSerialization()
        {
            // Create a comprehensive configuration
            var config = new RuntimeConfig();
            
            // Set basic settings
            config.X25.LocalMachineId = "200";
            config.Lapb.IsDCE = false;
            config.Lapb.T1Timeout = TimeSpan.FromMilliseconds(3000);
            config.Lapb.T2Timeout = TimeSpan.FromMilliseconds(1000);
            config.Lapb.T3Timeout = TimeSpan.FromMilliseconds(10000);
            config.Lapb.MaxRetries = 10;
            config.Lapb.WindowSize = 7;
            config.HistoryCapacity = 1000;

            // Configure routing
            config.Routing.Enabled = true;
            
            // Add serial interface
            config.Routing.SerialInterfaces.Add(new SerialInterfaceConfig
            {
                Name = "Serial0",
                Enabled = true,
                PortName = "COM1",
                BaudRate = 19200,
                DataBits = 8,
                Parity = System.IO.Ports.Parity.None,
                StopBits = System.IO.Ports.StopBits.One,
                RemoteMachineId = "100"
            });

            // Add XOT interface
            config.Routing.XotInterfaces.Add(new XotInterfaceConfig
            {
                Name = "XOT0",
                Enabled = true,
                LocalPort = 1998,
                RemoteIPAddress = "192.168.1.101",
                RemotePort = 1998,
                RemoteMachineId = "300"
            });

            // Add routing entries
            config.Routing.RoutingTable.Add(new X25RouteEntry
            {
                Name = "SerialNetwork",
                X121AddressPattern = "100.*",
                Type = RouteType.Interface,
                Destination = "Serial0",
                Priority = 100,
                Enabled = true
            });

            config.Routing.RoutingTable.Add(new X25RouteEntry
            {
                Name = "XOTNetwork",
                X121AddressPattern = "300.*",
                Type = RouteType.Interface,
                Destination = "XOT0",
                Priority = 90,
                Enabled = true
            });

            // Add known hosts
            config.Routing.KnownHosts.Add(new KnownHostEntry
            {
                Name = "TestHost1",
                MachineId = "400",
                Description = "Test host for multi-hop routing",
                Via = "300",
                Reachability = HostReachability.Gateway,
                Enabled = true
            });

            config.Routing.KnownHosts.Add(new KnownHostEntry
            {
                Name = "TestHost2", 
                MachineId = "500",
                Description = "Another test host",
                Via = "100",
                Reachability = HostReachability.Gateway,
                Enabled = true
            });

            // Test JSON serialization
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
            };

            var json = JsonSerializer.Serialize(config, options);
            
            TestContext.WriteLine("JSON Configuration:");
            TestContext.WriteLine(json);
            
            // Verify JSON contains all expected sections
            Assert.That(json, Contains.Substring("\"x25\":"));
            Assert.That(json, Contains.Substring("\"lapb\":"));
            Assert.That(json, Contains.Substring("\"routing\":"));
            Assert.That(json, Contains.Substring("\"serialInterfaces\":"));
            Assert.That(json, Contains.Substring("\"xotInterfaces\":"));
            Assert.That(json, Contains.Substring("\"routingTable\":"));
            Assert.That(json, Contains.Substring("\"knownHosts\":"));
            Assert.That(json, Contains.Substring("\"Serial0\""));
            Assert.That(json, Contains.Substring("\"XOT0\""));
            Assert.That(json, Contains.Substring("\"TestHost1\""));
            Assert.That(json, Contains.Substring("\"TestHost2\""));

            // Test deserialization
            var loadedConfig = JsonSerializer.Deserialize<RuntimeConfig>(json, options);
            Assert.That(loadedConfig, Is.Not.Null);

            // Verify basic settings
            Assert.That(loadedConfig.X25.LocalMachineId, Is.EqualTo("200"));
            Assert.That(loadedConfig.Lapb.IsDCE, Is.False);
            Assert.That(loadedConfig.Lapb.T1Timeout, Is.EqualTo(TimeSpan.FromMilliseconds(3000)));
            Assert.That(loadedConfig.Lapb.WindowSize, Is.EqualTo(7));
            Assert.That(loadedConfig.HistoryCapacity, Is.EqualTo(1000));

            // Verify routing configuration
            Assert.That(loadedConfig.Routing.Enabled, Is.True);
            Assert.That(loadedConfig.Routing.SerialInterfaces, Has.Count.EqualTo(1));
            Assert.That(loadedConfig.Routing.XotInterfaces, Has.Count.EqualTo(1));
            Assert.That(loadedConfig.Routing.RoutingTable, Has.Count.EqualTo(2));
            Assert.That(loadedConfig.Routing.KnownHosts, Has.Count.EqualTo(2));

            // Verify serial interface
            var serialIf = loadedConfig.Routing.SerialInterfaces[0];
            Assert.That(serialIf.Name, Is.EqualTo("Serial0"));
            Assert.That(serialIf.PortName, Is.EqualTo("COM1"));
            Assert.That(serialIf.BaudRate, Is.EqualTo(19200));
            Assert.That(serialIf.RemoteMachineId, Is.EqualTo("100"));

            // Verify XOT interface
            var xotIf = loadedConfig.Routing.XotInterfaces[0];
            Assert.That(xotIf.Name, Is.EqualTo("XOT0"));
            Assert.That(xotIf.LocalPort, Is.EqualTo(1998));
            Assert.That(xotIf.RemoteIPAddress, Is.EqualTo("192.168.1.101"));
            Assert.That(xotIf.RemoteMachineId, Is.EqualTo("300"));

            // Verify routing table
            var route1 = loadedConfig.Routing.RoutingTable.Find(r => r.Name == "SerialNetwork");
            Assert.That(route1, Is.Not.Null);
            Assert.That(route1.X121AddressPattern, Is.EqualTo("100.*"));
            Assert.That(route1.Type, Is.EqualTo(RouteType.Interface));
            Assert.That(route1.Destination, Is.EqualTo("Serial0"));

            // Verify known hosts
            var host1 = loadedConfig.Routing.KnownHosts.Find(h => h.Name == "TestHost1");
            Assert.That(host1, Is.Not.Null);
            Assert.That(host1.MachineId, Is.EqualTo("400"));
            Assert.That(host1.Via, Is.EqualTo("300"));
            Assert.That(host1.Enabled, Is.True);

            TestContext.WriteLine("[TEST] ✅ JSON configuration serialization/deserialization works correctly");
            TestContext.WriteLine($"[TEST] Configuration contains {loadedConfig.Routing.SerialInterfaces.Count} serial interfaces");
            TestContext.WriteLine($"[TEST] Configuration contains {loadedConfig.Routing.XotInterfaces.Count} XOT interfaces");
            TestContext.WriteLine($"[TEST] Configuration contains {loadedConfig.Routing.RoutingTable.Count} routing entries");
            TestContext.WriteLine($"[TEST] Configuration contains {loadedConfig.Routing.KnownHosts.Count} known hosts");
        }
    }
}