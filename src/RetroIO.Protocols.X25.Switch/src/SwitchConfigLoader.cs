//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
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
            return Load(IniConfig.Load(path));
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
            sw.MonitorPort = (ushort)ini.GetInt("switch", "monitor_port", 0);
            sw.Name = ini.GetString("switch", "name", string.Empty);
            sw.Location = ini.GetString("switch", "location", string.Empty);
            sw.Coordinates = ini.GetString("switch", "coordinates", string.Empty);
            sw.Contact = ini.GetString("switch", "contact", string.Empty);
            sw.MgmtAddr = ini.GetString("switch", "mgmt_addr", string.Empty);
            sw.CdrFile = ini.GetString("switch", "cdr_file", string.Empty);

            LoadPorts(ini, sw);
            LoadRoutes(ini, sw);
            LoadHuntGroups(ini, sw);
            LoadPvcs(ini, sw);
            LoadAccounts(ini, sw);
            LoadUsers(ini, sw);
            ApplyBackupPorts(ini, sw);

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
                else
                {
                    // "xot" and anything unrecognised both default to XOT.
                    pc.Transport = PortTransport.Xot;
                }

                pc.SerialDevice = ini.GetString(section, "device", string.Empty);
                pc.SerialBaud = (uint)ini.GetInt(section, "baud", 9600);
                pc.Mode = ini.Get(section, "mode") == "dte" ? PortMode.Dte : PortMode.Dce;
                pc.Host = ini.GetString(section, "host", string.Empty);
                pc.Port = (ushort)ini.GetInt(section, "port", 1998);
                pc.DefaultPacketSize = (ushort)ini.GetInt(section, "packet_size", 128);
                pc.DefaultWindowSize = (byte)ini.GetInt(section, "window_size", 2);
                pc.MaxPacketSize = (ushort)ini.GetInt(section, "max_packet_size", 1024);
                pc.MaxWindowSize = (byte)ini.GetInt(section, "max_window_size", 7);
                pc.LciBase = (ushort)ini.GetInt(section, "lci_base", 1);
                pc.LciCount = (ushort)ini.GetInt(section, "lci_count", 16);
                pc.AddrRangeStart = ini.GetString(section, "addr_start", string.Empty);
                pc.AddrRangeEnd = ini.GetString(section, "addr_end", string.Empty);

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

                sw.Routing.AddRoute(route);
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
                    AddrEnd = ini.GetString(section, "addr_end", string.Empty)
                });
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
