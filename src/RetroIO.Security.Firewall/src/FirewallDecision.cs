//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Security.Firewall;

/// <summary>
/// Firewall decision result
/// </summary>
public readonly struct FirewallDecision
{
    /// <summary>
    /// Gets whether the address is allowed.
    /// </summary>
    public bool IsAllowed { get; init; }
    /// <summary>
    /// Gets the text that says why the decision was made.
    /// </summary>
    public string Reason { get; init; }
    /// <summary>
    /// Gets the identifier of the rule that matched, or null when no rule matched.
    /// </summary>
    public Guid? MatchedRuleId { get; init; }

    /// <summary>
    /// Creates an allow decision.
    /// </summary>
    /// <param name="reason">
    /// The text that says why the address is allowed.
    /// </param>
    /// <param name="ruleId">
    /// The identifier of the rule that matched, or null.
    /// </param>
    /// <returns>
    /// A decision with IsAllowed true.
    /// </returns>
    public static FirewallDecision Allow(string reason, Guid? ruleId = null) =>
        new() { IsAllowed = true, Reason = reason, MatchedRuleId = ruleId };

    /// <summary>
    /// Creates a deny decision.
    /// </summary>
    /// <param name="reason">
    /// The text that says why the address is denied.
    /// </param>
    /// <param name="ruleId">
    /// The identifier of the rule that matched, or null.
    /// </param>
    /// <returns>
    /// A decision with IsAllowed false.
    /// </returns>
    public static FirewallDecision Deny(string reason, Guid? ruleId = null) =>
        new() { IsAllowed = false, Reason = reason, MatchedRuleId = ruleId };
}

/// <summary>
/// Firewall rule action
/// </summary>
public enum RuleAction
{
    /// <summary>
    /// The rule allows matching addresses.
    /// </summary>
    Allow,
    /// <summary>
    /// The rule denies matching addresses.
    /// </summary>
    Deny
}

/// <summary>
/// Default firewall policy
/// </summary>
public enum FirewallPolicy
{
    /// <summary>
    /// Addresses are allowed when no earlier check decides. Deny rules make exceptions.
    /// </summary>
    Allow,  // Default allow (whitelist exceptions with Deny rules)
    /// <summary>
    /// Addresses are denied when no earlier check decides. Allow rules make exceptions.
    /// </summary>
    Deny    // Default deny (whitelist with Allow rules)
}
