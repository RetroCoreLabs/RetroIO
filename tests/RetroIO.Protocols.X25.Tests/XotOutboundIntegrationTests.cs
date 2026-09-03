// =============================================================================
// XOT Outbound Integration Tests
//
// PREREQUISITES: The C x25emu demo network must be running before executing:
//   cd /home/ronny/repos/os/x25emu && make demo-start
//
// This starts:
//   Switch A (DNIC 2340) - ports 2001 (PAD), 2007 (C# emulator)
//   Switch B (DNIC 2341) - ports 2003 (trunk), 2008 (C# emulator)
//   Switch C (DNIC 2342) - ports 2005 (trunk), 2006 (echo), 2009 (C# emulator)
//   Echo service at address 23420001 on Switch C port 2006
//
// Run these tests:
//   dotnet test --filter "Category=Integration"
//
// Run without integration tests:
//   dotnet test --filter "Category!=Integration"
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RetroIO.Protocols.X25;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Runtime.Models;
using RetroIO.Protocols.X25.Sintran;
using RetroIO.Protocols.X25.Modules;
using RetroIO.Protocols.X25.Runtime.Transport;
using RetroIO.Protocols.X25.Transport;

namespace RetroIO.Protocols.X25.Tests
{
    [TestFixture]
    [Category("Integration")]
    [Category("X25Demo")]
    public class XotOutboundIntegrationTests
    {
        private ILoggerFactory _loggerFactory = null!;
        private ILogger _logger = null!;

        private const string ECHO_ADDRESS = "23420001";
        private const string LOCAL_ADDRESS = "23430001";
        private const int CONNECT_TIMEOUT_MS = 5000;
        private const int DATA_TIMEOUT_MS = 3000;

        // Switch access ports for C# emulator
        private const int SWITCH_A_PORT = 2007;
        private const int SWITCH_B_PORT = 2008;
        private const int SWITCH_C_PORT = 2009;

