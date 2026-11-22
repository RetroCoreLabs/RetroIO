//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Network.Pcap
{
    /// <summary>
    /// One host capture adapter as the choice sees it: what pcap calls it, and what the
    /// operating system's own adapter list says about the same adapter.
    /// </summary>
    public sealed class PcapAdapter
    {
        // ── Constructors ──

        /// <summary>
        /// Creates the description of one adapter.
        /// </summary>
        /// <param name="pcapName">
        /// The pcap device name (on Windows <c>\Device\NPF_{GUID}</c>).
        /// </param>
        /// <param name="description">
        /// The pcap description (the driver's text), or empty.
        /// </param>
        /// <param name="connectionName">
        /// The operating system's name for the adapter (on Windows the connection name, such
        /// as "ND-Loopback" or "Ethernet"), or <c>null</c> when the system list has no entry for it.
        /// </param>
        /// <param name="ipv4">
        /// The IPv4 addresses bound to the adapter.
        /// </param>
        public PcapAdapter(string pcapName, string? description, string? connectionName, string[] ipv4)
        {
            PcapName = pcapName ?? string.Empty;
            Description = description ?? string.Empty;
            ConnectionName = connectionName;
            Ipv4 = ipv4 ?? Array.Empty<string>();
        }

        // ── Properties ──

        /// <summary>
        /// Gets the pcap device name.
        /// </summary>
        public string PcapName { get; }

        /// <summary>
        /// Gets the pcap description, or empty.
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// Gets the operating system's name for the adapter, or <c>null</c>.
        /// </summary>
        public string? ConnectionName { get; }

        /// <summary>
        /// Gets the IPv4 addresses bound to the adapter.
        /// </summary>
        public string[] Ipv4 { get; }
    }

    /// <summary>
    /// Picks the host adapter pcap binds to, and says why. Pure: it works on a list of
    /// <see cref="PcapAdapter"/>, so it is tested with made-up lists; the real list is built by
    /// <see cref="PcapEthernetBackend"/> from pcap's list and the system's adapter list.
    /// </summary>
    /// <remarks>
    /// <para>With no selector: the adapter whose connection name is
    /// <see cref="DefaultConnectionName"/> when there is one (a Microsoft KM-TEST Loopback
    /// Adapter set up as a private link between this computer and the guests, so a guest never
    /// touches the main network card and this computer can reach it), else the first adapter,
    /// as before.</para>
    /// <para>With a selector, in this order: the connection name (exact, any case); the pcap
    /// name; the pcap description (part of it); an exact IPv4 address; an IPv4 prefix written
    /// <c>select:192.168.1</c> (the form the old ND-100 adapter menu took; it matches whole
    /// numbers only, so <c>192.168.1</c> does not match 192.168.199.1); and finally a whole
    /// dropdown label that contains the adapter's name, description or address.</para>
    /// </remarks>
    public static class PcapAdapterChoice
    {
        // ── Constants ──

        /// <summary>
        /// The connection name pcap uses when no adapter is named and it exists.
        /// </summary>
        public const string DefaultConnectionName = "ND-Loopback";

        /// <summary>
        /// The selector prefix that chooses by IPv4 prefix.
        /// </summary>
        public const string PrefixSelector = "select:";

        // ── Public methods ──

        /// <summary>
        /// Chooses an adapter.
        /// </summary>
        /// <param name="adapters">
        /// The adapters, in pcap's order.
        /// </param>
        /// <param name="selector">
        /// What the user named, or <c>null</c>/empty for the default.
        /// </param>
        /// <param name="reason">
        /// Receives, in plain words, which adapter was taken and why (or why none).
        /// </param>
        /// <returns>
        /// The index of the chosen adapter, or -1 when none fits.
        /// </returns>
        public static int Choose(IReadOnlyList<PcapAdapter> adapters, string? selector, out string reason)
        {
            if (adapters == null || adapters.Count == 0)
            {
                reason = "no capture adapter is available";
                return -1;
            }

            if (string.IsNullOrWhiteSpace(selector))
            {
                for (int i = 0; i < adapters.Count; i++)
                {
                    if (string.Equals(adapters[i].ConnectionName, DefaultConnectionName, StringComparison.OrdinalIgnoreCase))
                    {
                        reason = "'" + Label(adapters[i]) + "': no adapter was named, and " + DefaultConnectionName + " is the default when it exists";
                        return i;
                    }
                }
                reason = "'" + Label(adapters[0]) + "': no adapter was named and there is no " + DefaultConnectionName + " adapter, so the first in the list";
                return 0;
            }

            string s = selector.Trim();

            for (int i = 0; i < adapters.Count; i++)
            {
                if (adapters[i].ConnectionName != null && string.Equals(adapters[i].ConnectionName, s, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "'" + Label(adapters[i]) + "': its connection name is '" + s + "'";
                    return i;
                }
            }

            if (s.StartsWith(PrefixSelector, StringComparison.OrdinalIgnoreCase))
            {
                string prefix = s.Substring(PrefixSelector.Length).Trim();
                for (int i = 0; i < adapters.Count; i++)
                {
                    string[] ips = adapters[i].Ipv4;
                    for (int k = 0; k < ips.Length; k++)
                    {
                        if (HasPrefix(ips[k], prefix))
                        {
                            reason = "'" + Label(adapters[i]) + "': its address " + ips[k] + " starts with " + prefix;
                            return i;
                        }
                    }
                }
                reason = "no adapter has an IPv4 address starting with " + prefix;
                return -1;
            }

            for (int i = 0; i < adapters.Count; i++)
            {
                if (string.Equals(adapters[i].PcapName, s, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "'" + Label(adapters[i]) + "': its pcap name is '" + s + "'";
                    return i;
                }
            }
            for (int i = 0; i < adapters.Count; i++)
            {
                string d = adapters[i].Description;
                if (d.Length != 0 && d.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    reason = "'" + Label(adapters[i]) + "': its description contains '" + s + "'";
                    return i;
                }
            }
            for (int i = 0; i < adapters.Count; i++)
            {
                string[] ips = adapters[i].Ipv4;
                for (int k = 0; k < ips.Length; k++)
                {
                    if (string.Equals(ips[k], s, StringComparison.Ordinal))
                    {
                        reason = "'" + Label(adapters[i]) + "': it has the address " + s;
                        return i;
                    }
                }
            }
            // A whole dropdown label (see PcapEthernetBackend.ListDeviceLabels): it contains the
            // adapter's pcap name, description or one of its addresses.
            for (int i = 0; i < adapters.Count; i++)
            {
                PcapAdapter a = adapters[i];
                bool inLabel = (a.PcapName.Length != 0 && s.IndexOf(a.PcapName, StringComparison.OrdinalIgnoreCase) >= 0)
                    || (a.Description.Length != 0 && s.IndexOf(a.Description, StringComparison.OrdinalIgnoreCase) >= 0);
                for (int k = 0; !inLabel && k < a.Ipv4.Length; k++)
                {
                    inLabel = s.IndexOf(a.Ipv4[k], StringComparison.Ordinal) >= 0;
                }
                if (inLabel)
                {
                    reason = "'" + Label(a) + "': the name given is its label";
                    return i;
                }
            }

            reason = "no adapter matches '" + s + "' by connection name, pcap name, description or address";
            return -1;
        }

        /// <summary>
        /// Finds the operating system's name for a pcap adapter in the system's adapter list.
        /// On Windows the pcap name is <c>\Device\NPF_{GUID}</c> and the system list holds the
        /// same GUID as the adapter's id (with its braces); elsewhere the pcap name is the
        /// system's interface name itself.
        /// </summary>
        /// <param name="pcapName">
        /// The pcap device name.
        /// </param>
        /// <param name="systemIds">
        /// The ids of the system's adapters.
        /// </param>
        /// <param name="systemNames">
        /// The names of the system's adapters, in the same order.
        /// </param>
        /// <returns>
        /// The system's name for the adapter, or <c>null</c> when it is not in the list.
        /// </returns>
        public static string? ConnectionNameFor(string pcapName, IReadOnlyList<string> systemIds, IReadOnlyList<string> systemNames)
        {
            if (string.IsNullOrEmpty(pcapName))
            {
                return null;
            }
            int open = pcapName.IndexOf('{');
            int close = pcapName.LastIndexOf('}');
            string? guid = open >= 0 && close > open ? pcapName.Substring(open, close - open + 1) : null;
            for (int i = 0; i < systemIds.Count && i < systemNames.Count; i++)
            {
                if (guid != null && string.Equals(systemIds[i], guid, StringComparison.OrdinalIgnoreCase))
                {
                    return systemNames[i];
                }
                if (guid == null && string.Equals(systemNames[i], pcapName, StringComparison.Ordinal))
                {
                    return systemNames[i];
                }
            }
            return null;
        }

        // ── Private methods ──

        // The adapter as a person knows it: the connection name and the description when both
        // are there.
        private static string Label(PcapAdapter a)
        {
            if (a.ConnectionName != null && a.Description.Length != 0)
            {
                return a.ConnectionName + ", " + a.Description;
            }
            return a.ConnectionName ?? (a.Description.Length != 0 ? a.Description : a.PcapName);
        }

        // True when ip starts with prefix at a whole-number boundary: "192.168.1" matches
        // 192.168.1.5 but not 192.168.199.1; a prefix ending in a dot matches as written.
        private static bool HasPrefix(string ip, string prefix)
        {
            if (prefix.Length == 0 || !ip.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }
            return prefix[prefix.Length - 1] == '.' || ip.Length == prefix.Length || ip[prefix.Length] == '.';
        }
    }
}
