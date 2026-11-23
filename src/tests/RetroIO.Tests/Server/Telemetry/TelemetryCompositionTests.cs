//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RetroIO.Server;
using RetroIO.Telemetry;

namespace RetroIO.Tests.Server.Telemetry;

/// <summary>
/// RetroServer's composition with the telemetry log providers in it: the services can be built.
/// </summary>
/// <remarks>
/// Not run in parallel: the data folder is chosen by the RETROFS_DATA_PATH environment variable,
/// which is the same for the whole test process.
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class TelemetryCompositionTests
{
    private string _dataPath = string.Empty;
    private string? _oldDataPath;

    [SetUp]
    public void SetUp()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), "retroio-telemetry-composition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataPath);
        _oldDataPath = Environment.GetEnvironmentVariable("RETROFS_DATA_PATH");
        Environment.SetEnvironmentVariable("RETROFS_DATA_PATH", _dataPath);
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable("RETROFS_DATA_PATH", _oldDataPath);
        try
        {
            Directory.Delete(_dataPath, true);
        }
        catch (IOException)
        {
            // A log file can still be open for a moment; the folder is under the temp folder.
        }
    }

    [Test]
    public async Task WithDriveWireOn_TheLoggerFactoryAndTheService_AreBuilt_WithoutLoopingForEver()
    {
        // The DriveWire session log provider once asked for the DriveWire service at once; the service
        // takes a logger, the logger factory takes the provider, and start-up looped for ever
        // (2026-11-23). Built on another task with a time limit, so the loop fails the test rather
        // than hanging it.
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DriveWire:Enabled"] = "true",
            ["DriveWire:Port"] = "0",
        });
        builder.Services.AddRetroServer(builder.Configuration, builder.Logging, new RetroServerOptions
        {
            EnableTnfs = false,
            EnableEconet = false,
            EnableXmsgHub = false,
            EnableDialUp = false,
            EnableXmsgNode = false,
            EnableManagementApi = false,
            EnableSwagger = false,
            PrintBanners = false,
        });

        using ServiceProvider services = builder.Services.BuildServiceProvider();
        Task<int> build = Task.Run(() =>
        {
            ILoggerFactory factory = services.GetRequiredService<ILoggerFactory>();
            factory.CreateLogger("RetroIO.Protocols.DriveWire.Server.DriveWireSession").LogWarning("session line");
            int sources = 0;
            foreach (ITelemetrySource source in services.GetServices<ITelemetrySource>())
            {
                // IEnumerable from DI has no indexer.
                if (source.Name == "RetroIO.DriveWire")
                {
                    List<TelemetryLogRecord> records = new List<TelemetryLogRecord>();
                    source.CollectLog(0, records);
                    for (int i = 0; i < records.Count; i++)
                    {
                        if (records[i].Message == "session line")
                        {
                            sources++;
                        }
                    }
                }
            }

            return sources;
        });

        int kept = await build.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.That(kept, Is.EqualTo(1), "the session's line is kept once in DriveWire's log");
    }
}
