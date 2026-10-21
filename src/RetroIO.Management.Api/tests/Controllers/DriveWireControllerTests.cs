//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RetroIO.Management.Api.Controllers;
using RetroIO.Management.Api.Services;
using RetroIO.Management.Contracts;
using RetroIO.Protocols.DriveWire.Protocol;
using RetroIO.Protocols.DriveWire.Server;

namespace RetroIO.Management.Api.Tests.Controllers;

/// <summary>
/// The DriveWire endpoints of the management API, and the server manager stopping and starting the DriveWire server.
/// </summary>
[TestFixture]
public class DriveWireControllerTests
{
    [Test]
    public void GetStatistics_NoServer_AnswersNotHosted_NotAnError()
    {
        DriveWireController controller = new DriveWireController();

        OkObjectResult? ok = controller.GetStatistics().Result as OkObjectResult;

        Assert.That(ok, Is.Not.Null);
        ApiResponse<DriveWireStatisticsResponse>? response = ok!.Value as ApiResponse<DriveWireStatisticsResponse>;
        Assert.That(response!.Data!.IsHosted, Is.False);
        Assert.That(response.Data.Transactions, Is.Zero);
    }

    [Test]
    public void GetDrives_NoServer_AnswersNoDrives()
    {
        DriveWireController controller = new DriveWireController();

        OkObjectResult? ok = controller.GetDrives().Result as OkObjectResult;

        ApiResponse<DriveWireDrivesResponse>? response = ok!.Value as ApiResponse<DriveWireDrivesResponse>;
        Assert.That(response!.Data!.Drives, Is.Empty);
    }

    [Test]
    public async Task ServerManager_StopsAndStartsTheDriveWireServer_AndTheControllerReportsIt()
    {
        ServiceCollection services = new ServiceCollection();
        services.AddLogging();
        services.AddDriveWireServer(o => o.Port = 0);
        using ServiceProvider provider = services.BuildServiceProvider();
        DriveWireServerService server = provider.GetRequiredService<DriveWireServerService>();
        List<IHostedService> hosted = new List<IHostedService>(provider.GetServices<IHostedService>());
        Assert.That(hosted, Has.Count.EqualTo(1));
        Assert.That(hosted[0], Is.SameAs(server), "the hosted service is the same object the API reports on");

        await server.StartAsync(CancellationToken.None);
        IPEndPoint first = await server.WaitUntilListeningAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        await AssertAnswersTimeAsync(first);

        ServerManager manager = new ServerManager(NullLogger<ServerManager>.Instance, provider);
        ServerActionResponse stopped = await manager.StopAsync();
        Assert.That(stopped.Success, Is.True, stopped.Message);
        using (TcpClient refused = new TcpClient())
        {
            Assert.ThrowsAsync<SocketException>(async () => await refused.ConnectAsync(first), "stopped means the port is closed");
        }

        ServerActionResponse started = await manager.StartAsync();
        Assert.That(started.Success, Is.True, started.Message);
        IPEndPoint second = await server.WaitUntilListeningAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        await AssertAnswersTimeAsync(second);

        DriveWireController controller = new DriveWireController(server);
        ApiResponse<DriveWireStatisticsResponse>? stats =
            (controller.GetStatistics().Result as OkObjectResult)!.Value as ApiResponse<DriveWireStatisticsResponse>;
        Assert.That(stats!.Data!.IsHosted, Is.True);
        Assert.That(stats.Data.Transactions, Is.EqualTo(2), "one TIME per run");
        Assert.That(stats.Data.TotalConnections, Is.EqualTo(2));
        Assert.That(stats.Data.Listening, Is.EqualTo(second.ToString()));

        await manager.StopAsync();
    }

    private static async Task AssertAnswersTimeAsync(IPEndPoint endPoint)
    {
        using TcpClient client = new TcpClient();
        await client.ConnectAsync(endPoint);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync(new[] { DriveWireOpCode.Time });
        byte[] time = new byte[6];
        using CancellationTokenSource limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await stream.ReadExactlyAsync(time, limit.Token);
        Assert.That(time[1], Is.InRange(1, 12), "the month of the TIME answer");
    }
}
