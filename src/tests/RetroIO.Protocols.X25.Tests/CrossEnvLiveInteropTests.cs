using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.X25.Client;
using RetroIO.Protocols.X25.Monitor;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Live cross-implementation interop: a C# <see cref="X25Client"/> places a call across XOT to the
    /// running reference (WSL C) switch network and reaches its echo service, proving the C# and C XOT
    /// framing interoperate in both wire directions (the C# CallRequest/Data are parsed by C, and the C
    /// CallAccepted/Data reply is parsed by C#).
    /// </summary>
    /// <remarks>
    /// Excluded from the normal suite — it needs the reference demo running and its host reachable. Enable by
    /// setting <c>CROSSENV_WSL_HOST</c> to the reference switch's address (and optionally
    /// <c>CROSSENV_WSL_PORT</c>, default 2001 = Switch A access port). Run with:
    ///  - <c>dotnet test --filter TestCategory=CrossEnvLive</c> and the env var set.
    /// </remarks>
    [TestFixture]
    [Category("CrossEnvLive")]
    public class CrossEnvLiveInteropTests
    {
        [Test]
        public void CSharpClient_CallsReferenceEcho_AcrossXot()
        {
            string? host = Environment.GetEnvironmentVariable("CROSSENV_WSL_HOST");
            if (string.IsNullOrWhiteSpace(host))
            {
                Assert.Ignore("Set CROSSENV_WSL_HOST to the running reference (WSL) switch address to run this test.");
            }

            string portText = Environment.GetEnvironmentVariable("CROSSENV_WSL_PORT") ?? "2001";
            ushort port = ushort.TryParse(portText, out ushort p) ? p : (ushort)2001;

            using X25Client client = new X25Client { ServiceName = "cs-crossenv" };

            StringBuilder received = new StringBuilder();
            object gate = new object();
            client.SetOnData((c, vc, data, length, qBit, mBit, dBit) =>
            {
                lock (gate)
                {
                    received.Append(Encoding.ASCII.GetString(data, 0, length));
                }
            });

            client.Open(new X25ClientConfig
            {
                Host = host!,
                Port = port,
                LocalAddress = "23400099",
                Framing = X25ClientFraming.Xot
            }, NullLogger.Instance);

            // Reference echo service answers on 23420001 (reference Switch C).
            X25Vc? vc = client.CallWait("23420001", 5000);
            Assert.That(vc, Is.Not.Null, "the reference C switch should accept and route the C# call");

            const string message = "HELLO-FROM-CSHARP";
            Assert.That(client.SendText(vc!, message), Is.True);

            bool echoed = SpinWait.SpinUntil(() =>
            {
                lock (gate)
                {
                    return received.ToString().Contains(message);
                }
            }, 5000);

            string got;
            lock (gate)
            {
                got = received.ToString();
            }

            Assert.That(echoed, Is.True, $"expected the reference echo to return '{message}', got: '{got}'");
        }

        [Test]
        public void WslSwitchTopology_IsDecodedByCSharpMonitorClient()
        {
            // Proves the C# monitor client decodes the X25MON/1 binary topology emitted by the live
            // reference (WSL C) switch's monitor server -- the two implementations are wire-compatible.
            string? host = Environment.GetEnvironmentVariable("CROSSENV_WSL_MON_HOST");
            if (string.IsNullOrWhiteSpace(host))
            {
                Assert.Ignore("Set CROSSENV_WSL_MON_HOST to the running WSL switch's monitor host to run this test.");
            }

            string portText = Environment.GetEnvironmentVariable("CROSSENV_WSL_MON_PORT") ?? "4199";
            int port = int.TryParse(portText, out int p) ? p : 4199;

            using MonitorClient client = new MonitorClient();
            TopologySnapshot? topo = null;
            ManualResetEventSlim got = new ManualResetEventSlim(false);
            client.TopologyReceived += t =>
            {
                topo = t;
                got.Set();
            };

            Assert.That(client.Connect(host!, port), Is.True, "should connect to the WSL switch monitor stream");
            Assert.That(got.Wait(5000), Is.True, "the WSL switch should send a topology snapshot on connect");

            Assert.That(topo, Is.Not.Null);
            Assert.That(topo!.Name, Is.EqualTo("WSL-W5"), "decoded node name from the C switch");
            Assert.That(topo.Dnic, Is.EqualTo("2345"), "decoded DNIC from the C switch");

            // The WSL switch config declares the C# Switch-A as its neighbour.
            bool hasCSharpNeighbour = false;
            for (int i = 0; i < topo.Neighbours.Count; i++)
            {
                if (string.Equals(topo.Neighbours[i].Name, "CSharp-SwitchA", StringComparison.Ordinal))
                {
                    hasCSharpNeighbour = true;
                }
            }
            Assert.That(hasCSharpNeighbour, Is.True, "the decoded WSL topology should list the declared C# neighbour");
        }

        [Test]
        [Category("DemoSmoke")]
        public void CSharpDemo_PadReachesEcho_ThroughAllThreeSwitches()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSHARP_DEMO")))
            {
                Assert.Ignore("Start the C# demo (./demo/demo.ps1 start) and set CSHARP_DEMO=1 to run this smoke test.");
            }

            using X25Client client = new X25Client { ServiceName = "smoke" };
            StringBuilder received = new StringBuilder();
            object gate = new object();
            client.SetOnData((c, vc, data, length, qBit, mBit, dBit) =>
            {
                lock (gate)
                {
                    received.Append(Encoding.ASCII.GetString(data, 0, length));
                }
            });

            client.Open(new X25ClientConfig
            {
                Host = "127.0.0.1",
                Port = 2200,
                LocalAddress = "23500099",
                Framing = X25ClientFraming.Xot
            }, NullLogger.Instance);

            // 23520001 = echo on Switch C; routes A -> B -> C -> echo.
            X25Vc? vc = client.CallWait("23520001", 5000);
            Assert.That(vc, Is.Not.Null, "the call should route through all three C# switches to the echo");

            const string message = "SMOKE-TEST";
            Assert.That(client.SendText(vc!, message), Is.True);

            bool echoed = SpinWait.SpinUntil(() =>
            {
                lock (gate) { return received.ToString().Contains(message); }
            }, 5000);
            Assert.That(echoed, Is.True, "the echo should return through the switch chain");
        }

        [Test]
        public void ReferenceClient_CallsCSharpEcho_AcrossXot()
        {
            string? host = Environment.GetEnvironmentVariable("CROSSENV_WSL_HOST");
            string? repo = Environment.GetEnvironmentVariable("CROSSENV_WSL_REPO");
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(repo))
            {
                Assert.Ignore("Set CROSSENV_WSL_HOST and CROSSENV_WSL_REPO to run the reverse cross-environment test.");
            }

            string portText = Environment.GetEnvironmentVariable("CROSSENV_WSL_CSPORT") ?? "2007";
            ushort csPort = ushort.TryParse(portText, out ushort p) ? p : (ushort)2007;

            // Stand up a C# echo CONNECTED INTO the reference switch on the DNIC-2343 access port. The
            // reference routes calls for 2343 to this connection.
            using X25Client echo = new X25Client { ServiceName = "cs-echo" };

            ManualResetEventSlim gotCall = new ManualResetEventSlim(false);
            ManualResetEventSlim gotData = new ManualResetEventSlim(false);
            string calling = string.Empty;

            echo.SetOnIncoming((c, vc) =>
            {
                calling = vc.CallingAddress;
                c.Accept(vc);
                gotCall.Set();
            });
            echo.SetOnData((c, vc, data, length, qBit, mBit, dBit) =>
            {
                c.Send(vc, data, length, qBit);
                gotData.Set();
            });
            echo.Open(new X25ClientConfig
            {
                Host = host!,
                Port = csPort,
                LocalAddress = "23430001",
                Framing = X25ClientFraming.Xot
            }, NullLogger.Instance);

            Assert.That(SpinWait.SpinUntil(() => echo.IsOpen, 3000), Is.True, "the C# echo should connect to the reference switch");

            // Drive the reference traffic generator to call us. Retry a few times so the reference switch has
            // registered our access-port connection before the call is placed.
            bool called = false;
            for (int attempt = 0; attempt < 5 && !called; attempt++)
            {
                using Process caller = StartWslCall(repo!, "23430001", 2);
                called = gotCall.Wait(3000);
                if (!caller.WaitForExit(3000))
                {
                    caller.Kill(entireProcessTree: true);
                }
            }

            Assert.That(called, Is.True, "the C# echo should receive an incoming call from the reference side");
            Assert.That(gotData.Wait(5000), Is.True, "the C# echo should receive data from the reference side");
            Assert.That(calling.Length, Is.GreaterThan(0), "the incoming call should carry a calling address");
        }

        private static Process StartWslCall(string repo, string calledAddress, int count)
        {
            // Invoke the reference traffic generator directly (a built binary) rather than a shell script,
            // avoiding exec-bit/line-ending issues on scripts authored from the Windows side.
            ProcessStartInfo psi = new ProcessStartInfo("wsl.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("bash");
            psi.ArgumentList.Add("-lc");
            psi.ArgumentList.Add($"cd '{repo}' && ./tools/x25traffic -x 127.0.0.1:2001 -d {calledAddress} -n {count}");

            Process p = new Process { StartInfo = psi };
            p.Start();
            return p;
        }
    }
}
