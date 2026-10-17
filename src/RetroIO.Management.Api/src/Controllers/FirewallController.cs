//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.AspNetCore.Mvc;
using RetroIO.Management.Contracts;
using RetroIO.Security.Firewall;

namespace RetroIO.Management.Api.Controllers;

/// <summary>
/// Management API controller for the firewall configuration and rules.
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
public class FirewallController : ControllerBase
{
    private readonly FirewallEngine _firewall;
    private readonly ILogger<FirewallController> _logger;

    /// <summary>
    /// Creates the controller.
    /// </summary>
    /// <param name="firewall">
    /// The firewall engine to read and change.
    /// </param>
    /// <param name="logger">
    /// The logger for error messages.
    /// </param>
    public FirewallController(FirewallEngine firewall, ILogger<FirewallController> logger)
    {
        _firewall = firewall;
        _logger = logger;
    }

    /// <summary>
    /// Get firewall configuration
    /// </summary>
    [HttpGet("config")]
    public ActionResult<ApiResponse<FirewallConfigDto>> GetConfig()
    {
        try
        {
            FirewallConfigDto? configDto = null;

            _firewall.UpdateConfiguration(config =>
            {
                var rules = new List<FirewallRuleDto>();
                for (int i = 0; i < config.Rules.Count; i++)
                {
                    var rule = config.Rules[i];
                    rules.Add(new FirewallRuleDto
                    {
                        RuleId = rule.RuleId.ToString(),
                        Name = rule.Name,
                        IPCondition = rule.IPCondition,
                        Action = rule.Action.ToString(),
                        Priority = rule.Priority,
                        Enabled = rule.Enabled,
                        Description = rule.Description,
                        CreatedAt = rule.CreatedAt
                    });
                }

                configDto = new FirewallConfigDto
                {
                    Enabled = config.Enabled,
                    DefaultPolicy = config.DefaultPolicy.ToString(),
                    AllowLocalhost = config.AllowLocalhost,
                    AllowPrivateNetworks = config.AllowPrivateNetworks,
                    Rules = rules,
                    AllowedIpAddresses = new List<string>(config.AllowedIpAddresses),
                    BlockedIpAddresses = new List<string>(config.BlockedIpAddresses),
                    AllowedNetworks = new List<string>(config.AllowedNetworks)
                };
            });

            return Ok(ApiResponse<FirewallConfigDto>.Ok(configDto!));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting firewall configuration");
            return StatusCode(500, ApiResponse<FirewallConfigDto>.Fail("Internal server error"));
        }
    }

    /// <summary>
    /// Get all firewall rules
    /// </summary>
    [HttpGet("rules")]
    public ActionResult<ApiResponse<FirewallRulesResponse>> GetRules()
    {
        try
        {
            FirewallRulesResponse? response = null;

            _firewall.UpdateConfiguration(config =>
            {
                var rules = new List<FirewallRuleDto>();
                for (int i = 0; i < config.Rules.Count; i++)
                {
                    var rule = config.Rules[i];
                    rules.Add(new FirewallRuleDto
                    {
                        RuleId = rule.RuleId.ToString(),
                        Name = rule.Name,
                        IPCondition = rule.IPCondition,
                        Action = rule.Action.ToString(),
                        Priority = rule.Priority,
                        Enabled = rule.Enabled,
                        Description = rule.Description,
                        CreatedAt = rule.CreatedAt
                    });
                }

                response = new FirewallRulesResponse
                {
                    Rules = rules,
                    Total = rules.Count
                };
            });

            return Ok(ApiResponse<FirewallRulesResponse>.Ok(response!));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing firewall rules");
            return StatusCode(500, ApiResponse<FirewallRulesResponse>.Fail("Internal server error"));
        }
    }

