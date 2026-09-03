using System;
using System.Text;
using RetroIO.Protocols.X25.Routing;
using RetroIO.Protocols.X25.Switch.Model;
using RetroIO.Protocols.X25.Switch.Ports;

namespace RetroIO.Protocols.X25.Switch.Management
{
    /// <summary>
    /// The machine-readable management API: it answers request keywords with line-oriented, LF-terminated
    /// responses for a network monitor to parse.
    /// </summary>
    /// <remarks>
    /// Request keywords are matched exactly. INFO and STATS use a <c>|</c>-delimited field list (their values
    /// may contain spaces); PORT, ROUTE, VC and ACCOUNT records use space-delimited <c>key=value</c> fields
    /// (their values never contain spaces). Every line ends with LF.
    /// </remarks>
    public sealed class SwitchApiProcessor
    {
        private readonly SwitchNode _node;
        private readonly Func<long> _nowMs;

        /// <summary>
        /// Creates the API processor over a switch node.
        /// </summary>
        /// <param name="node">
        /// The switch state to report.
        /// </param>
        /// <param name="nowMs">
        /// Millisecond clock for uptime; defaults to the environment tick count.
        /// </param>
        public SwitchApiProcessor(SwitchNode node, Func<long>? nowMs = null)
        {
            _node = node ?? throw new ArgumentNullException(nameof(node));
            _nowMs = nowMs ?? (() => Environment.TickCount64);
        }

        /// <summary>
        /// Processes one request keyword and returns its response (empty for an unknown keyword).
        /// </summary>
        /// <param name="request">
        /// The request keyword, with any line terminator already removed.
        /// </param>
        /// <returns>
        /// The response text, or an empty string for an unrecognised request.
        /// </returns>
        public string Process(string request)
        {
            switch (request)
            {
                case "PING": return "PONG\n";
                case "GET_INFO": return Info();
                case "GET_STATS": return Stats();
                case "GET_PORTS": return Ports();
                case "GET_ROUTES": return Routes();
                case "GET_TOPOLOGY": return Topology();
                case "GET_VC": return Vcs();
                case "GET_ACCOUNTS": return Accounts();
                case "SUBSCRIBE": return "OK subscribed\n";
                case "UNSUBSCRIBE": return "OK unsubscribed\n";
                default: return string.Empty;
            }
        }

        private long UptimeSeconds()
        {
            long ms = _nowMs() - _node.UptimeStartMs;
            return ms > 0 ? ms / 1000 : 0;
        }

        private string Info()
        {
            SwitchConfig c = _node.Config;
            return "INFO dnic=" + c.Dnic + "|name=" + c.Name + "|location=" + c.Location + "|coords=" + c.Coordinates + "\n";
        }

        private string Stats()
        {
            SwitchConfig c = _node.Config;
            uint callsIn = 0, pktsIn = 0, pktsOut = 0;
            for (int i = 0; i < _node.Ports.Count; i++)
            {
                PortCounters ct = _node.Ports[i].Counters;
                callsIn += ct.CallsIn;
                pktsIn += ct.PacketsIn;
                pktsOut += ct.PacketsOut;
            }

            ulong bytes = 0;
            System.Collections.Generic.List<VcStitch> stitches = _node.SnapshotStitches();
            for (int i = 0; i < stitches.Count; i++)
            {
                bytes += stitches[i].BytesForward + stitches[i].BytesReverse;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("STATS dnic=").Append(c.Dnic)
              .Append("|ports=").Append(_node.Ports.Count)
              .Append("|stitches=").Append(_node.ActiveStitchCount)
              .Append("|calls=").Append(callsIn)
              .Append("|pkts_in=").Append(pktsIn)
              .Append("|pkts_out=").Append(pktsOut)
              .Append("|bytes=").Append(bytes)
              .Append("|up=").Append(UptimeSeconds())
              .Append('\n');
            return sb.ToString();
        }

        private string Ports()
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < _node.Ports.Count; i++)
            {
                ISwitchPort p = _node.Ports[i];
                string type = p.Config.Type == PortType.Access ? "access" : "trunk";
                string mode = p.Config.Mode == PortMode.Dte ? "dte" : "dce";
                string active = p.Active ? "yes" : "no";
                sb.Append("PORT id=").Append(p.Id)
                  .Append(" type=").Append(type)
                  .Append(" mode=").Append(mode)
                  .Append(" active=").Append(active)
                  .Append(" calls_in=").Append(p.Counters.CallsIn)
                  .Append(" calls_out=").Append(p.Counters.CallsOut)
                  .Append('\n');
            }
            return sb.ToString();
        }

        private string Routes()
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < _node.Config.Routing.Routes.Count; i++)
            {
                X121Route r = _node.Config.Routing.Routes[i];
                sb.Append("ROUTE type=").Append(RouteTypeLabel(r.Type))
                  .Append(" addr=").Append(r.Address)
                  .Append(" port=").Append(r.DestPort)
                  .Append(" prefix=").Append(r.PrefixLen)
                  .Append('\n');
            }
            return sb.ToString();
        }

        private string Topology()
        {
            // One NEIGHBOUR line per trunk that declares a peer. mgmt is the neighbour's management
            // address for seed-and-walk discovery; up reflects the live trunk link. Byte-identical to
            // the C switch's GET_TOPOLOGY (see docs/NMC_API_V1.md section 4.6).
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < _node.Ports.Count; i++)
            {
                ISwitchPort p = _node.Ports[i];
                if (p.Config.PeerName.Length == 0)
                {
                    continue;
                }

                sb.Append("NEIGHBOUR name=").Append(p.Config.PeerName)
                  .Append(" mgmt=").Append(p.Config.PeerMgmtAddr)
                  .Append(" via=").Append(p.Id)
                  .Append(" up=").Append(p.PeerConnected ? 1 : 0)
                  .Append('\n');
            }
            return sb.ToString();
        }

        private string Vcs()
        {
            StringBuilder sb = new StringBuilder();
            System.Collections.Generic.List<VcStitch> stitches = _node.SnapshotStitches();
            for (int i = 0; i < stitches.Count; i++)
            {
                VcStitch s = stitches[i];
                sb.Append("VC in_port=").Append(s.InPort.Id)
                  .Append(" in_lci=").Append(s.InLci)
                  .Append(" out_port=").Append(s.OutPort.Id)
                  .Append(" out_lci=").Append(s.OutLci)
                  .Append(" calling=").Append(s.CallingAddress)
                  .Append(" called=").Append(s.CalledAddress)
                  .Append(" pkts_fwd=").Append(s.PacketsForward)
                  .Append(" pkts_rev=").Append(s.PacketsReverse)
                  .Append('\n');
            }
            return sb.ToString();
        }

        private string Accounts()
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < _node.Accounts.Count; i++)
            {
                SwitchAccount a = _node.Accounts[i];
                sb.Append("ACCOUNT name=").Append(a.Name)
                  .Append(" addr_start=").Append(a.AddrStart)
                  .Append(" addr_end=").Append(a.AddrEnd)
                  .Append(" calls_made=").Append(a.CallsMade)
                  .Append(" calls_recv=").Append(a.CallsReceived)
                  .Append('\n');
            }
            return sb.ToString();
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
    }
}
