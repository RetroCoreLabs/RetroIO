using System;
using System.Globalization;
using System.Text;
using RetroIO.Protocols.X25.Routing;
using RetroIO.Protocols.X25.Switch.Model;
using RetroIO.Protocols.X25.Switch.Ports;

namespace RetroIO.Protocols.X25.Switch.Management
{
    /// <summary>
    /// The switch management command processor: it parses a single command line and renders the exact
    /// text response of the management CLI, and it applies the mutating commands (add/delete route, clear
    /// a circuit, write configuration).
    /// </summary>
    /// <remarks>
    /// Dispatch is an ordered, case-sensitive prefix match — the first matching rule wins, and trailing
    /// text is ignored unless a rule parses arguments. All CLI responses use CR-LF line endings. This is a
    /// pure text transform over the switch state; the caller supplies the clock and any mutation hooks, so
    /// the exact output is reproducible under test.
    /// </remarks>
    public sealed class SwitchCli
    {
        private const string Eol = "\r\n";

        private readonly SwitchNode _node;
        private readonly Func<long> _nowMs;

        /// <summary>
        /// Optional hook invoked by <c>clear x25 vc</c>. Returns true if a circuit was found and cleared.
        /// </summary>
        public Func<int, ushort, bool>? ClearVcHandler { get; set; }

        /// <summary>
        /// Creates a command processor over a switch node.
        /// </summary>
        /// <param name="node">
        /// The switch state to read and mutate.
        /// </param>
        /// <param name="nowMs">
        /// Millisecond clock used for uptime; defaults to the environment tick count.
        /// </param>
        public SwitchCli(SwitchNode node, Func<long>? nowMs = null)
        {
            _node = node ?? throw new ArgumentNullException(nameof(node));
            _nowMs = nowMs ?? (() => Environment.TickCount64);
        }

        /// <summary>
        /// Processes one command line and returns its response text.
        /// </summary>
        /// <param name="cmd">
        /// The command line, with any trailing CR/LF already stripped.
        /// </param>
        /// <returns>
        /// The response text (CR-LF terminated lines).
        /// </returns>
        public string Process(string cmd)
        {
            if (cmd.StartsWith("show info", StringComparison.Ordinal))
            {
                return ShowInfo();
            }
            if (cmd.StartsWith("show x25 route", StringComparison.Ordinal))
            {
                return ShowRoute();
            }
            if (cmd.StartsWith("show x25 vc", StringComparison.Ordinal))
            {
                return ShowVc();
            }
            if (cmd.StartsWith("show x25 ports", StringComparison.Ordinal) || cmd == "show x25 port")
            {
                return ShowPorts();
            }
            if (cmd.StartsWith("show x25 stats", StringComparison.Ordinal))
            {
                return ShowStats();
            }
            if (cmd.StartsWith("show x25 hunt", StringComparison.Ordinal))
            {
                return ShowHunt();
            }
            if (cmd.StartsWith("show x25 pvc", StringComparison.Ordinal))
            {
                return ShowPvc();
            }
            if (cmd.StartsWith("clear x25 vc ", StringComparison.Ordinal))
            {
                return ClearVc(cmd.Substring(13));
            }
            if (cmd.StartsWith("x25 route add ", StringComparison.Ordinal))
            {
                return RouteAdd(cmd.Substring(14));
            }
            if (cmd.StartsWith("x25 route del ", StringComparison.Ordinal))
            {
                return RouteDel(cmd.Substring(14));
            }
            if (cmd.StartsWith("show x25 port ", StringComparison.Ordinal))
            {
                return ShowPortDetail(cmd.Substring(14));
            }
            if (cmd.StartsWith("show running-config", StringComparison.Ordinal))
            {
                return ShowRunningConfig();
            }
            if (cmd.StartsWith("write memory ", StringComparison.Ordinal))
            {
                return WriteMemory(cmd.Substring(13));
            }
            if (cmd.StartsWith("debug x25 packets", StringComparison.Ordinal))
            {
                return "Debug logging enabled" + Eol;
            }
            if (cmd.StartsWith("debug x25 trace", StringComparison.Ordinal))
            {
                return "Trace logging enabled" + Eol;
            }
            if (cmd.StartsWith("no debug", StringComparison.Ordinal))
            {
                return "Debug logging disabled" + Eol;
            }
            if (cmd.StartsWith("show x25 account", StringComparison.Ordinal))
            {
                return ShowAccount();
            }
            if (cmd.StartsWith("reload", StringComparison.Ordinal))
            {
                return "Reload not supported at runtime (restart process with new config)" + Eol;
            }

            return Help();
        }

