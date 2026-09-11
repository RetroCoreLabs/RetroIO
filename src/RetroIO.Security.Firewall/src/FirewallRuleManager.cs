//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Security.Firewall;

/// <summary>
/// Firewall rule manager with RBAC (Role-Based Access Control)
/// Manages firewall rules and permissions
/// </summary>
public sealed class FirewallRuleManager
{
    private readonly FirewallEngine _engine;
    private readonly FirewallPersistence _persistence;

    public FirewallRuleManager(FirewallEngine engine, FirewallPersistence persistence)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
    }

    /// <summary>
    /// Check if user can modify a specific firewall rule
    /// </summary>
    public bool CanModifyRule(Guid userId, FirewallRule rule, bool isAdmin)
    {
        // Admins can modify all rules
        if (isAdmin)
            return true;

        // Rule creator can modify their rule
        if (rule.CreatedBy == userId)
            return true;

        // Check if user is in AllowedEditors
        if (rule.AllowedEditors.Contains(userId))
            return true;

        return false;
    }

    /// <summary>
    /// Check if user can create firewall rules
    /// </summary>
    public bool CanCreateRule(Guid userId, bool isAdmin)
    {
        // For now, only admins can create rules
        // Can be extended with permission system later
        return isAdmin;
    }

    /// <summary>
    /// Add new firewall rule
    /// </summary>
    public async Task<(bool Success, string Message)> AddRuleAsync(
        Guid userId,
        bool isAdmin,
        FirewallRule newRule)
    {
        if (!CanCreateRule(userId, isAdmin))
        {
            return (false, "Insufficient permissions to create firewall rules");
        }

        if (newRule == null)
        {
            return (false, "Rule cannot be null");
        }

        // Set metadata
        newRule.RuleId = Guid.NewGuid();
        newRule.CreatedBy = userId;
        newRule.CreatedAt = DateTime.UtcNow;

        // Add rule and clear cache
        _engine.UpdateConfiguration(config =>
        {
            config.Rules.Add(newRule);
        });

        // Persist changes
        var currentConfig = _persistence.Load();
        if (currentConfig != null)
        {
            currentConfig.Rules.Add(newRule);
            await _persistence.SaveAsync(currentConfig);
        }

        return (true, $"Firewall rule '{newRule.Name}' created successfully");
    }

    /// <summary>
    /// Update existing firewall rule
    /// </summary>
    public async Task<(bool Success, string Message)> UpdateRuleAsync(
        Guid userId,
        bool isAdmin,
        FirewallRule updatedRule)
    {
        var currentConfig = _persistence.Load();
        if (currentConfig == null)
        {
            return (false, "Failed to load firewall configuration");
        }

        var existingRule = currentConfig.Rules.FirstOrDefault(r => r.RuleId == updatedRule.RuleId);
        if (existingRule == null)
        {
            return (false, $"Rule {updatedRule.RuleId} not found");
        }

        if (!CanModifyRule(userId, existingRule, isAdmin))
        {
            return (false, "Insufficient permissions to modify this rule");
        }

        // Update rule metadata
        updatedRule.ModifiedBy = userId;
        updatedRule.ModifiedAt = DateTime.UtcNow;

        // Replace rule
        var index = currentConfig.Rules.IndexOf(existingRule);
        currentConfig.Rules[index] = updatedRule;

        // Update engine and persist
        _engine.UpdateConfiguration(config =>
        {
            var idx = config.Rules.FindIndex(r => r.RuleId == updatedRule.RuleId);
            if (idx >= 0)
            {
                config.Rules[idx] = updatedRule;
            }
        });

        await _persistence.SaveAsync(currentConfig);

        return (true, $"Firewall rule '{updatedRule.Name}' updated successfully");
    }

    /// <summary>
    /// Delete firewall rule
    /// </summary>
    public async Task<(bool Success, string Message)> DeleteRuleAsync(
        Guid userId,
        bool isAdmin,
        Guid ruleId)
    {
        var currentConfig = _persistence.Load();
        if (currentConfig == null)
        {
            return (false, "Failed to load firewall configuration");
        }

        var existingRule = currentConfig.Rules.FirstOrDefault(r => r.RuleId == ruleId);
        if (existingRule == null)
        {
            return (false, $"Rule {ruleId} not found");
        }

        if (!CanModifyRule(userId, existingRule, isAdmin))
        {
            return (false, "Insufficient permissions to delete this rule");
        }

        // Remove rule
        currentConfig.Rules.Remove(existingRule);

        // Update engine and persist
        _engine.UpdateConfiguration(config =>
        {
            var rule = config.Rules.FirstOrDefault(r => r.RuleId == ruleId);
            if (rule != null)
            {
                config.Rules.Remove(rule);
            }
        });

        await _persistence.SaveAsync(currentConfig);

        return (true, $"Firewall rule '{existingRule.Name}' deleted successfully");
    }

    /// <summary>
    /// List all firewall rules (filtered by permissions)
    /// </summary>
    public List<FirewallRule> ListRules(Guid userId, bool isAdmin)
    {
        var currentConfig = _persistence.Load();
        if (currentConfig == null)
        {
            return new List<FirewallRule>();
        }

        // Admins see all rules
        if (isAdmin)
        {
            return currentConfig.Rules;
        }

        // Non-admins only see rules they created or can edit
        return currentConfig.Rules
            .Where(r => r.CreatedBy == userId || r.AllowedEditors.Contains(userId))
            .ToList();
    }
}
