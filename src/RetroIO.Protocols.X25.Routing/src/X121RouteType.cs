//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.X25.Routing
{
    /// <summary>
    /// The kind of an X.121 routing-table entry, and its evaluation precedence.
    /// </summary>
    /// <remarks>
    /// Values are assigned explicitly so configuration indices and any serialized form remain stable.
    /// </remarks>
    public enum X121RouteType
    {
        /// <summary>
        /// Permanent virtual circuit cross-connect. Handled at setup time, skipped by dynamic lookup.
        /// </summary>
        Pvc = 0,

        /// <summary>
        /// Exact full-address match. First match (in table order) short-circuits the lookup.
        /// </summary>
        Exact = 1,

        /// <summary>
        /// Hunt group: an exact address match resolves to a round-robin member port.
        /// </summary>
        Hunt = 2,

        /// <summary>
        /// Leading-digit prefix match. The longest matching prefix wins.
        /// </summary>
        Prefix = 3,

        /// <summary>
        /// DNIC (first four digits) match, considered lower priority than any prefix match.
        /// </summary>
        Dnic = 4,

        /// <summary>
        /// Catch-all default route, used only when nothing else matches.
        /// </summary>
        Default = 5,

        /// <summary>
        /// Regular-expression address match. An extension beyond the structured rule types (which have no
        /// regex), added so this generic table can also express endpoint pattern routing and become the
        /// single routing engine. Evaluated like <see cref="Exact"/> (first match, table order).
        /// </summary>
        Regex = 6
    }
}
