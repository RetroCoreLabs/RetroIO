//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Management.Contracts;

/// <summary>
/// The firewall configuration as the management API returns it.
/// </summary>
public class FirewallConfigDto
{
    /// <summary>
    /// Gets or sets whether the firewall is enabled.
    /// </summary>
    public bool Enabled { get; set; }
    /// <summary>
    /// Gets or sets the default policy as text.
    /// </summary>
    public string DefaultPolicy { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets whether localhost is allowed.
    /// </summary>
    public bool AllowLocalhost { get; set; }
    /// <summary>
    /// Gets or sets whether private networks are allowed.
    /// </summary>
    public bool AllowPrivateNetworks { get; set; }
    /// <summary>
    /// Gets or sets the firewall rules.
    /// </summary>
    public List<FirewallRuleDto> Rules { get; set; } = new();
    /// <summary>
    /// Gets or sets the IP addresses that are allowed.
    /// </summary>
    public List<string> AllowedIpAddresses { get; set; } = new();
    /// <summary>
    /// Gets or sets the IP addresses that are blocked.
    /// </summary>
    public List<string> BlockedIpAddresses { get; set; } = new();
    /// <summary>
    /// Gets or sets the networks that are allowed.
    /// </summary>
    public List<string> AllowedNetworks { get; set; } = new();
}

/// <summary>
/// A list of firewall rules.
/// </summary>
public class FirewallRulesResponse
{
    /// <summary>
    /// Gets or sets the rules.
    /// </summary>
    public List<FirewallRuleDto> Rules { get; set; } = new();
    /// <summary>
    /// Gets or sets the number of rules.
    /// </summary>
    public int Total { get; set; }
}

/// <summary>
/// One firewall rule.
/// </summary>
public class FirewallRuleDto
{
    /// <summary>
    /// Gets or sets the rule identifier.
    /// </summary>
    public string RuleId { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the rule name.
    /// </summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the IP condition the rule matches on, as text.
    /// </summary>
    public string IPCondition { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the action the rule takes, as text.
    /// </summary>
    public string Action { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the rule priority.
    /// </summary>
    public int Priority { get; set; }
    /// <summary>
    /// Gets or sets whether the rule is enabled.
    /// </summary>
    public bool Enabled { get; set; }
    /// <summary>
    /// Gets or sets the rule description, or null when there is none.
    /// </summary>
    public string? Description { get; set; }
    /// <summary>
    /// Gets or sets when the rule was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// A request to create a firewall rule.
/// </summary>
public class CreateFirewallRuleRequest
{
    /// <summary>
    /// Gets or sets the rule name.
    /// </summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the IP condition the rule matches on, as text.
    /// </summary>
    public string IPCondition { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the action the rule takes, as text. The default is <c>Allow</c>.
    /// </summary>
    public string Action { get; set; } = "Allow";
    /// <summary>
    /// Gets or sets the rule priority. The default is 100.
    /// </summary>
    public int Priority { get; set; } = 100;
    /// <summary>
    /// Gets or sets whether the rule is enabled. The default is true.
    /// </summary>
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// Gets or sets the rule description, or null when there is none.
    /// </summary>
    public string? Description { get; set; }
}

/// <summary>
/// A request to change a firewall rule. A property left null is not changed.
/// </summary>
public class UpdateFirewallRuleRequest
{
    /// <summary>
    /// Gets or sets the new rule name, or null to keep the current one.
    /// </summary>
    public string? Name { get; set; }
    /// <summary>
    /// Gets or sets the new IP condition, or null to keep the current one.
    /// </summary>
    public string? IPCondition { get; set; }
    /// <summary>
    /// Gets or sets the new action, or null to keep the current one.
    /// </summary>
    public string? Action { get; set; }
    /// <summary>
    /// Gets or sets the new priority, or null to keep the current one.
    /// </summary>
    public int? Priority { get; set; }
    /// <summary>
    /// Gets or sets whether the rule is enabled, or null to keep the current value.
    /// </summary>
    public bool? Enabled { get; set; }
    /// <summary>
    /// Gets or sets the new description, or null to keep the current one.
    /// </summary>
    public string? Description { get; set; }
}

/// <summary>
/// A request to turn the firewall on or off.
/// </summary>
public class SetFirewallEnabledRequest
{
    /// <summary>
    /// Gets or sets whether the firewall is enabled.
    /// </summary>
    public bool Enabled { get; set; }
}

/// <summary>
/// A request to set the default firewall policy.
/// </summary>
public class SetFirewallPolicyRequest
{
    /// <summary>
    /// Gets or sets the policy as text. The default is <c>Allow</c>.
    /// </summary>
    public string Policy { get; set; } = "Allow";
}

/// <summary>
/// A request to test an IP address against the firewall.
/// </summary>
public class TestFirewallRequest
{
    /// <summary>
    /// Gets or sets the IP address to test.
    /// </summary>
    public string IpAddress { get; set; } = string.Empty;
}

/// <summary>
/// The result of testing an IP address against the firewall.
/// </summary>
public class FirewallTestResult
{
    /// <summary>
    /// Gets or sets the IP address that was tested.
    /// </summary>
    public string IpAddress { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets whether the address is allowed.
    /// </summary>
    public bool Allowed { get; set; }
    /// <summary>
    /// Gets or sets the reason for the decision.
    /// </summary>
    public string Reason { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the identifier of the rule that decided, or null when no rule did.
    /// </summary>
    public string? RuleId { get; set; }
}

/// <summary>
/// Counters for the firewall decision cache.
/// </summary>
public class FirewallCacheStats
{
    /// <summary>
    /// Gets or sets the number of entries in the cache.
    /// </summary>
    public int TotalEntries { get; set; }
    /// <summary>
    /// Gets or sets the number of cache entries that are active.
    /// </summary>
    public int ActiveEntries { get; set; }
}
