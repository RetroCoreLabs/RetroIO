//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using RetroIO.Protocols.X25.Common;
using RetroIO.Protocols.X25.PacketLayer;
using RetroIO.Protocols.X25.Routing;

namespace RetroIO.Protocols.X25.Switch
{
    /// <summary>
    /// Builds a <see cref="SwitchConfig"/> from an <see cref="IniConfig"/> per the switch configuration
    /// spec: the indexed section names, field defaults, and edge-case behaviours.
    /// </summary>
    public static class SwitchConfigLoader
    {
        // Iteration bounds per config section type.
        private const int MaxPorts = 256;
        private const int MaxRoutes = 512;
        private const int MaxHuntGroups = 32;
        private const int MaxHuntMembers = 16;
        private const int MaxStitches = 4096;
        private const int MaxAccounts = 64;
        private const int MaxUsers = 16;
        private const int MaxIniSettings = 16384;

        /// <summary>
        /// Loads a switch configuration from a file.
        /// </summary>
        /// <param name="path">
        /// Path to the INI configuration file.
        /// </param>
        /// <returns>
        /// The parsed switch configuration.
        /// </returns>
        public static SwitchConfig LoadFile(string path)
        {
            // The reader keeps at most MaxIniSettings settings; Load refuses a file that has more rather than lose them.
            SwitchConfig sw = Load(IniConfig.Load(path, MaxIniSettings));

            // Where "write memory" may write, as in x25emu: config_dir when the file names one, otherwise the folder this
            // file came from, which is where a person running the switch expects the saved file to land.
            if (sw.ConfigDir.Length == 0)
            {
                string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
                sw.ConfigDir = string.IsNullOrEmpty(dir) ? "." : dir;
            }

            return sw;
        }

        /// <summary>
        /// Builds a switch configuration from an already-parsed INI.
        /// </summary>
        /// <param name="ini">
        /// The parsed configuration source.
        /// </param>
        /// <returns>
        /// The switch configuration.
        /// </returns>
        public static SwitchConfig Load(IniConfig ini)
        {
            if (ini == null)
            {
                throw new ArgumentNullException(nameof(ini));
            }

            SwitchConfig sw = new SwitchConfig();

            // A file longer than the reader keeps would lose its last lines without a word: refused instead.
            if (ini.Dropped > 0)
            {
                throw new InvalidDataException("the file has more than " + ini.MaxKeys + " settings; line "
                    + ini.FirstDroppedLine + " and " + (ini.Dropped - 1) + " more were not read");
            }

            // [switch] identity. DNIC defaults to 2342 (kept from the SwitchConfig default) when absent. When the
            // file names the own network ([network] own_dnic, or this key), the DNIC is the first own DNIC; see
            // LoadNetwork.
            string? dnic = ini.Get("switch", "dnic");
            if (dnic != null)
            {
                sw.Dnic = dnic;
            }

            sw.MaxHops = ini.GetInt("switch", "max_hops", 15);

            // The most calls in progress on the whole switch; 0 or absent is no limit (SwitchConfig.MaxCalls).
            sw.MaxCalls = ini.GetInt("switch", "max_calls", 0);
            if (sw.MaxCalls < 0)
            {
                throw new InvalidDataException("[switch] max_calls = " + sw.MaxCalls + " (line " + ini.GetLine("switch", "max_calls")
                    + "): must be 0 (no limit) or a positive number of calls");
            }
            sw.MgmtPort = (ushort)ini.GetInt("switch", "mgmt_port", 9000);
            sw.MgmtOpen = ini.GetInt("switch", "mgmt_open", 0) != 0;
            sw.MonitorPort = (ushort)ini.GetInt("switch", "monitor_port", 0);
            sw.PvcRecordMinutes = ini.GetInt("switch", "pvc_record_minutes", 15);
            if (sw.PvcRecordMinutes < 0)
            {
                throw new InvalidDataException("[switch] pvc_record_minutes = " + sw.PvcRecordMinutes + ": 0 (off) or more");
            }
            sw.SegmentSize = ini.GetInt("switch", "segment_size", Model.SegmentCounter.SegmentOctets);
            if (sw.SegmentSize < 1 || sw.SegmentSize > Model.SegmentCounter.MaxSegmentOctets)
            {
                throw new InvalidDataException("[switch] segment_size = " + sw.SegmentSize + ": 1 to "
                    + Model.SegmentCounter.MaxSegmentOctets);
            }
            sw.Name = ini.GetString("switch", "name", string.Empty);
            sw.Location = ini.GetString("switch", "location", string.Empty);
            sw.Coordinates = ini.GetString("switch", "coordinates", string.Empty);
            sw.Contact = ini.GetString("switch", "contact", string.Empty);
            sw.MgmtAddr = ini.GetString("switch", "mgmt_addr", string.Empty);
            sw.CdrFile = ini.GetString("switch", "cdr_file", string.Empty);
            sw.TariffFile = ini.GetString("switch", "tariff_file", string.Empty);
            sw.ConfigDir = ini.GetString("switch", "config_dir", string.Empty);

            LoadPorts(ini, sw);
            LoadNetwork(ini, sw);
            LoadRoutes(ini, sw);
            LoadHuntGroups(ini, sw);
            CheckHuntRoutes(ini, sw);
            LoadPvcs(ini, sw);
            LoadAccounts(ini, sw);
            LoadUsers(ini, sw);
            CheckDefaultNuis(sw);

            return sw;
        }

