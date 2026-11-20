//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Model;
using RetroIO.Protocols.X25.Switch.Ports;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The RFC 1613 6.5 PVC setup between C# and x25emu (RetroX25 PLAN C3). x25emu's switch is the responder
    /// (<c>xot_pvc_respond</c>), so here the C# XOT port is the initiator.
    /// </summary>
    /// <remarks>
    /// Out of the normal suite, like the other cross-environment tests: it needs x25emu built in WSL. Run it with
    /// <c>dotnet test --filter TestCategory=CrossEnvLive</c>. The x25emu switch is stopped by the process id it wrote
    /// down, never by name.
    /// </remarks>
    [TestFixture]
    [Category("CrossEnvLive")]
    public class CrossEnvXotPvcTests
    {
        private const int CPvcPort = 41101;
        private const int COtherPort = 41102;
        private const int CManagementPort = 41190;
        private const int CMonitorPort = 41191;

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
        public void X25emuInitiator_IsAcceptedByTheCSharpSwitch_AndItsDataCrosses()
        {
            string emuDir = CrossEnvCClientToCSharpSwitchTests.EmuDir();
            string hostIp = CrossEnvCClientToCSharpSwitchTests.HostAddressForWsl();
            if (string.IsNullOrWhiteSpace(hostIp))
            {
                Assert.Ignore("No WSL interface found. Set CROSSENV_HOST_IP to the address WSL should use for this machine.");
            }

            // The C# switch: an XOT responder port (PVC channel 1, the channel x25emu's client asks for) joined to a
            // loopback port's channel 2. x25traffic runs window 7, so the C# port does too, or 0x18 is the answer.
            SwitchConfig cfg = new SwitchConfig { Dnic = "2340" };
            cfg.Pvcs.Add(new PvcConfig { SourcePort = 0, SourceLci = 1, DestPort = 1, DestLci = 2 });
            SwitchNode node = new SwitchNode(cfg);
            TrunkListLogger csLog = new TrunkListLogger();
            using XotSwitchPort xot = new XotSwitchPort(new PortConfig
            {
                Id = 0, Mode = PortMode.Dce, Host = "0.0.0.0", Port = 0, LciBase = 16, LciCount = 16,
                PvcBase = 1, PvcCount = 8, InterfaceName = "Serial1", DefaultWindowSize = 7, DefaultPacketSize = 128,
            }, csLog);
            LoopbackSwitchPort local = new LoopbackSwitchPort(new PortConfig { Id = 1, LciBase = 16 });
            node.AddPort(xot);
            node.AddPort(local);
            Assert.That(xot.Start(), Is.True);
            local.Start();
            SwitchCore core = new SwitchCore(node);

            string output = RunInWsl("cd " + emuDir + " && timeout 30 tools/x25traffic -x " + hostIp + ":" + xot.BoundPort
                + " -P 1 -I Serial1 -n 5 -s 64 -r 20", 60000);

            Assert.That(SpinWait.SpinUntil(() => local.SentData.Count >= 5, 5000), Is.True,
                "5 packets crossed. x25traffic:\n" + output + "\nC# log:\n" + csLog.All());
            Assert.That(local.SentData[0].Lci, Is.EqualTo((ushort)2));
            Assert.That(local.SentData[0].Data.Length, Is.EqualTo(64));
            GC.KeepAlive(core);
        }

        [Test]
        public void CSharpInitiator_IsAcceptedByTheX25emuSwitch_AndThePvcCarriesData()
        {
            string emuDir = CrossEnvCClientToCSharpSwitchTests.EmuDir();

            // x25emu: PVC channel 1 on XOT port 0 joined to channel 1 on XOT port 1. Window 2, packet 128 on both, the
            // same as the C# end, so the flow-control check (0x18) passes.
            string dir = "/tmp/x25-crossenv-pvc-" + Environment.ProcessId;
            string conf = string.Join("\n",
                "[switch]", "dnic = 2340", "max_hops = 15", "mgmt_port = " + CManagementPort, "monitor_port = " + CMonitorPort, "name = CrossEnvPvc",
                "[port.0]", "type = access", "transport = xot", "mode = dce", "port = " + CPvcPort, "lci_base = 16", "lci_count = 16",
                "pvc_base = 1", "pvc_count = 8", "def_win_size = 2", "def_pkt_size = 128",
                "[port.1]", "type = access", "transport = xot", "mode = dce", "port = " + COtherPort, "lci_base = 16", "lci_count = 16",
                "pvc_base = 1", "pvc_count = 8", "def_win_size = 2", "def_pkt_size = 128",
                "[pvc.0]", "src_port = 0", "src_lci = 1", "dst_port = 1", "dst_lci = 1",
                string.Empty);

            string start = "set -e; mkdir -p " + dir + "; cat > " + dir + "/switch.conf <<'CONF'\n" + conf + "CONF\n" +
                           "cd " + emuDir + "; setsid nohup tools/x25switch -c " + dir + "/switch.conf > " + dir + "/switch.log 2>&1 < /dev/null & echo $! > " + dir + "/pid; sleep 3; cat " + dir + "/pid";
            string pidText = RunInWsl(start, 20000).Trim();
            string log = string.Empty;
            TrunkListLogger csLog = new TrunkListLogger();
            try
            {
                Assert.That(int.TryParse(pidText, out int pid) && pid > 0, Is.True, "the x25emu switch should have started: " + pidText);

                using XotSwitchPort initiator = new XotSwitchPort(new PortConfig
                {
                    Id = 0, Mode = PortMode.Dce, Host = "127.0.0.1", Port = 0, LciBase = 16, LciCount = 16,
                    PvcBase = 1, PvcCount = 8, InterfaceName = "Serial0", DefaultWindowSize = 2, DefaultPacketSize = 128,
                    XotPvcInitiator = true,
                }, csLog);
                Assert.That(initiator.AddPvc(3), Is.True);
                Assert.That(initiator.Start(), Is.True);

                // x25emu does not read an interface name from its file, so it accepts any name asked for.
                // Windows' 127.0.0.1 does not reach a WSL listener here (measured: "actively refused"), so WSL's own
                // address is asked for and dialled.
                string wslIp = RunInWsl("hostname -I | cut -d' ' -f1", 10000).Trim();
                initiator.StartPvcSetup(3, wslIp + ":" + CPvcPort, "Serial1", 1);

                bool sent = SpinWait.SpinUntil(() => initiator.SendData(3, new byte[] { 1, 2, 3 }, false, false, false) == PortSendResult.Sent, 10000);
                Thread.Sleep(500);
                log = RunInWsl("cat " + dir + "/switch.log", 10000);
                Assert.That(sent, Is.True, "the PVC was joined (status 0x00) and took data. x25emu log:\n" + log
                    + "\nC# log:\n" + csLog.All());
            }
            finally
            {
                RunInWsl("kill $(cat " + dir + "/pid) 2>/dev/null; sleep 1; rm -rf " + dir + "; echo stopped", 10000);
            }

            TestContext.Out.WriteLine(log);
        }
    }
}
