//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using RetroIO.Protocols.Econet.FileServer.Configuration;
using RetroIO.Server.Hosting;
using RetroIO.Server.Hosting.Configuration;

namespace RetroIO.Tests.Server.Hosting;

[TestFixture]
public sealed class ProtocolConfigurationLoaderTests
{
    [Test]
    public void LoadTnfsConfiguration_AppliesOverrides()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "retrofs-hosting-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);

        try
        {
            IConfiguration config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Tnfs:RootDirectory"] = @"C:\tnfs-override",
                    ["Tnfs:UdpPort"] = "17000"
                })
                .Build();

            TnfsServerConfiguration result = ProtocolConfigurationLoader.LoadTnfsConfiguration(tempPath, config);

            Assert.That(result.RootDirectory, Is.EqualTo(@"C:\tnfs-override"));
            Assert.That(result.UdpPort, Is.EqualTo(17000));
        }
        finally
        {
            if (Directory.Exists(tempPath))
                Directory.Delete(tempPath, true);
        }
    }

    [Test]
    public void LoadTnfsConfiguration_ExistingFile_IsLeftAloneAndNotReportedAsCreated()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "retrofs-hosting-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);
        TextWriter originalOut = Console.Out;

        try
        {
            string configPath = Path.Combine(tempPath, "tnfs-config.json");
            const string original = "{ \"UdpPort\": 12345 }";
            File.WriteAllText(configPath, original);

            StringWriter captured = new StringWriter();
            Console.SetOut(captured);
            TnfsServerConfiguration result = ProtocolConfigurationLoader.LoadTnfsConfiguration(
                tempPath, new ConfigurationBuilder().Build());
            Console.SetOut(originalOut);

            Assert.That(result.UdpPort, Is.EqualTo(12345));
            Assert.That(File.ReadAllText(configPath), Is.EqualTo(original), "an existing file was rewritten");
            Assert.That(captured.ToString(), Does.Not.Contain("Created default"));
        }
        finally
        {
            Console.SetOut(originalOut);
            if (Directory.Exists(tempPath))
                Directory.Delete(tempPath, true);
        }
    }

    [Test]
    public void LoadEconetConfiguration_MissingFile_IsCreatedAndReported()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "retrofs-hosting-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);
        TextWriter originalOut = Console.Out;

        try
        {
            string configPath = Path.Combine(tempPath, "econet-config.json");

            StringWriter captured = new StringWriter();
            Console.SetOut(captured);
            ProtocolConfigurationLoader.LoadEconetConfiguration(tempPath, new ConfigurationBuilder().Build());
            Console.SetOut(originalOut);

            Assert.That(File.Exists(configPath), Is.True);
            Assert.That(captured.ToString(), Does.Contain("Created default Econet configuration"));
        }
        finally
        {
            Console.SetOut(originalOut);
            if (Directory.Exists(tempPath))
                Directory.Delete(tempPath, true);
        }
    }

    [Test]
    public void LoadEconetConfiguration_AppliesOverrides()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "retrofs-hosting-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);

        try
        {
            IConfiguration config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Econet:RootDirectory"] = @"C:\econet-override",
                    ["Econet:UdpPort"] = "33000"
                })
                .Build();

            EconetServerConfiguration result = ProtocolConfigurationLoader.LoadEconetConfiguration(tempPath, config);

            Assert.That(result.RootDirectory, Is.EqualTo(@"C:\econet-override"));
            Assert.That(result.UdpPort, Is.EqualTo(33000));
        }
        finally
        {
            if (Directory.Exists(tempPath))
                Directory.Delete(tempPath, true);
        }
    }

    [Test]
    public void LoadDriveWireConfiguration_MissingFile_GivesTheDefaults_AndIsCreated()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "retrofs-hosting-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);
        TextWriter originalOut = Console.Out;

        try
        {
            Console.SetOut(new StringWriter());
            DriveWireServerConfiguration result =
                ProtocolConfigurationLoader.LoadDriveWireConfiguration(tempPath, new ConfigurationBuilder().Build());
            Console.SetOut(originalOut);

            Assert.That(result.Enabled, Is.True);
            Assert.That(result.Port, Is.EqualTo(65504), "the Becker port default");
            Assert.That(result.BindAddress, Is.EqualTo("127.0.0.1"));
            Assert.That(result.TransactionTimeoutMilliseconds, Is.EqualTo(250), "the specification's time limit");
            Assert.That(result.Drives, Is.Empty);
            Assert.That(File.Exists(Path.Combine(tempPath, "drivewire-config.json")), Is.True);
        }
        finally
        {
            Console.SetOut(originalOut);
            if (Directory.Exists(tempPath))
                Directory.Delete(tempPath, true);
        }
    }

    [Test]
    public void LoadDriveWireConfiguration_OverridesSetPortAndDrives_ByDriveNumber()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "retrofs-hosting-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);

        try
        {
            File.WriteAllText(
                Path.Combine(tempPath, "drivewire-config.json"),
                "{ \"Port\": 7000, \"Drives\": [ { \"Drive\": 0, \"Path\": \"a.dsk\" }, { \"Drive\": 1, \"Path\": \"b.dsk\", \"ReadOnly\": true } ] }");
            IConfiguration config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DriveWire:Port"] = "65510",
                    ["DriveWire:BindAddress"] = "0.0.0.0",
                    ["DriveWire:Drives:0:Path"] = "boot.dsk",
                    ["DriveWire:Drives:0:ReadOnly"] = "true",
                    ["DriveWire:Drives:3:Path"] = "work.dsk"
                })
                .Build();

            DriveWireServerConfiguration result = ProtocolConfigurationLoader.LoadDriveWireConfiguration(tempPath, config);

            Assert.That(result.Port, Is.EqualTo(65510));
            Assert.That(result.BindAddress, Is.EqualTo("0.0.0.0"));
            Assert.That(result.Drives, Has.Count.EqualTo(3));
            DriveWireDriveConfiguration? drive0 = result.Drives.Find(d => d.Drive == 0);
            DriveWireDriveConfiguration? drive1 = result.Drives.Find(d => d.Drive == 1);
            DriveWireDriveConfiguration? drive3 = result.Drives.Find(d => d.Drive == 3);
            Assert.That(drive0!.Path, Is.EqualTo("boot.dsk"), "the override replaces drive 0 from the file");
            Assert.That(drive0.ReadOnly, Is.True);
            Assert.That(drive1!.Path, Is.EqualTo("b.dsk"));
            Assert.That(drive1.ReadOnly, Is.True);
            Assert.That(drive3!.Path, Is.EqualTo("work.dsk"));
            Assert.That(drive3.ReadOnly, Is.False);
        }
        finally
        {
            if (Directory.Exists(tempPath))
                Directory.Delete(tempPath, true);
        }
    }

    [Test]
    public void LoadEconetConfiguration_ParsesDiscsAndWireProfileFromJson()
    {
        // Exercises the REAL loader (System.Text.Json, default options) over a representative
        // econet-config.json — the Discs[] surface AND the WireProfile enum-as-string binding. Without
        // the JsonStringEnumConverter on WireProfile, "Classic1983" would throw and the loader would
        // silently fall back to defaults (Discs=null, WireProfile=LaterFS) — this test would then fail.
        string tempPath = Path.Combine(Path.GetTempPath(), "retrofs-hosting-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);

        try
        {
            string json = """
            {
              "RootDirectory": "/econet-root",
              "DiscName": "RetroFS",
              "WireProfile": "Classic1983",
              "WireProfileOverrides": [
                { "Net": 1, "Station": 42, "Profile": "LaterFS" }
              ],
              "Discs": [
                { "Index": 0, "AcornName": "LOCAL", "Provider": "native", "Uri": "/data/local", "ReadOnly": false },
                { "Index": 1, "AcornName": "CLOUD", "Provider": "tnfs", "Uri": "tnfs://tnfs.example:16384/", "ReadOnly": true }
              ]
            }
            """;
            File.WriteAllText(Path.Combine(tempPath, "econet-config.json"), json);

            EconetServerConfiguration result =
                ProtocolConfigurationLoader.LoadEconetConfiguration(tempPath, new ConfigurationBuilder().Build());

            // WireProfile bound from the string name.
            Assert.That(result.WireProfile, Is.EqualTo(WireProfile.Classic1983));

            // Discs[] bound.
            Assert.That(result.Discs, Is.Not.Null);
            Assert.That(result.Discs!.Count, Is.EqualTo(2));
            Assert.That(result.Discs[0].Provider, Is.EqualTo("native"));
            Assert.That(result.Discs[0].ReadOnly, Is.False);
            Assert.That(result.Discs[1].Provider, Is.EqualTo("tnfs"));
            Assert.That(result.Discs[1].Uri, Is.EqualTo("tnfs://tnfs.example:16384/"));
            Assert.That(result.Discs[1].ReadOnly, Is.True);
            Assert.That(result.Discs[1].ResolveDirName(), Is.EqualTo("1CLOUD"));

            // Per-machine override → resolver array (Profile also bound from its string name).
            WireProfileResolver.MachineOverride[]? overrides = result.ToWireProfileOverrides();
            Assert.That(overrides, Is.Not.Null);
            Assert.That(overrides!.Length, Is.EqualTo(1));
            Assert.That(overrides[0].Net, Is.EqualTo(1));
            Assert.That(overrides[0].Station, Is.EqualTo(42));
            Assert.That(overrides[0].Profile, Is.EqualTo(WireProfile.LaterFS));
        }
        finally
        {
            if (Directory.Exists(tempPath))
                Directory.Delete(tempPath, true);
        }
    }
}
