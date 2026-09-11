//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
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
        _controller = new FirewallController(_firewall, _mockLogger.Object);
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