    /// <summary>
    /// Create a new firewall rule
    /// </summary>
    [HttpPost("rules")]
    public ActionResult<ApiResponse<FirewallRuleDto>> CreateRule([FromBody] CreateFirewallRuleRequest request)
    {
        try
        {
            var newRule = new FirewallRule
            {
                RuleId = Guid.NewGuid(),
                Name = request.Name,
                IPCondition = request.IPCondition,
                Action = Enum.Parse<RuleAction>(request.Action, true),
                Priority = request.Priority,
                Enabled = request.Enabled,
                Description = request.Description ?? string.Empty,
                CreatedAt = DateTime.UtcNow
            };

            _firewall.UpdateConfiguration(config =>
            {
                config.Rules.Add(newRule);
            });

            var ruleDto = new FirewallRuleDto
            {
                RuleId = newRule.RuleId.ToString(),
                Name = newRule.Name,
                IPCondition = newRule.IPCondition,
                Action = newRule.Action.ToString(),
                Priority = newRule.Priority,
                Enabled = newRule.Enabled,
                Description = newRule.Description,
                CreatedAt = newRule.CreatedAt
            };

            return Ok(ApiResponse<FirewallRuleDto>.Ok(ruleDto, "Firewall rule created successfully"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating firewall rule");
            return StatusCode(500, ApiResponse<FirewallRuleDto>.Fail("Failed to create firewall rule"));
        }
    }

    /// <summary>
    /// Delete a firewall rule
    /// </summary>
    [HttpDelete("rules/{ruleId}")]
    public ActionResult<ApiResponse<object>> DeleteRule(string ruleId)
    {
        try
        {
            var guid = Guid.Parse(ruleId);
            bool found = false;

            _firewall.UpdateConfiguration(config =>
            {
                for (int i = config.Rules.Count - 1; i >= 0; i--)
                {
                    if (config.Rules[i].RuleId == guid)
                    {
                        config.Rules.RemoveAt(i);
                        found = true;
                        break;
                    }
                }
            });

            if (!found)
            {
                return NotFound(ApiResponse<object>.Fail("Firewall rule not found", "RULE_NOT_FOUND"));
            }

            return Ok(ApiResponse<object>.Ok(null, "Firewall rule deleted successfully"));
        }
        catch (FormatException)
        {
            return BadRequest(ApiResponse<object>.Fail("Invalid rule ID format", "INVALID_FORMAT"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting firewall rule {RuleId}", ruleId);
            return StatusCode(500, ApiResponse<object>.Fail("Failed to delete firewall rule"));
        }
    }

    /// <summary>
    /// Update firewall rule
    /// </summary>
    [HttpPut("rules/{ruleId}")]
    public ActionResult<ApiResponse<FirewallRuleDto>> UpdateRule(string ruleId, [FromBody] UpdateFirewallRuleRequest request)
    {
        try
        {
            var guid = Guid.Parse(ruleId);
            FirewallRuleDto? updatedDto = null;

            _firewall.UpdateConfiguration(config =>
            {
                for (int i = 0; i < config.Rules.Count; i++)
                {
                    if (config.Rules[i].RuleId == guid)
                    {
                        var rule = config.Rules[i];

                        if (request.Name != null) rule.Name = request.Name;
                        if (request.IPCondition != null) rule.IPCondition = request.IPCondition;
                        if (request.Action != null) rule.Action = Enum.Parse<RuleAction>(request.Action, true);
                        if (request.Priority.HasValue) rule.Priority = request.Priority.Value;
                        if (request.Enabled.HasValue) rule.Enabled = request.Enabled.Value;
                        if (request.Description != null) rule.Description = request.Description;

                        updatedDto = new FirewallRuleDto
                        {
                            RuleId = rule.RuleId.ToString(),
                            Name = rule.Name,
                            IPCondition = rule.IPCondition,
                            Action = rule.Action.ToString(),
                            Priority = rule.Priority,
                            Enabled = rule.Enabled,
                            Description = rule.Description,
                            CreatedAt = rule.CreatedAt
                        };
                        break;
                    }
                }
            });

            if (updatedDto == null)
            {
                return NotFound(ApiResponse<FirewallRuleDto>.Fail("Firewall rule not found", "RULE_NOT_FOUND"));
            }

            return Ok(ApiResponse<FirewallRuleDto>.Ok(updatedDto, "Firewall rule updated successfully"));
        }
        catch (FormatException)
        {
            return BadRequest(ApiResponse<FirewallRuleDto>.Fail("Invalid rule ID format", "INVALID_FORMAT"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating firewall rule {RuleId}", ruleId);
            return StatusCode(500, ApiResponse<FirewallRuleDto>.Fail("Failed to update firewall rule"));
        }
    }

    /// <summary>
    /// Enable or disable firewall
    /// </summary>
    [HttpPut("enabled")]
    public ActionResult<ApiResponse<object>> SetEnabled([FromBody] SetFirewallEnabledRequest request)
    {
        try
        {
            _firewall.UpdateConfiguration(config =>
            {
                config.Enabled = request.Enabled;
            });

            return Ok(ApiResponse<object>.Ok(null, $"Firewall {(request.Enabled ? "enabled" : "disabled")} successfully"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting firewall enabled state");
            return StatusCode(500, ApiResponse<object>.Fail("Failed to update firewall state"));
        }
    }

    /// <summary>
    /// Set default firewall policy
    /// </summary>
    [HttpPut("policy")]
    public ActionResult<ApiResponse<object>> SetDefaultPolicy([FromBody] SetFirewallPolicyRequest request)
    {
        try
        {
            _firewall.UpdateConfiguration(config =>
            {
                config.DefaultPolicy = Enum.Parse<FirewallPolicy>(request.Policy, true);
            });

            return Ok(ApiResponse<object>.Ok(null, $"Default policy set to {request.Policy}"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting default firewall policy");
            return StatusCode(500, ApiResponse<object>.Fail("Failed to update firewall policy"));
        }
    }

    /// <summary>
    /// Test IP address against firewall rules
    /// </summary>
    [HttpPost("test")]
    public ActionResult<ApiResponse<FirewallTestResult>> TestIpAddress([FromBody] TestFirewallRequest request)
    {
        try
        {
            var decision = _firewall.CheckAccess(request.IpAddress);

            var result = new FirewallTestResult
            {
                IpAddress = request.IpAddress,
                Allowed = decision.IsAllowed,
                Reason = decision.Reason,
                RuleId = decision.MatchedRuleId?.ToString()
            };

            return Ok(ApiResponse<FirewallTestResult>.Ok(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing IP address {IpAddress}", request.IpAddress);
            return StatusCode(500, ApiResponse<FirewallTestResult>.Fail("Failed to test IP address"));
        }
    }

    /// <summary>
    /// Get firewall cache statistics
    /// </summary>
    [HttpGet("cache/stats")]
    public ActionResult<ApiResponse<FirewallCacheStats>> GetCacheStats()
    {
        try
        {
            var (total, active) = _firewall.GetCacheStats();

            var stats = new FirewallCacheStats
            {
                TotalEntries = total,
                ActiveEntries = active
            };

            return Ok(ApiResponse<FirewallCacheStats>.Ok(stats));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cache stats");
            return StatusCode(500, ApiResponse<FirewallCacheStats>.Fail("Failed to get cache stats"));
        }
    }

    /// <summary>
    /// Clear firewall cache
    /// </summary>
    [HttpPost("cache/clear")]
    public ActionResult<ApiResponse<object>> ClearCache()
    {
        try
        {
            _firewall.ClearCache();
            return Ok(ApiResponse<object>.Ok(null, "Cache cleared successfully"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing cache");
            return StatusCode(500, ApiResponse<object>.Fail("Failed to clear cache"));
        }
    }
}
