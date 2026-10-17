//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Security.Firewall;

/// <summary>
/// Firewall rule with RBAC support
/// </summary>
public sealed class FirewallRule
{
    /// <summary>
    /// Unique rule ID
    /// </summary>
    public Guid RuleId { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Rule name (for management)
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Rule description
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Rule action (Allow/Deny)
    /// </summary>
    public RuleAction Action { get; set; } = RuleAction.Deny;

    /// <summary>
    /// IP condition (single IP, CIDR, or range)
    /// </summary>
    public string IPCondition { get; set; } = string.Empty;

    /// <summary>
    /// Rule priority (higher = evaluated first)
    /// </summary>
    public int Priority { get; set; } = 100;

    /// <summary>
    /// Is rule enabled?
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// User GUIDs who can edit this rule (RBAC)
    /// </summary>
    public List<Guid> AllowedEditors { get; set; } = new();

    /// <summary>
    /// User who created this rule
    /// </summary>
    public Guid CreatedBy { get; set; }

    /// <summary>
    /// When rule was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When rule was last modified
    /// </summary>
    public DateTime? ModifiedAt { get; set; }

    /// <summary>
    /// User who last modified this rule
    /// </summary>
    public Guid? ModifiedBy { get; set; }
}
