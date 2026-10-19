//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RetroIO.Identity;
using RetroIO.Protocols.Econet.FileServer;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;
using RetroIO.Server.Core.FileSystem;
using RetroIO.Tests.TNFS;

namespace RetroIO.Tests.Econet;

/// <summary>
/// The station number of the file server itself. The reference is PiFS in PiEconetBridge utilities/fs.c:
/// PiBridge call 19 answers "Fileserver at net.stn shutting down" and then logs every station off
/// (lines 4296 to 4304, fs_shutdown lines 3819 to 3840, fs_reply_ok_with_data lines 3909 to 3924).
/// An AUN datagram has no station field: include/econet-gpio-consumer.h lines 155 to 165 (struct
/// __econet_packet_udp) holds ptype, port, ctrl, pad and the 32-bit seq, then the data.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class FileServerStationTests
{
    [Test]
    public void Options_StationDefaultsTo254()
    {
        Assert.That(new EconetFileServerOptions().Station, Is.EqualTo(254));
    }

    [Test]
    public void Registration_PassesTheConfiguredNetAndStationToTheContext()
    {
        ServiceCollection services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IFileSystemBackend>(new MockFileSystemBackend());
        EconetTestUsers.CreateDirectory(out IRetroIDP idp);
        services.AddSingleton(idp);

        services.AddEconetFileServer(o =>
        {
            o.EnableUdp = false;
            o.Net = 7;
            o.Station = 99;
        });

        using ServiceProvider provider = services.BuildServiceProvider();
        FileServerContext context = provider.GetRequiredService<FileServerContext>();

        Assert.That(context.ServerNet, Is.EqualTo(7));
        Assert.That(context.ServerStation, Is.EqualTo(99));
    }

    [Test]
    public void PiBridgeShutDown_NamesTheConfiguredNetAndStation()
    {
        EconetFsTestHarness harness = new EconetFsTestHarness(5, 200);
        harness.LoginAsSyst();

        byte[] reply = harness.Send(FileServerOpcodes.PiBridge, [19]).Payload;

        // fs_reply_ok_with_data: two zero bytes, then the text. The text ends with a carriage return.
        Assert.That(reply[0], Is.EqualTo(0));
        Assert.That(reply[1], Is.EqualTo(0));
        Assert.That(Encoding.ASCII.GetString(reply, 2, reply.Length - 2), Is.EqualTo("Fileserver at 5.200 shutting down\r"));
    }

    [Test]
    public void PiBridgeShutDown_UsesTheDefaultAddressWhenNoneIsConfigured()
    {
        EconetFsTestHarness harness = new EconetFsTestHarness();
        harness.LoginAsSyst();

        byte[] reply = harness.Send(FileServerOpcodes.PiBridge, [19]).Payload;

        Assert.That(Encoding.ASCII.GetString(reply, 2, reply.Length - 2), Is.EqualTo("Fileserver at 1.254 shutting down\r"));
    }

    [Test]
    public void PiBridgeShutDown_LogsEveryStationOff()
    {
        // fs_shutdown (3819 to 3840) calls fs_bye for every active station, not only the caller.
        EconetFsTestHarness harness = new EconetFsTestHarness();
        harness.LoginAsSyst();
        Assert.That(harness.Sessions.GetActiveSessionCount(), Is.EqualTo(1));

        harness.Send(FileServerOpcodes.PiBridge, [19]);

        Assert.That(harness.Sessions.GetActiveSessionCount(), Is.EqualTo(0));
    }

    [Test]
    public void PiBridgeShutDown_NeedsAnAdministrator()
    {
        // fs.c 4160 to 4165: arguments 16 to 31 need the SYST privilege.
        EconetFsTestHarness harness = new EconetFsTestHarness();
        harness.LoginAsUser();

        byte[] reply = harness.Send(FileServerOpcodes.PiBridge, [19]).Payload;

        Assert.That(reply[1], Is.EqualTo(FileServerErrors.InsufficientAccess));
        Assert.That(harness.Sessions.GetActiveSessionCount(), Is.EqualTo(1));
    }
}
