//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.X25.Switch
{
    /// <summary>
    /// A billing/access-control account: a name and the X.121 address range it covers. This is the
    /// configuration portion only (lifetime counters live on the runtime account model).
    /// </summary>
    public sealed class AccountConfig
    {
        /// <summary>
        /// Account name.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Start of the account's X.121 address range.
        /// </summary>
        public string AddrStart { get; set; } = string.Empty;

        /// <summary>
        /// End of the account's X.121 address range.
        /// </summary>
        public string AddrEnd { get; set; } = string.Empty;

        /// <summary>
        /// The network user identifier that names this account in a call (X.25 6.21, NUI selection 7.3.7), compared octet
        /// for octet with the identifier in the Call Request. Its format "is determined by the Administration" (7.3.7), so
        /// any text. A call naming it is billed here whatever line it came in on. Config key <c>nui</c>; empty for an
        /// account reached only by its address range.
        /// </summary>
        public string Nui { get; set; } = string.Empty;
    }
}