        [SetUp]
        public void Setup()
        {
            // The C reference demo network must be up; skip cleanly when it is not.
            X25DemoNetwork.Require();

            _loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Debug);
            });
            _logger = _loggerFactory.CreateLogger<XotOutboundIntegrationTests>();
        }

        [TearDown]
        public void TearDown()
        {
            _loggerFactory?.Dispose();
        }

        // =====================================================================
        // Helper: Create runtime with XOT client config pointing at a switch
        // =====================================================================
        private ProtocolRuntime CreateRuntimeWithXotClient(string name, string remoteIP, int remotePort)
        {
            // Callers pass the loopback default. Resolve it through X25DemoNetwork.Host so a demo
            // living in WSL - where 127.0.0.1 is not reachable from Windows - is dialled at
            // X25_DEMO_HOST instead. With that variable unset this is still 127.0.0.1, so nothing
            // changes for a demo running on this machine.
            if (remoteIP == "127.0.0.1")
            {
                remoteIP = X25DemoNetwork.Host;
            }

            var config = new RuntimeConfig();
            config.X25.LocalMachineId = "23430200";
            config.TestMode = true;

            config.Routing.XotInterfaces.Add(new XotInterfaceConfig
            {
                Name = name,
                Enabled = true,
                Mode = XotMode.Client,
                RemoteIPAddress = remoteIP,
                RemotePort = remotePort,
                RemoteMachineId = "remote",
                ConnectTimeoutMs = CONNECT_TIMEOUT_MS,
                Description = $"XOT client to {remoteIP}:{remotePort}"
            });

            config.Routing.RoutingTable.Add(new X25RouteEntry
            {
                Name = "EchoRoute",
                X121AddressPattern = "2342.*",
                Type = RetroIO.Protocols.X25.Runtime.RouteType.Interface,
                Destination = name,
                Priority = 100,
                Enabled = true,
                Description = "Route to echo service via switch"
            });

            return new ProtocolRuntime(_logger, config);
        }

        // =====================================================================
        // Helper: Connect PAD, send data, verify echo, return round-trip time
        // =====================================================================
        private (bool success, string response, long roundTripMs) CallEchoService(
            ProtocolRuntime runtime, string message)
        {
            var padClient = runtime.CreatePadClient("TestPAD");
            string receivedData = "";
            var dataEvent = new ManualResetEvent(false);
            var connectEvent = new ManualResetEvent(false);

            padClient.ConnectionStateChanged += (sender, args) =>
            {
                _logger.LogInformation("PAD state: {State} - {Reason}", args.State, args.Reason);
                if (args.State == PadConnectionState.Connected)
                {
                    connectEvent.Set();
                }
            };

            padClient.DataReceived += (sender, args) =>
            {
                receivedData += args.Data;
                _logger.LogInformation("PAD received: {Data}", args.Data);
                if (receivedData.Contains(message))
                    dataEvent.Set();
            };

            // Connect to echo service
            var callResult = padClient.Connect(ECHO_ADDRESS, LOCAL_ADDRESS);
            if (!callResult)
            {
                padClient.Dispose();
                return (false, "Connect returned false", 0);
            }

            // Wait for Call Accepted
            if (!connectEvent.WaitOne(CONNECT_TIMEOUT_MS))
            {
                padClient.Dispose();
                return (false, "Timeout waiting for connection", 0);
            }

            // Send data and measure round-trip
            var sw = Stopwatch.StartNew();
            padClient.SendText(message + "\r\n");

            // Wait for echo response
            bool gotResponse = dataEvent.WaitOne(DATA_TIMEOUT_MS);
            sw.Stop();

            padClient.Disconnect();
            padClient.Dispose();

            return (gotResponse, receivedData, sw.ElapsedMilliseconds);
        }

        // =====================================================================
        // Test 1: Call echo service via Switch A (2 hops: A -> B -> C)
        // =====================================================================
        [Test]
        public void CallEchoViaSwitchA_ShouldReceiveResponse()
        {
            using var runtime = CreateRuntimeWithXotClient("XOT_SwitchA", "127.0.0.1", SWITCH_A_PORT);

            var (success, response, rtMs) = CallEchoService(runtime, "HelloFromCSharpViaA");

            _logger.LogInformation("Switch A: success={Success}, roundTrip={RT}ms", success, rtMs);
            Assert.That(success, Is.True, $"Should receive echo. Got: {response}");
            Assert.That(response, Does.Contain("HelloFromCSharpViaA"));
        }

        // =====================================================================
        // Test 2: Call echo service via Switch B (1 hop: B -> C)
        // =====================================================================
        [Test]
        public void CallEchoViaSwitchB_ShouldReceiveResponse()
        {
            using var runtime = CreateRuntimeWithXotClient("XOT_SwitchB", "127.0.0.1", SWITCH_B_PORT);

            var (success, response, rtMs) = CallEchoService(runtime, "HelloFromCSharpViaB");

            _logger.LogInformation("Switch B: success={Success}, roundTrip={RT}ms", success, rtMs);
            Assert.That(success, Is.True, $"Should receive echo. Got: {response}");
            Assert.That(response, Does.Contain("HelloFromCSharpViaB"));
        }

        // =====================================================================
        // Test 3: Call echo service via Switch C (direct, 0 hops)
        // =====================================================================
        [Test]
        public void CallEchoViaSwitchC_ShouldReceiveResponse()
        {
            using var runtime = CreateRuntimeWithXotClient("XOT_SwitchC", "127.0.0.1", SWITCH_C_PORT);

            var (success, response, rtMs) = CallEchoService(runtime, "HelloFromCSharpViaC");

            _logger.LogInformation("Switch C: success={Success}, roundTrip={RT}ms", success, rtMs);
            Assert.That(success, Is.True, $"Should receive echo. Got: {response}");
            Assert.That(response, Does.Contain("HelloFromCSharpViaC"));
        }

        // =====================================================================
        // Test 4: Throughput test via each switch
        // =====================================================================
        [Test]
        [TestCase("127.0.0.1", SWITCH_A_PORT, "SwitchA", TestName = "ThroughputViaSwitchA")]
        [TestCase("127.0.0.1", SWITCH_B_PORT, "SwitchB", TestName = "ThroughputViaSwitchB")]
        [TestCase("127.0.0.1", SWITCH_C_PORT, "SwitchC", TestName = "ThroughputViaSwitchC")]
        public void ThroughputTest_ShouldEchoAllMessages(string ip, int port, string label)
        {
            const int MESSAGE_COUNT = 50;
            using var runtime = CreateRuntimeWithXotClient($"XOT_{label}", ip, port);

            var padClient = runtime.CreatePadClient("PerfPAD");
            int echoCount = 0;
            var receivedMessages = new List<string>();
            var connectEvent = new ManualResetEvent(false);

            padClient.ConnectionStateChanged += (sender, args) =>
            {
                if (args.State == PadConnectionState.Connected)
                {
                    connectEvent.Set();
                }
            };

            padClient.DataReceived += (sender, args) =>
            {
                receivedMessages.Add(args.Data);
                // Count messages that contain our test prefix
                if (args.Data.Contains("MSG-"))
                    Interlocked.Increment(ref echoCount);
            };

            padClient.Connect(ECHO_ADDRESS, LOCAL_ADDRESS);
            Assert.That(connectEvent.WaitOne(CONNECT_TIMEOUT_MS), Is.True, "Should connect");

            // Send messages and measure throughput
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < MESSAGE_COUNT; i++)
            {
                padClient.SendText($"MSG-{i:D4}\r\n");
                Thread.Sleep(50); // Small delay to avoid window overflow
            }

            // Wait for responses (generous timeout for network traversal)
            Thread.Sleep(MESSAGE_COUNT * 100 + 2000);
            sw.Stop();

            padClient.Disconnect();
            padClient.Dispose();

            double messagesPerSecond = MESSAGE_COUNT / (sw.Elapsed.TotalSeconds);
            _logger.LogInformation("[{Label}] Throughput: {Count}/{Total} messages, {MPS:F1} msg/s, {Elapsed}ms total",
                label, echoCount, MESSAGE_COUNT, messagesPerSecond, sw.ElapsedMilliseconds);

            // At least 80% of messages should echo back
            Assert.That(echoCount, Is.GreaterThanOrEqualTo(MESSAGE_COUNT * 8 / 10),
                $"Expected at least {MESSAGE_COUNT * 8 / 10} echoes, got {echoCount}");
        }

        // =====================================================================
        // Test 5: Sequential reconnect (A -> B -> C)
        // =====================================================================
        [Test]
        public void SequentialReconnect_ShouldWorkAcrossAllSwitches()
        {
            var switches = new[]
            {
                ("SwitchA", "127.0.0.1", SWITCH_A_PORT),
                ("SwitchB", "127.0.0.1", SWITCH_B_PORT),
                ("SwitchC", "127.0.0.1", SWITCH_C_PORT)
            };

            foreach (var (name, ip, port) in switches)
            {
                _logger.LogInformation("=== Testing via {Name} ({IP}:{Port}) ===", name, ip, port);

                using var runtime = CreateRuntimeWithXotClient($"XOT_{name}", ip, port);
                var message = $"ReconnectTest-{name}";
                var (success, response, rtMs) = CallEchoService(runtime, message);

                _logger.LogInformation("{Name}: success={Success}, roundTrip={RT}ms", name, success, rtMs);
                Assert.That(success, Is.True, $"Echo via {name} should work. Got: {response}");
                Assert.That(response, Does.Contain(message), $"Response from {name} should contain message");
            }
        }

        // =====================================================================
        // Test 6: Simultaneous connections to all three switches
        // =====================================================================
        [Test]
        public void SimultaneousConnections_ShouldAllWork()
        {
            var config = new RuntimeConfig();
            config.X25.LocalMachineId = "23430200";
            config.TestMode = true;

            // Add all three XOT client interfaces
            config.Routing.XotInterfaces.Add(new XotInterfaceConfig
            {
                Name = "XOT_A", Enabled = true, Mode = XotMode.Client,
                RemoteIPAddress = X25DemoNetwork.Host, RemotePort = SWITCH_A_PORT,
                RemoteMachineId = "switchA", ConnectTimeoutMs = CONNECT_TIMEOUT_MS
            });
            config.Routing.XotInterfaces.Add(new XotInterfaceConfig
            {
                Name = "XOT_B", Enabled = true, Mode = XotMode.Client,
                RemoteIPAddress = X25DemoNetwork.Host, RemotePort = SWITCH_B_PORT,
                RemoteMachineId = "switchB", ConnectTimeoutMs = CONNECT_TIMEOUT_MS
            });
            config.Routing.XotInterfaces.Add(new XotInterfaceConfig
            {
                Name = "XOT_C", Enabled = true, Mode = XotMode.Client,
                RemoteIPAddress = X25DemoNetwork.Host, RemotePort = SWITCH_C_PORT,
                RemoteMachineId = "switchC", ConnectTimeoutMs = CONNECT_TIMEOUT_MS
            });

            // Route via first available (all go to echo service)
            config.Routing.RoutingTable.Add(new X25RouteEntry
            {
                Name = "EchoViaA", X121AddressPattern = "2342.*",
                Type = RetroIO.Protocols.X25.Runtime.RouteType.Interface, Destination = "XOT_A",
                Priority = 100, Enabled = true
            });

            using var runtime = new ProtocolRuntime(_logger, config);

            // Create 3 PAD clients
            var results = new List<(string label, bool success, string response)>();

            for (int i = 0; i < 3; i++)
            {
                var label = $"Client{i}";
                var padClient = runtime.CreatePadClient(label);
                string padResponse = "";
                var padConnectEvent = new ManualResetEvent(false);
                var padDataEvent = new ManualResetEvent(false);
                string testMessage = $"SimTest-{label}";

                padClient.ConnectionStateChanged += (s, a) =>
                {
                    if (a.State == PadConnectionState.Connected)
                    {
                        padConnectEvent.Set();
                    }
                };
                padClient.DataReceived += (s, a) =>
                {
                    padResponse += a.Data;
                    if (padResponse.Contains(testMessage))
                        padDataEvent.Set();
                };

                padClient.Connect(ECHO_ADDRESS, LOCAL_ADDRESS);

                if (padConnectEvent.WaitOne(CONNECT_TIMEOUT_MS))
                {
                    padClient.SendText(testMessage + "\r\n");
                    bool gotEcho = padDataEvent.WaitOne(DATA_TIMEOUT_MS);
                    results.Add((label, gotEcho, padResponse));
                }
                else
                {
                    results.Add((label, false, "Connection timeout"));
                }

                padClient.Disconnect();
                padClient.Dispose();
                Thread.Sleep(500); // Brief pause between clients
            }

            foreach (var (label, success, response) in results)
            {
                _logger.LogInformation("{Label}: success={Success}", label, success);
                Assert.That(success, Is.True, $"{label} should receive echo. Got: {response}");
            }
        }
    }
}
