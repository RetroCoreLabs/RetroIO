//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.X25.Client;
using RetroIO.Protocols.X25.Routing;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Model;
using RetroIO.Protocols.X25.Switch.Ports;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The C emulator's client calling into the C# switch: the direction that had no test.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>CrossEnvLiveInteropTests</c> covers the other way round, a C# client calling the C
    /// switch's echo service. This one runs <c>x25traffic</c> from the C tree against a C# switch
    /// started here, with a C# client answering on the far port. Both directions of the wire are
    /// therefore parsed by the other implementation: the C Call Request and Data are read by C#, and
    /// the C# Call Accepted and Data are read by C.
    /// </para>
    /// <para>
    /// Out of the normal suite: it needs the C tree built in WSL, and the switch's listening socket
    /// has to be reachable from there, which depends on the Windows firewall. Run it with
    /// <c>dotnet test --filter TestCategory=CrossEnvLive</c>. Set <c>X25EMU_DIR</c> if the C tree is
    /// not at the default path, and <c>CROSSENV_HOST_IP</c> to override the address WSL should use
    /// to reach this machine.
    /// </para>
    /// </remarks>
    [TestFixture]
    [Category("CrossEnvLive")]
    public class CrossEnvCClientToCSharpSwitchTests
    {
        private const string DefaultEmuDir = "/home/ronny/repos/os/x25emu";

        /// <summary>
        /// The address WSL uses to reach this machine: the default gateway of its own interface.
        /// </summary>
        private static string HostAddressForWsl()
        {
            string? overridden = Environment.GetEnvironmentVariable("CROSSENV_HOST_IP");
            if (!string.IsNullOrWhiteSpace(overridden))
            {
                return overridden!;
            }

            NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
            for (int i = 0; i < interfaces.Length; i++)
            {
                NetworkInterface nic = interfaces[i];
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                if (nic.Description.IndexOf("WSL", StringComparison.OrdinalIgnoreCase) < 0 &&
                    nic.Name.IndexOf("WSL", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                UnicastIPAddressInformationCollection addresses = nic.GetIPProperties().UnicastAddresses;
                foreach (UnicastIPAddressInformation address in addresses)
                {
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        return address.Address.ToString();
                    }
                }
            }

            return string.Empty;
        }

        private static void WaitFor(Func<bool> condition, int timeoutMs)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (!condition() && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(20);
            }
        }

        [Test]
        public void CClient_CallsThroughTheCSharpSwitch_AndItsDataArrives()
        {
            string emuDir = Environment.GetEnvironmentVariable("X25EMU_DIR") ?? DefaultEmuDir;
            string hostIp = HostAddressForWsl();
            if (string.IsNullOrWhiteSpace(hostIp))
            {
                Assert.Ignore("No WSL interface found. Set CROSSENV_HOST_IP to the address WSL should use for this machine.");
            }

            SwitchConfig cfg = new SwitchConfig { Dnic = "2340" };
            cfg.Routing.AddRoute(new X121Route
            {
                Type = X121RouteType.Prefix,
                Address = "2342",
                PrefixLen = 4,
                DestPort = 1,
            });

            SwitchNode node = new SwitchNode(cfg);
            using XotSwitchPort access = new XotSwitchPort(new PortConfig { Id = 0, Mode = PortMode.Dce, Port = 0, LciBase = 1, LciCount = 16 });
            using XotSwitchPort service = new XotSwitchPort(new PortConfig { Id = 1, Mode = PortMode.Dce, Port = 0, LciBase = 1, LciCount = 16 });

            Assert.That(access.Start(), Is.True);
            Assert.That(service.Start(), Is.True);
            node.AddPort(access);
            node.AddPort(service);
            SwitchCore core = new SwitchCore(node);
            Assert.That(core, Is.Not.Null);

            // The far end: a C# client that answers calls to 23420001 and counts what arrives.
            object gate = new object();
            int packets = 0;
            long bytes = 0;

            using X25Client callee = new X25Client { ServiceName = "crossenv-callee" };
            callee.SetOnIncoming((c, vc) => c.Accept(vc));
            callee.SetOnData((c, vc, data, length, q, m, d) =>
            {
                lock (gate)
                {
                    packets++;
                    bytes += length;
                }
            });

            callee.Open(new X25ClientConfig
            {
                Host = "127.0.0.1",
                Port = service.BoundPort,
                LocalAddress = "23420001",
                Framing = X25ClientFraming.Xot,
            }, NullLogger.Instance);

            WaitFor(() => service.PeerConnected, 5000);
            Assert.That(service.PeerConnected, Is.True, "the C# callee should have reached port 1");

            // Now the C client, from WSL, into port 0.
            string args = $"-e {emuDir}/tools/x25traffic -x {hostIp}:{access.BoundPort} -d 23420001 -n 5 -s 64 -v";
            ProcessStartInfo psi = new ProcessStartInfo("wsl", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using Process? proc = Process.Start(psi);
            Assert.That(proc, Is.Not.Null, "could not start wsl");

            string stdout = proc!.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(30000);

            WaitFor(() => { lock (gate) { return packets >= 5; } }, 5000);

            int got;
            long gotBytes;
            lock (gate)
            {
                got = packets;
                gotBytes = bytes;
            }

            TestContext.Out.WriteLine(stdout);
            TestContext.Out.WriteLine(stderr);
            TestContext.Out.WriteLine($"switch stitches: {node.ActiveStitchCount}");

            Assert.That(got, Is.EqualTo(5),
                $"the C client's data should have crossed the C# switch (stdout: {stdout.Trim()}; stderr: {stderr.Trim()})");
            Assert.That(gotBytes, Is.EqualTo(5 * 64));
        }
    }
}
