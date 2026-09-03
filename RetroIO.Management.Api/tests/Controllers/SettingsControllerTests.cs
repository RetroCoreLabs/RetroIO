//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using RetroIO.Management.Api.Controllers;
using RetroIO.Management.Contracts;

namespace RetroIO.Management.Api.Tests.Controllers;

[TestFixture]
public class SettingsControllerTests
{
    private Mock<ILogger<SettingsController>> _mockLogger = null!;
    private SettingsController _controller = null!;
    private string _testConfigPath = null!;

    [SetUp]
    public void SetUp()
    {
        _mockLogger = new Mock<ILogger<SettingsController>>();

        // A directory of our own, per fixture run. Without handing this to the controller it
        // falls back to the real per-user directory (Documents\RetroFS\config) and these tests
        // overwrite the developer's live TNFS/server settings -- which also made the two
        // "ReturnsDefaultSettings" tests fail, because they read back what the Update tests wrote.
        _testConfigPath = Path.Combine(Path.GetTempPath(), "retrofs-test-config-" + Guid.NewGuid().ToString("N"));
        _controller = new SettingsController(_mockLogger.Object, _testConfigPath);

        // Clean up any existing test config
        if (Directory.Exists(_testConfigPath))
        {
            Directory.Delete(_testConfigPath, true);
        }
    }

    [TearDown]
    public void TearDown()
    {
        // Clean up test config
        if (Directory.Exists(_testConfigPath))
        {
            Directory.Delete(_testConfigPath, true);
        }
    }

    [Test]
    public void GetTnfsSettings_ReturnsDefaultSettings()
    {
        // Act
        var result = _controller.GetTnfsSettings();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = result.Result as OkObjectResult;
        Assert.That(okResult, Is.Not.Null);
        var response = okResult!.Value as ApiResponse<TnfsSettingsDto>;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Success, Is.True);
        Assert.That(response.Data!.UdpPort, Is.EqualTo(16384));
        Assert.That(response.Data.TcpPort, Is.EqualTo(16384));
        Assert.That(response.Data.UdpEnabled, Is.True);
        Assert.That(response.Data.TcpEnabled, Is.True);
    }

    [Test]
    public void UpdateTnfsSettings_UpdatesSettings()
    {
        // Arrange
        var request = new UpdateTnfsSettingsRequest
        {
            UdpPort = 20000,
            TcpPort = 20001,
            MaxConnections = 200
        };

        // Act
        var result = _controller.UpdateTnfsSettings(request);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = result.Result as OkObjectResult;
        Assert.That(okResult, Is.Not.Null);
        var response = okResult!.Value as ApiResponse<TnfsSettingsDto>;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Success, Is.True);
        Assert.That(response.Data!.UdpPort, Is.EqualTo(20000));
        Assert.That(response.Data.TcpPort, Is.EqualTo(20001));
        Assert.That(response.Data.MaxConnections, Is.EqualTo(200));
    }

    [Test]
    public void GetServerSettings_ReturnsDefaultSettings()
    {
        // Act
        var result = _controller.GetServerSettings();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = result.Result as OkObjectResult;
        Assert.That(okResult, Is.Not.Null);
        var response = okResult!.Value as ApiResponse<ServerSettingsDto>;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Success, Is.True);
        Assert.That(response.Data!.AutoStart, Is.False);
        Assert.That(response.Data.LogLevel, Is.EqualTo("Information"));
        Assert.That(response.Data.MaxLogEntries, Is.EqualTo(1000));
    }

    [Test]
    public void UpdateServerSettings_UpdatesSettings()
    {
        // Arrange
        var request = new UpdateServerSettingsRequest
        {
            AutoStart = true,
            LogLevel = "Debug",
            MaxLogEntries = 2000
        };

        // Act
        var result = _controller.UpdateServerSettings(request);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = result.Result as OkObjectResult;
        Assert.That(okResult, Is.Not.Null);
        var response = okResult!.Value as ApiResponse<ServerSettingsDto>;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Success, Is.True);
        Assert.That(response.Data!.AutoStart, Is.True);
        Assert.That(response.Data.LogLevel, Is.EqualTo("Debug"));
        Assert.That(response.Data.MaxLogEntries, Is.EqualTo(2000));
    }
}
