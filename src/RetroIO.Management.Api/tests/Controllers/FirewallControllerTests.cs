//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using RetroIO.Management.Api.Controllers;
using RetroIO.Management.Contracts;
using RetroIO.Security.Firewall;

namespace RetroIO.Management.Api.Tests.Controllers;

[TestFixture]
public class FirewallControllerTests
{
    private FirewallEngine _firewall = null!;
    private Mock<ILogger<FirewallController>> _mockLogger = null!;
    private FirewallController _controller = null!;
    private string _file = null!;

    [SetUp]
    public void SetUp()
    {
        var config = new FirewallConfiguration
        {
            Enabled = true,
            DefaultPolicy = FirewallPolicy.Allow,
            AllowLocalhost = true,
            AllowPrivateNetworks = true
        };
        _firewall = new FirewallEngine(config);
        _mockLogger = new Mock<ILogger<FirewallController>>();
        _file = Path.Combine(Path.GetTempPath(), "firewall-test-" + Guid.NewGuid().ToString("N") + ".json");
        _controller = new FirewallController(_firewall, new FirewallPersistence(_file), _mockLogger.Object);
    }

    [TearDown]
    public void TearDown()
    {
        File.Delete(_file);
    }

    /// <summary>
    /// A rule created through the API is in firewall-config.json, so it survives a restart. Until
    /// 2026-11-10 it lived in the running engine only.
    /// </summary>
    [Test]
    public void CreateRule_IsWrittenToTheFile()
    {
        _controller.CreateRule(new CreateFirewallRuleRequest
        {
            Name = "Kept",
            IPCondition = "192.168.1.100",
            Action = "Allow",
            Priority = 100,
            Enabled = true,
        });

        FirewallConfiguration? saved = new FirewallPersistence(_file).Load();
        Assert.That(saved, Is.Not.Null);
        Assert.That(saved!.Rules.Count, Is.EqualTo(1));
        Assert.That(saved.Rules[0].Name, Is.EqualTo("Kept"));
    }

    /// <summary>
    /// Every firewall setting can be changed in one call, lands in the file, and is read back.
    /// </summary>
    [Test]
    public void UpdateConfig_ChangesEverySetting_AndWritesTheFile()
    {
        _controller.UpdateConfig(new UpdateFirewallConfigRequest
        {
            Enabled = false,
            DefaultPolicy = "deny",
            AllowLocalhost = false,
            AllowPrivateNetworks = false,
            AllowedIpAddresses = new List<string> { "10.0.0.5" },
            BlockedIpAddresses = new List<string> { "203.0.113.9" },
            AllowedNetworks = new List<string> { "192.168.1.0/24" },
            CacheTTLSeconds = 30,
        });

        FirewallConfiguration saved = new FirewallPersistence(_file).Load()!;
        Assert.That(saved.Enabled, Is.False);
        Assert.That(saved.DefaultPolicy, Is.EqualTo(FirewallPolicy.Deny));
        Assert.That(saved.AllowLocalhost, Is.False);
        Assert.That(saved.AllowPrivateNetworks, Is.False);
        Assert.That(saved.AllowedIpAddresses, Is.EqualTo(new[] { "10.0.0.5" }));
        Assert.That(saved.BlockedIpAddresses, Is.EqualTo(new[] { "203.0.113.9" }));
        Assert.That(saved.AllowedNetworks, Is.EqualTo(new[] { "192.168.1.0/24" }));
        Assert.That(saved.CacheTTLSeconds, Is.EqualTo(30));

        ApiResponse<FirewallConfigDto> read = (ApiResponse<FirewallConfigDto>)((OkObjectResult)_controller.GetConfig().Result!).Value!;
        Assert.That(read.Data!.CacheTTLSeconds, Is.EqualTo(30));
    }

    /// <summary>
    /// A policy that is neither Allow nor Deny is refused and nothing is written.
    /// </summary>
    [Test]
    public void UpdateConfig_WithAnUnknownPolicy_IsRefused()
    {
        ActionResult<ApiResponse<FirewallConfigDto>> result = _controller.UpdateConfig(new UpdateFirewallConfigRequest { DefaultPolicy = "maybe" });

        Assert.That(result.Result, Is.InstanceOf<BadRequestObjectResult>());
        Assert.That(File.Exists(_file), Is.False);
    }

    /// <summary>
    /// Switching the firewall off and changing the default policy are written to the file too.
    /// </summary>
    [Test]
    public void SetEnabledAndPolicy_AreWrittenToTheFile()
    {
        _controller.SetEnabled(new SetFirewallEnabledRequest { Enabled = false });
        _controller.SetDefaultPolicy(new SetFirewallPolicyRequest { Policy = "Deny" });

        FirewallConfiguration? saved = new FirewallPersistence(_file).Load();
        Assert.That(saved!.Enabled, Is.False);
        Assert.That(saved.DefaultPolicy, Is.EqualTo(FirewallPolicy.Deny));
    }

