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
        _controller = new SettingsController(_mockLogger.Object, _testConfigPath, _testConfigPath);

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

    /// <summary>
    /// The TNFS page writes tnfs-config.json, the file the server loads, limits and timeout included.
    /// </summary>
    [Test]
    public void UpdateTnfsSettings_IsWhatTheServerLoads()
    {
        var request = new UpdateTnfsSettingsRequest
        {
            TcpPort = 20001,
            MaxConnections = 8,
            MaxSessions = 9,
            MaxSessionsPerIP = 2,
            SessionTimeoutSeconds = 30,
            RootPath = "/srv/tnfs",
        };

        Assert.That(_controller.UpdateTnfsSettings(request).Result, Is.InstanceOf<OkObjectResult>());

        RetroIO.Server.Hosting.Configuration.TnfsServerConfiguration loaded =
            RetroIO.Server.Hosting.ProtocolConfigurationLoader.LoadTnfsConfiguration(
                _testConfigPath, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        Assert.That(loaded.TcpPort, Is.EqualTo(20001));
        Assert.That(loaded.MaxTcpConnections, Is.EqualTo(8));
        Assert.That(loaded.MaxSessions, Is.EqualTo(9));
        Assert.That(loaded.MaxSessionsPerIP, Is.EqualTo(2));
        Assert.That(loaded.SessionTimeoutSeconds, Is.EqualTo(30));
        Assert.That(loaded.RootDirectory, Is.EqualTo("/srv/tnfs"));
    }

    /// <summary>
    /// A limit of 0 is refused and nothing is written.
    /// </summary>
    [Test]
    public void UpdateTnfsSettings_ZeroLimit_IsRefused()
    {
        Assert.That(_controller.UpdateTnfsSettings(new UpdateTnfsSettingsRequest { MaxSessions = 0 }).Result,
            Is.InstanceOf<BadRequestObjectResult>());
        Assert.That(File.Exists(Path.Combine(_testConfigPath, "tnfs-config.json")), Is.False);
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

    /// <summary>
    /// Saving the Econet page changes only the fields it edits and keeps every other field of
    /// econet-config.json. Until 2026-11-10 one Save wrote a new file with seven fields and deleted
    /// the disc list, the wire profiles and AunStationOctetIndex.
    /// </summary>
    [Test]
    public void UpdateEconetSettings_KeepsTheFieldsThePageDoesNotEdit()
    {
        Directory.CreateDirectory(_testConfigPath);
        string file = Path.Combine(_testConfigPath, "econet-config.json");
        File.WriteAllText(file, """
            {
              "RootDirectory": "/data/econet",
              "DiscName": "OLD",
              "UdpPort": 32768,
              "Net": 1,
              "Station": 254,
              "AunStationOctetIndex": 2,
              "EnableUdp": true,
              "Discs": [ { "Index": 1, "AcornName": "GAMES", "DirName": "games", "Provider": "native", "Uri": "/data/games" } ]
            }
            """);

        _controller.UpdateEconetSettings(new UpdateEconetSettingsRequest { DiscName = "NEW", Station = 200 });

        using System.Text.Json.JsonDocument saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
        System.Text.Json.JsonElement root = saved.RootElement;
        Assert.That(root.GetProperty("DiscName").GetString(), Is.EqualTo("NEW"));
        Assert.That(root.GetProperty("Station").GetInt32(), Is.EqualTo(200));
        Assert.That(root.GetProperty("AunStationOctetIndex").GetInt32(), Is.EqualTo(2));
        Assert.That(root.GetProperty("RootDirectory").GetString(), Is.EqualTo("/data/econet"));
        System.Text.Json.JsonElement discs = root.GetProperty("Discs");
        Assert.That(discs.GetArrayLength(), Is.EqualTo(1));
        Assert.That(discs[0].GetProperty("AcornName").GetString(), Is.EqualTo("GAMES"));
    }
}
