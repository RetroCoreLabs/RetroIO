//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Text;
using System.Threading;

namespace RetroIO.Network.Pcap
{
    /// <summary>
    /// What <see cref="GuestMacFilter.Classify"/> decided about one captured frame.
    /// </summary>
    internal enum CapturedFrameVerdict
    {
        /// <summary>
        /// The frame is for the guest (its own MAC, broadcast or multicast): deliver it.
        /// </summary>
        Deliver,

        /// <summary>
        /// The guest sent this frame itself; pcap captured our own transmit. Drop it.
        /// </summary>
        SelfEcho,

        /// <summary>
        /// A unicast frame for some other machine on the LAN. Drop it.
        /// </summary>
        NotForGuest,

        /// <summary>
        /// Shorter than an Ethernet header (14 bytes). Drop it.
        /// </summary>
        TooShort,
    }

    /// <summary>
    /// Learns the guest's MAC address(es) from the frames the guest sends, and decides which
    /// captured frames the guest gets: frames for its own MAC, broadcast and multicast - and
    /// never a frame the guest sent itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why: pcap opens the adapter promiscuous and, like libpcap always does, captures both
    /// directions, so without this the guest saw its own transmits and every unicast frame on
    /// the LAN. The legacy pcap backend (Emulated.HW/Common/Network/PcapEthernetBackend.cs,
    /// ShouldAccept) had exactly this rule; the NuGet backend lost it. An old guest driver can
    /// stop receiving for good when it is flooded with traffic it did not ask for (measured,
    /// see the host networking design, section 3.3).
    /// </para>
    /// <para>
    /// Where the MAC comes from: the source address of the frames the guest transmits. That is
    /// the address the guest really uses - also when the guest programs a different MAC than
    /// the card's default (a LANCE gets its address from the guest's init block; a Linux guest
    /// can change a virtio card's address) - and it needs no change to any card. Until the
    /// guest has sent its first frame only broadcast and multicast are delivered: nobody on the
    /// LAN can have learned the guest's address before the guest has spoken.
    /// </para>
    /// <para>
    /// Threads: <see cref="Learn"/> runs on the emulation thread (one writer), <see cref="Classify"/>
    /// on the capture thread. Each address is one 64-bit value written with a volatile write,
    /// so the capture thread sees either the old or the new address, never half of one.
    /// </para>
    /// </remarks>
    internal sealed class GuestMacFilter
    {
        // ── Constants ──

        /// <summary>
        /// How many guest addresses are kept. A machine has one backend for all its cards, so a
        /// machine with two cards needs two; when more are seen the oldest is replaced.
        /// </summary>
        public const int MaxGuestMacs = 4;

        // ── Fields ──

        /// <summary>
        /// The learned addresses, 48 bits each in network order; 0 marks a free entry.
        /// </summary>
        private readonly long[] _macs = new long[MaxGuestMacs];

        /// <summary>
        /// The entry the next new address goes into (oldest first).
        /// </summary>
        private int _next;

        /// <summary>
        /// Changes each time the set of addresses changes.
        /// </summary>
        private int _version;

        // ── Properties ──

        /// <summary>
        /// Gets a number that changes whenever a new guest address is learned.
        /// </summary>
        public int Version => Volatile.Read(ref _version);

        /// <summary>
        /// Gets how many guest addresses have been learned (at most <see cref="MaxGuestMacs"/>).
        /// </summary>
        public int Count
        {
            get
            {
                int count = 0;
                for (int i = 0; i < MaxGuestMacs; i++)
                {
                    if (Volatile.Read(ref _macs[i]) != 0)
                    {
                        count++;
                    }
                }
                return count;
            }
        }

        // ── Public methods ──

        /// <summary>
        /// Notes the source address of a frame the guest is sending.
        /// </summary>
        /// <remarks>
        /// A source address with the group bit set, or all zeros, is not a real station address
        /// and is not learned.
        /// </remarks>
        /// <param name="frame">
        /// The frame the guest transmits, from the destination MAC on.
        /// </param>
        /// <returns>
        /// <c>true</c> when the address was new.
        /// </returns>
        public bool Learn(ReadOnlySpan<byte> frame)
        {
            if (frame.Length < 14)
            {
                return false;
            }

            long source = ReadMac(frame.Slice(6, 6));
            if (source == 0 || (frame[6] & 0x01) != 0)
            {
                return false;
            }

            for (int i = 0; i < MaxGuestMacs; i++)
            {
                if (Volatile.Read(ref _macs[i]) == source)
                {
                    return false;
                }
            }

            Volatile.Write(ref _macs[_next], source);
            _next = (_next + 1) % MaxGuestMacs;
            Interlocked.Increment(ref _version);
            return true;
        }