        private static void LoadPorts(IniConfig ini, SwitchConfig sw)
        {
            for (int i = 0; i < MaxPorts; i++)
            {
                string section = "port." + i;
                string? type = ini.Get(section, "type");
                if (type == null)
                {
                    continue;
                }

                PortConfig pc = new PortConfig
                {
                    Id = sw.Ports.Count,
                    Type = type == "trunk" ? PortType.Trunk : PortType.Access
                };

                string? transport = ini.Get(section, "transport");
                if (transport == "hdlc-tcp")
                {
                    pc.Transport = PortTransport.HdlcTcp;
                }
                else if (transport == "hdlc-ppp")
                {
                    pc.Transport = PortTransport.HdlcPpp;
                }
                else if (transport == "hdlc-serial")
                {
                    pc.Transport = PortTransport.HdlcSerial;
                }
                else if (transport == null || transport == "xot")
                {
                    // No transport key means XOT, the default.
                    pc.Transport = PortTransport.Xot;
                }
                else
                {
                    // A transport that is written but not one we know is refused. It used to fall back to XOT,
                    // which turned a typo into a port that silently ran the wrong protocol.
                    throw new InvalidDataException("[" + section + "] transport = " + transport
                        + " is not a transport this switch knows (xot, hdlc-tcp, hdlc-ppp, hdlc-serial)");
                }

                pc.SerialDevice = ini.GetString(section, "device", string.Empty);
                pc.SerialBaud = (uint)ini.GetInt(section, "baud", 9600);
                pc.SerialDataBits = ini.GetInt(section, "databits", 8);
                pc.SerialParity = ini.GetString(section, "parity", "none");
                pc.SerialStopBits = ini.GetInt(section, "stopbits", 1);
                pc.SerialFlow = ini.GetString(section, "flow", "rtscts");
                pc.LapbT1Ms = ini.GetInt(section, "lapb_t1", 0);
                pc.LapbT2Ms = ini.GetInt(section, "lapb_t2", 0);
                pc.LapbT3Ms = ini.GetInt(section, "lapb_t3", 0);
                pc.LapbN2 = ini.GetInt(section, "lapb_n2", 0);
                pc.LapbK = ini.GetInt(section, "lapb_k", 0);
                pc.Mode = ini.Get(section, "mode") == "dte" ? PortMode.Dte : PortMode.Dce;

                // trunk_kind = network | internal (x25emu's key): network makes the trunk an X.75 link between networks.
                // Any other word is refused, and so is network on an access port - a subscriber's DTE does not speak X.75.
                string trunkKind = ini.GetString(section, "trunk_kind", "internal");
                if (trunkKind == "network")
                {
                    if (pc.Type != PortType.Trunk)
                    {
                        throw new InvalidDataException("[" + section + "] trunk_kind = network needs type = trunk: a link "
                            + "between networks (X.75) is a trunk, never a subscriber's access port");
                    }

                    pc.BetweenNetworks = true;
                }
                else if (trunkKind != "internal")
                {
                    throw new InvalidDataException("[" + section + "] trunk_kind = " + trunkKind + ": use network or internal");
                }

                // Which end plugs in the emulated cable (hdlc-tcp only). Not written = follow the X.25 role.
                string tcpRole = ini.GetString(section, "tcp", string.Empty);
                if (tcpRole.Length == 0)
                {
                    pc.TcpRole = PortTcpRole.FromMode;
                }
                else if (tcpRole == "listen")
                {
                    pc.TcpRole = PortTcpRole.Listen;
                }
                else if (tcpRole == "dial")
                {
                    pc.TcpRole = PortTcpRole.Dial;
                }
                else
                {
                    // Refused like an unknown transport: a typo must not quietly pick a direction.
                    throw new InvalidDataException("[" + section + "] tcp = " + tcpRole
                        + " is not one of: listen, dial");
                }
                pc.Host = ini.GetString(section, "host", string.Empty);
                pc.Port = (ushort)ini.GetInt(section, "port", 1998);
                pc.DefaultPacketSize = (ushort)ini.GetInt(section, "packet_size", 128);
                pc.DefaultWindowSize = (byte)ini.GetInt(section, "window_size", 2);
                pc.MaxPacketSize = (ushort)ini.GetInt(section, "max_packet_size", 1024);
                pc.MaxWindowSize = (byte)ini.GetInt(section, "max_window_size", 7);
                // The virtual call channels. With no lci_count the port gets every channel from lci_base up to 4095
                // (X.25 3.1: group <= 15, channel <= 255, channel 0 kept for restart; decided 2026-11-18, the
                // default was 16), trunks the same. A configured range that leaves 1..4095 is refused here, by name,
                // instead of being cut short or failing later inside the packet layer.
                int lciBase = ini.GetInt(section, "lci_base", 1);
                if (lciBase < 1 || lciBase > X25Channels.Highest)
                {
                    throw new InvalidDataException("[" + section + "] lci_base = " + lciBase + " (line " + ini.GetLine(section, "lci_base")
                        + "): logical channels run 1..4095 (X.25 3.1)");
                }

                pc.LciBase = (ushort)lciBase;
                if (ini.Get(section, "lci_count") != null)
                {
                    int lciCount = ini.GetInt(section, "lci_count", 0);
                    int lciLast = lciBase + lciCount - 1;
                    if (lciCount < 1 || lciLast > X25Channels.Highest)
                    {
                        throw new InvalidDataException("[" + section + "] lci_base = " + lciBase + ", lci_count = " + lciCount
                            + " (line " + ini.GetLine(section, "lci_count") + "): the channels must lie in 1..4095 (X.25 3.1), so lci_count must be 1.."
                            + (X25Channels.Highest + 1 - lciBase) + " with this lci_base; leave lci_count out to get every channel up to 4095");
                    }

                    pc.LciCount = (ushort)lciCount;
                }

                // PVC channels (X.25 Annex A): kept apart from the virtual call range, the same keys as x25emu.
                pc.PvcBase = (ushort)ini.GetInt(section, "pvc_base", 1);
                pc.PvcCount = (ushort)ini.GetInt(section, "pvc_count", 0);
                pc.InterfaceName = ini.GetString(section, "iface_name", string.Empty);

                // The port's name, which a route may use instead of its number ("port = x75-gw").
                pc.Name = ini.GetString(section, "name", string.Empty);
                if (pc.Name.Length > 0)
                {
                    if (X121RouteRules.IsDigits(pc.Name) || string.Equals(pc.Name, X121RoutingTable.LocalPortName, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("[" + section + "] name = " + pc.Name + " (line " + ini.GetLine(section, "name")
                            + "): a port name may not be a number or 'local'");
                    }

                    for (int other = 0; other < sw.Ports.Count; other++)
                    {
                        if (string.Equals(sw.Ports[other].Name, pc.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException("[" + section + "] name = " + pc.Name + " (line " + ini.GetLine(section, "name")
                                + "): port " + other + " already has that name");
                        }
                    }

                    sw.Routing.SetPortName(pc.Id, pc.Name);
                }

                // How the called number is written in a call sent out of this port. X.121 7.8.1 and 7.12.6-7.12.7:
                // toward another network the full international number, without prefix, so a trunk defaults to
                // that. What a local terminal wants is not in the spec; an access port keeps the number as received.
                string form = ini.GetString(section, "called_address", string.Empty).Trim().ToLowerInvariant();
                switch (form)
                {
                    case "":
                        pc.CalledAddressForm = pc.Type == PortType.Trunk ? X121AddressForm.International : X121AddressForm.AsReceived;
                        break;
                    case "as_received":
                        pc.CalledAddressForm = X121AddressForm.AsReceived;
                        break;
                    case "international":
                        pc.CalledAddressForm = X121AddressForm.International;
                        break;
                    case "national":
                        pc.CalledAddressForm = X121AddressForm.National;
                        break;
                    default:
                        throw new InvalidDataException("[" + section + "] called_address = " + form + " (line "
                            + ini.GetLine(section, "called_address") + ") is not one of: as_received, international, national");
                }

                // RFC 1613 6.5 names exactly two roles; any other word is refused rather than guessed.
                string role = ini.GetString(section, "xot_pvc_role", string.Empty).Trim().ToLowerInvariant();
                if (role == "initiator")
                {
                    pc.XotPvcInitiator = true;
                }
                else if (role.Length > 0 && role != "responder")
                {
                    throw new InvalidDataException("[" + section + "] xot_pvc_role = " + role
                        + ": must be initiator or responder (RFC 1613 6.5)");
                }

                if (pc.PvcCount > 0)
                {
                    int pvcLast = pc.PvcBase + pc.PvcCount - 1;
                    int callFirst = pc.LciBase;
                    int callLast = pc.LciBase + pc.LciCount - 1;
                    if (pc.PvcBase == 0 || pvcLast > 4095)
                    {
                        throw new InvalidDataException("[" + section + "] pvc_base = " + pc.PvcBase + ", pvc_count = " + pc.PvcCount
                            + ": PVC channels must lie in 1..4095");
                    }

                    if (pc.PvcBase <= callLast && pvcLast >= callFirst)
                    {
                        throw new InvalidDataException("[" + section + "] PVC channels " + pc.PvcBase + ".." + pvcLast
                            + " overlap the virtual call channels " + callFirst + ".." + callLast
                            + " (X.25 Annex A keeps them apart; raise lci_base)");
                    }
                }

                pc.AddrRangeStart = ini.GetString(section, "addr_start", string.Empty);
                pc.AddrRangeEnd = ini.GetString(section, "addr_end", string.Empty);
                pc.NuiRequired = ReadYesNo(ini, section, "nui_required");
                pc.ChargingInfo = ReadYesNo(ini, section, "charging_info");
                pc.DefaultNui = ini.GetString(section, "default_nui", string.Empty);

                // The calling address the network gives each call on this port (PortConfig.CallingAddressBase). Refused
                // here rather than sent wrong: only digits, at most 15 of them (X.25 5.2.1), and room for the highest
                // channel without one more digit.
                string callingBase = ini.GetString(section, "calling_address_base", string.Empty).Trim();
                if (callingBase.Length > 0)
                {
                    bool digits = callingBase.Length <= 15;
                    for (int d = 0; d < callingBase.Length && digits; d++)
                    {
                        digits = callingBase[d] >= '0' && callingBase[d] <= '9';
                    }

                    if (!digits)
                    {
                        throw new InvalidDataException("[" + section + "] calling_address_base = " + callingBase
                            + ": must be 1 to 15 digits (X.25 5.2.1)");
                    }

                    pc.CallingAddressBase = callingBase;
                    int highest = pc.LciBase + pc.LciCount - 1;
                    if (pc.CallingAddressFor(highest).Length > callingBase.Length)
                    {
                        throw new InvalidDataException("[" + section + "] calling_address_base = " + callingBase
                            + ": adding the highest channel (" + highest + ") needs one more digit");
                    }
                }

                // Neighbour declaration (management/monitor plane only) for the network map. peer_monitor_host
                // defaults to the port's host so a trunk usually only needs peer_name + peer_monitor_port.
                pc.PeerName = ini.GetString(section, "peer_name", string.Empty);
                pc.PeerMonitorPort = ini.GetInt(section, "peer_monitor_port", 0);
                pc.PeerMonitorHost = ini.GetString(section, "peer_monitor_host", string.Empty);
                if (pc.PeerName.Length > 0 && pc.PeerMonitorHost.Length == 0)
                {
                    pc.PeerMonitorHost = pc.Host;
                }
                // Neighbour's in-band management address, for GET_TOPOLOGY seed-and-walk.
                pc.PeerMgmtAddr = ini.GetString(section, "peer_mgmt_addr", string.Empty);

                sw.Ports.Add(pc);
            }
        }

        // [network]: the own network. X.121 7.6 and A.2: a network has one DNIC (or several), optionally with
        // the digit after it; 7.8: the international prefix. Without [network], [switch] dnic is the own DNIC
        // when it is written; a file that writes neither leaves the own network unset, so no number is completed.
        private static void LoadNetwork(IniConfig ini, SwitchConfig sw)
        {
            string? own = ini.Get("network", "own_dnic");
            string ownKeySection = "network";
            if (own == null && ini.Get("switch", "dnic") != null)
            {
                own = ini.Get("switch", "dnic");
                ownKeySection = "switch";
            }

            if (own != null)
            {
                string key = ownKeySection == "network" ? "own_dnic" : "dnic";
                try
                {
                    sw.Routing.SetOwnDnics(own.Split(','));
                }
                catch (ArgumentException ex)
                {
                    throw new InvalidDataException("[" + ownKeySection + "] " + key + " = " + own + " (line "
                        + ini.GetLine(ownKeySection, key) + "): " + ex.Message, ex);
                }

                if (sw.Routing.OwnDnics.Count > 0)
                {
                    // The switch's DNIC (used for its own address and the network map) is the first own DNIC.
                    sw.Dnic = sw.Routing.OwnDnics[0].Substring(0, X121RoutingTable.DnicLength);
                }
            }

            string? prefix = ini.Get("network", "international_prefix");
            if (prefix != null)
            {
                try
                {
                    sw.Routing.InternationalPrefix = prefix;
                }
                catch (ArgumentException ex)
                {
                    throw new InvalidDataException("[network] international_prefix = " + prefix + " (line "
                        + ini.GetLine("network", "international_prefix") + "): " + ex.Message, ex);
                }
            }
        }

        // The keys that say what a route matches, without a type key: exactly one per route.
        private static readonly string[] MatchKeys = { "number", "exact", "prefix", "dnic", "dcc", "default", "regex", "hunt" };

        /// <summary>
        /// The [route.N] sections. Two ways to write one, and both load into the same table:
        ///  - the old way: <c>type = exact|prefix|dnic|default|hunt</c> (now also <c>dcc</c> and <c>regex</c>)
        ///    with <c>address</c>, <c>prefix_len</c>, <c>dnic</c>, <c>hunt_group</c>, <c>port</c> (a number) and
        ///    <c>backup_port</c>;
        ///  - the easy way: one key that says what to match (<c>number = 2422110006</c>, <c>prefix = 242201</c>,
        ///    <c>dnic = 2402</c>, <c>dcc = 238</c>, <c>default = yes</c>, <c>regex = ^9.*</c>,
        ///    <c>hunt = 555</c> with <c>hunt_group</c>), and <c>port</c> by name or number.
        /// Both may add <c>name</c>, <c>note</c>, <c>enabled</c>, <c>direction</c> and <c>backup_ports</c> (a
        /// list, tried in its order when the port is out of order: X.110 3.5 and Appendix I.1, "direct route,
        /// alternative 1, alternative 2"). A route the loader does not understand stops the load and names the
        /// section and line; it used to be skipped, as in the C, which turned a typo into a missing route.
        /// </summary>
        private static void LoadRoutes(IniConfig ini, SwitchConfig sw)
        {
            for (int i = 0; i < MaxRoutes; i++)
            {
                string section = "route." + i;
                if (!ini.HasSection(section))
                {
                    continue;
                }

                sw.Routing.AddRoute(ReadRoute(ini, sw, section));
            }
        }

        private static X121Route ReadRoute(IniConfig ini, SwitchConfig sw, string section)
        {
            X121Route route = new X121Route
            {
                Name = ini.GetString(section, "name", string.Empty),
                Note = ini.GetString(section, "note", string.Empty),
            };

            string? type = ini.Get(section, "type");
            bool oldForm = type != null;
            if (oldForm)
            {
                if (!X121TableBuilder.TryParseKind(type!, out X121RouteType kind))
                {
                    throw Bad(ini, section, "type", "type = " + type + " is not a route type this switch knows ("
                        + string.Join(", ", X121TableBuilder.KindWords) + ")");
                }

                route.Type = kind;
                switch (kind)
                {
                    case X121RouteType.Exact:
                    case X121RouteType.Hunt:
                        route.Address = ini.GetString(section, "address", string.Empty);
                        break;
                    case X121RouteType.Prefix:
                        route.Address = ini.GetString(section, "address", string.Empty);
                        route.PrefixLen = ini.GetInt(section, "prefix_len", 0);
                        break;
                    case X121RouteType.Dnic:
                        route.Dnic = ini.GetString(section, "dnic", string.Empty);
                        break;
                    case X121RouteType.Dcc:
                        route.Dcc = ini.GetString(section, "dcc", ini.GetString(section, "address", string.Empty));
                        break;
                    case X121RouteType.Regex:
                        route.Pattern = ini.GetString(section, "regex", ini.GetString(section, "address", string.Empty));
                        break;
                }
            }
            else
            {
                string found = string.Empty;
                for (int k = 0; k < MatchKeys.Length; k++)
                {
                    if (ini.Get(section, MatchKeys[k]) == null)
                    {
                        continue;
                    }

                    if (found.Length > 0)
                    {
                        throw Bad(ini, section, MatchKeys[k], "says both " + found + " and " + MatchKeys[k]
                            + "; a route matches one thing");
                    }

                    found = MatchKeys[k];
                }

                if (found.Length == 0)
                {
                    throw new InvalidDataException("[" + section + "] (line " + ini.GetSectionLine(section)
                        + ") says nothing to match: write one of " + string.Join(", ", MatchKeys) + ", or type =");
                }

                string value = ini.GetString(section, found, string.Empty);
                switch (found)
                {
                    case "number":
                    case "exact":
                        route.Type = X121RouteType.Exact;
                        route.Address = value;
                        break;
                    case "prefix":
                        route.Type = X121RouteType.Prefix;
                        route.Address = value;
                        route.PrefixLen = value.Length;
                        break;
                    case "dnic":
                        route.Type = X121RouteType.Dnic;
                        route.Dnic = value;
                        break;
                    case "dcc":
                        route.Type = X121RouteType.Dcc;
                        route.Dcc = value;
                        break;
                    case "regex":
                        route.Type = X121RouteType.Regex;
                        route.Pattern = value;
                        break;
                    case "hunt":
                        route.Type = X121RouteType.Hunt;
                        route.Address = value;
                        break;
                    default:
                        if (!ReadYesNo(ini, section, "default"))
                        {
                            throw Bad(ini, section, "default", "default = " + value + ": write default = yes, or leave the key out");
                        }

                        route.Type = X121RouteType.Default;
                        break;
                }
            }

            route.Enabled = ini.Get(section, "enabled") == null || ReadYesNo(ini, section, "enabled");
            if (!X121TableBuilder.TryParseDirection(ini.Get(section, "direction"), out X121RouteDirection direction))
            {
                throw Bad(ini, section, "direction", "direction = " + ini.Get(section, "direction") + " is not one of: both, incoming, outgoing");
            }

            route.Direction = direction;

            if (route.Type == X121RouteType.Hunt)
            {
                route.HuntGroupId = ini.GetInt(section, "hunt_group", -1);
                if (route.HuntGroupId < 0)
                {
                    throw Bad(ini, section, oldForm ? "type" : "hunt", "a hunt route needs hunt_group = <the [hunt.N] number>");
                }
            }
            else
            {
                string? port = ini.Get(section, "port");

                // The old form took a missing port as port 0; kept, so an old file loads as it did.
                route.DestPort = port == null && oldForm ? 0 : FindPort(ini, sw, section, "port", port);
            }

            string? oldBackup = ini.Get(section, "backup_port");
            if (oldBackup != null && IniConfig.Atoi(oldBackup) >= 0)
            {
                route.BackupPorts.Add(FindPort(ini, sw, section, "backup_port", oldBackup));
            }

            string? backups = ini.Get(section, "backup_ports");
            if (backups != null)
            {
                string[] parts = backups.Split(',');
                for (int b = 0; b < parts.Length; b++)
                {
                    if (parts[b].Trim().Length > 0)
                    {
                        route.BackupPorts.Add(FindPort(ini, sw, section, "backup_ports", parts[b]));
                    }
                }
            }

            if (!X121RouteRules.Check(route, out string error))
            {
                string key = oldForm ? "type" : FirstPresent(ini, section);
                throw Bad(ini, section, key, error);
            }

            return route;
        }

        private static string FirstPresent(IniConfig ini, string section)
        {
            for (int k = 0; k < MatchKeys.Length; k++)
            {
                if (ini.Get(section, MatchKeys[k]) != null)
                {
                    return MatchKeys[k];
                }
            }

            return "type";
        }

        // A port named in a route: by its name ([port.N] name) or its number, and it must be a port of this
        // switch. "local" (the runtime's own services) has no meaning in the switch: its own services answer at
        // [switch] mgmt_addr.
        private static int FindPort(IniConfig ini, SwitchConfig sw, string section, string key, string? value)
        {
            string v = (value ?? string.Empty).Trim();
            if (!sw.Routing.TryFindPort(v, out int port) || port == X121RoutingTable.LocalPort || port < 0 || port >= sw.Ports.Count)
            {
                throw Bad(ini, section, key, key + " = " + v + " is not a port of this switch (a [port.N] number or name)");
            }

            return port;
        }

        private static InvalidDataException Bad(IniConfig ini, string section, string key, string message)
        {
            int line = ini.GetLine(section, key);
            if (line == 0)
            {
                line = ini.GetSectionLine(section);
            }

            return new InvalidDataException("[" + section + "] line " + line + ": " + message);
        }

        // A hunt route's hunt_group must name a [hunt.N] that was read.
        private static void CheckHuntRoutes(IniConfig ini, SwitchConfig sw)
        {
            for (int i = 0; i < sw.Routing.Routes.Count; i++)
            {
                X121Route r = sw.Routing.Routes[i];
                if (r.Type == X121RouteType.Hunt && r.HuntGroupId >= sw.Routing.HuntGroups.Count)
                {
                    throw new InvalidDataException("route " + (r.Name.Length > 0 ? "'" + r.Name + "'" : r.Address)
                        + ": hunt_group = " + r.HuntGroupId + " is not a [hunt.N] of this file");
                }
            }
        }
        private static void LoadHuntGroups(IniConfig ini, SwitchConfig sw)
        {
            for (int i = 0; i < MaxHuntGroups; i++)
            {
                string section = "hunt." + i;
                string? members = ini.Get(section, "members");
                if (members == null)
                {
                    continue;
                }

                X121HuntGroup hg = new X121HuntGroup { Id = sw.Routing.HuntGroups.Count };

                string[] parts = members.Split(',');
                for (int m = 0; m < parts.Length && hg.Members.Count < MaxHuntMembers; m++)
                {
                    hg.Members.Add(IniConfig.Atoi(parts[m]));
                }

                sw.Routing.AddHuntGroup(hg);
            }
        }

        private static void LoadPvcs(IniConfig ini, SwitchConfig sw)
        {
            for (int i = 0; i < MaxStitches; i++)
            {
                string section = "pvc." + i;
                string? src = ini.Get(section, "src_port");
                if (src == null)
                {
                    continue;
                }

                X121Route route = new X121Route
                {
                    Type = X121RouteType.Pvc,
                    SourcePort = IniConfig.Atoi(src),
                    SourceLci = (ushort)ini.GetInt(section, "src_lci", 1),
                    DestPort = ini.GetInt(section, "dst_port", 0),
                    DestLci = (ushort)ini.GetInt(section, "dst_lci", 1)
                };

                // Refused rather than ignored (x25emu logs and skips; a PVC that silently is not there is the failure
                // nobody notices). Each end must be a configured port, inside that port's PVC channels.
                string remoteHost = ini.GetString(section, "remote_host", string.Empty);
                string remoteIface = ini.GetString(section, "remote_iface", string.Empty);
                ushort remoteLci = (ushort)ini.GetInt(section, "remote_lci", 0);
                CheckPvcEnd(sw, section, "src", route.SourcePort, route.SourceLci, remoteHost, remoteIface, remoteLci);
                CheckPvcEnd(sw, section, "dst", route.DestPort, route.DestLci, remoteHost, remoteIface, remoteLci);
                if (sw.Ports[route.SourcePort].Transport == PortTransport.Xot && sw.Ports[route.DestPort].Transport == PortTransport.Xot)
                {
                    // One set of remote_* settings describes one far XOT; a PVC with an XOT at each end would need two.
                    throw new InvalidDataException("[" + section + "] has an XOT port at both ends; only one end may be XOT");
                }
                if (route.SourcePort == route.DestPort && route.SourceLci == route.DestLci)
                {
                    throw new InvalidDataException("[" + section + "] joins a channel to itself");
                }

                for (int p = 0; p < sw.Pvcs.Count; p++)
                {
                    PvcConfig other = sw.Pvcs[p];
                    if (UsesEnd(other, route.SourcePort, route.SourceLci) || UsesEnd(other, route.DestPort, route.DestLci))
                    {
                        throw new InvalidDataException("[" + section + "] uses a channel another [pvc.N] already joins");
                    }
                }

                sw.Routing.AddRoute(route);
                sw.Pvcs.Add(new PvcConfig
                {
                    SourcePort = route.SourcePort,
                    SourceLci = route.SourceLci,
                    DestPort = route.DestPort,
                    DestLci = route.DestLci,
                    Account = ini.GetString(section, "account", string.Empty),
                    RemoteInterface = ini.GetString(section, "remote_iface", string.Empty),
                    RemoteLci = (ushort)ini.GetInt(section, "remote_lci", 0),
                    RemoteHost = ini.GetString(section, "remote_host", string.Empty),
                });
            }
        }

        private static bool UsesEnd(PvcConfig pvc, int port, ushort lci)
        {
            return (pvc.SourcePort == port && pvc.SourceLci == lci) || (pvc.DestPort == port && pvc.DestLci == lci);
        }

        private static void CheckPvcEnd(SwitchConfig sw, string section, string end, int port, ushort lci,
            string remoteHost, string remoteIface, ushort remoteLci)
        {
            if (port < 0 || port >= sw.Ports.Count)
            {
                throw new InvalidDataException("[" + section + "] " + end + "_port = " + port + " is not a configured port");
            }

            PortConfig pc = sw.Ports[port];
            if (pc.Transport == PortTransport.Xot && pc.XotPvcInitiator
                && (remoteHost.Length == 0 || remoteIface.Length == 0 || remoteLci == 0))
            {
                // RFC 1613 6.5: "Each XOT PVC is configured with the identity of the other XOT (i.e., IP address), the
                // name of the interface to connect to, the Logical Channel Number on that interface". The initiator sends
                // all three; without them it has nothing to ask for.
                throw new InvalidDataException("[" + section + "] " + end + "_port = " + port
                    + " is an XOT initiator port; the PVC needs remote_host, remote_iface and remote_lci (RFC 1613 6.5)");
            }

            if (pc.PvcCount == 0 || lci < pc.PvcBase || lci > pc.PvcBase + pc.PvcCount - 1)
            {
                throw new InvalidDataException("[" + section + "] " + end + "_lci = " + lci + " is not one of port " + port
                    + "'s PVC channels (pvc_base " + pc.PvcBase + ", pvc_count " + pc.PvcCount + ")");
            }
        }

        private static void LoadAccounts(IniConfig ini, SwitchConfig sw)
        {
            for (int i = 0; i < MaxAccounts; i++)
            {
                string section = "account." + i;
                string? name = ini.Get(section, "name");
                if (name == null)
                {
                    continue;
                }

                sw.Accounts.Add(new AccountConfig
                {
                    Name = name,
                    AddrStart = ini.GetString(section, "addr_start", string.Empty),
                    AddrEnd = ini.GetString(section, "addr_end", string.Empty),
                    Nui = ini.GetString(section, "nui", string.Empty)
                });
            }
        }

        // A port's default NUI must name an account: refused here, when the file is read, rather than found as a
        // cleared call later.
        private static void CheckDefaultNuis(SwitchConfig sw)
        {
            for (int p = 0; p < sw.Ports.Count; p++)
            {
                string nui = sw.Ports[p].DefaultNui;
                if (nui.Length == 0)
                {
                    continue;
                }

                bool found = false;
                for (int a = 0; a < sw.Accounts.Count && !found; a++)
                {
                    found = string.Equals(sw.Accounts[a].Nui, nui, StringComparison.Ordinal);
                }

                if (!found)
                {
                    throw new InvalidDataException("[port." + sw.Ports[p].Id + "] default_nui = " + nui
                        + " is the NUI of no [account.N]");
                }
            }
        }

        // yes/no for a switch setting; anything else is refused like an unknown transport, so a typo does not quietly
        // turn a check off.
        private static bool ReadYesNo(IniConfig ini, string section, string key)
        {
            string value = ini.GetString(section, key, string.Empty);
            switch (value)
            {
                case "": return false;
                case "yes": case "true": case "1": return true;
                case "no": case "false": case "0": return false;
                default:
                    throw new InvalidDataException("[" + section + "] " + key + " = " + value + " is not yes or no");
            }
        }

        private static void LoadUsers(IniConfig ini, SwitchConfig sw)
        {
            for (int i = 0; i < MaxUsers; i++)
            {
                string section = "user." + i;
                string? name = ini.Get(section, "name");
                if (name == null)
                {
                    continue;
                }

                sw.Users.Add(new UserConfig
                {
                    Name = name,
                    Password = ini.GetString(section, "password", string.Empty),
                    Level = ini.GetInt(section, "level", 0)
                });
            }
        }
    }
}
