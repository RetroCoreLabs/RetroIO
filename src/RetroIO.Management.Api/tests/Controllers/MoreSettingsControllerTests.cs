//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using RetroIO.Management.Api.Controllers;
using RetroIO.Management.Contracts;
using RetroIO.Server.Hosting;
using RetroIO.Server.Hosting.Configuration;
using RetroIO.Storage.Core;

namespace RetroIO.Management.Api.Tests.Controllers;

/// <summary>
/// The settings added 2026-11-10: the TNFS and Econet on/off switches, the Econet discs and reply formats, the mounted
/// volumes list and the start-up file. Each is checked against what the server itself reads.
/// </summary>
[TestFixture]
public class MoreSettingsControllerTests
{
    private string _data = null!;

    [SetUp]
    public void SetUp()
    {
        _data = Path.Combine(Path.GetTempPath(), "retroserver-more-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_data);
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
    /// A TNFS or Econet file written before the switch existed reads as switched on, so nothing changes for it.
    /// </summary>
    [Test]
    public void OldFilesWithoutTheSwitch_ReadAsOn()
    {
        File.WriteAllText(Path.Combine(_data, "tnfs-config.json"), "{ \"TcpPort\": 16384 }");
        File.WriteAllText(Path.Combine(_data, "econet-config.json"), "{ \"UdpPort\": 32768 }");

        Assert.That(ProtocolConfigurationLoader.LoadTnfsConfiguration(_data, Empty()).Enabled, Is.True);
        Assert.That(ProtocolConfigurationLoader.LoadEconetConfiguration(_data, Empty()).Enabled, Is.True);
    }

    /// <summary>
    /// The switches, the station byte, the reply formats and the disc list saved through the API are what the server loads.
    /// </summary>
    [Test]
    public void EconetAndTnfs_Saved_IsWhatTheServerLoads()
    {
        SettingsController controller = new SettingsController(new Mock<ILogger<SettingsController>>().Object, _data, _data);

        Assert.That(controller.UpdateTnfsSettings(new UpdateTnfsSettingsRequest { Enabled = false }).Result, Is.InstanceOf<OkObjectResult>());

        ActionResult<ApiResponse<EconetSettingsDto>> result = controller.UpdateEconetSettings(new UpdateEconetSettingsRequest
        {
            Enabled = false,
            AunStationOctetIndex = 2,
            WireProfile = "classic1983",
            WireProfileOverrides = new List<EconetWireProfileOverrideDto>
            {
                new EconetWireProfileOverrideDto { Net = 1, Station = 100, Profile = "LaterFS" },
            },
            Discs = new List<EconetDiscDto>
            {
                new EconetDiscDto { Index = 0, AcornName = "Games", Provider = "native", Uri = "/discs/games" },
                new EconetDiscDto { Index = 1, AcornName = "Work", MountRef = "work-volume", ReadOnly = true },
            },
        });
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());

        Assert.That(ProtocolConfigurationLoader.LoadTnfsConfiguration(_data, Empty()).Enabled, Is.False);
        EconetServerConfiguration econet = ProtocolConfigurationLoader.LoadEconetConfiguration(_data, Empty());
        Assert.That(econet.Enabled, Is.False);
        Assert.That(econet.AunStationOctetIndex, Is.EqualTo(2));
        Assert.That(econet.WireProfile.ToString(), Is.EqualTo("Classic1983"));
        Assert.That(econet.WireProfileOverrides!.Count, Is.EqualTo(1));
        Assert.That(econet.WireProfileOverrides[0].Station, Is.EqualTo(100));
        Assert.That(econet.Discs!.Count, Is.EqualTo(2));
        Assert.That(econet.Discs[0].Uri, Is.EqualTo("/discs/games"));
        Assert.That(econet.Discs[1].MountRef, Is.EqualTo("work-volume"));
        Assert.That(econet.Discs[1].ReadOnly, Is.True);

        // Clearing the disc list writes "no list", which is one folder per disc again.
        controller.UpdateEconetSettings(new UpdateEconetSettingsRequest { Discs = new List<EconetDiscDto>() });
        Assert.That(ProtocolConfigurationLoader.LoadEconetConfiguration(_data, Empty()).Discs, Is.Null);
    }

    /// <summary>
    /// An unknown reply format, a disc with both kinds of storage, and a station byte over 3 are refused.
    /// </summary>
    [Test]
    public void Econet_BadSettings_AreRefused()
    {
        SettingsController controller = new SettingsController(new Mock<ILogger<SettingsController>>().Object, _data, _data);

        Assert.That(controller.UpdateEconetSettings(new UpdateEconetSettingsRequest { WireProfile = "Fast" }).Result,
            Is.InstanceOf<BadRequestObjectResult>());
        Assert.That(controller.UpdateEconetSettings(new UpdateEconetSettingsRequest { AunStationOctetIndex = 4 }).Result,
            Is.InstanceOf<BadRequestObjectResult>());
        Assert.That(controller.UpdateEconetSettings(new UpdateEconetSettingsRequest
        {
            Discs = new List<EconetDiscDto> { new EconetDiscDto { Index = 0, AcornName = "X", Provider = "native", Uri = "/x", MountRef = "y" } },
        }).Result, Is.InstanceOf<BadRequestObjectResult>());

        Assert.That(File.Exists(Path.Combine(_data, "econet-config.json")), Is.False);
    }

    /// <summary>
    /// The mounted volumes list is saved in the file the Econet discs read; a password is never sent back and is kept
    /// when no new one is given.
    /// </summary>
    [Test]
    public void Mounts_Saved_KeepThePassword_AndNeverShowIt()
    {
        MountsController controller = new MountsController(_data);
        List<MountDto> mounts = new List<MountDto>
        {
            new MountDto { VolumeId = "work", ProviderType = "tnfs", SourceUri = "tnfs://host/work", Username = "u", NewPassword = "secret" },
        };

        List<MountDto> saved = Data(controller.PutMounts(mounts));
        Assert.That(saved[0].PasswordSet, Is.True);
        Assert.That(saved[0].NewPassword, Is.Null);

        saved[0].SourceUri = "tnfs://host/work2";
        controller.PutMounts(saved);

        UnifiedMountConfig file = UnifiedMountConfigStore.Load(Path.Combine(_data, "mounts.json"));
        Assert.That(file.Mounts[0].SourceUri, Is.EqualTo("tnfs://host/work2"));
        Assert.That(file.Mounts[0].Password, Is.EqualTo("secret"));

        mounts.Add(new MountDto { VolumeId = "WORK", ProviderType = "native", SourceUri = "/x" });
        Assert.That(controller.PutMounts(mounts).Result, Is.InstanceOf<BadRequestObjectResult>(), "the same id twice");
    }

    /// <summary>
    /// A start-up save changes the keys on the page and keeps the rest of the file; the connection string is never
    /// sent back; a bad address or log level is refused.
    /// </summary>
    [Test]
    public void Startup_Saved_KeepsOtherKeys_AndHidesTheConnectionString()
    {
        string file = Path.Combine(_data, "appsettings.json");
        File.WriteAllText(file, "{ \"Logging\": { \"LogLevel\": { \"Default\": \"Information\" } }, \"Azure\": { \"Storage\": { \"ConnectionString\": \"secret\" } }, \"Other\": 7 }");

        StartupController controller = new StartupController(null, _data);
        StartupSettingsDto settings = Data(controller.GetSettings());
        Assert.That(settings.AzureConnectionStringSet, Is.True);
        Assert.That(settings.FileSystemBackend, Is.EqualTo("Local"));

        settings.LogLevels["RetroIO"] = "debug";
        settings.ManagementUrls = "http://localhost:5100;https://localhost:5101";
        Assert.That(controller.PutSettings(settings).Result, Is.InstanceOf<OkObjectResult>());

        IConfiguration read = new ConfigurationBuilder().AddJsonFile(file).Build();
        Assert.That(read["Logging:LogLevel:RetroIO"], Is.EqualTo("Debug"));
        Assert.That(read["Management:Urls"], Is.EqualTo("http://localhost:5100;https://localhost:5101"));
        Assert.That(read["Azure:Storage:ConnectionString"], Is.EqualTo("secret"), "no new string given: the old one stays");
        Assert.That(read["Other"], Is.EqualTo("7"), "a key the page does not show is kept");

        settings.ManagementUrls = "localhost:5000";
        Assert.That(controller.PutSettings(settings).Result, Is.InstanceOf<BadRequestObjectResult>());
        settings.ManagementUrls = string.Empty;
        settings.LogLevels["RetroIO"] = "Loud";
        Assert.That(controller.PutSettings(settings).Result, Is.InstanceOf<BadRequestObjectResult>());
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
