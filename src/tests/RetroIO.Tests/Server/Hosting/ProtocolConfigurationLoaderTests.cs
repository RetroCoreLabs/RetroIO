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
