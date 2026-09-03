//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Security.Firewall;

/// <summary>
/// Firewall decision result
/// </summary>
public readonly struct FirewallDecision
{
    public bool IsAllowed { get; init; }
    public string Reason { get; init; }
    public Guid? MatchedRuleId { get; init; }

    public static FirewallDecision Allow(string reason, Guid? ruleId = null) =>
        new() { IsAllowed = true, Reason = reason, MatchedRuleId = ruleId };

    public static FirewallDecision Deny(string reason, Guid? ruleId = null) =>
        new() { IsAllowed = false, Reason = reason, MatchedRuleId = ruleId };
}

/// <summary>
/// Firewall rule action
/// </summary>
public enum RuleAction
{
    Allow,
    Deny
}

/// <summary>
/// Default firewall policy
/// </summary>
public enum FirewallPolicy
{
    Allow,  // Default allow (whitelist exceptions with Deny rules)
    Deny    // Default deny (whitelist with Allow rules)
}
