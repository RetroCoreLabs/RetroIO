//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Security.Firewall;

/// <summary>
/// Firewall configuration with JSON persistence
/// </summary>
public sealed class FirewallConfiguration
{
    /// <summary>Is firewall enabled?</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Default policy when no rules match</summary>
    public FirewallPolicy DefaultPolicy { get; set; } = FirewallPolicy.Deny;

    /// <summary>Quick allow list (simple IPs)</summary>
    public List<string> AllowedIpAddresses { get; set; } = new();

    /// <summary>Quick block list (simple IPs)</summary>
    public List<string> BlockedIpAddresses { get; set; } = new();

    /// <summary>Allowed networks in CIDR notation</summary>
    public List<string> AllowedNetworks { get; set; } = new();

    /// <summary>Allow localhost (127.0.0.1, ::1)?</summary>
    public bool AllowLocalhost { get; set; } = true;

    /// <summary>Allow private networks (10.x, 192.168.x, 172.16-31.x)?</summary>
    public bool AllowPrivateNetworks { get; set; } = true;

    /// <summary>Cache TTL in seconds (0 = no cache)</summary>
    public int CacheTTLSeconds { get; set; } = 60;

    /// <summary>Priority-ordered firewall rules</summary>
    public List<FirewallRule> Rules { get; set; } = new();
}