        private long UptimeSeconds()
        {
            long ms = _nowMs() - _node.UptimeStartMs;
            return ms > 0 ? ms / 1000 : 0;
        }

        private static string OrNotSet(string s)
        {
            return string.IsNullOrEmpty(s) ? "(not set)" : s;
        }

        private static string TransportLabel(PortTransport t)
        {
            switch (t)
            {
                case PortTransport.Xot: return "xot";
                case PortTransport.HdlcPpp: return "hdlc-ppp";
                case PortTransport.HdlcTcp: return "hdlc-tcp";
                default: return "hdlc-serial";
            }
        }

        private static string RouteTypeLabel(X121RouteType t)
        {
            switch (t)
            {
                case X121RouteType.Exact: return "exact";
                case X121RouteType.Prefix: return "prefix";
                case X121RouteType.Dnic: return "dnic";
                case X121RouteType.Default: return "default";
                case X121RouteType.Hunt: return "hunt";
                case X121RouteType.Pvc: return "pvc";
                default: return "???";
            }
        }

        private string ShowInfo()
        {
            SwitchConfig c = _node.Config;
            StringBuilder sb = new StringBuilder();
            sb.Append("Name:        ").Append(OrNotSet(c.Name)).Append(Eol);
            sb.Append("DNIC:        ").Append(c.Dnic).Append(Eol);
            sb.Append("Location:    ").Append(OrNotSet(c.Location)).Append(Eol);
            sb.Append("Coordinates: ").Append(OrNotSet(c.Coordinates)).Append(Eol);
            sb.Append("Contact:     ").Append(OrNotSet(c.Contact)).Append(Eol);
            sb.Append("Uptime:      ").Append(UptimeSeconds()).Append('s').Append(Eol);
            sb.Append("Ports:       ").Append(c.Ports.Count).Append(Eol);
            sb.Append("Routes:      ").Append(c.Routing.Routes.Count).Append(Eol);
            return sb.ToString();
        }

        private string ShowRoute()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("Type     Address          Port  Prefix").Append(Eol);
            sb.Append("-------- ---------------- ----- ------").Append(Eol);
            for (int i = 0; i < _node.Config.Routing.Routes.Count; i++)
            {
                X121Route r = _node.Config.Routing.Routes[i];
                sb.Append(Pad(RouteTypeLabel(r.Type), -8)).Append(' ')
                  .Append(Pad(r.Address, -16)).Append(' ')
                  .Append(Pad(r.DestPort.ToString(CultureInfo.InvariantCulture), 5)).Append(' ')
                  .Append(r.PrefixLen.ToString(CultureInfo.InvariantCulture)).Append(Eol);
            }
            return sb.ToString();
        }

