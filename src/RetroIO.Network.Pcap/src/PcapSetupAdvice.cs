//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Network.Pcap
{
    /// <summary>
    /// The host operating systems that need different pcap setup steps.
    /// </summary>
    public enum PcapHostSystem
    {
        /// <summary>
        /// Windows: Npcap.
        /// </summary>
        Windows,

        /// <summary>
        /// Linux: libpcap plus the CAP_NET_RAW capability or root.
        /// </summary>
        Linux,

        /// <summary>
        /// macOS: read access to /dev/bpf*.
        /// </summary>
        MacOS,

        /// <summary>
        /// FreeBSD: access to /dev/bpf through a devfs rule.
        /// </summary>
        FreeBsd,

        /// <summary>
        /// Any other system.
        /// </summary>
        Other,
    }

    /// <summary>
    /// The text that tells a user what pcap needs on their system, and what pcap can never do.
    /// Used in the error when an adapter cannot be listed or opened, and in the help.
    /// </summary>
    /// <remarks>
    /// Sources (host networking design, sections 4 and 5): capture on Linux needs root or
    /// CAP_NET_RAW, on macOS and the BSDs read access to /dev/bpf (pcap(3PCAP), verified);
    /// Wi-Fi access points drop frames sent under a MAC that is not the client's own; a frame
    /// injected with pcap never reaches the host's own network stack (Npcap issue 544,
    /// verified), and the Microsoft KM-TEST loopback adapter is the measured way round that on
    /// Windows (ND-100 notes, 2026-09-27).
    /// </remarks>
    public static class PcapSetupAdvice
    {
        // ── Constants ──

        /// <summary>
        /// What pcap cannot do on any system; appended to every system's text.
        /// </summary>
        public const string Limits =
            "pcap needs a WIRED adapter: Wi-Fi drops frames sent with the guest's own MAC address. "
            + "The computer that runs the emulator cannot reach the guest through the same adapter, "
            + "because pcap puts the guest's frames on the wire and never hands them to this computer's own network stack; "
            + "use a second adapter, or on Windows the Microsoft KM-TEST Loopback Adapter as a private link between this computer and the guest.";

        /// <summary>
        /// Which adapter pcap takes when none is named, and how to name another; appended to
        /// every system's text (see <see cref="PcapAdapterChoice"/>).
        /// </summary>
        public const string AdapterChoice =
            "With no adapter named, pcap takes the adapter whose connection name is " + PcapAdapterChoice.DefaultConnectionName
            + " when there is one (on Windows a KM-TEST Loopback Adapter given that name, for example with the address 192.168.199.1), "
            + "else the first adapter in the list; net.status says which was taken and why. "
            + "Name another by its connection name (net.pcap \"Ethernet\"), its number in net.devices, part of its description, "
            + "its address, or an address prefix (net.pcap select:192.168.1).";

        // ── Public methods ──

        /// <summary>
        /// Gets the system the emulator runs on.
        /// </summary>
        /// <returns>
        /// The host system.
        /// </returns>
        public static PcapHostSystem CurrentSystem()
        {
            if (OperatingSystem.IsWindows())
            {
                return PcapHostSystem.Windows;
            }
            if (OperatingSystem.IsLinux())
            {
                return PcapHostSystem.Linux;
            }
            if (OperatingSystem.IsMacOS())
            {
                return PcapHostSystem.MacOS;
            }
            if (OperatingSystem.IsFreeBSD())
            {
                return PcapHostSystem.FreeBsd;
            }
            return PcapHostSystem.Other;
        }

        /// <summary>
        /// Gets the setup steps pcap needs on one system, followed by <see cref="Limits"/>.
        /// </summary>
        /// <param name="system">
        /// The host system.
        /// </param>
        /// <returns>
        /// The advice, as one paragraph.
        /// </returns>
        public static string For(PcapHostSystem system)
        {
            string steps;
            switch (system)
            {
                case PcapHostSystem.Windows:
                    steps = "On Windows pcap needs Npcap (https://npcap.com), installed once by an administrator. "
                        + "If Npcap was installed with access restricted to administrators, run RetroCore as administrator.";
                    break;
                case PcapHostSystem.Linux:
                    steps = "On Linux pcap needs libpcap from the package manager, and root or the CAP_NET_RAW capability: "
                        + "an administrator runs once 'sudo setcap cap_net_raw+ep <the RetroCore program>' on the published RetroCore program "
                        + "(never on the shared dotnet program; replacing the file removes the capability), or RetroCore runs as root.";
                    break;
                case PcapHostSystem.MacOS:
                    steps = "On macOS pcap needs read access to /dev/bpf*: install ChmodBPF once (it comes with Wireshark, or as a Homebrew cask) and restart, or run RetroCore as root.";
                    break;
                case PcapHostSystem.FreeBsd:
                    steps = "On FreeBSD pcap needs access to /dev/bpf: an administrator adds a devfs rule that gives a group access to bpf* "
                        + "(in /etc/devfs.rules, enabled in /etc/rc.conf) and puts the user in that group, or RetroCore runs as root.";
                    break;
                default:
                    steps = "pcap needs the system's packet capture library and permission to capture; on most systems that means root.";
                    break;
            }
            return steps + " " + Limits + " " + AdapterChoice;
        }

        /// <summary>
        /// Gets the setup steps for the system the emulator runs on.
        /// </summary>
        /// <returns>
        /// The advice, as one paragraph.
        /// </returns>
        public static string ForThisSystem()
        {
            return For(CurrentSystem());
        }
    }
}
