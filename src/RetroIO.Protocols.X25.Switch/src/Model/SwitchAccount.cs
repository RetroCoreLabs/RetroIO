//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.X25.Switch.Model
{
    /// <summary>
    /// A runtime account: an address range plus lifetime usage counters accumulated as calls complete.
    /// </summary>
    public sealed class SwitchAccount
    {
        /// <summary>
        /// Account name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Start of the account's X.121 address range (inclusive).
        /// </summary>
        public string AddrStart { get; }

        /// <summary>
        /// End of the account's X.121 address range (inclusive).
        /// </summary>
        public string AddrEnd { get; }

        /// <summary>
        /// Calls of this account up now, as caller or called party: the column an operator watching a busy subscriber
        /// wants.
        /// </summary>
        public uint CallsActive { get; set; }

        /// <summary>
        /// Calls placed by an address in this account's range.
        /// </summary>
        public uint CallsMade { get; set; }

        /// <summary>
        /// Calls received for an address in this account's range.
        /// </summary>
        public uint CallsReceived { get; set; }

        /// <summary>
        /// Packets sent.
        /// </summary>
        public ulong PacketsSent { get; set; }

        /// <summary>
        /// Packets received.
        /// </summary>
        public ulong PacketsReceived { get; set; }

        /// <summary>
        /// Bytes sent.
        /// </summary>
        public ulong BytesSent { get; set; }

        /// <summary>
        /// Bytes received.
        /// </summary>
        public ulong BytesReceived { get; set; }

        /// <summary>
        /// Sum of all call durations, in milliseconds.
        /// </summary>
        public ulong TotalDurationMs { get; set; }

        /// <summary>
        /// Charging segments this account's calls sent (ITU-T D.12), added when each call ends.
        /// </summary>
        public ulong SegmentsSent { get; set; }

        /// <summary>
        /// Charging segments this account's calls received.
        /// </summary>
        public ulong SegmentsReceived { get; set; }

        /// <summary>
        /// Creates a runtime account from its configuration.
        /// </summary>
        /// <param name="config">
        /// The account configuration.
        /// </param>
        public SwitchAccount(AccountConfig config)
        {
            Name = config.Name;
            AddrStart = config.AddrStart;
            AddrEnd = config.AddrEnd;
            Nui = config.Nui;
        }

        /// <summary>
        /// The network user identifier that names this account in a call (see <see cref="AccountConfig.Nui"/>); empty when
        /// the account is reached only by its address range.
        /// </summary>
        public string Nui { get; }

        /// <summary>
        /// Tests whether a network user identifier from a Call Request names this account, octet for octet.
        /// </summary>
        /// <param name="identifier">
        /// The parameter field of the NUI selection element (X.25 7.3.7).
        /// </param>
        /// <returns>
        /// True when the account has an NUI and it is exactly these octets.
        /// </returns>
        public bool HasNui(ReadOnlySpan<byte> identifier)
        {
            if (Nui.Length == 0 || Nui.Length != identifier.Length)
            {
                return false;
            }

            for (int i = 0; i < identifier.Length; i++)
            {
                if (Nui[i] != identifier[i])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Tests whether an X.121 address falls within this account's inclusive range.
        /// </summary>
        /// <param name="address">
        /// The address to test.
        /// </param>
        /// <returns>
        /// True if the address is within <see cref="AddrStart"/>..<see cref="AddrEnd"/>.
        /// </returns>
        public bool Covers(string address)
        {
            if (string.IsNullOrEmpty(AddrStart) || string.IsNullOrEmpty(AddrEnd))
            {
                return false;
            }

            return string.CompareOrdinal(address, AddrStart) >= 0
                && string.CompareOrdinal(address, AddrEnd) <= 0;
        }
    }
}
