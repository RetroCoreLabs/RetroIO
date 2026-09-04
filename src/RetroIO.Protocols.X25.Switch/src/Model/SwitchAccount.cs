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
