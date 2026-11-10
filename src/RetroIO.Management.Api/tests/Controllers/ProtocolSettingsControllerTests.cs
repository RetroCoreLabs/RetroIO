//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using RetroIO.Management.Api.Controllers;
using RetroIO.Management.Contracts;
using RetroIO.Server.Hosting;
using RetroIO.Server.Hosting.Configuration;

namespace RetroIO.Management.Api.Tests.Controllers;

/// <summary>
/// The DriveWire and XMSG node settings pages write the files the server itself loads at start.
/// </summary>
[TestFixture]
public class ProtocolSettingsControllerTests
{
    private string _data = null!;

    [SetUp]
    public void SetUp()
    {
        _data = Path.Combine(Path.GetTempPath(), "retroserver-settings-test-" + Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, true);
        }
    }

    /// <summary>
    /// DriveWire settings saved through the API are what the server's own loader reads at the next start.
    /// </summary>
    [Test]
    public void DriveWire_Saved_IsWhatTheServerLoads()
    {
        DriveWireController controller = new DriveWireController(null, _data);
        DriveWireSettingsDto settings = Data(controller.GetSettings());
        settings.Port = 65500;
        settings.TransactionTimeoutMilliseconds = 400;
        settings.PrintDirectory = "/print";
        settings.Drives.Add(new DriveWireDriveSettingDto { Drive = 1, Path = "/disks/games.dsk", ReadOnly = true });

        Assert.That(controller.PutSettings(settings).Result, Is.InstanceOf<OkObjectResult>());

        DriveWireServerConfiguration loaded = ProtocolConfigurationLoader.LoadDriveWireConfiguration(_data, Empty());
        Assert.That(loaded.Port, Is.EqualTo(65500));
        Assert.That(loaded.TransactionTimeoutMilliseconds, Is.EqualTo(400));
        Assert.That(loaded.PrintDirectory, Is.EqualTo("/print"));
        Assert.That(loaded.Drives.Count, Is.EqualTo(1));
        Assert.That(loaded.Drives[0].Path, Is.EqualTo("/disks/games.dsk"));
        Assert.That(loaded.Drives[0].ReadOnly, Is.True);
    }

    /// <summary>
    /// A drive listed twice, or a bad address, is refused and nothing is written.
    /// </summary>
    [Test]
    public void DriveWire_BadSettings_AreRefused()
    {
        DriveWireController controller = new DriveWireController(null, _data);
        DriveWireSettingsDto settings = Data(controller.GetSettings());
        settings.Drives.Add(new DriveWireDriveSettingDto { Drive = 0, Path = "a.dsk" });
        settings.Drives.Add(new DriveWireDriveSettingDto { Drive = 0, Path = "b.dsk" });
        Assert.That(controller.PutSettings(settings).Result, Is.InstanceOf<BadRequestObjectResult>());

        settings.Drives.Clear();
        settings.BindAddress = "not-an-address";
        Assert.That(controller.PutSettings(settings).Result, Is.InstanceOf<BadRequestObjectResult>());

        Assert.That(File.Exists(Path.Combine(_data, ProtocolConfigurationLoader.DriveWireConfigurationFileName)), Is.False);
    }

    /// <summary>
    /// Node settings saved through the API are what the server loads; the token is never sent back, is kept when
    /// no new one is given, and is removed by an empty one.
    /// </summary>
    [Test]
    public void XmsgNode_Saved_IsWhatTheServerLoads_AndTheTokenIsNeverShown()
    {
        XmsgNodeController controller = new XmsgNodeController(_data);
        XmsgNodeSettingsDto settings = Data(controller.GetSettings());
        settings.Enabled = true;
        settings.SystemNumber = 19998;
        settings.PeerSystemNumber = 102;
        settings.ProgramLogMinimum = "debug";
        settings.NewProgramToken = "secret";

        XmsgNodeSettingsDto saved = Data(controller.PutSettings(settings));
        Assert.That(saved.ProgramTokenSet, Is.True);
        Assert.That(saved.NewProgramToken, Is.Null);

        XmsgNodeServerConfiguration loaded = ProtocolConfigurationLoader.LoadXmsgNodeConfiguration(_data, Empty());
        Assert.That(loaded.Enabled, Is.True);
        Assert.That(loaded.SystemNumber, Is.EqualTo(19998));
        Assert.That(loaded.PeerSystemNumber, Is.EqualTo(102));
        Assert.That(loaded.ProgramLogMinimum, Is.EqualTo("Debug"));
        Assert.That(loaded.ProgramToken, Is.EqualTo("secret"));

        // No new token: the stored one stays.
        XmsgNodeSettingsDto again = Data(controller.GetSettings());
        again.Motd = "hello";
        controller.PutSettings(again);
        Assert.That(ProtocolConfigurationLoader.LoadXmsgNodeConfiguration(_data, Empty()).ProgramToken, Is.EqualTo("secret"));

        // An empty one removes it.
        again.NewProgramToken = string.Empty;
        controller.PutSettings(again);
        Assert.That(ProtocolConfigurationLoader.LoadXmsgNodeConfiguration(_data, Empty()).ProgramToken, Is.Null);
    }

    /// <summary>
    /// Switching the node on without system numbers, or with an unknown log level, is refused.
    /// </summary>
    [Test]
    public void XmsgNode_BadSettings_AreRefused()
    {
        XmsgNodeController controller = new XmsgNodeController(_data);
        XmsgNodeSettingsDto settings = Data(controller.GetSettings());
        settings.Enabled = true;
        Assert.That(controller.PutSettings(settings).Result, Is.InstanceOf<BadRequestObjectResult>());

        settings.SystemNumber = 19998;
        settings.PeerSystemNumber = 102;
        settings.ProgramLogMinimum = "Loud";
        Assert.That(controller.PutSettings(settings).Result, Is.InstanceOf<BadRequestObjectResult>());
    }

    /// <summary>
    /// Security settings saved through the API are what AddRetroSecurity reads; an empty limit stays "no limit".
    /// </summary>
    [Test]
    public void Security_Saved_IsWhatTheServerLoads()
    {
        SecurityController controller = new SecurityController(_data);
        SecuritySettingsDto settings = Data(controller.GetSettings());
        Assert.That(settings.PerIpRequestsPerSecond, Is.EqualTo(200), "the default is the number that was built in");
        settings.PerIpRequestsPerSecond = 5;
        settings.ServerMegabytesPerHour = null;
        settings.AuditFlushIntervalSeconds = 30;

        Assert.That(controller.PutSettings(settings).Result, Is.InstanceOf<OkObjectResult>());

        SecurityServerConfiguration loaded = ProtocolConfigurationLoader.ReadSettingsFile(
            _data, ProtocolConfigurationLoader.SecurityConfigurationFileName, SecurityServerConfiguration.GetDefault);
        Assert.That(loaded.PerIpRequestsPerSecond, Is.EqualTo(5));
        Assert.That(loaded.ServerMegabytesPerHour, Is.Null);
        Assert.That(loaded.AuditFlushIntervalSeconds, Is.EqualTo(30));
    }

    /// <summary>
    /// A limit of 0 or an audit batch of 0 is refused and nothing is written.
    /// </summary>
    [Test]
    public void Security_BadSettings_AreRefused()
    {
        SecurityController controller = new SecurityController(_data);
        SecuritySettingsDto settings = Data(controller.GetSettings());
        settings.PerUserRequestsPerSecond = 0;
        Assert.That(controller.PutSettings(settings).Result, Is.InstanceOf<BadRequestObjectResult>());

        settings.PerUserRequestsPerSecond = null;
        settings.AuditBatchSize = 0;
        Assert.That(controller.PutSettings(settings).Result, Is.InstanceOf<BadRequestObjectResult>());

        Assert.That(File.Exists(Path.Combine(_data, ProtocolConfigurationLoader.SecurityConfigurationFileName)), Is.False);
    }

    private static T Data<T>(ActionResult<ApiResponse<T>> result)
    {
        return ((ApiResponse<T>)((OkObjectResult)result.Result!).Value!).Data!;
    }

    private static IConfiguration Empty()
    {
        return new ConfigurationBuilder().Build();
    }
}
