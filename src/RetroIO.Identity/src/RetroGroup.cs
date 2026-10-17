//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Identity
{
    /// <summary>
    /// RetroIDP Group - GUID-based group identity
    /// </summary>
    public sealed class RetroGroup
    {
        /// <summary>Primary identity (never changes)</summary>
        public Guid GroupId { get; set; }

        /// <summary>Group name (unique)</summary>
        public string GroupName { get; set; } = string.Empty;

        /// <summary>Description</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>Members (list of user GUIDs)</summary>
        public List<Guid> Members { get; set; } = new();

        /// <summary>Created timestamp</summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Additional metadata</summary>
        public Dictionary<string, string> Metadata { get; set; } = new();
    }
}
