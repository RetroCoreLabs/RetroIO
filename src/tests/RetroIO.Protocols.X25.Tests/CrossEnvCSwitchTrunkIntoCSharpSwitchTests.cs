//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Diagnostics;
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
    /// The C emulator's switch calling into a C# switch across a trunk: a call that crosses two
    /// switches of two implementations, with a connection for each circuit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A C switch is started in WSL with an access port and a trunk that dials this process. The
    /// C <c>x25traffic</c> tool calls through it. The C switch opens a spare connection on the trunk
    /// and a new one for the call (RFC 1613 section 5), and numbers the call with its own channel.
    /// The C# trunk port has to accept both, renumber the channel and carry the call to a C# client
    /// behind it. A port that took one connection at a time could not take the call.
    /// </para>
    /// <para>
    /// Out of the normal suite, like the other cross-environment tests: it needs the C tree built in
    /// WSL and this machine reachable from there. Run it with
    /// <c>dotnet test --filter TestCategory=CrossEnvLive</c>. It uses the same <c>X25EMU_DIR</c> and
    /// <c>CROSSENV_HOST_IP</c> settings as <see cref="CrossEnvCClientToCSharpSwitchTests"/>. The C
    /// switch is stopped by the process id it wrote down, never by name.
    /// </para>
    /// </remarks>
    [TestFixture]
    [Category("CrossEnvLive")]
    public class CrossEnvCSwitchTrunkIntoCSharpSwitchTests
    {
        private const int CAccessPort = 41001;
        private const int CManagementPort = 41090;
        private const int CMonitorPort = 41091;

        private static string RunInWsl(string script, int timeoutMs)
        {
            ProcessStartInfo psi = new ProcessStartInfo("wsl")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("bash");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(script);

            using Process? proc = Process.Start(psi);
            Assert.That(proc, Is.Not.Null, "could not start wsl");
            string output = proc!.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            proc.WaitForExit(timeoutMs);
            return output;
        }

        [Test]
        public void CSwitchTrunk_CallsIntoTheCSharpSwitch_AndTheDataArrives()
        {
            string emuDir = Environment.GetEnvironmentVariable("X25EMU_DIR") ?? CrossEnvCClientToCSharpSwitchTests.DefaultEmuDir;
            string hostIp = CrossEnvCClientToCSharpSwitchTests.HostAddressForWsl();
            if (string.IsNullOrWhiteSpace(hostIp))
            {
                Assert.Ignore("No WSL interface found. Set CROSSENV_HOST_IP to the address WSL should use for this machine.");
            }

            // The C# network: a trunk port the C switch dials, and a service port for the callee.
            SwitchConfig cfg = new SwitchConfig { Dnic = "2342" };
            cfg.Routing.AddRoute(new X121Route { Type = X121RouteType.Prefix, Address = "2342", PrefixLen = 4, DestPort = 1 });
            SwitchNode node = new SwitchNode(cfg);

            using XotSwitchPort trunk = new XotSwitchPort(new PortConfig { Id = 0, Mode = PortMode.Dce, Host = "0.0.0.0", Port = 0, LciBase = 1, LciCount = 16 });
            using XotSwitchPort service = new XotSwitchPort(new PortConfig { Id = 1, Mode = PortMode.Dce, Host = "127.0.0.1", Port = 0, LciBase = 1, LciCount = 16 });
            node.AddPort(trunk);
            node.AddPort(service);
            SwitchCore core = new SwitchCore(node);
            Assert.That(core, Is.Not.Null);
            Assert.That(trunk.Start(), Is.True);
            Assert.That(service.Start(), Is.True);

            object gate = new object();
            int packets = 0;
            using X25Client callee = new X25Client { ServiceName = "crossenv-callee" };
            callee.SetOnIncoming((c, vc) => c.Accept(vc));
            callee.SetOnData((c, vc, data, length, q, m, d) =>
            {
                lock (gate)
                {
                    packets++;
                }
            });
            callee.Open(new X25ClientConfig
            {
                Host = "127.0.0.1",
                Port = service.BoundPort,
                LocalAddress = "23420001",
                Framing = X25ClientFraming.Xot,
            }, NullLogger.Instance);

            long deadline = Environment.TickCount64 + 5000;
            while (!service.PeerConnected && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(20);
            }

            Assert.That(service.PeerConnected, Is.True, "the C# callee should have reached the service port");

            // The C switch: an access port for the traffic tool, a trunk that dials the C# trunk
            // port, and 2342 routed to the trunk. Its process id is written to a file to stop it by.
            string dir = "/tmp/x25-crossenv-trunk-" + Environment.ProcessId;
            string conf = string.Join("\n",
                "[switch]", "dnic = 2340", "max_hops = 15", "mgmt_port = " + CManagementPort, "monitor_port = " + CMonitorPort, "name = CrossEnv",
                "[port.0]", "type = access", "transport = xot", "mode = dce", "port = " + CAccessPort, "lci_base = 1", "lci_count = 64",
                "[port.1]", "type = trunk", "transport = xot", "mode = dte", "host = " + hostIp, "port = " + trunk.BoundPort, "lci_base = 1", "lci_count = 64",
                "[route.0]", "type = prefix", "address = 2342", "prefix_len = 4", "port = 1",
                "[route.1]", "type = default", "port = 1",
                string.Empty);

            string start = "set -e; mkdir -p " + dir + "; cat > " + dir + "/switch.conf <<'CONF'\n" + conf + "CONF\n" +
                           "cd " + emuDir + "; setsid nohup tools/x25switch -c " + dir + "/switch.conf > " + dir + "/switch.log 2>&1 < /dev/null & echo $! > " + dir + "/pid; sleep 3; cat " + dir + "/pid";
            string pidText = RunInWsl(start, 20000).Trim();
            string stdout = string.Empty;
            try
            {
                Assert.That(int.TryParse(pidText, out int pid) && pid > 0, Is.True, "the C switch should have started: " + pidText);

                stdout = RunInWsl("cd " + emuDir + " && timeout 40 tools/x25traffic -x 127.0.0.1:" + CAccessPort + " -d 23420001 -n 5 -s 64 -v", 60000);

                long until = Environment.TickCount64 + 5000;
                while (Environment.TickCount64 < until)
                {
                    lock (gate)
                    {
                        if (packets >= 5)
                        {
                            break;
                        }
                    }

                    Thread.Sleep(20);
                }
            }
            finally
            {
                RunInWsl("kill $(cat " + dir + "/pid) 2>/dev/null; sleep 1; rm -rf " + dir + "; echo stopped", 10000);
            }

            int got;
            lock (gate)
            {
                got = packets;
            }

            Assert.That(got, Is.EqualTo(5),
                "the C switch's call and data should have crossed the trunk into the C# network (traffic tool output: " + stdout.Trim() + ")");
            Assert.That(trunk.ConnectAttempts, Is.GreaterThanOrEqualTo(2), "the C trunk opens a spare connection and one for the call");
        }
    }
}