        private string ShowVc()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("InPort InLCI OutPort OutLCI Calling     Called      PktFwd").Append(Eol);
            sb.Append("------ ----- ------- ------ ----------- ----------- ------").Append(Eol);
            System.Collections.Generic.List<VcStitch> stitches = _node.SnapshotStitches();
            for (int i = 0; i < stitches.Count; i++)
            {
                VcStitch s = stitches[i];
                uint pkts = s.PacketsForward + s.PacketsReverse;
                sb.Append(Pad(s.InPort.Id.ToString(CultureInfo.InvariantCulture), 6)).Append(' ')
                  .Append(Pad(s.InLci.ToString(CultureInfo.InvariantCulture), 5)).Append(' ')
                  .Append(Pad(s.OutPort.Id.ToString(CultureInfo.InvariantCulture), 7)).Append(' ')
                  .Append(Pad(s.OutLci.ToString(CultureInfo.InvariantCulture), 6)).Append(' ')
                  .Append(Pad(s.CallingAddress, -11)).Append(' ')
                  .Append(Pad(s.CalledAddress, -11)).Append(' ')
                  .Append(pkts.ToString(CultureInfo.InvariantCulture)).Append(Eol);
            }
            return sb.ToString();
        }

        private string ShowPorts()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("Port Type   Mode Transport Active CallsIn CallsOut").Append(Eol);
            sb.Append("---- ------ ---- --------- ------ ------- --------").Append(Eol);
            for (int i = 0; i < _node.Ports.Count; i++)
            {
                ISwitchPort p = _node.Ports[i];
                PortConfig pc = p.Config;
                string type = pc.Type == PortType.Access ? "access" : "trunk";
                string mode = pc.Mode == PortMode.Dte ? "DTE" : "DCE";
                string active = p.Active ? "yes" : "no";
                sb.Append(Pad(p.Id.ToString(CultureInfo.InvariantCulture), 4)).Append(' ')
                  .Append(Pad(type, -6)).Append(' ')
                  .Append(Pad(mode, -4)).Append(' ')
                  .Append(Pad(TransportLabel(pc.Transport), -9)).Append(' ')
                  .Append(Pad(active, -6)).Append(' ')
                  .Append(Pad(p.Counters.CallsIn.ToString(CultureInfo.InvariantCulture), 7)).Append(' ')
                  .Append(Pad(p.Counters.CallsOut.ToString(CultureInfo.InvariantCulture), 8)).Append(Eol);
            }
            return sb.ToString();
        }

        private string ShowStats()
        {
            SwitchConfig c = _node.Config;
            long up = UptimeSeconds();
            uint callsIn = 0, callsOut = 0, callsCleared = 0, pktsIn = 0, pktsOut = 0;
            for (int i = 0; i < _node.Ports.Count; i++)
            {
                PortCounters ct = _node.Ports[i].Counters;
                callsIn += ct.CallsIn;
                callsOut += ct.CallsOut;
                callsCleared += ct.CallsCleared;
                pktsIn += ct.PacketsIn;
                pktsOut += ct.PacketsOut;
            }

            ulong bytesFwd = 0, bytesRev = 0;
            System.Collections.Generic.List<VcStitch> stitches = _node.SnapshotStitches();
            for (int i = 0; i < stitches.Count; i++)
            {
                bytesFwd += stitches[i].BytesForward;
                bytesRev += stitches[i].BytesReverse;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("Switch: ").Append(string.IsNullOrEmpty(c.Name) ? "Switch" : c.Name)
              .Append(" (DNIC ").Append(c.Dnic).Append(')').Append(Eol);
            sb.Append("Location: ").Append(string.IsNullOrEmpty(c.Location) ? "-" : c.Location).Append(Eol);
            sb.Append("Uptime: ").Append(up / 3600).Append("h ").Append((up / 60) % 60).Append("m ").Append(up % 60).Append('s').Append(Eol);
            sb.Append(Eol);
            sb.Append("Ports: ").Append(c.Ports.Count).Append(" active, Max hops: ").Append(c.MaxHops).Append(Eol);
            sb.Append("Active VCs: ").Append(_node.ActiveStitchCount).Append(Eol);
            sb.Append(Eol);
            sb.Append("Calls:").Append(Eol);
            sb.Append("  Incoming:  ").Append(callsIn).Append(Eol);
            sb.Append("  Outgoing:  ").Append(callsOut).Append(Eol);
            sb.Append("  Cleared:   ").Append(callsCleared).Append(Eol);
            sb.Append(Eol);
            sb.Append("Packets:").Append(Eol);
            sb.Append("  Received:  ").Append(pktsIn).Append(Eol);
            sb.Append("  Sent:      ").Append(pktsOut).Append(Eol);
            sb.Append(Eol);
            sb.Append("Bytes forwarded: ").Append(bytesFwd).Append(Eol);
            sb.Append("Bytes reversed:  ").Append(bytesRev).Append(Eol);
            sb.Append("Total bytes:     ").Append(bytesFwd + bytesRev).Append(Eol);
            return sb.ToString();
        }

        private string ShowHunt()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("ID  Members              NextIdx").Append(Eol);
            sb.Append("--- -------------------- -------").Append(Eol);
            for (int i = 0; i < _node.Config.Routing.HuntGroups.Count; i++)
            {
                X121HuntGroup hg = _node.Config.Routing.HuntGroups[i];
                sb.Append(Pad(i.ToString(CultureInfo.InvariantCulture), 3)).Append(' ');
                int startLen = sb.Length;
                for (int m = 0; m < hg.Members.Count; m++)
                {
                    if (m > 0)
                    {
                        sb.Append(',');
                    }
                    sb.Append(hg.Members[m].ToString(CultureInfo.InvariantCulture));
                }
                sb.Append(' ').Append(hg.NextIdx.ToString(CultureInfo.InvariantCulture)).Append(Eol);
            }
            return sb.ToString();
        }

        private string ShowPvc()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("SrcPort SrcLCI DstPort DstLCI Active PktFwd PktRev").Append(Eol);
            sb.Append("------- ------ ------- ------ ------ ------ ------").Append(Eol);
            System.Collections.Generic.List<VcStitch> stitches = _node.SnapshotStitches();
            for (int i = 0; i < stitches.Count; i++)
            {
                VcStitch s = stitches[i];
                if (s.HopCount != -1)
                {
                    continue; // PVC circuits use the -1 sentinel
                }
                sb.Append(Pad(s.InPort.Id.ToString(CultureInfo.InvariantCulture), 7)).Append(' ')
                  .Append(Pad(s.InLci.ToString(CultureInfo.InvariantCulture), 6)).Append(' ')
                  .Append(Pad(s.OutPort.Id.ToString(CultureInfo.InvariantCulture), 7)).Append(' ')
                  .Append(Pad(s.OutLci.ToString(CultureInfo.InvariantCulture), 6)).Append(' ')
                  .Append(Pad("yes", -6)).Append(' ')
                  .Append(Pad(s.PacketsForward.ToString(CultureInfo.InvariantCulture), 6)).Append(' ')
                  .Append(Pad(s.PacketsReverse.ToString(CultureInfo.InvariantCulture), 6)).Append(Eol);
            }
            return sb.ToString();
        }

        private string ShowAccount()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("Name             AddrRange            Calls    Pkts       Bytes      Duration").Append(Eol);
            sb.Append("---------------- -------------------- -------- ---------- ---------- --------").Append(Eol);
            if (_node.Accounts.Count == 0)
            {
                sb.Append("(no accounts configured)").Append(Eol);
                return sb.ToString();
            }
            for (int i = 0; i < _node.Accounts.Count; i++)
            {
                SwitchAccount a = _node.Accounts[i];
                string range = a.AddrStart + "-" + a.AddrEnd;
                sb.Append(Pad(a.Name, -16)).Append(' ')
                  .Append(Pad(range, -20)).Append(' ')
                  .Append(Pad(a.CallsMade.ToString(CultureInfo.InvariantCulture), 4)).Append('/').Append(Pad(a.CallsReceived.ToString(CultureInfo.InvariantCulture), -3)).Append(' ')
                  .Append(Pad(a.PacketsSent.ToString(CultureInfo.InvariantCulture), 5)).Append('/').Append(Pad(a.PacketsReceived.ToString(CultureInfo.InvariantCulture), -4)).Append(' ')
                  .Append(Pad(a.BytesSent.ToString(CultureInfo.InvariantCulture), 5)).Append('/').Append(Pad(a.BytesReceived.ToString(CultureInfo.InvariantCulture), -4)).Append(' ')
                  .Append((a.TotalDurationMs / 1000).ToString(CultureInfo.InvariantCulture)).Append('s').Append(Eol);
            }
            return sb.ToString();
        }

        private string ShowPortDetail(string arg)
        {
            int n = ParseLeadingInt(arg);
            ISwitchPort? port = _node.GetPort(n);
            if (port == null || n < 0 || n >= _node.Ports.Count)
            {
                return "Invalid port number" + Eol;
            }

            PortConfig pc = port.Config;
            string type = pc.Type == PortType.Access ? "access" : "trunk";
            string mode = pc.Mode == PortMode.Dte ? "DTE" : "DCE";
            StringBuilder sb = new StringBuilder();
            sb.Append("Port ").Append(n).Append(':').Append(Eol);
            sb.Append("  Type:      ").Append(type).Append(Eol);
            sb.Append("  Mode:      ").Append(mode).Append(Eol);
            sb.Append("  Transport: ").Append(TransportLabel(pc.Transport)).Append(Eol);
            sb.Append("  TCP port:  ").Append(pc.Port).Append(Eol);
            sb.Append("  Host:      ").Append(string.IsNullOrEmpty(pc.Host) ? "(any)" : pc.Host).Append(Eol);
            sb.Append("  Active:    ").Append(port.Active ? "yes" : "no").Append(Eol);
            string addrRange = (string.IsNullOrEmpty(pc.AddrRangeStart) && string.IsNullOrEmpty(pc.AddrRangeEnd))
                ? "(none) - (none)"
                : (OrNone(pc.AddrRangeStart) + " - " + OrNone(pc.AddrRangeEnd));
            sb.Append("  Addr range: ").Append(addrRange).Append(Eol);
            sb.Append(Eol);
            sb.Append("  Packet size: ").Append(pc.DefaultPacketSize).Append(" (max ").Append(pc.MaxPacketSize).Append(')').Append(Eol);
            sb.Append("  Window size: ").Append(pc.DefaultWindowSize).Append(" (max ").Append(pc.MaxWindowSize).Append(')').Append(Eol);
            sb.Append("  LCI base:   ").Append(pc.LciBase).Append("  count: ").Append(pc.LciCount).Append(Eol);
            sb.Append(Eol);
            sb.Append("  Counters:").Append(Eol);
            sb.Append("    Calls in:      ").Append(port.Counters.CallsIn).Append(Eol);
            sb.Append("    Calls out:     ").Append(port.Counters.CallsOut).Append(Eol);
            sb.Append("    Calls cleared: ").Append(port.Counters.CallsCleared).Append(Eol);
            sb.Append("    Packets in:    ").Append(port.Counters.PacketsIn).Append(Eol);
            sb.Append("    Packets out:   ").Append(port.Counters.PacketsOut).Append(Eol);
            sb.Append(Eol).Append("  Active VCs:").Append(Eol);
            bool any = false;
            System.Collections.Generic.List<VcStitch> stitches = _node.SnapshotStitches();
            for (int i = 0; i < stitches.Count; i++)
            {
                VcStitch s = stitches[i];
                if (s.InPort.Id == n)
                {
                    sb.Append("    LCI ").Append(s.InLci).Append(" -> port").Append(s.OutPort.Id).Append("/LCI").Append(s.OutLci)
                      .Append(" (fwd=").Append(s.PacketsForward).Append(" rev=").Append(s.PacketsReverse).Append(')').Append(Eol);
                    any = true;
                }
                else if (s.OutPort.Id == n)
                {
                    sb.Append("    LCI ").Append(s.OutLci).Append(" <- port").Append(s.InPort.Id).Append("/LCI").Append(s.InLci)
                      .Append(" (fwd=").Append(s.PacketsForward).Append(" rev=").Append(s.PacketsReverse).Append(')').Append(Eol);
                    any = true;
                }
            }
            if (!any)
            {
                sb.Append("    (none)").Append(Eol);
            }
            return sb.ToString();
        }

        private string ClearVc(string arg)
        {
            string[] parts = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[0], out int port) || !int.TryParse(parts[1], out int lci))
            {
                return "Usage: clear x25 vc <port> <lci>" + Eol;
            }

            bool cleared = ClearVcHandler != null && ClearVcHandler(port, (ushort)lci);
            return cleared ? "VC cleared" + Eol : "VC not found" + Eol;
        }

        private string RouteAdd(string arg)
        {
            string p = arg;
            if (p.StartsWith("prefix ", StringComparison.Ordinal))
            {
                string[] t = p.Substring(7).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length >= 3 && int.TryParse(t[1], out int len) && int.TryParse(t[2], out int port))
                {
                    _node.Config.Routing.AddRoute(new X121Route { Type = X121RouteType.Prefix, Address = t[0], PrefixLen = len, DestPort = port });
                    return "Route added" + Eol;
                }
            }
            else if (p.StartsWith("exact ", StringComparison.Ordinal))
            {
                string[] t = p.Substring(6).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length >= 2 && int.TryParse(t[1], out int port))
                {
                    _node.Config.Routing.AddRoute(new X121Route { Type = X121RouteType.Exact, Address = t[0], DestPort = port });
                    return "Route added" + Eol;
                }
            }
            else if (p.StartsWith("dnic ", StringComparison.Ordinal))
            {
                string[] t = p.Substring(5).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length >= 2 && int.TryParse(t[1], out int port))
                {
                    string dnic = t[0].Length > 4 ? t[0].Substring(0, 4) : t[0];
                    _node.Config.Routing.AddRoute(new X121Route { Type = X121RouteType.Dnic, Dnic = dnic, DestPort = port });
                    return "Route added" + Eol;
                }
            }
            else if (p.StartsWith("default ", StringComparison.Ordinal))
            {
                if (int.TryParse(p.Substring(8).Trim(), out int port))
                {
                    _node.Config.Routing.AddRoute(new X121Route { Type = X121RouteType.Default, DestPort = port });
                    return "Route added" + Eol;
                }
            }

            return "Usage: x25 route add <prefix|exact|dnic|default> ..." + Eol;
        }

        private string RouteDel(string arg)
        {
            int idx = ParseLeadingInt(arg);
            if (_node.Config.Routing.DeleteRouteAt(idx))
            {
                return "Route " + idx.ToString(CultureInfo.InvariantCulture) + " deleted" + Eol;
            }
            return "Invalid route index" + Eol;
        }

        private string ShowRunningConfig()
        {
            return RenderConfig(Eol);
        }

        private string WriteMemory(string arg)
        {
            string fname = arg.Trim();
            if (fname.Length == 0)
            {
                return "Usage: write memory <filename>" + Eol;
            }

            try
            {
                System.IO.File.WriteAllText(fname, RenderConfig("\n"));
            }
            catch (System.Exception)
            {
                return "Error: cannot open " + fname + Eol;
            }

            return "Config written to " + fname + Eol;
        }

        private string RenderConfig(string eol)
        {
            SwitchConfig c = _node.Config;
            StringBuilder sb = new StringBuilder();
            sb.Append("[switch]").Append(eol);
            sb.Append("dnic = ").Append(c.Dnic).Append(eol);
            sb.Append("max_hops = ").Append(c.MaxHops).Append(eol);
            sb.Append("mgmt_port = ").Append(c.MgmtPort).Append(eol).Append(eol);
            for (int j = 0; j < c.Ports.Count; j++)
            {
                PortConfig pc = c.Ports[j];
                sb.Append("[port.").Append(j).Append(']').Append(eol);
                sb.Append("type = ").Append(pc.Type == PortType.Access ? "access" : "trunk").Append(eol);
                sb.Append("transport = ").Append(TransportLabel(pc.Transport)).Append(eol);
                sb.Append("mode = ").Append(pc.Mode == PortMode.Dte ? "dte" : "dce").Append(eol);
                sb.Append("host = ").Append(pc.Host).Append(eol);
                sb.Append("port = ").Append(pc.Port).Append(eol);
                sb.Append("packet_size = ").Append(pc.DefaultPacketSize).Append(eol);
                sb.Append("window_size = ").Append(pc.DefaultWindowSize).Append(eol);
                sb.Append("max_packet_size = ").Append(pc.MaxPacketSize).Append(eol);
                sb.Append("max_window_size = ").Append(pc.MaxWindowSize).Append(eol);
                sb.Append("lci_base = ").Append(pc.LciBase).Append(eol);
                sb.Append("lci_count = ").Append(pc.LciCount).Append(eol);
                if (pc.AddrRangeStart.Length > 0)
                {
                    sb.Append("addr_start = ").Append(pc.AddrRangeStart).Append(eol);
                }
                if (pc.AddrRangeEnd.Length > 0)
                {
                    sb.Append("addr_end = ").Append(pc.AddrRangeEnd).Append(eol);
                }
                sb.Append(eol);
            }
            for (int j = 0; j < c.Routing.Routes.Count; j++)
            {
                X121Route r = c.Routing.Routes[j];
                sb.Append("[route.").Append(j).Append(']').Append(eol);
                sb.Append("type = ").Append(RouteTypeLabel(r.Type)).Append(eol);
                sb.Append("port = ").Append(r.DestPort).Append(eol);
                if (r.Address.Length > 0)
                {
                    sb.Append("address = ").Append(r.Address).Append(eol);
                }
                if (r.PrefixLen > 0)
                {
                    sb.Append("prefix_len = ").Append(r.PrefixLen).Append(eol);
                }
                if (r.Dnic.Length > 0)
                {
                    sb.Append("dnic = ").Append(r.Dnic).Append(eol);
                }
                if (r.Type == X121RouteType.Hunt)
                {
                    sb.Append("hunt_group = ").Append(r.HuntGroupId).Append(eol);
                }
                sb.Append(eol);
            }
            for (int j = 0; j < c.Routing.HuntGroups.Count; j++)
            {
                X121HuntGroup hg = c.Routing.HuntGroups[j];
                sb.Append("[hunt.").Append(j).Append(']').Append(eol);
                sb.Append("members = ");
                for (int m = 0; m < hg.Members.Count; m++)
                {
                    if (m > 0)
                    {
                        sb.Append(',');
                    }
                    sb.Append(hg.Members[m].ToString(CultureInfo.InvariantCulture));
                }
                sb.Append(eol).Append(eol);
            }
            return sb.ToString();
        }

        private static string Help()
        {
            return
                "Commands:\r\n" +
                "  show info           - Display switch identity and uptime\r\n" +
                "  show x25 route      - Display routing table\r\n" +
                "  show x25 vc         - Display active virtual circuits\r\n" +
                "  show x25 pvc        - Display PVC cross-connects\r\n" +
                "  show x25 port       - Display port summary\r\n" +
                "  show x25 port <N>   - Display detailed port stats\r\n" +
                "  show x25 hunt       - Display hunt groups\r\n" +
                "  show x25 stats      - Display switch statistics\r\n" +
                "  show running-config - Display current config (INI)\r\n" +
                "  write memory <file> - Save running config to file\r\n" +
                "  clear x25 vc P L    - Clear VC on port P, LCI L\r\n" +
                "  x25 route add ...   - Add route (prefix/exact/dnic/default)\r\n" +
                "  x25 route del N     - Delete route index N\r\n" +
                "  debug x25 packets   - Enable packet debug logging\r\n" +
                "  debug x25 trace     - Enable trace logging (hex dumps)\r\n" +
                "  no debug            - Disable debug logging\r\n" +
                "  show x25 account    - Display account statistics\r\n" +
                "  quit                - Disconnect\r\n";
        }

        private static string OrNone(string s)
        {
            return string.IsNullOrEmpty(s) ? "(none)" : s;
        }

        private static int ParseLeadingInt(string s)
        {
            return IniParseInt(s);
        }

        private static int IniParseInt(string s)
        {
            int i = 0;
            int n = s.Length;
            while (i < n && char.IsWhiteSpace(s[i]))
            {
                i++;
            }
            bool neg = false;
            if (i < n && (s[i] == '+' || s[i] == '-'))
            {
                neg = s[i] == '-';
                i++;
            }
            long v = 0;
            bool any = false;
            while (i < n && s[i] >= '0' && s[i] <= '9')
            {
                v = v * 10 + (s[i] - '0');
                any = true;
                i++;
            }
            if (!any)
            {
                return 0;
            }
            return neg ? (int)(-v) : (int)v;
        }

        private static string Pad(string s, int width)
        {
            if (width < 0)
            {
                int w = -width;
                return s.Length >= w ? s : s + new string(' ', w - s.Length);
            }
            return s.Length >= width ? s : new string(' ', width - s.Length) + s;
        }

        /// <summary>
        /// Renders the configuration in the on-disk INI form (LF line endings), used by the write-memory
        /// command.
        /// </summary>
        /// <returns>
        /// The configuration text with LF line endings.
        /// </returns>
        public string RenderConfigForFile()
        {
            return RenderConfig("\n");
        }
    }
}
