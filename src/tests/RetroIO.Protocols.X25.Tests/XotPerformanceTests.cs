//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

// =============================================================================
// XOT Performance Tests - Mirrors x25traffic from C x25emu
//
// PREREQUISITES: C demo network must be running:
//   cd /home/ronny/repos/os/x25emu && make demo-start
//
// Run:
//   dotnet test --filter "Category=Integration&FullyQualifiedName~Performance"
//
// Comparable C command:
//   ./tools/x25traffic -x 127.0.0.1:2001 -d 23420001 -n 1000 -s 128 -r 0
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

namespace RetroIO.Protocols.X25.Tests
{
    [TestFixture]
    [Category("Integration")]
    [Category("X25Demo")]
    public class XotPerformanceTests
    {
        private ILoggerFactory _loggerFactory = null!;
        private ILogger _logger = null!;

        private const string ECHO_ADDRESS = "23420001";
        private const string LOCAL_ADDRESS = "23430001";
        private const int CONNECT_TIMEOUT_MS = 5000;

        // Switch access ports
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
                builder.SetMinimumLevel(LogLevel.Information);
            });
            _logger = _loggerFactory.CreateLogger<XotPerformanceTests>();
        }

        [TearDown]
        public void TearDown()
        {
            _loggerFactory?.Dispose();
        }

        private ProtocolRuntime CreateRuntime(string name, int port)
        {
            var config = new RuntimeConfig();
            config.X25.LocalMachineId = "23430200";
            config.TestMode = true;

            config.Routing.XotInterfaces.Add(new XotInterfaceConfig
            {
                Name = name,
                Enabled = true,
                Mode = XotMode.Client,
                RemoteIPAddress = X25DemoNetwork.Host,
                RemotePort = port,
                RemoteMachineId = "remote",
                ConnectTimeoutMs = CONNECT_TIMEOUT_MS
            });

            config.Routing.RoutingTable.Add(new X25RouteEntry
            {
                Name = "EchoRoute",
                X121AddressPattern = "2342.*",
                Type = RetroIO.Protocols.X25.Runtime.RouteType.Interface,
                Destination = name,
                Priority = 100,
                Enabled = true
            });

            return new ProtocolRuntime(_logger, config);
        }

        private PadClient ConnectPad(ProtocolRuntime runtime, string padName)
        {
            var pad = runtime.CreatePadClient(padName);
            var connectEvent = new ManualResetEvent(false);

            pad.ConnectionStateChanged += (s, a) =>
            {
                if (a.State == PadConnectionState.Connected)
                    connectEvent.Set();
            };

            pad.Connect(ECHO_ADDRESS, LOCAL_ADDRESS);
            Assert.That(connectEvent.WaitOne(CONNECT_TIMEOUT_MS), Is.True, $"{padName} should connect");
            return pad;
        }

        // =====================================================================
        // Stream mode: one call, continuous data, measure throughput
        // Mirrors: x25traffic -x host:port -d 23420001 -n N -s S -r 0
        // =====================================================================
        [Test]
        // The last argument is the minimum delivery percentage the route must achieve.
        // It is 100 on every route, because X.25 loses nothing: the Recommendation's own summary
        // says the service "permits the transmission of data without loss nor duplication", and
        // 4.4.1.3 gives a receiver exactly two conformant outcomes for an in-sequence data packet -
        // accept it, or reset the circuit. Silently dropping is not one of them, anywhere.
        //
        // Getting there took four separate silent-discard bugs, all the same shape: a send that can
        // be refused, and a caller that ignores the refusal. MEASURED 2026-09-05, in the order they
        // were found:
        //
        //   1. x25emu tools/x25switch.c:2750 - lapb_send's refusal discarded, and x25.c:124 had
        //      already spent P(S), so the neighbour saw a gap and reset the circuit.
        //   2. x25emu tools/x25switch.c port_on_data - the packet that triggered the RNR was itself
        //      thrown away. Proven exactly: a 100-packet run delivered 84 and logged 16 drops.
        //   3. OURS, ProtocolRuntime - the piggybacked P(R) of an incoming DATA packet cleared the
        //      peer's busy condition. X.25 4.4.1.6 says only an RR or a reset does that. On an echo
        //      circuit every packet coming back undid the RNR the switch had just sent, so we
        //      ploughed straight through it and overran the switch's forwarding queue.
        //   4. x25emu tools/x25echo.c - the echo ignored x25c_send's refusal when its own window
        //      filled. This is why the FAST path lost packets and the slow multi-hop ones did not:
        //      a trunk paces arrivals enough that the echo's window never saturates.
        //
        // A fifth thing was wrong and it was in this file: the wait was a fixed
        // packetCount * 200 + 5000 ms, which expired while packets were still in flight and counted
        // everything still in the pipe as lost. Every delivery figure measured before this fix mixed
        // real loss with "had not arrived yet", which is why the numbers wandered between 3% and
        // 100% on an unchanged network and why no threshold could be made to stick. The wait is now
        // on quiescence.
        //
        // With all five fixed: 100% on all four routes, zero drops, zero resets, 500 packets in
        // 2.95 s. If one of these ever fails, something is dropping packets again - find it, do not
        // lower the number.
        [TestCase(100, 60, SWITCH_C_PORT, "DirectC", 100, TestName = "PerformanceStream_100x60B_DirectC")]
        [TestCase(500, 60, SWITCH_C_PORT, "DirectC", 100, TestName = "PerformanceStream_500x60B_DirectC")]
        [TestCase(100, 60, SWITCH_A_PORT, "ViaA", 100, TestName = "PerformanceStream_100x60B_ViaA")]
        [TestCase(100, 60, SWITCH_B_PORT, "ViaB", 100, TestName = "PerformanceStream_100x60B_ViaB")]
        public void PerformanceStream(int packetCount, int payloadSize, int port, string label, int minDeliveryPct)
        {
            using var runtime = CreateRuntime($"XOT_{label}", port);
            var pad = ConnectPad(runtime, "PerfPAD");

            int recvPackets = 0;
            long recvBytes = 0;
            var allReceived = new ManualResetEventSlim(false);

            long lastArrivalTicks = Environment.TickCount64;

            pad.DataReceived += (s, a) =>
            {
                int count = Interlocked.Increment(ref recvPackets);
                Interlocked.Add(ref recvBytes, a.Data.Length);
                Interlocked.Exchange(ref lastArrivalTicks, Environment.TickCount64);
                if (count >= packetCount)
                    allReceived.Set();
            };

            // Build text payload (printable ASCII, fits in one X.25 packet with CR+LF)
            var sb = new StringBuilder(payloadSize);
            for (int i = 0; i < payloadSize; i++)
                sb.Append((char)('A' + (i % 26)));
            string payload = sb.ToString();

            // Stream send at max speed — window-based flow control handles pacing
            var sw = Stopwatch.StartNew();
            int sent = 0;
            for (int i = 0; i < packetCount; i++)
            {
                if (pad.SendText(payload + "\r\n"))
                    sent++;
            }

            // Wait until the echoes STOP ARRIVING, not until an arbitrary clock runs out.
            //
            // This used to wait packetCount * 200 + 5000 ms, which for 100 packets is 25 seconds -
            // and when the path is slow, the send loop alone can take that long. The wait then
            // expired while packets were still in flight and everything still in the pipe was
            // counted as lost. Every delivery figure measured before 2026-09-05 mixed real loss
            // with "had not arrived yet", which is why the numbers wandered between 3% and 100%
            // on an unchanged network and why no threshold could be made to stick.
            //
            // Quiescence is the honest test: keep waiting while packets are still coming, and only
            // conclude that the rest are gone after a clear idle gap. The hard deadline is a
            // backstop for a path that has died completely, not the normal exit.
            const int IDLE_GAP_MS = 5000;
            long hardDeadline = Environment.TickCount64 + 120000;
            while (Volatile.Read(ref recvPackets) < packetCount)
            {
                if (allReceived.Wait(250))
                    break;
                if (Environment.TickCount64 - Interlocked.Read(ref lastArrivalTicks) > IDLE_GAP_MS)
                    break;
                if (Environment.TickCount64 > hardDeadline)
                    break;
            }
            sw.Stop();

            double elapsedSec = sw.Elapsed.TotalSeconds;
            long sentBytes = (long)sent * (payloadSize + 2); // +2 for CR+LF
            double sentKbps = sentBytes * 8.0 / elapsedSec / 1000.0;
            double recvKbps = recvBytes * 8.0 / elapsedSec / 1000.0;
            double deliveryPct = sent > 0 ? 100.0 * recvPackets / sent : 0;

            pad.Disconnect();
            pad.Dispose();

            Console.WriteLine();
            Console.WriteLine($"=== Stream Results [{label}] ===");
            Console.WriteLine($"Duration:  {elapsedSec:F2} seconds");
            Console.WriteLine($"Sent:      {sent} pkts, {sentBytes} bytes ({sentKbps:F0} Kbit/s)");
            Console.WriteLine($"Received:  {recvPackets} pkts, {recvBytes} bytes ({recvKbps:F0} Kbit/s)");
            Console.WriteLine($"Delivery:  {deliveryPct:F1}%");
            Console.WriteLine($"Pkt rate:  {sent / elapsedSec:F1} pkt/s send, {recvPackets / elapsedSec:F1} pkt/s recv");
            Console.WriteLine();

            Assert.That(sent, Is.EqualTo(packetCount),
                $"Route {label} refused a send: only {sent} of {packetCount} packets were accepted for sending.");

            Assert.That(recvPackets, Is.GreaterThanOrEqualTo(sent * minDeliveryPct / 100),
                $"Delivery on route {label} was {recvPackets} of {sent}. X.25 does not lose packets - "
                + "every loss found on this path so far has been a send refusal that some caller ignored. "
                + "See the TestCase comment for the four that were fixed, and look for a fifth rather "
                + "than lowering this number.");
        }

        // =====================================================================
        // Call storm: rapid call/data/clear cycles
        // Mirrors: x25traffic -x host:port -d 23420001 -C -n N -p 3
        // =====================================================================
        [Test]
        [TestCase(10, 3, SWITCH_C_PORT, "DirectC", TestName = "PerformanceCallStorm_10calls_DirectC")]
        [TestCase(10, 3, SWITCH_A_PORT, "ViaA", TestName = "PerformanceCallStorm_10calls_ViaA")]
        public void PerformanceCallStorm(int numCalls, int packetsPerCall, int port, string label)
        {
            int connected = 0;
            int echoed = 0;
            int cleared = 0;
            int failed = 0;

            var sw = Stopwatch.StartNew();

            for (int c = 0; c < numCalls; c++)
            {
                using var runtime = CreateRuntime($"XOT_Storm{c}", port);
                var pad = runtime.CreatePadClient($"Storm{c}");
                var connectEvent = new ManualResetEvent(false);
                var dataEvent = new ManualResetEvent(false);
                int callEchoes = 0;

                pad.ConnectionStateChanged += (s, a) =>
                {
                    if (a.State == PadConnectionState.Connected)
                        connectEvent.Set();
                };

                pad.DataReceived += (s, a) =>
                {
                    Interlocked.Increment(ref callEchoes);
                    if (callEchoes >= packetsPerCall)
                        dataEvent.Set();
                };

                pad.Connect(ECHO_ADDRESS, LOCAL_ADDRESS);

                if (!connectEvent.WaitOne(CONNECT_TIMEOUT_MS))
                {
                    failed++;
                    pad.Dispose();
                    continue;
                }
                connected++;

                // Send data packets
                for (int p = 0; p < packetsPerCall; p++)
                {
                    pad.SendText($"STORM-{c}-PKT-{p}\r\n");
                    Thread.Sleep(50);
                }

                // Wait for echoes
                if (dataEvent.WaitOne(3000))
                    echoed += callEchoes;
                else
                    echoed += callEchoes; // Count what we got

                pad.Disconnect();
                pad.Dispose();
                cleared++;

                Thread.Sleep(100); // Brief pause between calls
            }

            sw.Stop();
            double elapsedSec = sw.Elapsed.TotalSeconds;
            double callsPerSec = connected / elapsedSec;

            Console.WriteLine();
            Console.WriteLine($"=== Call Storm Results [{label}] ===");
            Console.WriteLine($"Duration:    {elapsedSec:F2} seconds");
            Console.WriteLine($"Calls:       {connected}/{numCalls} connected ({failed} failed)");
            Console.WriteLine($"Cleared:     {cleared}");
            Console.WriteLine($"Data:        {echoed} echo packets received");
            Console.WriteLine($"Call rate:   {callsPerSec:F1} calls/sec");
            Console.WriteLine();

            Assert.That(connected, Is.GreaterThanOrEqualTo(numCalls * 8 / 10),
                $"At least 80% of calls should connect. Connected {connected}/{numCalls}");
        }

        // =====================================================================
        // Latency test: single packet round-trip times
        // =====================================================================
        [Test]
        [TestCase(SWITCH_C_PORT, "DirectC", TestName = "PerformanceLatency_DirectC")]
        [TestCase(SWITCH_A_PORT, "ViaA_2hops", TestName = "PerformanceLatency_ViaA")]
        [TestCase(SWITCH_B_PORT, "ViaB_1hop", TestName = "PerformanceLatency_ViaB")]
        public void PerformanceLatency(int port, string label)
        {
            const int SAMPLES = 20;
            using var runtime = CreateRuntime($"XOT_{label}", port);
            var pad = ConnectPad(runtime, "LatencyPAD");

            var latencies = new List<double>();

            for (int i = 0; i < SAMPLES; i++)
            {
                string marker = $"LAT-{i:D4}";
                var dataEvent = new ManualResetEvent(false);

                void handler(object? s, PadDataEventArgs a)
                {
                    if (a.Data.Contains(marker))
                        dataEvent.Set();
                }

                pad.DataReceived += handler;

                var sw = Stopwatch.StartNew();
                pad.SendText(marker + "\r\n");
                bool got = dataEvent.WaitOne(5000);
                sw.Stop();

                pad.DataReceived -= handler;

                if (got)
                    latencies.Add(sw.Elapsed.TotalMilliseconds);

                Thread.Sleep(50); // Pace between samples
            }

            pad.Disconnect();
            pad.Dispose();

            if (latencies.Count == 0)
            {
                Assert.Fail("No latency samples collected");
                return;
            }

            latencies.Sort();
            double min = latencies[0];
            double max = latencies[latencies.Count - 1];
            double median = latencies[latencies.Count / 2];
            double sum = 0;
            for (int i = 0; i < latencies.Count; i++) sum += latencies[i];
            double avg = sum / latencies.Count;
            double p95 = latencies[(int)(latencies.Count * 0.95)];

            Console.WriteLine();
            Console.WriteLine($"=== Latency Results [{label}] ({latencies.Count}/{SAMPLES} samples) ===");
            Console.WriteLine($"Min:     {min:F1} ms");
            Console.WriteLine($"Avg:     {avg:F1} ms");
            Console.WriteLine($"Median:  {median:F1} ms");
            Console.WriteLine($"P95:     {p95:F1} ms");
            Console.WriteLine($"Max:     {max:F1} ms");
            Console.WriteLine();

            Assert.That(latencies.Count, Is.GreaterThanOrEqualTo(SAMPLES * 8 / 10),
                $"At least 80% of samples should succeed. Got {latencies.Count}/{SAMPLES}");
        }
    }
}