    [Test]
    public void GetConfig_ReturnsConfiguration()
    {
        // Act
        var result = _controller.GetConfig();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = result.Result as OkObjectResult;
        Assert.That(okResult, Is.Not.Null);
        var response = okResult!.Value as ApiResponse<FirewallConfigDto>;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Success, Is.True);
        Assert.That(response.Data!.Enabled, Is.True);
        Assert.That(response.Data.DefaultPolicy, Is.EqualTo("Allow"));
    }

    [Test]
    public void GetRules_ReturnsEmptyList()
    {
        // Act
        var result = _controller.GetRules();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = result.Result as OkObjectResult;
        Assert.That(okResult, Is.Not.Null);
        var response = okResult!.Value as ApiResponse<FirewallRulesResponse>;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Success, Is.True);
        Assert.That(response.Data!.Rules.Count, Is.EqualTo(0));
    }

    [Test]
    public void CreateRule_AddsNewRule()
    {
        // Arrange
        var request = new CreateFirewallRuleRequest
        {
            Name = "Test Rule",
            IPCondition = "192.168.1.100",
            Action = "Allow",
            Priority = 100,
            Enabled = true,
            Description = "Test rule description"
        };

        // Act
        var result = _controller.CreateRule(request);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = result.Result as OkObjectResult;
        Assert.That(okResult, Is.Not.Null);
        var response = okResult!.Value as ApiResponse<FirewallRuleDto>;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Success, Is.True);
        Assert.That(response.Data!.Name, Is.EqualTo("Test Rule"));
        Assert.That(response.Data.IPCondition, Is.EqualTo("192.168.1.100"));
    }

    [Test]
    public void DeleteRule_ValidId_ReturnsOk()
    {
        // Arrange - First create a rule
        var createRequest = new CreateFirewallRuleRequest
        {
            Name = "Rule to Delete",
            IPCondition = "10.0.0.1",
            Action = "Deny"
        };
        var createResult = _controller.CreateRule(createRequest);
        var createOkResult = createResult.Result as OkObjectResult;
        var createResponse = createOkResult!.Value as ApiResponse<FirewallRuleDto>;
        var ruleId = createResponse!.Data!.RuleId;

        // Act
        var result = _controller.DeleteRule(ruleId);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public void DeleteRule_InvalidId_ReturnsBadRequest()
    {
        // Arrange
        var invalidRuleId = "not-a-guid";

        // Act
        var result = _controller.DeleteRule(invalidRuleId);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public void SetEnabled_UpdatesFirewallState()
    {
        // Arrange
        var request = new SetFirewallEnabledRequest { Enabled = false };

        // Act
        var result = _controller.SetEnabled(request);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = result.Result as OkObjectResult;
        Assert.That(okResult, Is.Not.Null);
        var response = okResult!.Value as ApiResponse<object>;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Success, Is.True);

        // The test's name promises the firewall state was updated — the assertions above only
        // checked that the controller reported success, not that Enabled actually changed. Read
        // it back via GetConfig to confirm SetEnabled had a real effect, not just a happy response.
        var configResult = _controller.GetConfig();
        var configOkResult = configResult.Result as OkObjectResult;
        Assert.That(configOkResult, Is.Not.Null);
        var configResponse = configOkResult!.Value as ApiResponse<FirewallConfigDto>;
        Assert.That(configResponse, Is.Not.Null);
        Assert.That(configResponse!.Data!.Enabled, Is.False);
    }

    [Test]
    public void TestIpAddress_AllowedIp_ReturnsAllowed()
    {
        // Arrange
        var request = new TestFirewallRequest { IpAddress = "127.0.0.1" };

        // Act
        var result = _controller.TestIpAddress(request);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = result.Result as OkObjectResult;
        Assert.That(okResult, Is.Not.Null);
        var response = okResult!.Value as ApiResponse<FirewallTestResult>;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Success, Is.True);
        Assert.That(response.Data!.Allowed, Is.True);
    }

    [Test]
    public void GetCacheStats_ReturnsStats()
    {
        // Act
        var result = _controller.GetCacheStats();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = result.Result as OkObjectResult;
        Assert.That(okResult, Is.Not.Null);
        var response = okResult!.Value as ApiResponse<FirewallCacheStats>;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Success, Is.True);
    }

    [Test]
    public void ClearCache_ReturnsOk()
    {
        // Act
        var result = _controller.ClearCache();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = result.Result as OkObjectResult;
        Assert.That(okResult, Is.Not.Null);
        var response = okResult!.Value as ApiResponse<object>;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Success, Is.True);
    }
}