        /// <summary>
        /// Decides whether a captured frame goes to the guest.
        /// </summary>
        /// <param name="frame">
        /// The captured frame, from the destination MAC on.
        /// </param>
        /// <returns>
        /// The verdict.
        /// </returns>
        public CapturedFrameVerdict Classify(ReadOnlySpan<byte> frame)
        {
            if (frame.Length < 14)
            {
                return CapturedFrameVerdict.TooShort;
            }

            long source = ReadMac(frame.Slice(6, 6));
            long destination = ReadMac(frame.Slice(0, 6));
            bool forGuest = false;
            for (int i = 0; i < MaxGuestMacs; i++)
            {
                long mac = Volatile.Read(ref _macs[i]);
                if (mac == 0)
                {
                    continue;
                }
                if (mac == source)
                {
                    // pcap captures what we inject too; the card did not really receive it.
                    return CapturedFrameVerdict.SelfEcho;
                }
                if (mac == destination)
                {
                    forGuest = true;
                }
            }

            // Bit 0 of the first destination byte is the group bit: multicast, and broadcast
            // (all ones) with it.
            if (forGuest || (frame[0] & 0x01) != 0)
            {
                return CapturedFrameVerdict.Deliver;
            }
            return CapturedFrameVerdict.NotForGuest;
        }

        /// <summary>
        /// Builds the pcap filter expression that lets the kernel (or the Npcap driver) apply
        /// the same rule as <see cref="Classify"/>, so frames for other machines are not even
        /// copied up to us.
        /// </summary>
        /// <returns>
        /// <c>ether broadcast or ether multicast</c> before any address is learned; after that
        /// the guest's addresses as destinations, broadcast and multicast, minus frames whose
        /// source is one of the guest's addresses.
        /// </returns>
        public string BuildKernelFilter()
        {
            StringBuilder destinations = new StringBuilder();
            StringBuilder sources = new StringBuilder();
            for (int i = 0; i < MaxGuestMacs; i++)
            {
                long mac = Volatile.Read(ref _macs[i]);
                if (mac == 0)
                {
                    continue;
                }
                string text = FormatMac(mac);
                destinations.Append("ether dst ").Append(text).Append(" or ");
                if (sources.Length > 0)
                {
                    sources.Append(" or ");
                }
                sources.Append("ether src ").Append(text);
            }

            if (sources.Length == 0)
            {
                return "ether broadcast or ether multicast";
            }
            return "(" + destinations.ToString() + "ether broadcast or ether multicast) and not (" + sources.ToString() + ")";
        }

        /// <summary>
        /// Gets the learned addresses as text, separated by commas.
        /// </summary>
        /// <returns>
        /// The addresses written aa:bb:cc:dd:ee:ff; empty when none is learned yet.
        /// </returns>
        public string DescribeMacs()
        {
            StringBuilder text = new StringBuilder();
            for (int i = 0; i < MaxGuestMacs; i++)
            {
                long mac = Volatile.Read(ref _macs[i]);
                if (mac == 0)
                {
                    continue;
                }
                if (text.Length > 0)
                {
                    text.Append(", ");
                }
                text.Append(FormatMac(mac));
            }
            return text.ToString();
        }

        // ── Private methods ──

        /// <summary>
        /// Reads six address bytes as one 48-bit number, first byte highest.
        /// </summary>
        private static long ReadMac(ReadOnlySpan<byte> mac)
        {
            return ((long)mac[0] << 40) | ((long)mac[1] << 32) | ((long)mac[2] << 24)
                | ((long)mac[3] << 16) | ((long)mac[4] << 8) | mac[5];
        }

        /// <summary>
        /// Writes a 48-bit address the way pcap filters and people read it: aa:bb:cc:dd:ee:ff.
        /// </summary>
        private static string FormatMac(long mac)
        {
            StringBuilder text = new StringBuilder(17);
            for (int shift = 40; shift >= 0; shift -= 8)
            {
                text.Append(((int)(mac >> shift) & 0xFF).ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                if (shift > 0)
                {
                    text.Append(':');
                }
            }
            return text.ToString();
        }
    }
}
