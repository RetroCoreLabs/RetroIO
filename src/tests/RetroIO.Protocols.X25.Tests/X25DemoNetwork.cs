// =============================================================================
// Precondition for the tests that need the C reference demo network.
//
// The demo is three C switches in series plus an echo service, started with:
//   wsl -e sh -c "cd /home/ronny/repos/os/x25emu && make demo-start"
// It listens on 127.0.0.1 ports 2007 (switch A), 2008 (B) and 2009 (C) for C#
// clients, with the echo service reachable at X.121 address 23420001. Each
// switch also has a management CLI on 9001, 9002 and 9003, and the probe below
// checks those too - they are what actually proves a switch finished starting.
//
// Without it a call cannot be placed and every test in those fixtures fails on a
// five-second connect timeout. That is an absent prerequisite, not a defect, so
// the tests skip - the same shape CrossEnvLiveInteropTests already uses for the
// live cross-implementation test.
// =============================================================================

using System;
using System.Net.Sockets;
using NUnit.Framework;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Detects the C reference demo network and skips the calling test when it is absent.
    /// </summary>
    public static class X25DemoNetwork
    {
        /// <summary>
        /// Name of the environment variable that names the host the demo runs on. Default 127.0.0.1.
        /// </summary>
        public const string HostVariable = "X25_DEMO_HOST";

        /// <summary>
        /// Name of the environment variable that forces a skip without probing.
        /// </summary>
        public const string SkipVariable = "X25_DEMO_SKIP";

        /// <summary>
        /// Switch access ports the C# tests dial (switch A, B and C).
        /// </summary>
        private static readonly int[] Ports = { 2007, 2008, 2009 };

        /// <summary>
        /// Management CLI ports of the three switches (A, B, C).
        /// <para>
        /// These are probed as well, and they are the ones that actually prove a switch is alive.
        /// An access port starts LISTENING early in the C switch's startup, and the kernel then
        /// queues an incoming connection in the listen backlog whether or not the switch has a
        /// thread accepting it - so dialling 2007 alone succeeds against a switch that is still
        /// stuck in startup and will never answer a call. The management listener is opened at the
        /// very END of that startup, after every port is up and just before the I/O threads run,
        /// so it answers only when the switch is genuinely ready.
        /// </para>
        /// <para>
        /// Measured on 2026-09-03: switch A hung in a blocking connect() to an unreachable trunk
        /// peer in its config, 9001 never listened while 9002 and 9003 did, and all seven tests
        /// routed through switch A failed on a connect timeout while the probe reported the demo
        /// up. Probing these ports turns that into an honest skip.
        /// </para>
        /// </summary>
        private static readonly int[] ManagementPorts = { 9001, 9002, 9003 };

        /// <summary>
        /// How long one probe may take. The demo is local, so 250 ms is generous.
        /// </summary>
        private const int ProbeTimeoutMs = 250;

        /// <summary>
        /// Guards the one-time probe.
        /// </summary>
        private static readonly object Gate = new object();

        private static bool _probed;
        private static bool _available;
        private static string _reason = string.Empty;

        /// <summary>
        /// Gets the host the demo network is expected on.
        /// </summary>
        public static string Host
        {
            get
            {
                string? value = Environment.GetEnvironmentVariable(HostVariable);
                return string.IsNullOrWhiteSpace(value) ? "127.0.0.1" : value;
            }
        }

        /// <summary>
        /// Skips the calling test unless the demo network answers on all three access ports.
        /// The probe runs once per test run and the result is cached.
        /// </summary>
        public static void Require()
        {
            bool available;
            string reason;
            lock (Gate)
            {
                if (!_probed)
                {
                    _probed = true;
                    _available = Probe(out _reason);
                }

                available = _available;
                reason = _reason;
            }

            if (!available)
            {
                Assert.Ignore(
                    "The C reference demo network is not running (" + reason + "). Start it with:  " +
                    "wsl -e sh -c \"cd /home/ronny/repos/os/x25emu && make demo-start\"  " +
                    "or run tools\\run-with-demo.ps1, which starts it, runs these tests and stops it again. " +
                    "Set " + SkipVariable + "=1 to skip without probing, or " + HostVariable +
                    " to point at another host.");
            }
        }

        /// <summary>
        /// Probes the three switch access ports.
        /// </summary>
        /// <param name="reason">
        /// Set to the reason the probe failed, or an empty string when it succeeded.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when every port accepted a connection.
        /// </returns>
        private static bool Probe(out string reason)
        {
            string? skip = Environment.GetEnvironmentVariable(SkipVariable);
            if (!string.IsNullOrEmpty(skip) && skip != "0")
            {
                reason = SkipVariable + " is set";
                return false;
            }

            string host = Host;
            for (int i = 0; i < Ports.Length; i++)
            {
                if (!CanConnect(host, Ports[i]))
                {
                    reason = "nothing listening on " + host + ":" + Ports[i];
                    return false;
                }
            }

            // The access ports answer even from a listen backlog, so they only say a switch was
            // started. The management ports say it finished starting. See ManagementPorts.
            for (int i = 0; i < ManagementPorts.Length; i++)
            {
                if (!CanConnect(host, ManagementPorts[i]))
                {
                    reason = "switch " + (char)('A' + i) + " is not fully started - its management " +
                             "port " + host + ":" + ManagementPorts[i] + " is not listening, so it " +
                             "is still in startup and will not answer a call. Check " +
                             "demo/logs/switch_" + (char)('a' + i) + ".log";
                    return false;
                }
            }

            reason = string.Empty;
            return true;
        }

        /// <summary>
        /// Opens and closes one TCP connection.
        /// </summary>
        /// <param name="host">Host to dial.</param>
        /// <param name="port">Port to dial.</param>
        /// <returns>
        /// <see langword="true"/> when the connection was accepted inside the timeout.
        /// </returns>
        private static bool CanConnect(string host, int port)
        {
            try
            {
                using TcpClient client = new TcpClient();
                IAsyncResult ar = client.BeginConnect(host, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(ProbeTimeoutMs))
                {
                    return false;
                }

                client.EndConnect(ar);
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }
}
