//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Threading;
using NUnit.Framework;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Model;
using RetroIO.Protocols.X25.Switch.Ports;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The restart cause on an HDLC trunk between the C# switch and the C emulator (x25emu), both ways round, read off
    /// the wire by a proxy between them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// X.25 (10/96) 4.6.2 names a reset of the data link layer as an out of order condition; 4.6.5 says that when it
    /// is recovered "the DCE will send a restart indication packet with the cause 'Network operational'"; Table 5-9
    /// gives a DCE only 01, 03 and 07, and a DTE 0 (note a). So whichever program is the DCE sends `10 00 FB 07 00`
    /// when the link comes up and again after a SABM on the up link, and the DTE sends cause 0. RetroX25 PLAN H4-1;
    /// C# fixed in RetroIO ce2fa5a, x25emu in b3b7ab1 (both 2026-11-20).
    /// </para>
    /// <para>
    /// Out of the normal suite: it needs the C tree built in WSL (<c>X25EMU_DIR</c>) and this machine reachable from
    /// WSL (<c>CROSSENV_HOST_IP</c>). Run with <c>dotnet test --filter TestCategory=CrossEnvLive</c>. The C switch is
    /// stopped by the process id it wrote down, never by name.
    /// </para>
    /// </remarks>
    [TestFixture]
    [Category("CrossEnvLive")]
    public class CrossEnvRestartCauseTests
    {
        private const int CTrunkPort = 41201;
        private const int CManagementPort = 41290;
        private const int CMonitorPort = 41291;

        /// <summary>
        /// Runs a bash script in WSL and returns what it printed.
        /// </summary>
        /// <param name="script">
        /// The script, given to <c>bash -c</c>.
        /// </param>
        /// <param name="timeoutMs">
        /// How long to wait for WSL to exit after its output has ended, in milliseconds.
        /// </param>
        /// <returns>
        /// Standard output, then standard error.
        /// </returns>
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

        /// <summary>
        /// Starts an x25emu switch with one HDLC-over-TCP trunk port and returns the folder it runs in.
        /// </summary>
        /// <remarks>
        /// The folder is under WSL's /tmp, named after this process, and holds the config, the log and the process id
        /// file that <see cref="StopCSwitch"/> kills by. The switch is started detached (setsid nohup) so it outlives
        /// the wsl call that started it.
        /// </remarks>
        /// <param name="portLines">
        /// The lines of the trunk port that differ between the tests: the mode, and the port or host and port.
        /// </param>
        /// <returns>
        /// The WSL path of the run folder.
        /// </returns>
        private static string StartCSwitch(string portLines)
        {
            string emuDir = CrossEnvCClientToCSharpSwitchTests.EmuDir();
            string dir = "/tmp/x25-crossenv-restart-" + Environment.ProcessId;
            string conf = string.Join("\n",
                "[switch]", "dnic = 2340", "max_hops = 15", "mgmt_port = " + CManagementPort, "monitor_port = " + CMonitorPort,
                "name = CrossEnvRestart", "[port.0]", "type = trunk", "transport = hdlc-tcp", portLines,
                "lci_base = 1", "lci_count = 16", "lapb_t1 = 500", string.Empty);
            string start = "set -e; mkdir -p " + dir + "; cat > " + dir + "/switch.conf <<'CONF'\n" + conf + "CONF\n" +
                           "cd " + emuDir + "; setsid nohup tools/x25switch -c " + dir + "/switch.conf > " + dir + "/switch.log 2>&1 < /dev/null & echo $! > " + dir + "/pid; sleep 3; cat " + dir + "/pid";
            string pidText = RunInWsl(start, 20000).Trim();
            Assert.That(int.TryParse(pidText, out int pid) && pid > 0, Is.True, "the x25emu switch should have started: " + pidText);
            return dir;
        }

        /// <summary>
        /// Stops the x25emu switch by the process id it wrote down (never by name) and deletes its run folder.
        /// </summary>
        /// <param name="dir">
        /// The run folder <see cref="StartCSwitch"/> returned.
        /// </param>
        private static void StopCSwitch(string dir)
        {
            RunInWsl("kill $(cat " + dir + "/pid) 2>/dev/null; sleep 1; rm -rf " + dir + "; echo stopped", 10000);
        }

        /// <summary>
        /// The restart packets (Restart Indication or Request, type 0xFB) the proxy saw in one direction, in order, as
        /// "cause diagnostic" hex pairs.
        /// </summary>
        /// <param name="proxy">
        /// The proxy between the two switches.
        /// </param>
        /// <param name="direction">
        /// Which way: toward the DTE end shows the DCE's restarts, toward the DCE end the DTE's.
        /// </param>
        /// <returns>
        /// One "cause diagnostic" string, for example "07 00", for each restart packet, in the order seen.
        /// </returns>
        private static List<string> Restarts(HdlcFaultProxy proxy, TrunkDirection direction)
        {
            List<string> found = new List<string>();
            List<ProxiedFrame> seen = proxy.Seen();
            for (int i = 0; i < seen.Count; i++)
            {
                byte[] b = seen[i].Body;
                // LAPB address, control, then the packet: GFI+LCG, LCN, type, cause, diagnostic (then 2 FCS octets).
                // 9 octets at least; type 0xFB is Restart Request (DTE) and Restart Indication (DCE), X.25 Table 5-1.
                if (seen[i].Direction == direction && seen[i].IsInformation && b.Length >= 9 && b[4] == 0xFB)
                {
                    found.Add(b[5].ToString("X2") + " " + b[6].ToString("X2"));
                }
            }

            return found;
        }

        /// <summary>
        /// Every frame the proxy saw, one line each, for the failure messages.
        /// </summary>
        /// <param name="proxy">
        /// The proxy between the two switches.
        /// </param>
        /// <returns>
        /// The direction and the frame's octets in hex (address, control, packet, FCS), a line per frame.
        /// </returns>
        private static string Dump(HdlcFaultProxy proxy)
        {
            StringBuilder text = new StringBuilder();
            List<ProxiedFrame> seen = proxy.Seen();
            for (int i = 0; i < seen.Count; i++)
            {
                text.Append(seen[i].Direction).Append(' ').Append(BitConverter.ToString(seen[i].Body)).Append('\n');
            }

            return text.ToString();
        }

        [Test]
        public void X25emuIsTheDce_ItRestartsWithNetworkOperational_AtLinkUpAndAfterALinkReset()
        {
            string wslIp = RunInWsl("hostname -I | cut -d' ' -f1", 10000).Trim();
            string dir = StartCSwitch("mode = dce\nport = " + CTrunkPort);
            string log = string.Empty;
            try
            {
                // C# DTE -> proxy (here) -> x25emu DCE (WSL). Windows' 127.0.0.1 does not reach a WSL listener, so the
                // proxy dials WSL's own address.
                using HdlcFaultProxy proxy = new HdlcFaultProxy(wslIp, CTrunkPort, IPAddress.Loopback);
                using HdlcTcpSwitchPort dte = new HdlcTcpSwitchPort(TrunkPair.Config(1, PortMode.Dte, "127.0.0.1", proxy.Port));
                Assert.That(dte.Start(), Is.True);

                Assert.That(SpinWait.SpinUntil(() => Restarts(proxy, TrunkDirection.ToDte).Count >= 1, 15000), Is.True,
                    "x25emu (DCE) restarts when the link comes up\n" + Dump(proxy));
                Assert.That(Restarts(proxy, TrunkDirection.ToDte)[0], Is.EqualTo("07 00"), "link up: Network operational\n" + Dump(proxy));
                Assert.That(SpinWait.SpinUntil(() => Restarts(proxy, TrunkDirection.ToDce).Count >= 1, 5000), Is.True, "the C# DTE restarts too");
                Assert.That(Restarts(proxy, TrunkDirection.ToDce)[0], Is.EqualTo("00 00"), "a DTE's cause is 0 (Table 5-9 note a)");
                Assert.That(SpinWait.SpinUntil(() => dte.PeerConnected, 5000), Is.True);

                // A SABM from the DTE side on the up link: x25emu's data link is reset.
                int before = Restarts(proxy, TrunkDirection.ToDte).Count;
                proxy.Inject(TrunkDirection.ToDce, new byte[] { 0x01, 0x3F });
                Assert.That(SpinWait.SpinUntil(() => Restarts(proxy, TrunkDirection.ToDte).Count > before, 10000), Is.True,
                    "x25emu restarts after the link reset\n" + Dump(proxy));
                Assert.That(Restarts(proxy, TrunkDirection.ToDte)[before], Is.EqualTo("07 00"), "after a link reset: Network operational\n" + Dump(proxy));
            }
            finally
            {
                log = RunInWsl("cat " + dir + "/switch.log", 10000);
                StopCSwitch(dir);
                TestContext.Out.WriteLine(log);
            }
        }

        [Test]
        public void TheCSharpSwitchIsTheDce_ItRestartsWithNetworkOperational_AndX25emuCompletesTheRestart()
        {
            string hostIp = CrossEnvCClientToCSharpSwitchTests.HostAddressForWsl();
            if (string.IsNullOrWhiteSpace(hostIp))
            {
                Assert.Ignore("No WSL interface found. Set CROSSENV_HOST_IP to the address WSL should use for this machine.");
            }

            // C# DCE (listening here) <- proxy (listening on every interface, so WSL reaches it) <- x25emu DTE (WSL).
            using HdlcTcpSwitchPort dce = new HdlcTcpSwitchPort(TrunkPair.Config(0, PortMode.Dce, "127.0.0.1", 0));
            Assert.That(dce.Start(), Is.True);
            using HdlcFaultProxy proxy = new HdlcFaultProxy("127.0.0.1", dce.BoundPort, IPAddress.Any);
            string dir = StartCSwitch("mode = dte\nhost = " + hostIp + "\nport = " + proxy.Port);
            string log = string.Empty;
            try
            {
                Assert.That(SpinWait.SpinUntil(() => Restarts(proxy, TrunkDirection.ToDte).Count >= 1, 15000), Is.True,
                    "the C# DCE restarts when the link comes up\n" + Dump(proxy));
                Assert.That(Restarts(proxy, TrunkDirection.ToDte)[0], Is.EqualTo("07 00"), "link up: Network operational\n" + Dump(proxy));
                Assert.That(SpinWait.SpinUntil(() => Restarts(proxy, TrunkDirection.ToDce).Count >= 1, 5000), Is.True, "x25emu (DTE) restarts too\n" + Dump(proxy));
                Assert.That(Restarts(proxy, TrunkDirection.ToDce)[0], Is.EqualTo("00 00"), "a DTE's cause is 0 (Table 5-9 note a)\n" + Dump(proxy));
                Assert.That(SpinWait.SpinUntil(() => dce.PeerConnected, 5000), Is.True);

                // A SABM from the DTE side on the up link: the C# DCE's data link is reset.
                int before = Restarts(proxy, TrunkDirection.ToDte).Count;
                proxy.Inject(TrunkDirection.ToDce, new byte[] { 0x01, 0x3F });
                Assert.That(SpinWait.SpinUntil(() => Restarts(proxy, TrunkDirection.ToDte).Count > before, 10000), Is.True,
                    "the C# DCE restarts after the link reset\n" + Dump(proxy));
                Assert.That(Restarts(proxy, TrunkDirection.ToDte)[before], Is.EqualTo("07 00"), "after a link reset: Network operational\n" + Dump(proxy));

                // x25emu completes the restart: with a DTE Restart Confirmation (`10 00 FF`), or with its own Restart
                // Request crossing ours, a restart collision, after which "the DCE will not expect a DTE restart
                // confirmation packet" (X.25 3.3.3). Measured 2026-11-20: the reset ends in FRMR and a new link set-up
                // (LAPB 2.4.7), and then both ends restart at once - a collision every time.
                int dteRestartsBefore = 1;
                Assert.That(SpinWait.SpinUntil(() => Confirmations(proxy, TrunkDirection.ToDce) >= 1
                    || Restarts(proxy, TrunkDirection.ToDce).Count > dteRestartsBefore, 5000), Is.True,
                    "x25emu completes the restart (a confirmation, or its own restart: a collision)\n" + Dump(proxy));
                List<string> fromX25emu = Restarts(proxy, TrunkDirection.ToDce);
                for (int i = 0; i < fromX25emu.Count; i++)
                {
                    Assert.That(fromX25emu[i], Is.EqualTo("00 00"), "every restart x25emu sends as the DTE has cause 0\n" + Dump(proxy));
                }
            }
            finally
            {
                log = RunInWsl("cat " + dir + "/switch.log", 10000);
                StopCSwitch(dir);
                TestContext.Out.WriteLine(log);
            }
        }

        /// <summary>
        /// The number of Restart Confirmation packets (type 0xFF) the proxy saw in one direction.
        /// </summary>
        /// <param name="proxy">
        /// The proxy between the two switches.
        /// </param>
        /// <param name="direction">
        /// Which way: toward the DCE end counts the DTE's confirmations.
        /// </param>
        /// <returns>
        /// How many were seen.
        /// </returns>
        private static int Confirmations(HdlcFaultProxy proxy, TrunkDirection direction)
        {
            int count = 0;
            List<ProxiedFrame> seen = proxy.Seen();
            for (int i = 0; i < seen.Count; i++)
            {
                byte[] b = seen[i].Body;
                if (seen[i].Direction == direction && seen[i].IsInformation && b.Length >= 7 && b[4] == 0xFF)
                {
                    count++;
                }
            }

            return count;
        }
    }
}
