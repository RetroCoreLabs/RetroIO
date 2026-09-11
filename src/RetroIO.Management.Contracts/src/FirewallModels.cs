//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Management.Contracts;

public class FirewallConfigDto
{
    public bool Enabled { get; set; }
    public string DefaultPolicy { get; set; } = string.Empty;
    public bool AllowLocalhost { get; set; }
    public bool AllowPrivateNetworks { get; set; }
    public List<FirewallRuleDto> Rules { get; set; } = new();
    public List<string> AllowedIpAddresses { get; set; } = new();
    public List<string> BlockedIpAddresses { get; set; } = new();
    public List<string> AllowedNetworks { get; set; } = new();
}

public class FirewallRulesResponse
{
    public List<FirewallRuleDto> Rules { get; set; } = new();
    public int Total { get; set; }
}

public class FirewallRuleDto
{
    public string RuleId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string IPCondition { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public int Priority { get; set; }
    public bool Enabled { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateFirewallRuleRequest
{
    public string Name { get; set; } = string.Empty;
    public string IPCondition { get; set; } = string.Empty;
    public string Action { get; set; } = "Allow";
    public int Priority { get; set; } = 100;
    public bool Enabled { get; set; } = true;
    public string? Description { get; set; }
}

public class UpdateFirewallRuleRequest
{
    public string? Name { get; set; }
    public string? IPCondition { get; set; }
    public string? Action { get; set; }
    public int? Priority { get; set; }
    public bool? Enabled { get; set; }
    public string? Description { get; set; }
}

public class SetFirewallEnabledRequest
{
    public bool Enabled { get; set; }
}

public class SetFirewallPolicyRequest
{
    public string Policy { get; set; } = "Allow";
}

public class TestFirewallRequest
{
    public string IpAddress { get; set; } = string.Empty;
}

public class FirewallTestResult
{
    public string IpAddress { get; set; } = string.Empty;
    public bool Allowed { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? RuleId { get; set; }
}

public class FirewallCacheStats
{
    public int TotalEntries { get; set; }
    public int ActiveEntries { get; set; }
}
