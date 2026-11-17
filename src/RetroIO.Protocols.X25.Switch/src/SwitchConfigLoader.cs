//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using RetroIO.Protocols.X25.Common;
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
            SwitchConfig sw = Load(IniConfig.Load(path));

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

            // [switch] identity. DNIC defaults to 2342 (kept from the SwitchConfig default) when absent.
            string? dnic = ini.Get("switch", "dnic");
            if (dnic != null)
            {
                sw.Dnic = dnic;
            }

            sw.MaxHops = ini.GetInt("switch", "max_hops", 15);
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
            LoadRoutes(ini, sw);
            LoadHuntGroups(ini, sw);
            LoadPvcs(ini, sw);
            LoadAccounts(ini, sw);
            LoadUsers(ini, sw);
            ApplyBackupPorts(ini, sw);
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
                pc.LciBase = (ushort)ini.GetInt(section, "lci_base", 1);
                pc.LciCount = (ushort)ini.GetInt(section, "lci_count", 16);

                // PVC channels (X.25 Annex A): kept apart from the virtual call range, the same keys as x25emu.
                pc.PvcBase = (ushort)ini.GetInt(section, "pvc_base", 1);
                pc.PvcCount = (ushort)ini.GetInt(section, "pvc_count", 0);
                pc.InterfaceName = ini.GetString(section, "iface_name", string.Empty);

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

        private static void LoadRoutes(IniConfig ini, SwitchConfig sw)
        {
            for (int i = 0; i < MaxRoutes; i++)
            {
                string section = "route." + i;
                string? type = ini.Get(section, "type");
                if (type == null)
                {
                    continue;
                }

                X121RouteType routeType;
                switch (type)
                {
                    case "exact": routeType = X121RouteType.Exact; break;
                    case "prefix": routeType = X121RouteType.Prefix; break;
                    case "dnic": routeType = X121RouteType.Dnic; break;
                    case "default": routeType = X121RouteType.Default; break;
                    case "hunt": routeType = X121RouteType.Hunt; break;
                    default: continue; // unrecognised type is skipped (as in the C)
                }

                X121Route route = new X121Route
                {
                    Type = routeType,
                    Address = ini.GetString(section, "address", string.Empty),
                    DestPort = ini.GetInt(section, "port", 0),
                    PrefixLen = ini.GetInt(section, "prefix_len", 0),
                    Dnic = ini.GetString(section, "dnic", string.Empty),
                    HuntGroupId = ini.GetInt(section, "hunt_group", -1)
                };

                sw.Routing.AddRoute(route);
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

        private static void ApplyBackupPorts(IniConfig ini, SwitchConfig sw)
        {
            // Backup ports are applied in a second pass indexed by the ROUTE ARRAY position, reading section
            // "route.<arrayindex>" -- so a backup only lands on a route whose array index matches an existing
            // route.<n> section (an edge case for non-contiguous configs).
            for (int i = 0; i < sw.Routing.Routes.Count; i++)
            {
                sw.Routing.Routes[i].BackupPort = ini.GetInt("route." + i, "backup_port", -1);
            }
        }
    }
}
