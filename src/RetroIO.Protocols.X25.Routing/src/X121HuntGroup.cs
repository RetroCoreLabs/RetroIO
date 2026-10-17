//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;

namespace RetroIO.Protocols.X25.Routing
{
    /// <summary>
    /// A hunt group: a set of member ports that a hunt route distributes calls across, round-robin.
    /// </summary>
    public sealed class X121HuntGroup
    {
        /// <summary>
        /// Group index. Hunt routes reference a group by this id (its position in the table).
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Member port numbers, in round-robin order.
        /// </summary>
        public List<int> Members { get; } = new List<int>();

        /// <summary>
        /// Round-robin cursor, advanced on every selection. Grows without bound and is taken modulo the
        /// member count at selection time.
        /// </summary>
        public int NextIdx { get; set; }

        /// <summary>
        /// Number of member ports.
        /// </summary>
        public int MemberCount => Members.Count;
    }
}
