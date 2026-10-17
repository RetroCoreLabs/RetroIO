//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Concurrent;
using System.Net;

namespace RetroIO.Security.Firewall;

/// <summary>
/// High-performance firewall engine with in-memory caching
/// Thread-safe for concurrent access
/// </summary>
public sealed class FirewallEngine
{
    private readonly FirewallConfiguration _config;
    private readonly ConcurrentDictionary<string, (FirewallDecision Decision, DateTime ExpiresAt)> _cache;
    private readonly ReaderWriterLockSlim _configLock = new();

    public FirewallEngine(FirewallConfiguration config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _cache = new ConcurrentDictionary<string, (FirewallDecision, DateTime)>();
    }

    /// <summary>
    /// Check if IP address is allowed (with caching)
    /// </summary>
    public FirewallDecision CheckAccess(string ipAddress)
    {
        if (!_config.Enabled)
        {
            return FirewallDecision.Allow("Firewall disabled");
        }

        // Hot path: check cache first (lockless read)
        if (_config.CacheTTLSeconds > 0 && _cache.TryGetValue(ipAddress, out var cached))
        {
            if (cached.ExpiresAt > DateTime.UtcNow)
            {
                return cached.Decision; // Cache hit, <10ns
            }
        }

        // Cache miss or expired: evaluate rules (slow path)
        _configLock.EnterReadLock();
        try
        {
            var decision = EvaluateRules(ipAddress);

            // Update cache with TTL
            if (_config.CacheTTLSeconds > 0)
            {
                var expiresAt = DateTime.UtcNow.AddSeconds(_config.CacheTTLSeconds);
                _cache[ipAddress] = (decision, expiresAt);
            }

            return decision;
        }
        finally
        {
            _configLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Evaluate firewall rules for IP address
    /// </summary>
    private FirewallDecision EvaluateRules(string ipAddress)
    {
        if (!IPAddress.TryParse(ipAddress, out var ip))
        {
            return FirewallDecision.Deny("Invalid IP address");
        }

        // Priority 1: Blocked IPs (highest priority)
        if (_config.BlockedIpAddresses.Contains(ipAddress))
        {
            return FirewallDecision.Deny("IP in block list");
        }

        // Priority 2: Allowed IPs
        if (_config.AllowedIpAddresses.Contains(ipAddress))
        {
            return FirewallDecision.Allow("IP in allow list");
        }

        // Priority 3: Localhost
        if (_config.AllowLocalhost && IsLocalhost(ip))
        {
            return FirewallDecision.Allow("Localhost");
        }

        // Priority 4: Private networks
        if (_config.AllowPrivateNetworks && IsPrivateNetwork(ip))
        {
            return FirewallDecision.Allow("Private network");
        }

        // Priority 5: Allowed networks (CIDR)
        for (int i = 0; i < _config.AllowedNetworks.Count; i++)
        {
            if (IsInNetwork(ip, _config.AllowedNetworks[i]))
            {
                return FirewallDecision.Allow($"Allowed network: {_config.AllowedNetworks[i]}");
            }
        }

        // Priority 6: Custom rules (priority-ordered)
        var sortedRules = _config.Rules
            .Where(r => r.Enabled)
            .OrderByDescending(r => r.Priority)
            .ToList();

        for (int i = 0; i < sortedRules.Count; i++)
        {
            var rule = sortedRules[i];
            if (MatchesRule(ip, rule))
            {
                return rule.Action == RuleAction.Allow
                    ? FirewallDecision.Allow($"Rule: {rule.Name}", rule.RuleId)
                    : FirewallDecision.Deny($"Rule: {rule.Name}", rule.RuleId);
            }
        }

        // Priority 7: Default policy
        return _config.DefaultPolicy == FirewallPolicy.Allow
            ? FirewallDecision.Allow("Default policy: Allow")
            : FirewallDecision.Deny("Default policy: Deny");
    }

    /// <summary>
    /// Check if IP matches a firewall rule
    /// </summary>
    private bool MatchesRule(IPAddress ip, FirewallRule rule)
    {
        var condition = rule.IPCondition;

        // Single IP match
        if (IPAddress.TryParse(condition, out var ruleIP))
        {
            return ip.Equals(ruleIP);
        }

        // CIDR notation
        if (condition.Contains('/'))
        {
            return IsInNetwork(ip, condition);
        }

        // IP range (e.g., "192.168.1.1-192.168.1.100")
        if (condition.Contains('-'))
        {
            return IsInRange(ip, condition);
        }

        return false;
    }

    /// <summary>
    /// Check if IP is localhost
    /// </summary>
    private static bool IsLocalhost(IPAddress ip)
    {
        return IPAddress.IsLoopback(ip);
    }

    /// <summary>
    /// Check if IP is in private network range
    /// </summary>
    private static bool IsPrivateNetwork(IPAddress ip)
    {
        byte[] bytes = ip.GetAddressBytes();

        // IPv4 private networks
        if (bytes.Length == 4)
        {
            // 10.0.0.0/8
            if (bytes[0] == 10)
                return true;

            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                return true;

            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168)
                return true;
        }
        // IPv6 private networks
        else if (bytes.Length == 16)
        {
            // fc00::/7 (Unique Local Addresses)
            if ((bytes[0] & 0xfe) == 0xfc)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Check if IP is in CIDR network
    /// </summary>
    private static bool IsInNetwork(IPAddress ip, string cidr)
    {
        string[] parts = cidr.Split('/');
        if (parts.Length != 2)
            return false;

        if (!IPAddress.TryParse(parts[0], out var networkAddress))
            return false;

        if (!int.TryParse(parts[1], out int prefixLength))
            return false;

        byte[] ipBytes = ip.GetAddressBytes();
        byte[] networkBytes = networkAddress.GetAddressBytes();

        if (ipBytes.Length != networkBytes.Length)
            return false; // IPv4 vs IPv6 mismatch

        int fullBytes = prefixLength / 8;
        int remainingBits = prefixLength % 8;

        // Check full bytes
        for (int i = 0; i < fullBytes; i++)
        {
            if (ipBytes[i] != networkBytes[i])
                return false;
        }

        // Check remaining bits
        if (remainingBits > 0)
        {
            byte mask = (byte)(0xFF << (8 - remainingBits));
            if ((ipBytes[fullBytes] & mask) != (networkBytes[fullBytes] & mask))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Check if IP is in range (e.g., "192.168.1.1-192.168.1.100")
    /// </summary>
    private static bool IsInRange(IPAddress ip, string range)
    {
        string[] parts = range.Split('-');
        if (parts.Length != 2)
            return false;

        if (!IPAddress.TryParse(parts[0].Trim(), out var startIP))
            return false;

        if (!IPAddress.TryParse(parts[1].Trim(), out var endIP))
            return false;

        byte[] ipBytes = ip.GetAddressBytes();
        byte[] startBytes = startIP.GetAddressBytes();
        byte[] endBytes = endIP.GetAddressBytes();

        if (ipBytes.Length != startBytes.Length || ipBytes.Length != endBytes.Length)
            return false;

        // Compare as big-endian integers
        for (int i = 0; i < ipBytes.Length; i++)
        {
            if (ipBytes[i] < startBytes[i])
                return false;
            if (ipBytes[i] > endBytes[i])
                return false;
            if (ipBytes[i] > startBytes[i] && ipBytes[i] < endBytes[i])
                return true; // Definitely in range
        }

        return true;
    }

    /// <summary>
    /// Clear cache (call when rules are updated)
    /// </summary>
    public void ClearCache()
    {
        _cache.Clear();
    }

    /// <summary>
    /// Update configuration and clear cache
    /// </summary>
    public void UpdateConfiguration(Action<FirewallConfiguration> updateAction)
    {
        _configLock.EnterWriteLock();
        try
        {
            updateAction(_config);
            ClearCache();
        }
        finally
        {
            _configLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Get cache statistics
    /// </summary>
    public (int CachedEntries, int ActiveEntries) GetCacheStats()
    {
        int total = _cache.Count;
        int active = _cache.Count(kvp => kvp.Value.ExpiresAt > DateTime.UtcNow);
        return (total, active);
    }
}
